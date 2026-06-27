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
        private CancellationTokenSource? cts;
        private WasapiCapture? waveSource;
        private WasapiOut? wasapiOut;

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

        // --- UDP streaming tunables ---
        // Reorder window: how many out-of-order datagrams the receiver holds waiting on a missing one before
        // giving up. Sender bursts — and thus reorder depth measured in packets — grow with the data rate, so
        // the window is scaled by the format's byte rate (AverageBytesPerSecond = sampleRate × channels ×
        // bytes/sample, covering all three) to keep the give-up *time* roughly constant (~40 ms) across formats;
        // a fixed count is too shallow for 32-bit / multichannel / hi-res. Base is the 16-bit/48 kHz/stereo
        // case; capped so a genuinely lost packet can't stall playback too long. (See ComputeReorderWindow.)
        private const int ReorderBaseWindowPackets = 8;
        private const int ReorderMaxWindowPackets = 64;
        private const int ReorderBaselineBytesPerSecond = 48000 * 2 * 2;   // 192000 B/s (16-bit stereo @ 48 kHz)

        /// <summary>Reorder-buffer depth scaled to the stream's byte rate (see the constants above).</summary>
        public static int ComputeReorderWindow(WaveFormat format) =>
            Math.Clamp(ReorderBaseWindowPackets * format.AverageBytesPerSecond / ReorderBaselineBytesPerSecond,
                       ReorderBaseWindowPackets, ReorderMaxWindowPackets);

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
            // Clear first so an in-flight capture rebuild bails and a teardown-driven RecordingStopped isn't
            // mistaken for an unexpected stop (see OnSenderRecordingStopped / RestartSenderCapture). The rebuild
            // loop's authoritative IsRunning check happens under senderLock, which StopSenderCapture also takes,
            // so a rebuild either finishes before this teardown (which then disposes it) or observes the cleared
            // flag and bails — never leaving a live capture running after Stop.
            isRunning = false;
            cts?.Cancel();

            StopSenderCapture();

            wasapiOut?.Stop();
            wasapiOut?.Dispose();
            wasapiOut = null;

            udpClient?.Close();
            udpClient = null;

            cts?.Dispose();
            cts = null;

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

        private void StartReceiver()
        {
            // Capture local non-null references so the closures and teardown don't depend on the nullable fields.
            var client = new UdpClient(CurrentConfig.Port);
            client.Client.ReceiveBufferSize = WireProtocol.SocketBufferBytes;
            client.Client.IOControl(WireProtocol.SIO_UDP_CONNRESET, new byte[4], null);   // false => don't surface 10054 on Receive
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
            var sequenceTracker = new SequenceLossTracker();   // separates true loss from reordering (persists across intervals)
            double minBacklogMs = double.MaxValue;   // lowest backlog seen this interval — drains toward 0 before an underrun
            UnderrunCountingWaveProvider? underrunMeter = null;   // wraps bufferedWaveProvider once the format is known

            void ReceiveAudio()
            {
                // Each receive loop owns its buffer + remote endpoint so the init and streaming loops never share
                // mutable state. Sized to the UDP maximum so an oversized datagram can't overflow it (ReceiveFrom
                // throws on truncation otherwise); reused per datagram to keep the thread allocation-free.
                byte[] receiveBuffer = new byte[65536];
                byte[] dropScratch = new byte[16384];   // reused to discard backlog when trimming latency
                EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

                // Feeds the audio buffer in sequence order (NICs can deliver the sender's bursts out of order).
                // The overflow check lives here because this is where samples actually reach the buffer.
                int reorderWindow = ComputeReorderWindow(bufferedWaveProvider.WaveFormat);
                LogLine($"Reorder window: {reorderWindow} packets");
                var reorderBuffer = new ReorderBuffer(reorderWindow, (buf, off, cnt) =>
                {
                    if (bufferedWaveProvider.BufferedBytes + cnt > bufferedWaveProvider.BufferLength)
                        overflows++;
                    bufferedWaveProvider.AddSamples(buf, off, cnt);
                });

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        int received = socket.ReceiveFrom(receiveBuffer, ref remoteEP);
                        if (received > WireProtocol.HeaderBytes)
                        {
                            int payload = received - WireProtocol.HeaderBytes;
                            packets++;
                            payloadBytes += payload;

                            // Sampled before AddSamples — the trough between packets, where the render thread has
                            // drained the buffer the most. A min trending toward 0 is the lead-in to an underrun.
                            double backlogNow = bufferedWaveProvider.BufferedDuration.TotalMilliseconds;
                            if (backlogNow < minBacklogMs) minBacklogMs = backlogNow;

                            // Sequence byte (index 3): classified into true loss vs reordering (late arrival).
                            sequenceTracker.OnReceived(receiveBuffer[WireProtocol.SequenceByteOffset]);

                            // Hand to the reorder buffer, which emits to the audio buffer in sequence order
                            // (the emit callback counts overflows and calls AddSamples).
                            reorderBuffer.Add(receiveBuffer[WireProtocol.SequenceByteOffset], receiveBuffer, WireProtocol.HeaderBytes, payload);

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
                            double minBacklog = minBacklogMs == double.MaxValue ? bufferedWaveProvider.BufferedDuration.TotalMilliseconds : minBacklogMs;
                            var (reorders, losses) = sequenceTracker.Exchange();
                            Report(DiagnosticsSnapshot.ForReceiver(
                                bufferedWaveProvider.BufferedDuration.TotalMilliseconds,
                                packets, payloadBytes / 1024, overflows, resyncs, losses, reorders,
                                underrunMeter?.ExchangeUnderruns() ?? 0, minBacklog));
                            packets = 0; payloadBytes = 0; overflows = 0; resyncs = 0;
                            minBacklogMs = double.MaxValue;
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
                        if (received >= WireProtocol.FormatHeaderBytes)
                        {
                            (int sampleRate, int bitDepth, int channels) = WireProtocol.ReadFormatHeader(receiveBuffer);
                            LogLine($"Sample rate: {sampleRate}, Bit depth: {bitDepth}, Channels: {channels} received from sender");

                            bufferedWaveProvider = new BufferedWaveProvider(new WaveFormat(sampleRate, bitDepth, channels))
                            {
                                BufferDuration = TimeSpan.FromMilliseconds(CurrentConfig.ReceiverAudioBufferMillisecondsLength),
                                DiscardOnBufferOverflow = true
                            };
                            // Play through the underrun meter so the render thread's starved reads are counted.
                            underrunMeter = new UnderrunCountingWaveProvider(bufferedWaveProvider);
                            output.Init(underrunMeter);
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
