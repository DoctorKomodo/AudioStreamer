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
        // Keep each datagram inside a standard 1500-byte Ethernet MTU (minus 20-byte IP + 8-byte UDP + our
        // 3-byte format header, with headroom for VPN/PPPoE) so IP never fragments the audio: one lost fragment
        // would otherwise drop the whole datagram. Audio is sliced on frame boundaries (see StartSender).
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
            client.Connect(IPAddress.Parse(CurrentConfig.HostName), CurrentConfig.Port);   // fixed remote; Send() needs no endpoint
            udpClient = client;

            var capture = new TweakedWasapiLoopbackCapture(CurrentConfig.SenderAudioBufferMillisecondsLength)
            {
                WaveFormat = new WaveFormat(CurrentConfig.SampleRate, CurrentConfig.BitsPerSample, CurrentConfig.Channels)
            };
            waveSource = capture;

            // Slice each captured buffer into MTU-sized, whole-frame datagrams. Frame alignment matters: if a
            // datagram is lost, dropping a whole number of frames leaves the stream aligned, whereas a partial
            // frame would shift every subsequent sample (channel swap / noise) until the next resync.
            int blockAlign = capture.WaveFormat.BlockAlign;
            int maxChunk = Math.Max(blockAlign, (MaxUdpAudioBytes / blockAlign) * blockAlign);

            // Reused across callbacks so the capture thread allocates nothing (a GC pause here == an audio
            // dropout). The 3-byte format header is constant for the session, so write it once up front.
            byte[] sendBuffer = new byte[3 + maxChunk];
            Buffer.BlockCopy(PackWaveFormat(CurrentConfig.SampleRate, CurrentConfig.BitsPerSample, CurrentConfig.Channels),
                             0, sendBuffer, 0, 3);

            var sendLogTimer = System.Diagnostics.Stopwatch.StartNew();
            int sentPackets = 0;
            long sentBytes = 0;
            capture.DataAvailable += (sender, e) =>
            {
                try
                {
                    for (int offset = 0; offset < e.BytesRecorded; offset += maxChunk)
                    {
                        int chunk = Math.Min(maxChunk, e.BytesRecorded - offset);
                        Buffer.BlockCopy(e.Buffer, offset, sendBuffer, 3, chunk);
                        client.Send(sendBuffer, chunk + 3);

                        sentPackets++;
                        sentBytes += chunk + 3;
                    }

                    if (sendLogTimer.ElapsedMilliseconds >= 1000)
                    {
                        Report(new DiagnosticsSnapshot(false, 0, sentPackets, sentBytes / 1024, 0, 0));
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
            var bufferedWaveProvider = new BufferedWaveProvider(new WaveFormat(48000, 16, 2))
            {
                BufferDuration = TimeSpan.FromMilliseconds(CurrentConfig.ReceiverAudioBufferMillisecondsLength),
                DiscardOnBufferOverflow = true
            };

            // Reused for every datagram so the receive thread allocates nothing. Sized to the UDP maximum so an
            // unexpectedly large datagram can't overflow it (ReceiveFrom throws on truncation otherwise).
            byte[] receiveBuffer = new byte[65536];
            EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
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

            void ReceiveAudio()
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        int received = socket.ReceiveFrom(receiveBuffer, ref remoteEP);
                        if (received > 3)
                        {
                            int payload = received - 3;
                            packets++;
                            payloadBytes += payload;

                            // DiscardOnBufferOverflow drops silently; count it so the log surfaces real loss.
                            if (bufferedWaveProvider.BufferedBytes + payload > bufferedWaveProvider.BufferLength)
                                overflows++;

                            bufferedWaveProvider.AddSamples(receiveBuffer, 3, payload);

                            // Fix #1 — cap latency. The backlog == current audio-behind-video delay; if clock
                            // drift lets it grow past the target, drop it so lip-sync can't degrade over time.
                            if (maxLatencyMs > 0 && bufferedWaveProvider.BufferedDuration.TotalMilliseconds > maxLatencyMs)
                            {
                                bufferedWaveProvider.ClearBuffer();
                                resyncs++;
                            }
                        }

                        // Periodic diagnostics: backlog is the key drift indicator (steady climb == clock drift).
                        if (logTimer.ElapsedMilliseconds >= 1000)
                        {
                            Report(new DiagnosticsSnapshot(true,
                                bufferedWaveProvider.BufferedDuration.TotalMilliseconds,
                                packets, payloadBytes / 1024, overflows, resyncs));
                            packets = 0; payloadBytes = 0; overflows = 0; resyncs = 0;
                            logTimer.Restart();
                        }
                    }
                    catch (Exception ex)
                    {
                        // Closing the socket in Stop() unblocks Receive() with an exception; the token check exits the loop.
                        LogLine("Error receiving audio: " + ex.Message);
                    }
                }
            }

            void InitializeReceiver()
            {
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
                        LogLine("Error receiving connection: " + ex.Message);
                    }
                }
            }

            // Runs on a background thread; this returns immediately and teardown happens in Stop().
            Task.Run(InitializeReceiver, token);
            LogLine("Receiving audio...");
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
                return AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
            }
        }
    }
}
