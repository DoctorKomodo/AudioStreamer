using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioStreamer
{
    public class AudioStreamerLogic
    {
        public enum ModeType
        {
            Sender,
            Receiver
        }
        public class Config
        {
            public ModeType Mode { get; set; } = ModeType.Receiver;
            public string HostName { get; set; } = "";   // blank by default so first-run users must enter the receiver's IP
            public int Port { get; set; } = 5005;
            public int SenderAudioBufferMillisecondsLength { get; set; } = 100;
            public int ReceiverAudioBufferMillisecondsLength { get; set; } = 1000;
            public int ReceiverAudioLatencyMilliseconds { get; set; } = 20;
            public int ReceiverMaxLatencyMilliseconds { get; set; } = 400;
            public int SampleRate { get; set; } = 48000;
            public int BitsPerSample { get; set; } = 16;
            public int Channels { get; set; } = 2;
            public bool StartMinimized { get; set; } = false;   // first run shows the window so it can be configured before starting
        }

        public Config CurrentConfig { get; set; } = new();
        private UdpClient? udpClient;
        private CancellationTokenSource? cts;
        private WasapiCapture? waveSource;
        private WasapiOut? wasapiOut;

        public bool IsRunning { get; private set; }

        public event Action<DiagnosticsSnapshot>? Diagnostics;
        private readonly DiagnosticsLog diagnosticsLog = new("diagnostics.log");

        // Cached because System.Text.Json recommends reusing options instances. Indented to keep
        // config.json hand-editable; default enum/property handling matches the old Newtonsoft output
        // (PascalCase names, Mode as an integer) so existing config files still load unchanged.
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        // --- UDP streaming tunables ---
        // Every datagram begins with a 3-byte wave-format header (see PackWaveFormat) plus a 1-byte wrapping
        // sequence number the receiver uses to count lost/reordered packets.
        private const int HeaderBytes = 4;
        // Keep each datagram inside a standard 1500-byte Ethernet MTU (minus 20-byte IP + 8-byte UDP + the
        // header above, with headroom for VPN/PPPoE) so IP never fragments the audio: one lost fragment would
        // otherwise drop the whole datagram. Audio is sliced on frame boundaries (see StartSender).
        private const int MaxUdpAudioBytes = 1440;
        // Roomy socket buffers absorb scheduling jitter and bursts so the kernel doesn't silently drop datagrams.
        private const int SocketBufferBytes = 1 << 20; // 1 MiB
        // Winsock ioctl that stops a UDP socket's Receive from throwing 10054 (WSAECONNRESET) after a prior send
        // drew an ICMP "port unreachable" (e.g. the sender started before the receiver bound the port).
        private const int SIO_UDP_CONNRESET = -1744830452; // 0x9800000C

        private void LogLine(string line) => diagnosticsLog.Log(line);

        private void Report(DiagnosticsSnapshot snapshot)
        {
            LogLine(snapshot.ToLogLine());
            Diagnostics?.Invoke(snapshot);
        }

        public AudioStreamerLogic()
        {
            LoadConfig();
        }

        public void Start()
        {
            if (IsRunning)
                return;

            try
            {
                if (CurrentConfig.Mode == ModeType.Sender)
                {
                    StartSender();
                }
                else
                {
                    StartReceiver();
                }
                IsRunning = true;
                LogLine($"=== session started: {CurrentConfig.Mode} on port {CurrentConfig.Port} ===");
            }
            catch
            {
                Stop();   // tear down any partially-created session
                throw;    // let the UI surface the failure
            }
        }

        public void Stop()
        {
            bool wasRunning = IsRunning;
            cts?.Cancel();

            waveSource?.StopRecording();
            waveSource?.Dispose();
            waveSource = null;

            wasapiOut?.Stop();
            wasapiOut?.Dispose();
            wasapiOut = null;

            udpClient?.Close();
            udpClient = null;

            cts?.Dispose();
            cts = null;

            IsRunning = false;

            if (wasRunning)
                LogLine("=== session stopped ===");
        }

        private void LoadConfig()
        {
            try
            {
                string configText = File.ReadAllText("config.json");
                if (!string.IsNullOrEmpty(configText))
                {
                    CurrentConfig = JsonSerializer.Deserialize<Config>(configText) ?? new Config();
                }
            }
            catch
            {
                CurrentConfig = new Config();
                SaveConfig();
            }
        }

        public void SaveConfig()
        {
            File.WriteAllText("config.json", JsonSerializer.Serialize(CurrentConfig, JsonOptions));
        }

        private void StartSender()
        {
            // Capture local non-null references so the closure and teardown don't depend on the nullable fields.
            var client = new UdpClient();
            client.Client.SendBufferSize = SocketBufferBytes;
            // On a Connect()ed UDP socket an ICMP "port unreachable" (receiver not up yet) otherwise surfaces as a
            // 10054 thrown from Send on the capture thread; suppress it the same way the receiver does.
            client.Client.IOControl(SIO_UDP_CONNRESET, new byte[4], null);
            client.Connect(IPAddress.Parse(CurrentConfig.HostName), CurrentConfig.Port);   // fixed remote; Send() needs no endpoint
            udpClient = client;

            var capture = new TweakedWasapiLoopbackCapture(CurrentConfig.SenderAudioBufferMillisecondsLength)
            {
                WaveFormat = new WaveFormat(CurrentConfig.SampleRate, CurrentConfig.BitsPerSample, CurrentConfig.Channels)
            };
            waveSource = capture;

            // Log what the device actually captures vs. what we put on the wire. The WASAPI shared-mode engine
            // converts the device's native mix format (often 32-bit float) to the requested format for us; if a
            // device ever rejects the requested format, StartRecording throws and this line shows what was asked.
            LogCaptureFormat(capture.WaveFormat);

            // Slice each captured buffer into MTU-sized, whole-frame datagrams. Frame alignment matters: if a
            // datagram is lost, dropping a whole number of frames leaves the stream aligned, whereas a partial
            // frame would shift every subsequent sample (channel swap / noise) until the next resync.
            int blockAlign = capture.WaveFormat.BlockAlign;
            // Largest whole-frame slice that fits the MTU budget. Guard: if one frame ever exceeded MaxUdpAudioBytes
            // (hundreds of channels — unreachable for normal PCM), this falls back to one frame per datagram, which
            // re-allows IP fragmentation but is the only option since a frame can't be split.
            int maxChunk = Math.Max(blockAlign, (MaxUdpAudioBytes / blockAlign) * blockAlign);

            // Reused across callbacks so the capture thread allocates nothing (a GC pause here == an audio
            // dropout). The 3-byte format header is constant for the session, so write it once up front; the
            // sequence byte at index 3 is overwritten per datagram.
            byte[] sendBuffer = new byte[HeaderBytes + maxChunk];
            Buffer.BlockCopy(PackWaveFormat(CurrentConfig.SampleRate, CurrentConfig.BitsPerSample, CurrentConfig.Channels),
                             0, sendBuffer, 0, 3);

            var sendLogTimer = System.Diagnostics.Stopwatch.StartNew();
            int sentPackets = 0;
            long sentBytes = 0;
            byte sequence = 0;
            capture.DataAvailable += (sender, e) =>
            {
                try
                {
                    for (int offset = 0; offset < e.BytesRecorded; offset += maxChunk)
                    {
                        int chunk = Math.Min(maxChunk, e.BytesRecorded - offset);
                        sendBuffer[3] = sequence++;   // wraps at 256; receiver counts gaps as lost/reordered
                        Buffer.BlockCopy(e.Buffer, offset, sendBuffer, HeaderBytes, chunk);
                        client.Send(sendBuffer, chunk + HeaderBytes);

                        sentPackets++;
                        sentBytes += chunk + HeaderBytes;
                    }

                    if (sendLogTimer.ElapsedMilliseconds >= 1000)
                    {
                        Report(new DiagnosticsSnapshot(false, 0, sentPackets, sentBytes / 1024, 0, 0, 0));
                        sentPackets = 0; sentBytes = 0; sendLogTimer.Restart();
                    }
                }
                catch (Exception ex)
                {
                    LogLine("Error sending audio: " + ex.Message);
                }
            };

            // NAudio raises DataAvailable on its own capture thread, so this returns immediately.
            // Teardown happens in Stop().
            capture.StartRecording();
            LogLine("Streaming system audio to " + CurrentConfig.HostName + "...");
        }

        private void StartReceiver()
        {
            // Capture local non-null references so the closures and teardown don't depend on the nullable fields.
            var client = new UdpClient(CurrentConfig.Port);
            client.Client.ReceiveBufferSize = SocketBufferBytes;
            client.Client.IOControl(SIO_UDP_CONNRESET, new byte[4], null);   // false => don't surface 10054 on Receive
            Socket socket = client.Client;
            udpClient = client;
            var output = new WasapiOut(AudioClientShareMode.Shared, CurrentConfig.ReceiverAudioLatencyMilliseconds);
            wasapiOut = output;
            // Built for real once the first packet reveals the sender's format (see InitializeReceiver).
            BufferedWaveProvider bufferedWaveProvider = null!;

            var tokenSource = new CancellationTokenSource();
            cts = tokenSource;
            var token = tokenSource.Token;

            // Diagnostics + latency-cap state, shared with the receive loop.
            int maxLatencyMs = CurrentConfig.ReceiverMaxLatencyMilliseconds;
            var logTimer = System.Diagnostics.Stopwatch.StartNew();
            int packets = 0;
            long payloadBytes = 0;
            int overflows = 0;
            int resyncs = 0;
            int lost = 0;
            int expectedSeq = -1;   // -1 until the first datagram; persists across diagnostics intervals

            void ReceiveAudio()
            {
                // Each receive loop owns its buffer + remote endpoint so the init and streaming loops never share
                // mutable state. Sized to the UDP maximum so an oversized datagram can't overflow it (ReceiveFrom
                // throws on truncation otherwise); reused per datagram to keep the thread allocation-free.
                byte[] receiveBuffer = new byte[65536];
                byte[] dropScratch = new byte[16384];   // reused to discard backlog when trimming latency
                EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        int received = socket.ReceiveFrom(receiveBuffer, ref remoteEP);
                        if (received > HeaderBytes)
                        {
                            int payload = received - HeaderBytes;
                            packets++;
                            payloadBytes += payload;

                            // Sequence byte (index 3): count datagrams missing since the previous one. A backward
                            // jump (reorder or duplicate) isn't counted as loss.
                            int seq = receiveBuffer[3];
                            if (expectedSeq >= 0)
                            {
                                int gap = (seq - expectedSeq) & 0xFF;
                                if (gap > 0 && gap < 128)
                                    lost += gap;
                            }
                            expectedSeq = (seq + 1) & 0xFF;

                            // DiscardOnBufferOverflow drops silently; count it so the log surfaces real loss.
                            if (bufferedWaveProvider.BufferedBytes + payload > bufferedWaveProvider.BufferLength)
                                overflows++;

                            bufferedWaveProvider.AddSamples(receiveBuffer, HeaderBytes, payload);

                            // Cap latency: the backlog == current audio-behind-video delay. If clock drift lets it
                            // grow past the target, drop just the excess down to a low-water mark (half the cap)
                            // rather than emptying the buffer — a full ClearBuffer would click and briefly go silent.
                            if (maxLatencyMs > 0 && bufferedWaveProvider.BufferedDuration.TotalMilliseconds > maxLatencyMs)
                            {
                                int targetBytes = bufferedWaveProvider.WaveFormat.AverageBytesPerSecond * (maxLatencyMs / 2) / 1000;
                                targetBytes -= targetBytes % bufferedWaveProvider.WaveFormat.BlockAlign;   // frame-aligned low-water mark
                                int dropBytes = bufferedWaveProvider.BufferedBytes - targetBytes;
                                while (dropBytes > 0)
                                {
                                    int n = bufferedWaveProvider.Read(dropScratch, 0, Math.Min(dropBytes, dropScratch.Length));
                                    if (n == 0) break;
                                    dropBytes -= n;
                                }
                                resyncs++;
                            }
                        }

                        // Periodic diagnostics: backlog is the key drift indicator (steady climb == clock drift).
                        if (logTimer.ElapsedMilliseconds >= 1000)
                        {
                            Report(new DiagnosticsSnapshot(true,
                                bufferedWaveProvider.BufferedDuration.TotalMilliseconds,
                                packets, payloadBytes / 1024, overflows, resyncs, lost));
                            packets = 0; payloadBytes = 0; overflows = 0; resyncs = 0; lost = 0;
                            logTimer.Restart();
                        }
                    }
                    catch (Exception ex)
                    {
                        // Stop() cancels the token then closes the socket, unblocking ReceiveFrom with an
                        // ObjectDisposedException/SocketException — expected teardown, so exit quietly.
                        if (token.IsCancellationRequested)
                            break;
                        LogLine("Error receiving audio: " + ex.Message);
                    }
                }
            }

            void InitializeReceiver()
            {
                // Own buffer + endpoint (see ReceiveAudio); this loop only needs to read the first header packet.
                byte[] receiveBuffer = new byte[65536];
                EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                LogLine("Waiting for audio connection from sender");
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        int received = socket.ReceiveFrom(receiveBuffer, ref remoteEP);
                        if (received >= 3)
                        {
                            byte[] header = new byte[3];
                            Buffer.BlockCopy(receiveBuffer, 0, header, 0, 3);
                            (int sampleRate, int bitDepth, int channels) = UnpackWaveFormat(header);
                            LogLine($"Sample rate: {sampleRate}, Bit depth: {bitDepth}, Channels: {channels} received from sender");

                            bufferedWaveProvider = new BufferedWaveProvider(new WaveFormat(sampleRate, bitDepth, channels))
                            {
                                BufferDuration = TimeSpan.FromMilliseconds(CurrentConfig.ReceiverAudioBufferMillisecondsLength),
                                DiscardOnBufferOverflow = true
                            };
                            output.Init(bufferedWaveProvider);
                            output.Play();
                            Task.Run(ReceiveAudio, token);
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (token.IsCancellationRequested)
                            break;
                        LogLine("Error receiving connection: " + ex.Message);
                    }
                }
            }

            // Runs on a background thread; this returns immediately and teardown happens in Stop().
            Task.Run(InitializeReceiver, token);
            LogLine("Receiving audio...");
        }

        private void LogCaptureFormat(WaveFormat wireFormat)
        {
            try
            {
                using var devices = new MMDeviceEnumerator();
                var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var mix = device.AudioClient.MixFormat;
                LogLine($"Capture '{device.FriendlyName}': device native {mix.Encoding} {mix.SampleRate}Hz {mix.BitsPerSample}bit {mix.Channels}ch; "
                      + $"sending {wireFormat.Encoding} {wireFormat.SampleRate}Hz {wireFormat.BitsPerSample}bit {wireFormat.Channels}ch");
            }
            catch (Exception ex)
            {
                LogLine("Could not read capture device format: " + ex.Message);
            }
        }

        private byte[] PackWaveFormat(int sampleRate, int bitDepth, int channels)
        {
            byte packedSampleRate = (byte)(sampleRate / 1000);
            byte packedBitDepth = (byte)bitDepth;
            byte packedChannels = (byte)channels;
            return new byte[] { packedSampleRate, packedBitDepth, packedChannels };
        }

        private (int sampleRate, int bitDepth, int channels) UnpackWaveFormat(byte[] packedData)
        {
            int sampleRate = packedData[0] * 1000;
            int bitDepth = packedData[1];
            int channels = packedData[2];
            return (sampleRate, bitDepth, channels);
        }

        public class TweakedWasapiLoopbackCapture : WasapiCapture
        {
            public TweakedWasapiLoopbackCapture(int audioBufferMillisecondsLength) :
                this(GetDefaultLoopbackCaptureDevice(), audioBufferMillisecondsLength)
            {
            }

            public TweakedWasapiLoopbackCapture(MMDevice captureDevice, int audioBufferMillisecondsLength) :
                base(captureDevice, true, audioBufferMillisecondsLength)
            {
            }

            public static MMDevice GetDefaultLoopbackCaptureDevice()
            {
                MMDeviceEnumerator devices = new MMDeviceEnumerator();
                return devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }

            protected override AudioClientStreamFlags GetAudioClientStreamFlags()
            {
                // AutoConvertPcm + SrcDefaultQuality let the shared-mode engine resample/convert the device's
                // native mix format to whatever PCM format we request (rate, bit depth, channels). Modern Windows
                // does this even without the flags, but they make it explicit and cover older/edge devices.
                return AudioClientStreamFlags.Loopback
                     | AudioClientStreamFlags.AutoConvertPcm
                     | AudioClientStreamFlags.SrcDefaultQuality
                     | base.GetAudioClientStreamFlags();
            }
        }
    }
}
