using System.Net;
using System.Net.Sockets;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioStreamer
{
    /// <summary>
    /// Receiver lifecycle: binds the UDP port, reads the format header from the first datagram, then streams
    /// incoming PCM through a ReorderBuffer into a BufferedWaveProvider played by WasapiOut. Owns its socket,
    /// NAudio objects, cancellation, and diagnostics state. Output device-loss recovery lives here (BuildAndPlayOutput).
    /// </summary>
    public sealed class ReceiverSession : IStreamSession
    {
        private const int ReorderBaseWindowPackets = 8;
        private const int ReorderMaxWindowPackets = 64;
        private const int ReorderBaselineBytesPerSecond = 48000 * 2 * 2;   // 192000 B/s (16-bit stereo @ 48 kHz)

        /// <summary>Reorder-buffer depth scaled to the stream's byte rate, keeping give-up time ~constant (~40 ms).</summary>
        public static int ComputeReorderWindow(WaveFormat format) =>
            Math.Clamp(ReorderBaseWindowPackets * format.AverageBytesPerSecond / ReorderBaselineBytesPerSecond,
                       ReorderBaseWindowPackets, ReorderMaxWindowPackets);

        private readonly AudioStreamerLogic.Config config;
        private readonly Action<string> logLine;
        private readonly Action<DiagnosticsSnapshot> report;

        // Serializes WasapiOut build / teardown / rebuild (Task 5) against Stop().
        private readonly object outputLock = new();

        private UdpClient? udpClient;
        private Socket? socket;
        private WasapiOut? wasapiOut;
        private BufferedWaveProvider bufferedWaveProvider = null!;   // built once the first packet reveals the format
        private UnderrunCountingWaveProvider? underrunMeter;
        private CancellationTokenSource? cts;
        private volatile bool isRunning;

        // Diagnostics + latency-cap state, shared across the receive loop iterations (were captured locals).
        private readonly SequenceLossTracker sequenceTracker = new();
        private readonly System.Diagnostics.Stopwatch logTimer = new();
        private int packets, overflows, resyncs;
        private long payloadBytes;
        private double minBacklogMs = double.MaxValue;
        private int maxLatencyMs;

        // The active wire format code, set in BuildPipeline. The receive loop rebuilds when an incoming code differs.
        private byte currentFormatCode;
        // Log-once guard for an unknown code seen mid-stream (the InitializeReceiver guard's flag is a local there).
        private bool loggedBadFormatMidStream;

        public ReceiverSession(AudioStreamerLogic.Config config, Action<string> logLine, Action<DiagnosticsSnapshot> report)
        {
            this.config = config;
            this.logLine = logLine;
            this.report = report;
        }

        public bool IsRunning => isRunning;

        public void Start()
        {
            maxLatencyMs = config.ReceiverMaxLatencyMilliseconds;
            var client = new UdpClient(config.Port);
            client.Client.ReceiveBufferSize = WireProtocol.SocketBufferBytes;
            client.Client.IOControl(WireProtocol.SIO_UDP_CONNRESET, new byte[4], null);
            socket = client.Client;
            udpClient = client;
            cts = new CancellationTokenSource();
            isRunning = true;
            logTimer.Restart();
            Task.Run(InitializeReceiver, cts.Token);
            logLine("Receiving audio...");
        }

        public void Stop()
        {
            isRunning = false;
            cts?.Cancel();                 // unblocks ReceiveFrom once the socket closes; loops observe the token
            lock (outputLock)
            {
                var output = wasapiOut;
                wasapiOut = null;
                if (output != null)
                {
                    output.PlaybackStopped -= OnPlaybackStopped;
                    try { output.Stop(); } catch { /* already stopped by the OS */ }
                    output.Dispose();
                }
            }
            udpClient?.Close();
            udpClient = null;
            socket = null;
            cts?.Dispose();
            cts = null;
        }

        // Builds (or rebuilds) WasapiOut around the existing underrun meter and starts playback. Single source of
        // truth for output creation so the recovery loop (Task 5) reuses it. Flushes to the live edge first so a
        // rebuild after a device outage resumes at ~0 backlog rather than dumping the piled-up backlog at once.
        // Constructed here (background thread) so WasapiOut captures no SynchronizationContext and PlaybackStopped
        // (Task 5) fires off the UI thread.
        private void BuildAndPlayOutput()
        {
            lock (outputLock)
            {
                // Re-check isRunning INSIDE the lock so this build is atomic against Stop(), which also takes
                // outputLock. This is the receiver mirror of the sender's senderLock-atomic IsRunning+StartCapture
                // pair: without it, a Stop() racing a recovery rebuild (Task 5) could resurrect a live WasapiOut
                // after the session stopped, leaking a playing device. On the first build (from InitializeReceiver)
                // isRunning is already true, so this passes.
                if (!isRunning)
                    return;
                if (wasapiOut is { } previous)   // mid-stream rebuild: tear down the old output before replacing it
                {
                    previous.PlaybackStopped -= OnPlaybackStopped;   // unsubscribe first so Stop() can't re-enter recovery
                    try { previous.Stop(); } catch { /* already stopped by the OS */ }
                    previous.Dispose();
                    wasapiOut = null;
                }
                bufferedWaveProvider.ClearBuffer();   // no-op on first build (buffer empty); live-edge flush on rebuild
                var output = new WasapiOut(AudioClientShareMode.Shared, config.ReceiverAudioLatencyMilliseconds);
                output.Init(underrunMeter);
                output.PlaybackStopped += OnPlaybackStopped;
                output.Play();
                wasapiOut = output;
            }
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            logLine(e.Exception is null
                ? "Receiver output stopped unexpectedly."
                : "Receiver output stopped: " + e.Exception.Message);

            bool restart;
            lock (outputLock)
            {
                if (ReferenceEquals(wasapiOut, sender))
                    wasapiOut = null;
                restart = isRunning;
            }

            var dead = sender as IDisposable;
            if (restart)
                RestartOutput(dead);     // disposes dead, then rebuilds on a poll
            else if (dead != null)
                Task.Run(() => dead.Dispose());
        }

        // Rebuild WasapiOut after the OS tore the render device down (e.g. the receiver's monitor entered
        // power-saving), retrying until it succeeds or the session stops. Unbounded like the sender's capture
        // recovery. BuildAndPlayOutput ClearBuffer()s first, so playback resumes at the live edge.
        private void RestartOutput(IDisposable? deadOutput)
        {
            var token = cts?.Token ?? CancellationToken.None;
            Task.Run(async () =>
            {
                deadOutput?.Dispose();   // off the PlaybackStopped callback thread
                bool loggedWaiting = false;
                while (isRunning && !token.IsCancellationRequested)
                {
                    await Task.Delay(1000);
                    try
                    {
                        if (!isRunning || token.IsCancellationRequested) return;
                        BuildAndPlayOutput();
                        logLine("Receiver output restarted.");
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (!loggedWaiting)
                        {
                            logLine($"Receiver output rebuild failed ({ex.Message}); waiting for an audio output device to become available...");
                            loggedWaiting = true;
                        }
                    }
                }
            });
        }

        // (Re)builds the format-dependent pipeline — BufferedWaveProvider, its underrun meter, and the WasapiOut
        // output — for a catalog format, and records the active wire code. Called from InitializeReceiver (first
        // packet) and from the receive loop on a mid-stream format change (a sender device-change can flip the
        // channel count under Auto). The buffer/meter field swap here is safe because the receive loop is
        // single-threaded (the only thread that touches those fields and calls AddSamples); only the WasapiOut
        // create/teardown inside BuildAndPlayOutput is serialized by outputLock (against Stop() / PlaybackStopped
        // recovery). Set the fields BEFORE BuildAndPlayOutput so the new WasapiOut is wired to the new buffer.
        private void BuildPipeline(AudioFormats.Format fmt, byte code)
        {
            currentFormatCode = code;
            bufferedWaveProvider = new BufferedWaveProvider(AudioFormats.ToWaveFormat(fmt.SampleRate, fmt.BitDepth, fmt.Channels))
            {
                BufferDuration = TimeSpan.FromMilliseconds(config.ReceiverAudioBufferMillisecondsLength),
                DiscardOnBufferOverflow = true
            };
            underrunMeter = new UnderrunCountingWaveProvider(bufferedWaveProvider);
            BuildAndPlayOutput();
        }

        // ReorderBuffer emit target. Reads the bufferedWaveProvider field live, so a rebuild that swaps the field
        // re-points this without rebuilding the delegate. Shared by the initial and post-rebuild ReorderBuffers.
        private void EmitSamples(byte[] buf, int off, int cnt)
        {
            if (bufferedWaveProvider.BufferedBytes + cnt > bufferedWaveProvider.BufferLength)
                overflows++;
            bufferedWaveProvider.AddSamples(buf, off, cnt);
        }

        private void InitializeReceiver()
        {
            byte[] receiveBuffer = new byte[65536];
            EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
            var token = cts!.Token;
            // Capture the socket once: Stop() closes it (to unblock ReceiveFrom) and nulls the field, so reading
            // the field per-iteration could race to null. The local keeps a stable reference; teardown surfaces as
            // an ObjectDisposedException/SocketException caught below, where the cancelled token exits the loop.
            Socket socket = this.socket!;
            bool loggedBadFormat = false;
            logLine("Waiting for audio connection from sender");
            while (!token.IsCancellationRequested)
            {
                try
                {
                    int received = socket.ReceiveFrom(receiveBuffer, ref remoteEP);
                    if (received >= WireProtocol.HeaderBytes)
                    {
                        byte code = WireProtocol.ReadFormatCode(receiveBuffer);
                        if (AudioFormats.FromCode(code) is not { } fmt)
                        {
                            // Unknown code: a foreign datagram on the port, or a version mismatch. Ignore it and
                            // keep waiting (log once so a stream of them doesn't flood the log). [followups item 16]
                            if (!loggedBadFormat)
                            {
                                logLine($"Ignoring datagram with unknown format code {code} (sender/receiver version mismatch?).");
                                loggedBadFormat = true;
                            }
                            continue;
                        }
                        logLine($"Sample rate: {fmt.SampleRate}, Bit depth: {fmt.BitDepth}, Channels: {fmt.Channels} received from sender");
                        BuildPipeline(fmt, code);   // sets currentFormatCode before ReceiveAudio runs — no first-packet double-build
                        Task.Run(ReceiveAudio, token);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) break;
                    logLine("Error receiving connection: " + ex.Message);
                }
            }
        }

        private void ReceiveAudio()
        {
            byte[] receiveBuffer = new byte[65536];
            byte[] dropScratch = new byte[16384];
            EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
            var token = cts!.Token;
            Socket socket = this.socket!;   // captured once; see InitializeReceiver for why (Stop() nulls the field)

            int reorderWindow = ComputeReorderWindow(bufferedWaveProvider.WaveFormat);
            logLine($"Reorder window: {reorderWindow} packets");
            var reorderBuffer = new ReorderBuffer(reorderWindow, EmitSamples);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    int received = socket.ReceiveFrom(receiveBuffer, ref remoteEP);
                    if (received > WireProtocol.HeaderBytes)
                    {
                        byte code = WireProtocol.ReadFormatCode(receiveBuffer);
                        if (code != currentFormatCode)
                        {
                            if (AudioFormats.FromCode(code) is not { } newFmt)
                            {
                                // Unknown code mid-stream (foreign datagram / version mismatch): ignore, log once,
                                // no rebuild. Steady-state path stays this single compare when code == current.
                                if (!loggedBadFormatMidStream)
                                {
                                    logLine($"Ignoring mid-stream datagram with unknown format code {code}.");
                                    loggedBadFormatMidStream = true;
                                }
                                continue;
                            }

                            // Valid new format: rebuild the whole pipeline BEFORE feeding this datagram, so no
                            // old-format-aligned bytes land in the new buffer. Single-threaded loop => atomic vs AddSamples.
                            logLine($"Wire format changed to {newFmt.SampleRate}Hz {newFmt.BitDepth}bit {newFmt.Channels}ch; rebuilding output.");
                            BuildPipeline(newFmt, code);
                            reorderWindow = ComputeReorderWindow(bufferedWaveProvider.WaveFormat);
                            reorderBuffer = new ReorderBuffer(reorderWindow, EmitSamples);
                            logLine($"Reorder window: {reorderWindow} packets");
                        }

                        int payload = received - WireProtocol.HeaderBytes;
                        packets++;
                        payloadBytes += payload;

                        double backlogNow = bufferedWaveProvider.BufferedDuration.TotalMilliseconds;
                        if (backlogNow < minBacklogMs) minBacklogMs = backlogNow;

                        sequenceTracker.OnReceived(receiveBuffer[WireProtocol.SequenceByteOffset]);
                        reorderBuffer.Add(receiveBuffer[WireProtocol.SequenceByteOffset], receiveBuffer, WireProtocol.HeaderBytes, payload);
                        TrimBacklog(dropScratch);
                    }

                    if (logTimer.ElapsedMilliseconds >= 1000)
                        ReportInterval();
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) break;
                    logLine("Error receiving audio: " + ex.Message);
                }
            }
        }

        // Cap latency: backlog == current audio-behind-video delay. If clock drift grows it past the cap, drop just
        // the excess down to a frame-aligned low-water mark (half the cap) rather than emptying the buffer.
        private void TrimBacklog(byte[] scratch)
        {
            if (maxLatencyMs <= 0 || bufferedWaveProvider.BufferedDuration.TotalMilliseconds <= maxLatencyMs)
                return;
            int targetBytes = bufferedWaveProvider.WaveFormat.AverageBytesPerSecond * (maxLatencyMs / 2) / 1000;
            targetBytes -= targetBytes % bufferedWaveProvider.WaveFormat.BlockAlign;
            int dropBytes = bufferedWaveProvider.BufferedBytes - targetBytes;
            while (dropBytes > 0)
            {
                int n = bufferedWaveProvider.Read(scratch, 0, Math.Min(dropBytes, scratch.Length));
                if (n == 0) break;
                dropBytes -= n;
            }
            resyncs++;
        }

        private void ReportInterval()
        {
            double minBacklog = minBacklogMs == double.MaxValue ? bufferedWaveProvider.BufferedDuration.TotalMilliseconds : minBacklogMs;
            // MUST NOT SWAP: Exchange() returns (Reorders, Losses) but ForReceiver wants (..., lostPerSec, reorderPerSec, ...).
            // So the call below passes `losses, reorders` in that order — same as the original (source line 463). Do not "tidy".
            var (reorders, losses) = sequenceTracker.Exchange();
            report(DiagnosticsSnapshot.ForReceiver(
                bufferedWaveProvider.BufferedDuration.TotalMilliseconds,
                packets, payloadBytes / 1024, overflows, resyncs, losses, reorders,
                underrunMeter?.ExchangeUnderruns() ?? 0, minBacklog));
            packets = 0; payloadBytes = 0; overflows = 0; resyncs = 0;
            minBacklogMs = double.MaxValue;
            logTimer.Restart();
        }
    }
}
