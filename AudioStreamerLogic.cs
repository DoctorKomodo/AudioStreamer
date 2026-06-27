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
            public int ReceiverMaxLatencyMilliseconds { get; set; } = 150;   // drift cap; trims backlog to half this. 150 keeps lip-sync tight while leaving jitter headroom (400 was too laggy in the field)
            public int SampleRate { get; set; } = 48000;
            public int BitsPerSample { get; set; } = 16;
            public int Channels { get; set; } = 2;
            public bool StartMinimized { get; set; } = false;   // first run shows the window so it can be configured before starting
        }

        public Config CurrentConfig { get; set; } = new();
        private UdpClient? udpClient;
        private WasapiCapture? waveSource;
        private ReceiverSession? receiverSession;

        // Serializes the sender capture lifecycle (build / start / stop / auto-rebuild) so the once-per-second
        // restart loop and a user Stop() can't race on waveSource or leave a live capture running after Stop.
        private readonly object senderLock = new();

        // volatile so the restart loop on a thread-pool thread reliably observes a Stop() from the UI thread.
        private volatile bool isRunning;
        public bool IsRunning => isRunning;

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
                    var receiver = new ReceiverSession(CurrentConfig, LogLine, Report);
                    receiver.Start();
                    receiverSession = receiver;
                }
                isRunning = true;
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
            isRunning = false;
            StopSenderCapture();
            udpClient?.Close();
            udpClient = null;
            receiverSession?.Stop();
            receiverSession = null;
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
            // Capture a local non-null reference so the closure and teardown don't depend on the nullable field.
            var client = new UdpClient();
            client.Client.SendBufferSize = WireProtocol.SocketBufferBytes;
            // On a Connect()ed UDP socket an ICMP "port unreachable" (receiver not up yet) otherwise surfaces as a
            // 10054 thrown from Send on the capture thread; suppress it the same way the receiver does.
            client.Client.IOControl(WireProtocol.SIO_UDP_CONNRESET, new byte[4], null);
            client.Connect(IPAddress.Parse(CurrentConfig.HostName), CurrentConfig.Port);   // fixed remote; Send() needs no endpoint
            udpClient = client;

            StartSenderCapture();
        }

        // Builds the WASAPI loopback capture, wires it to the already-open UDP socket, and starts it. Factored
        // out of StartSender so it can be re-invoked to transparently rebuild the stream: locking the workstation
        // or a default-device change invalidates the loopback capture, NAudio ends its capture thread and raises
        // RecordingStopped, and nothing is captured afterwards until the stream is recreated (which previously
        // meant a manual Stop/Start from the UI). The new capture re-acquires the current default device.
        private void StartSenderCapture()
        {
            // The whole build+publish runs under senderLock so a concurrent Stop()/rebuild can't interleave with
            // the StartRecording()→waveSource hand-off. Reentrant when called from RestartSenderCapture (which
            // already holds the lock); StartRecording only spins up NAudio's capture thread and returns, so the
            // lock is held briefly.
            TweakedWasapiLoopbackCapture capture;
            lock (senderLock)
            {
                capture = new TweakedWasapiLoopbackCapture(CurrentConfig.SenderAudioBufferMillisecondsLength)
                {
                    WaveFormat = new WaveFormat(CurrentConfig.SampleRate, CurrentConfig.BitsPerSample, CurrentConfig.Channels)
                };

                // Slice each captured buffer into MTU-sized, whole-frame datagrams. Frame alignment matters: if a
                // datagram is lost, dropping a whole number of frames leaves the stream aligned, whereas a partial
                // frame would shift every subsequent sample (channel swap / noise) until the next resync.
                int blockAlign = capture.WaveFormat.BlockAlign;
                // Largest whole-frame slice that fits the MTU budget. Guard: if one frame ever exceeded MaxUdpAudioBytes
                // (hundreds of channels — unreachable for normal PCM), this falls back to one frame per datagram, which
                // re-allows IP fragmentation but is the only option since a frame can't be split.
                int maxChunk = Math.Max(blockAlign, (WireProtocol.MaxUdpAudioBytes / blockAlign) * blockAlign);

                // Reused across callbacks so the capture thread allocates nothing (a GC pause here == an audio
                // dropout). The 3-byte format header is constant for the session, so write it once up front; the
                // sequence byte at index 3 is overwritten per datagram.
                byte[] sendBuffer = new byte[WireProtocol.HeaderBytes + maxChunk];
                WireProtocol.WriteFormatHeader(sendBuffer, CurrentConfig.SampleRate, CurrentConfig.BitsPerSample, CurrentConfig.Channels);

                var sendLogTimer = System.Diagnostics.Stopwatch.StartNew();
                int sentPackets = 0;
                long sentBytes = 0;
                // Per-rebuild fresh state: the wrapping sequence restarts at 0 after a recovery, so the receiver
                // sees one discontinuity at resume — a one-interval blip in its lost/reorder meter, not an audio
                // bug (the outage was already a real gap). Acceptable; not worth persisting across rebuilds.
                byte sequence = 0;
                capture.DataAvailable += (sender, e) =>
                {
                    var socket = udpClient;   // read the field each callback so a Stop()/restart can't send on a closed socket
                    if (socket == null)
                        return;
                    try
                    {
                        for (int offset = 0; offset < e.BytesRecorded; offset += maxChunk)
                        {
                            int chunk = Math.Min(maxChunk, e.BytesRecorded - offset);
                            sendBuffer[WireProtocol.SequenceByteOffset] = sequence++;   // wraps at 256; receiver counts gaps as lost/reordered
                            Buffer.BlockCopy(e.Buffer, offset, sendBuffer, WireProtocol.HeaderBytes, chunk);
                            socket.Send(sendBuffer, chunk + WireProtocol.HeaderBytes);

                            sentPackets++;
                            sentBytes += chunk + WireProtocol.HeaderBytes;
                        }

                        if (sendLogTimer.ElapsedMilliseconds >= 1000)
                        {
                            Report(DiagnosticsSnapshot.ForSender(sentPackets, sentBytes / 1024));
                            sentPackets = 0; sentBytes = 0; sendLogTimer.Restart();
                        }
                    }
                    catch (Exception ex)
                    {
                        LogLine("Error sending audio: " + ex.Message);
                    }
                };
                // Watch for the OS tearing the stream down (lock screen / device change) so we can rebuild it.
                capture.RecordingStopped += OnSenderRecordingStopped;

                try
                {
                    // NAudio raises DataAvailable on its own capture thread, so this returns immediately.
                    capture.StartRecording();
                }
                catch
                {
                    // Nothing started — unwire and dispose so a failed (re)build leaks no unmanaged AudioClient
                    // and leaves waveSource untouched (still null / the previously live capture). The once-per-
                    // second retry would otherwise strand a dead capture every second the device is absent.
                    capture.RecordingStopped -= OnSenderRecordingStopped;
                    capture.Dispose();
                    throw;
                }

                // Publish only after StartRecording succeeds, so a throw above can never leave a dead capture in
                // waveSource. Teardown happens in Stop() / StopSenderCapture().
                waveSource = capture;
            }

            // Outside the lock: LogCaptureFormat does COM endpoint enumeration that can briefly block during a
            // device-change storm; keeping it off senderLock means it can't stall a UI-thread Stop(). The capture
            // is already published and started, so logging its format here is safe. (Device native mix format is
            // usually 32-bit float; the WASAPI shared-mode engine converts it to the requested format for us.)
            LogCaptureFormat(capture.WaveFormat);
            LogLine("Streaming system audio to " + CurrentConfig.HostName + "...");
        }

        private void OnSenderRecordingStopped(object? sender, StoppedEventArgs e)
        {
            LogLine(e.Exception is null
                ? "Sender capture stopped unexpectedly."
                : "Sender capture stopped: " + e.Exception.Message);

            // An intentional teardown unsubscribes this handler first (see StopSenderCapture), so reaching here
            // means Windows ended the capture out from under us (lock screen / default-device change).
            bool restart;
            lock (senderLock)
            {
                if (ReferenceEquals(waveSource, sender))
                    waveSource = null;
                restart = IsRunning;
            }

            // Dispose the dead capture off this callback thread. Rebuilt captures are constructed without a
            // SynchronizationContext, so this handler runs on NAudio's capture thread; disposing the COM
            // AudioClient from inside its own stopped-event callback is best avoided.
            var dead = sender as IDisposable;
            if (restart)
                RestartSenderCapture(dead);   // disposes dead, then rebuilds
            else if (dead != null)
                Task.Run(() => dead.Dispose());
        }

        // Rebuild the capture after the OS tore it down, retrying until it succeeds or the session stops. The
        // default render endpoint can be gone for a long, unbounded time — e.g. an HDMI-monitor audio output that
        // disappears whenever the monitor enters power-saving — so we can't cap the attempts; we poll until a
        // usable output device is back (typically when the monitor wakes), then resume. The first failure is
        // logged and the rest suppressed so a long sleep doesn't flood the log. Runs off the capture thread; the
        // IsRunning + StartSenderCapture pair runs under senderLock so a concurrent Stop() either tears the
        // rebuilt capture down or makes this bail — never leaving a live capture after Stop.
        private void RestartSenderCapture(IDisposable? deadCapture)
        {
            Task.Run(async () =>
            {
                deadCapture?.Dispose();   // off the RecordingStopped callback thread
                bool loggedWaiting = false;
                while (IsRunning)
                {
                    await Task.Delay(1000);
                    try
                    {
                        lock (senderLock)
                        {
                            if (!IsRunning)
                                return;
                            StartSenderCapture();   // reentrant lock; publishes waveSource only on success
                        }
                        LogLine("Sender capture restarted.");
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (!loggedWaiting)
                        {
                            LogLine($"Sender capture rebuild failed ({ex.Message}); waiting for an audio output device to become available...");
                            loggedWaiting = true;
                        }
                    }
                }
            });
        }

        // Stop and dispose the capture under senderLock. Unsubscribes RecordingStopped first so this teardown isn't
        // mistaken for an unexpected stop (which would trigger an auto-restart). Safe to call when no capture is
        // running (receiver), and idempotent (NAudio's StopRecording/Dispose tolerate being called twice).
        private void StopSenderCapture()
        {
            lock (senderLock)
            {
                var capture = waveSource;
                waveSource = null;
                if (capture == null)
                    return;
                capture.RecordingStopped -= OnSenderRecordingStopped;
                try { capture.StopRecording(); } catch { /* already stopped by the OS */ }
                capture.Dispose();
            }
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
