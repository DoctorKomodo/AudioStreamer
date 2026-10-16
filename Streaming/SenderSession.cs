using System.Net;
using System.Net.Sockets;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioStreamer
{
    /// <summary>
    /// Sender lifecycle: opens the UDP socket to HostName:Port and streams WASAPI loopback capture, routing each
    /// captured buffer through the codec encoder (<see cref="IAudioEncoder"/>), which produces the wire datagrams
    /// (MTU-sized slicing for PCM, frame accumulation + encode for Opus). Self-heals when Windows invalidates the
    /// loopback stream (lock screen / HDMI-monitor power-save / default-device change) by rebuilding the capture
    /// on a 1s poll.
    /// </summary>
    public sealed class SenderSession : IStreamSession
    {
        private readonly AudioStreamerLogic.Config config;
        private readonly Action<string> logLine;
        private readonly Action<DiagnosticsSnapshot> report;
        // Reports true while the capture is being (re)acquired and false once it is live, so the UI status
        // line reflects "waiting for audio device" instead of a misleading "Running" during a device outage.
        private readonly Action<bool> reportWaiting;

        // Serializes the sender capture lifecycle (build / start / stop / auto-rebuild) so the once-per-second
        // restart loop and a user Stop() can't race on waveSource or leave a live capture running after Stop.
        private readonly object senderLock = new();

        // volatile so the restart loop on a thread-pool thread reliably observes a Stop() from the UI thread.
        private volatile bool isRunning;
        private UdpClient? udpClient;
        private WasapiCapture? waveSource;
        private IAudioEncoder? encoder;
        // Follows Windows' default render endpoint so a device switch rebuilds the capture (see
        // OnDefaultRenderDeviceChanged). Watches Role.Multimedia, the role the loopback capture binds
        // (TryGetDefaultRenderInfo / TweakedWasapiLoopbackCapture.GetDefaultLoopbackCaptureDevice) — not
        // Role.Console, which is what WasapiOut binds on the receiver side.
        private DefaultRenderDeviceWatcher? deviceWatcher;

        public SenderSession(AudioStreamerLogic.Config config, Action<string> logLine, Action<DiagnosticsSnapshot> report, Action<bool> reportWaiting)
        {
            this.config = config;
            this.logLine = logLine;
            this.report = report;
            this.reportWaiting = reportWaiting;
        }

        public bool IsRunning => isRunning;

        public void Start()
        {
            // Mark running before opening the socket / building the capture so ANY "resource not ready at login"
            // failure falls into the same once-per-second retry that recovers from mid-stream device loss (that
            // loop runs while isRunning). At Windows login the app auto-starts before its dependencies are
            // necessarily up: the network stack (NIC/DHCP) may have no route yet, so Connect() throws, and the
            // default render endpoint may not be ready yet, so the loopback capture throws. Either one used to
            // abort the start — and because the failure path never logged, it did so invisibly, leaving the
            // sender idle with an empty log until a manual Start. Retrying rides out both transients.
            isRunning = true;
            // Subscribe before the first capture is built: the watcher seeds itself from the current default
            // endpoint, which is the one StartCapture will bind to, so no spurious first rebuild.
            deviceWatcher = new DefaultRenderDeviceWatcher(OnDefaultRenderDeviceChanged, logLine, Role.Multimedia);
            deviceWatcher.Start();
            try
            {
                OpenSocket();
                StartCapture();
            }
            catch (Exception ex)
            {
                logLine($"Sender start could not complete yet ({ex.Message}); retrying until the network and audio device are available...");
                RestartCapture(null);
            }
        }

        // Opens the UDP socket to HostName:Port. Idempotent — a no-op once the socket exists — so the retry loop
        // can call it every attempt without leaking sockets. Connect() can throw at login if the local network
        // stack isn't up yet (no route to the LAN); that's a transient the retry loop rides out. (A genuinely bad
        // Host Name is rejected upstream before Start() by the UI's IPAddress.TryParse guard.)
        private void OpenSocket()
        {
            if (udpClient != null)
                return;
            var client = new UdpClient();
            client.Client.SendBufferSize = WireProtocol.SocketBufferBytes;
            // On a Connect()ed UDP socket an ICMP "port unreachable" (receiver not up yet) otherwise surfaces as a
            // 10054 thrown from Send on the capture thread; suppress it the same way the receiver does.
            client.Client.IOControl(WireProtocol.SIO_UDP_CONNRESET, new byte[4], null);
            client.Connect(IPAddress.Parse(config.HostName), config.Port);   // fixed remote; Send() needs no endpoint
            udpClient = client;
        }

        public void Stop()
        {
            isRunning = false;
            // Unregister before tearing the capture down so no device-change rebuild is queued during teardown. A
            // callback already in flight is harmless: its rebuild re-checks isRunning inside senderLock.
            deviceWatcher?.Dispose();
            deviceWatcher = null;
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
            // Acquire the render endpoint once, before senderLock (lock-free on the first-start path; under the
            // caller's lock on the RestartCapture path — accepted). MixFormat.Channels is what Auto matches.
            var renderInfo = TryGetDefaultRenderInfo();
            int nativeChannels = renderInfo?.Mix.Channels ?? AudioFormats.DefaultChannels;

            // Effective capture format. Opus is fixed to 48 kHz/16-bit/stereo; PCM uses the configured/Auto format.
            // Do NOT write these back into config — switching to PCM must restore the user's stored values.
            bool opus = config.Codec == CodecId.Opus;
            int effRate = opus ? 48000 : config.SampleRate;
            int effBits = opus ? 16 : config.BitsPerSample;
            int effChannels = opus ? 2 : AudioFormats.ResolveCaptureChannels(config.Channels, nativeChannels);
            int frameSamples = AudioStreamerLogic.ClampOpusFrameSamples(config.OpusFrameSamples);

            // The whole build+publish runs under senderLock so a concurrent Stop()/rebuild can't interleave with
            // the StartRecording()→waveSource hand-off. Reentrant when called from RestartCapture (which
            // already holds the lock); StartRecording only spins up NAudio's capture thread and returns, so the
            // lock is held briefly.
            TweakedWasapiLoopbackCapture capture;
            lock (senderLock)
            {
                capture = new TweakedWasapiLoopbackCapture(config.SenderAudioBufferMillisecondsLength)
                {
                    WaveFormat = AudioFormats.ToWaveFormat(effRate, effBits, effChannels)
                };

                var format = capture.WaveFormat;
                // `encoder` is a field so StopCapture can dispose it. Dispose any prior encoder first so a
                // device-flip rebuild (RestartCapture) doesn't leak the previous one. The encoder owns the
                // MTU/whole-frame slicing (PcmEncoderAdapter) or Opus frame accumulation.
                encoder?.Dispose();
                encoder = CodecFactory.CreateEncoder(config.Codec, format, frameSamples, logLine);

                // Reused across callbacks so the capture thread allocates nothing (a GC pause here == an audio
                // dropout). The format code + codec id are constant for the session, so write them once up front;
                // the sequence byte at WireProtocol.SequenceByteOffset is overwritten per datagram.
                byte[] sendBuffer = new byte[WireProtocol.HeaderBytes + WireProtocol.MaxUdpAudioBytes];
                WireProtocol.WriteFormatCode(sendBuffer, AudioFormats.ToCode(effRate, effBits, effChannels));
                WireProtocol.WriteCodec(sendBuffer, config.Codec);

                var sendLogTimer = System.Diagnostics.Stopwatch.StartNew();
                int sentPackets = 0;
                long sentBytes = 0;
                // Per-rebuild fresh state: the wrapping sequence restarts at 0 after a recovery, so the receiver
                // sees one discontinuity at resume — a one-interval blip in its lost/reorder meter, not an audio
                // bug (the outage was already a real gap). Acceptable; not worth persisting across rebuilds.
                byte sequence = 0;
                capture.DataAvailable += (sender, e) =>
                {
                    var socket = udpClient;   // read per callback so a Stop()/restart can't send on a closed socket
                    if (socket == null)
                        return;
                    try
                    {
                        encoder.Push(e.Buffer, 0, e.BytesRecorded, (payload, off, len) =>
                        {
                            var s = udpClient;
                            if (s == null) return;
                            sendBuffer[WireProtocol.SequenceByteOffset] = sequence++;   // wraps at 256; receiver counts gaps as lost/reordered
                            Buffer.BlockCopy(payload, off, sendBuffer, WireProtocol.HeaderBytes, len);
                            s.Send(sendBuffer, len + WireProtocol.HeaderBytes);
                            sentPackets++;
                            sentBytes += len + WireProtocol.HeaderBytes;
                        });

                        if (sendLogTimer.ElapsedMilliseconds >= 1000)
                        {
                            report(DiagnosticsSnapshot.ForSender(sentPackets, sentBytes / 1024, config.Codec));
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
            LogCaptureFormat(renderInfo, capture.WaveFormat);
            logLine("Streaming system audio to " + config.HostName + "...");
            reportWaiting(false);   // capture is live — clear any "waiting for audio device" status
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
                reportWaiting(true);      // surface "waiting for audio device" until a rebuild succeeds
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
                            OpenSocket();     // retry the socket too: at login Connect() may have failed (network
                                              // not ready), not just the capture. No-op once the socket is open.
                            StartCapture();   // reentrant lock; publishes waveSource only on success
                        }
                        logLine("Sender start completed.");
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

        // The user picked a different output device in Windows. Unlike device *loss*, this raises no
        // RecordingStopped: the endpoint we are capturing from is still present and enabled, so WASAPI leaves the
        // stream alone and audio keeps being read from the old device until the session is restarted. The loopback
        // capture resolves its endpoint once in its constructor, so following the switch means rebuilding it. This
        // is the sibling of OnRecordingStopped: same rebuild, different trigger.
        private void OnDefaultRenderDeviceChanged(string deviceId)
        {
            // Off the OS notification thread — that callback must return promptly and must not block on
            // senderLock (Stop() can hold it while stopping the capture).
            Task.Run(() =>
            {
                lock (senderLock)
                {
                    // No live capture means there is nothing bound to the stale endpoint: either the pipeline
                    // hasn't been built yet (the first StartCapture resolves the current default), or
                    // RestartCapture is already mid-poll after a device loss (its next attempt does the same).
                    // Either way it lands on the new default on its own, and rebuilding here would race that.
                    if (!isRunning || waveSource == null)
                        return;
                }
                try
                {
                    logLine("Default audio output device changed; moving sender capture to it.");
                    lock (senderLock)
                    {
                        // Re-check isRunning INSIDE the lock so this rebuild is atomic against Stop(), which also
                        // takes senderLock — mirrors the receiver's BuildAndPlayOutput re-check.
                        if (!isRunning)
                            return;
                        StopCapture();    // tears down the old, still-live capture bound to the stale endpoint
                        StartCapture();   // rebuilds against the current default device (reentrant lock)
                    }
                }
                catch (Exception ex)
                {
                    // The new endpoint can briefly refuse activation (still initialising after the switch). Hand
                    // off to the existing once-per-second poll rather than giving up.
                    logLine($"Sender capture rebuild after device change failed ({ex.Message}); retrying...");
                    RestartCapture(null);
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
                encoder?.Dispose();
                encoder = null;
            }
        }

        // Acquires the default render endpoint once: its friendly name and shared-mode mix format. The mix
        // channel count is what Auto matches (endpoint configuration, not content). Returns null on any
        // COM/enumeration failure so the caller falls back to a stereo capture rather than throwing.
        private static (string Name, WaveFormat Mix)? TryGetDefaultRenderInfo()
        {
            try
            {
                using var devices = new MMDeviceEnumerator();
                var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return (device.FriendlyName, device.AudioClient.MixFormat);
            }
            catch
            {
                return null;
            }
        }

        private void LogCaptureFormat((string Name, WaveFormat Mix)? renderInfo, WaveFormat wireFormat)
        {
            if (renderInfo is not { } info)
            {
                logLine($"Sending {wireFormat.Encoding} {wireFormat.SampleRate}Hz {wireFormat.BitsPerSample}bit {wireFormat.Channels}ch "
                      + "(capture device format unavailable)");
                return;
            }

            var mix = info.Mix;
            logLine($"Capture '{info.Name}': device native {mix.Encoding} {mix.SampleRate}Hz {mix.BitsPerSample}bit {mix.Channels}ch; "
                  + $"sending {wireFormat.Encoding} {wireFormat.SampleRate}Hz {wireFormat.BitsPerSample}bit {wireFormat.Channels}ch");
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
