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
            var endPoint = new IPEndPoint(IPAddress.Parse(CurrentConfig.HostName), CurrentConfig.Port);
            udpClient = client;

            var capture = new TweakedWasapiLoopbackCapture(CurrentConfig.SenderAudioBufferMillisecondsLength)
            {
                WaveFormat = new WaveFormat(CurrentConfig.SampleRate, CurrentConfig.BitsPerSample, CurrentConfig.Channels)
            };
            waveSource = capture;

            var audioPropertyHeader = PackWaveFormat(CurrentConfig.SampleRate, CurrentConfig.BitsPerSample, CurrentConfig.Channels);
            var sendLogTimer = System.Diagnostics.Stopwatch.StartNew();
            int sentPackets = 0;
            long sentBytes = 0;
            capture.DataAvailable += (sender, e) =>
            {
                try
                {
                    byte[] dataWithHeader = new byte[e.BytesRecorded + 3];
                    Buffer.BlockCopy(audioPropertyHeader, 0, dataWithHeader, 0, 3);
                    Buffer.BlockCopy(e.Buffer, 0, dataWithHeader, 3, e.BytesRecorded);

                    client.Send(dataWithHeader, dataWithHeader.Length, endPoint);

                    sentPackets++;
                    sentBytes += dataWithHeader.Length;
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
            udpClient = client;
            var output = new WasapiOut(AudioClientShareMode.Shared, CurrentConfig.ReceiverAudioLatencyMilliseconds);
            wasapiOut = output;
            var bufferedWaveProvider = new BufferedWaveProvider(new WaveFormat(48000, 16, 2))
            {
                BufferDuration = TimeSpan.FromMilliseconds(CurrentConfig.ReceiverAudioBufferMillisecondsLength),
                DiscardOnBufferOverflow = true
            };

            var remoteEP = new IPEndPoint(IPAddress.Any, CurrentConfig.Port);
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
                        byte[] receivedBytes = client.Receive(ref remoteEP);
                        if (receivedBytes.Length > 3)
                        {
                            int payload = receivedBytes.Length - 3;
                            packets++;
                            payloadBytes += payload;

                            // DiscardOnBufferOverflow drops silently; count it so the log surfaces real loss.
                            if (bufferedWaveProvider.BufferedBytes + payload > bufferedWaveProvider.BufferLength)
                                overflows++;

                            bufferedWaveProvider.AddSamples(receivedBytes, 3, payload);

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
                        byte[] receivedBytes = client.Receive(ref remoteEP);
                        if (receivedBytes.Length >= 3)
                        {
                            byte[] header = new byte[3];
                            Buffer.BlockCopy(receivedBytes, 0, header, 0, 3);
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
