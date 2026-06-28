using System.Net;
using System.Net.Sockets;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioStreamer
{
    /// <summary>
    /// Sender lifecycle: opens the UDP socket to HostName:Port and streams WASAPI loopback capture, slicing each
    /// buffer into MTU-sized whole-frame datagrams. Self-heals when Windows invalidates the loopback stream
    /// (lock screen / HDMI-monitor power-save / default-device change) by rebuilding the capture on a 1s poll.
    /// </summary>
    public sealed class SenderSession : IStreamSession
    {
        private readonly AudioStreamerLogic.Config config;
        private readonly Action<string> logLine;
        private readonly Action<DiagnosticsSnapshot> report;

        // Serializes the sender capture lifecycle (build / start / stop / auto-rebuild) so the once-per-second
        // restart loop and a user Stop() can't race on waveSource or leave a live capture running after Stop.
        private readonly object senderLock = new();

        // volatile so the restart loop on a thread-pool thread reliably observes a Stop() from the UI thread.
        private volatile bool isRunning;
        private UdpClient? udpClient;
        private WasapiCapture? waveSource;

        public SenderSession(AudioStreamerLogic.Config config, Action<string> logLine, Action<DiagnosticsSnapshot> report)
        {
            this.config = config;
            this.logLine = logLine;
            this.report = report;
        }

        public bool IsRunning => isRunning;

        public void Start()
        {
            // Capture a local non-null reference so the closure and teardown don't depend on the nullable field.
            var client = new UdpClient();
            client.Client.SendBufferSize = WireProtocol.SocketBufferBytes;
            // On a Connect()ed UDP socket an ICMP "port unreachable" (receiver not up yet) otherwise surfaces as a
            // 10054 thrown from Send on the capture thread; suppress it the same way the receiver does.
            client.Client.IOControl(WireProtocol.SIO_UDP_CONNRESET, new byte[4], null);
            client.Connect(IPAddress.Parse(config.HostName), config.Port);   // fixed remote; Send() needs no endpoint
            udpClient = client;

            StartCapture();
            isRunning = true;
        }

        public void Stop()
        {
            isRunning = false;
            StopCapture();
            udpClient?.Close();
            udpClient = null;
        }

        // Builds the WASAPI loopback capture, wires it to the already-open UDP socket, and starts it. Factored
        // out of Start so it can be re-invoked to transparently rebuild the stream: locking the workstation
        // or a default-device change invalidates the loopback capture, NAudio ends its capture thread and raises
        // RecordingStopped, and nothing is captured afterwards until the stream is recreated (which previously
        // meant a manual Stop/Start from the UI). The new capture re-acquires the current default device.
        private void StartCapture()
        {
            // The whole build+publish runs under senderLock so a concurrent Stop()/rebuild can't interleave with
            // the StartRecording()→waveSource hand-off. Reentrant when called from RestartCapture (which
            // already holds the lock); StartRecording only spins up NAudio's capture thread and returns, so the
            // lock is held briefly.
            TweakedWasapiLoopbackCapture capture;
            lock (senderLock)
            {
                capture = new TweakedWasapiLoopbackCapture(config.SenderAudioBufferMillisecondsLength)
                {
                    WaveFormat = AudioFormats.ToWaveFormat(config.SampleRate, config.BitsPerSample, config.Channels)
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
                // dropout). The 1-byte format code is constant for the session, so write it once up front; the
                // sequence byte at index 1 is overwritten per datagram.
                byte[] sendBuffer = new byte[WireProtocol.HeaderBytes + maxChunk];
                WireProtocol.WriteFormatCode(sendBuffer, AudioFormats.ToCode(config.SampleRate, config.BitsPerSample, config.Channels));

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
                            report(DiagnosticsSnapshot.ForSender(sentPackets, sentBytes / 1024));
                            sentPackets = 0; sentBytes = 0; sendLogTimer.Restart();
                        }
                    }
                    catch (Exception ex)
                    {
                        logLine("Error sending audio: " + ex.Message);
                    }
                };
                // Watch for the OS tearing the stream down (lock screen / device change) so we can rebuild it.
                capture.RecordingStopped += OnRecordingStopped;

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
                    capture.RecordingStopped -= OnRecordingStopped;
                    capture.Dispose();
                    throw;
                }

                // Publish only after StartRecording succeeds, so a throw above can never leave a dead capture in
                // waveSource. Teardown happens in Stop() / StopCapture().
                waveSource = capture;
            }

            // Outside the lock: LogCaptureFormat does COM endpoint enumeration that can briefly block during a
            // device-change storm; keeping it off senderLock means it can't stall a UI-thread Stop(). The capture
            // is already published and started, so logging its format here is safe. (Device native mix format is
            // usually 32-bit float; the WASAPI shared-mode engine converts it to the requested format for us.)
            LogCaptureFormat(capture.WaveFormat);
            logLine("Streaming system audio to " + config.HostName + "...");
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            logLine(e.Exception is null
                ? "Sender capture stopped unexpectedly."
                : "Sender capture stopped: " + e.Exception.Message);

            // An intentional teardown unsubscribes this handler first (see StopCapture), so reaching here
            // means Windows ended the capture out from under us (lock screen / default-device change).
            bool restart;
            lock (senderLock)
            {
                if (ReferenceEquals(waveSource, sender))
                    waveSource = null;
                restart = isRunning;
            }

            // Dispose the dead capture off this callback thread. Rebuilt captures are constructed without a
            // SynchronizationContext, so this handler runs on NAudio's capture thread; disposing the COM
            // AudioClient from inside its own stopped-event callback is best avoided.
            var dead = sender as IDisposable;
            if (restart)
                RestartCapture(dead);   // disposes dead, then rebuilds
            else if (dead != null)
                Task.Run(() => dead.Dispose());
        }

        // Rebuild the capture after the OS tore it down, retrying until it succeeds or the session stops. The
        // default render endpoint can be gone for a long, unbounded time — e.g. an HDMI-monitor audio output that
        // disappears whenever the monitor enters power-saving — so we can't cap the attempts; we poll until a
        // usable output device is back (typically when the monitor wakes), then resume. The first failure is
        // logged and the rest suppressed so a long sleep doesn't flood the log. Runs off the capture thread; the
        // isRunning + StartCapture pair runs under senderLock so a concurrent Stop() either tears the
        // rebuilt capture down or makes this bail — never leaving a live capture after Stop.
        private void RestartCapture(IDisposable? deadCapture)
        {
            Task.Run(async () =>
            {
                deadCapture?.Dispose();   // off the RecordingStopped callback thread
                bool loggedWaiting = false;
                while (isRunning)
                {
                    await Task.Delay(1000);
                    try
                    {
                        lock (senderLock)
                        {
                            if (!isRunning)
                                return;
                            StartCapture();   // reentrant lock; publishes waveSource only on success
                        }
                        logLine("Sender capture restarted.");
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (!loggedWaiting)
                        {
                            logLine($"Sender capture rebuild failed ({ex.Message}); waiting for an audio output device to become available...");
                            loggedWaiting = true;
                        }
                    }
                }
            });
        }

        // Stop and dispose the capture under senderLock. Unsubscribes RecordingStopped first so this teardown isn't
        // mistaken for an unexpected stop (which would trigger an auto-restart). Safe to call when no capture is
        // running (receiver), and idempotent (NAudio's StopRecording/Dispose tolerate being called twice).
        private void StopCapture()
        {
            lock (senderLock)
            {
                var capture = waveSource;
                waveSource = null;
                if (capture == null)
                    return;
                capture.RecordingStopped -= OnRecordingStopped;
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
                logLine($"Capture '{device.FriendlyName}': device native {mix.Encoding} {mix.SampleRate}Hz {mix.BitsPerSample}bit {mix.Channels}ch; "
                      + $"sending {wireFormat.Encoding} {wireFormat.SampleRate}Hz {wireFormat.BitsPerSample}bit {wireFormat.Channels}ch");
            }
            catch (Exception ex)
            {
                logLine("Could not read capture device format: " + ex.Message);
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
