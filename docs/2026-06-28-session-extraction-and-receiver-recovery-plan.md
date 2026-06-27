# Session Extraction + Receiver Device-Loss Recovery — Implementation Plan

> **For agentic workers:** implement task-by-task. Steps use checkbox (`- [ ]`) syntax. Each task ends with a build (and, where noted, a runtime smoke). There is **no test project** in this repo (per CLAUDE.md), so verification is `dotnet build` clean (0/0) + targeted runtime smoke + field test for the device-loss path — the same validation standard used for the sender auto-recovery work.

**Goal:** Refactor the monolithic `AudioStreamerLogic` streaming code into two focused session classes and give the receiver the same render-device-loss recovery the sender already has.

**Architecture:** `AudioStreamerLogic` becomes a thin coordinator (config load/save, the `DiagnosticsLog`, the `Diagnostics` event, mode selection). The sender and receiver each become an `IStreamSession` (`SenderSession`, `ReceiverSession`) owning their own UDP socket, NAudio objects, lifecycle lock, and recovery loop. Wire-protocol geometry + pack/unpack move to a shared static `WireProtocol`. `DiagnosticsSnapshot` gains named factories so the per-second reports stop being positional 10-arg calls full of literal zeros.

**Tech stack:** WPF/.NET 10 (`net10.0-windows`), NAudio (WASAPI loopback capture + `WasapiOut`), `System.Net.Sockets.UdpClient`.

## Global constraints

- Fully-qualify WinForms types (`System.Windows.Forms.*`); do not add a `using` for it. (N/A to these files but holds repo-wide.)
- Nullable reference types are enabled; nullable session fields guarded with `?.` are intentional.
- Do **not** reintroduce `Console.ReadKey` keep-alive loops.
- Commit/push only when the user asks; if on `main`, branch first.
- Behaviour-preserving tasks (1–4) must not change any observed runtime behaviour — only structure. R1 (task 5) is the only intentional behaviour change.

## Design decisions (locked)

- **R1 recovery = flush to live edge.** On render-device loss the receiver rebuilds `WasapiOut` around the *existing* `BufferedWaveProvider` + `UnderrunCountingWaveProvider`, but `ClearBuffer()`s first so playback resumes at ~0 backlog (drops the audio that piled up during the outage). Polls once/sec until a device returns — unbounded, like the sender (monitor power-save can last arbitrarily long).
- **Full symmetric extraction.** Both `SenderSession` and `ReceiverSession`; `AudioStreamerLogic` holds a single `IStreamSession? session`.

## File structure

- **Create** `WireProtocol.cs` — static: header geometry constants, socket constants, `WriteFormatHeader`/`ReadFormatHeader`. (S3 + S4)
- **Create** `IStreamSession.cs` — `{ bool IsRunning; void Start(); void Stop(); }`.
- **Create** `SenderSession.cs` — sender lifecycle + capture auto-recovery + `TweakedWasapiLoopbackCapture` + `LogCaptureFormat`. (moved from `AudioStreamerLogic`)
- **Create** `ReceiverSession.cs` — receiver lifecycle + reorder/diagnostics + `ComputeReorderWindow` + R1 output recovery. (moved from `AudioStreamerLogic`, plus R1)
- **Modify** `DiagnosticsSnapshot.cs` — add `ForSender`/`ForReceiver`. (S2)
- **Modify** `AudioStreamerLogic.cs` — strip sender+receiver bodies; become coordinator.
- **Modify** `CLAUDE.md`, `docs/2026-06-27-streaming-review-followups.md` — update `ComputeReorderWindow` reference + document the new structure and receiver recovery.

Ordering rationale: shared leaf pieces first (WireProtocol, factories) so both sessions consume them; then extract receiver (the larger self-contained chunk) leaving the already-factored sender inline; then extract sender, leaving a thin coordinator; then add R1 on the clean structure; then docs. Every task stays green.

---

## Task 1: `WireProtocol` — shared header geometry + pack/unpack (S3 + S4)

**Files:**
- Create: `WireProtocol.cs`
- Modify: `AudioStreamerLogic.cs` (replace private wire constants + `PackWaveFormat`/`UnpackWaveFormat` usages)

**Produces (consumed by tasks 3–4):** `WireProtocol.HeaderBytes`, `.FormatHeaderBytes`, `.SequenceByteOffset`, `.MaxUdpAudioBytes`, `.SocketBufferBytes`, `.SIO_UDP_CONNRESET`, `WireProtocol.WriteFormatHeader(byte[] dest, int sampleRate, int bitDepth, int channels)`, `WireProtocol.ReadFormatHeader(byte[] src) -> (int sampleRate, int bitDepth, int channels)`.

- [ ] **Step 1: Create `WireProtocol.cs`**

```csharp
namespace AudioStreamer
{
    /// <summary>
    /// The on-the-wire UDP framing shared by sender and receiver. Every datagram is a 4-byte header
    /// (3-byte wave-format descriptor + 1-byte wrapping sequence number) followed by raw PCM audio.
    /// </summary>
    internal static class WireProtocol
    {
        public const int HeaderBytes = 4;          // total header (audio starts here on both sides)
        public const int FormatHeaderBytes = 3;    // bytes 0-2: packed wave format
        public const int SequenceByteOffset = 3;   // byte 3: wrapping per-datagram sequence number

        // Keep each datagram inside a 1500-byte Ethernet MTU (minus IP/UDP/header, with VPN/PPPoE headroom)
        // so IP never fragments the audio. Audio is sliced on whole-frame boundaries by the sender.
        public const int MaxUdpAudioBytes = 1440;
        // Roomy socket buffers absorb scheduling jitter/bursts so the kernel doesn't silently drop datagrams.
        public const int SocketBufferBytes = 1 << 20; // 1 MiB
        // Winsock ioctl that stops a UDP socket's Receive/Send from throwing 10054 (WSAECONNRESET) after a
        // prior send drew an ICMP "port unreachable" (e.g. the sender started before the receiver bound).
        public const int SIO_UDP_CONNRESET = -1744830452; // 0x9800000C

        /// <summary>Writes the 3-byte format descriptor into dest[0..2]. No allocation.</summary>
        public static void WriteFormatHeader(byte[] dest, int sampleRate, int bitDepth, int channels)
        {
            dest[0] = (byte)(sampleRate / 1000);
            dest[1] = (byte)bitDepth;
            dest[2] = (byte)channels;
        }

        /// <summary>Reads the 3-byte format descriptor from src[0..2].</summary>
        public static (int sampleRate, int bitDepth, int channels) ReadFormatHeader(byte[] src) =>
            (src[0] * 1000, src[1], src[2]);
    }
}
```

- [ ] **Step 2: Point `AudioStreamerLogic` at `WireProtocol`**

In `AudioStreamerLogic.cs`:
- Delete the private constants `HeaderBytes`, `MaxUdpAudioBytes`, `SocketBufferBytes`, `SIO_UDP_CONNRESET` (lines ~57–66).
- Delete the `PackWaveFormat` and `UnpackWaveFormat` methods (lines ~542–556).
- Replace every use of the deleted names with the `WireProtocol.*` equivalents:
  - Sender socket setup: `WireProtocol.SocketBufferBytes`, `WireProtocol.SIO_UDP_CONNRESET`.
  - `int maxChunk = Math.Max(blockAlign, (WireProtocol.MaxUdpAudioBytes / blockAlign) * blockAlign);`
  - `byte[] sendBuffer = new byte[WireProtocol.HeaderBytes + maxChunk];`
  - Replace the `Buffer.BlockCopy(PackWaveFormat(...), 0, sendBuffer, 0, 3)` line with `WireProtocol.WriteFormatHeader(sendBuffer, CurrentConfig.SampleRate, CurrentConfig.BitsPerSample, CurrentConfig.Channels);`
  - `sendBuffer[WireProtocol.SequenceByteOffset] = sequence++;`
  - `socket.Send(sendBuffer, chunk + WireProtocol.HeaderBytes);` and the `sentBytes += chunk + WireProtocol.HeaderBytes;`
  - Receiver: `received > WireProtocol.HeaderBytes`, `int payload = received - WireProtocol.HeaderBytes`, `receiveBuffer[WireProtocol.SequenceByteOffset]` (both the tracker and `reorderBuffer.Add`), `reorderBuffer.Add(..., WireProtocol.HeaderBytes, payload)`.
  - Receiver init: replace `received >= 3` with `received >= WireProtocol.FormatHeaderBytes`; replace the `byte[] header = new byte[3]; Buffer.BlockCopy(...); UnpackWaveFormat(header)` block with `(int sampleRate, int bitDepth, int channels) = WireProtocol.ReadFormatHeader(receiveBuffer);`

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: `0 Warning(s) 0 Error(s)`.

- [ ] **Step 4: Commit**

```bash
git add WireProtocol.cs AudioStreamerLogic.cs
git commit -m "Extract wire-protocol framing into WireProtocol; name header offsets"
```

---

## Task 2: `DiagnosticsSnapshot` named factories (S2)

**Files:**
- Modify: `DiagnosticsSnapshot.cs`
- Modify: `AudioStreamerLogic.cs` (the two construction sites)

**Produces:** `DiagnosticsSnapshot.ForSender(int packetsPerSec, long kbPerSec)`, `DiagnosticsSnapshot.ForReceiver(double backlogMs, int packetsPerSec, long kbPerSec, int overflowPerSec, int resyncPerSec, int lostPerSec, int reorderPerSec, int underrunPerSec, double minBacklogMs)`.

- [ ] **Step 1: Add the factories**

In `DiagnosticsSnapshot.cs`, inside the record struct body:

```csharp
        /// <summary>Sender side: only packet/byte throughput is meaningful; everything else is zero.</summary>
        public static DiagnosticsSnapshot ForSender(int packetsPerSec, long kbPerSec) =>
            new(false, 0, packetsPerSec, kbPerSec, 0, 0, 0, 0, 0, 0);

        /// <summary>Receiver side: full backlog/loss/drift telemetry.</summary>
        public static DiagnosticsSnapshot ForReceiver(double backlogMs, int packetsPerSec, long kbPerSec,
            int overflowPerSec, int resyncPerSec, int lostPerSec, int reorderPerSec, int underrunPerSec,
            double minBacklogMs) =>
            new(true, backlogMs, packetsPerSec, kbPerSec, overflowPerSec, resyncPerSec, lostPerSec,
                reorderPerSec, underrunPerSec, minBacklogMs);
```

- [ ] **Step 2: Use them at the two call sites in `AudioStreamerLogic.cs`**

- Sender (~line 245):
  `Report(DiagnosticsSnapshot.ForSender(sentPackets, sentBytes / 1024));`
- Receiver (~lines 461–464):
  ```csharp
  Report(DiagnosticsSnapshot.ForReceiver(
      bufferedWaveProvider.BufferedDuration.TotalMilliseconds,
      packets, payloadBytes / 1024, overflows, resyncs, losses, reorders,
      underrunMeter?.ExchangeUnderruns() ?? 0, minBacklog));
  ```

- [ ] **Step 3: Build**

Run: `dotnet build` → expect `0 Warning(s) 0 Error(s)`.

- [ ] **Step 4: Commit**

```bash
git add DiagnosticsSnapshot.cs AudioStreamerLogic.cs
git commit -m "Add DiagnosticsSnapshot.ForSender/ForReceiver factories"
```

---

## Task 3: Extract `ReceiverSession`

**Files:**
- Create: `IStreamSession.cs`
- Create: `ReceiverSession.cs`
- Modify: `AudioStreamerLogic.cs` (delegate receiver mode; drop receiver fields/bodies)

**Interfaces:**
- Produces: `IStreamSession`, `ReceiverSession(Config config, Action<string> logLine, Action<DiagnosticsSnapshot> report)`, `public static int ReceiverSession.ComputeReorderWindow(WaveFormat format)`.
- Consumes: `WireProtocol.*` (Task 1), `DiagnosticsSnapshot.ForReceiver` (Task 2), `ReorderBuffer`, `SequenceLossTracker`, `UnderrunCountingWaveProvider`.

This task is a behaviour-preserving move. The receive loop, init loop, latency-trim, and diagnostics counters become **fields** of `ReceiverSession` (the S1 win — no more 10 captured locals). **No** `WasapiOut.PlaybackStopped` handling yet (that is Task 5); build `BuildAndPlayOutput()` now as the single place output is created so Task 5 only has to call it from a recovery loop.

- [ ] **Step 1: Create `IStreamSession.cs`**

```csharp
namespace AudioStreamer
{
    /// <summary>A running streaming session (sender or receiver). Start is non-blocking; Stop tears down.</summary>
    public interface IStreamSession
    {
        bool IsRunning { get; }
        void Start();
        void Stop();
    }
}
```

- [ ] **Step 2: Create `ReceiverSession.cs`**

Move the receiver code out of `AudioStreamerLogic.StartReceiver` (current lines ~367–524) plus `ComputeReorderWindow` + its three constants (lines ~73–80) into this class. Promote the captured locals to fields. Concretely:

```csharp
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

        private readonly Config config;
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

        public ReceiverSession(Config config, Action<string> logLine, Action<DiagnosticsSnapshot> report)
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
                    // Task 5 will unsubscribe PlaybackStopped here before stopping.
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
                bufferedWaveProvider.ClearBuffer();   // no-op on first build (buffer empty); live-edge flush on rebuild
                var output = new WasapiOut(AudioClientShareMode.Shared, config.ReceiverAudioLatencyMilliseconds);
                output.Init(underrunMeter);
                output.Play();
                wasapiOut = output;
            }
        }

        private void InitializeReceiver()
        {
            byte[] receiveBuffer = new byte[65536];
            EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
            var token = cts!.Token;
            logLine("Waiting for audio connection from sender");
            while (!token.IsCancellationRequested)
            {
                try
                {
                    int received = socket!.ReceiveFrom(receiveBuffer, ref remoteEP);
                    if (received >= WireProtocol.FormatHeaderBytes)
                    {
                        var (sampleRate, bitDepth, channels) = WireProtocol.ReadFormatHeader(receiveBuffer);
                        logLine($"Sample rate: {sampleRate}, Bit depth: {bitDepth}, Channels: {channels} received from sender");
                        bufferedWaveProvider = new BufferedWaveProvider(new WaveFormat(sampleRate, bitDepth, channels))
                        {
                            BufferDuration = TimeSpan.FromMilliseconds(config.ReceiverAudioBufferMillisecondsLength),
                            DiscardOnBufferOverflow = true
                        };
                        underrunMeter = new UnderrunCountingWaveProvider(bufferedWaveProvider);
                        BuildAndPlayOutput();
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

            int reorderWindow = ComputeReorderWindow(bufferedWaveProvider.WaveFormat);
            logLine($"Reorder window: {reorderWindow} packets");
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
                    int received = socket!.ReceiveFrom(receiveBuffer, ref remoteEP);
                    if (received > WireProtocol.HeaderBytes)
                    {
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
```

- [ ] **Step 3: Make `Config` reachable from `ReceiverSession`**

`ReceiverSession` references `Config`. `Config` is nested in `AudioStreamerLogic`, so the unqualified name won't resolve from another file. Either (a) reference it as `AudioStreamerLogic.Config` in the constructor signature, or (b) — preferred for the coordinator end-state — leave `Config` nested and use `AudioStreamerLogic.Config` here. Use option (a)/(b): change the two `Config` mentions to `AudioStreamerLogic.Config`. (Do not move `Config` out in this task; keep the diff focused. A later optional cleanup could promote `Config` to a top-level type.)

- [ ] **Step 4: Delegate receiver mode in `AudioStreamerLogic`**

- Add field: `private ReceiverSession? receiverSession;`
- In `Start()`, replace the `StartReceiver();` call with:
  ```csharp
  var receiver = new ReceiverSession(CurrentConfig, LogLine, Report);
  receiver.Start();
  receiverSession = receiver;
  ```
- In `Stop()`, after the existing sender teardown, add:
  ```csharp
  receiverSession?.Stop();
  receiverSession = null;
  ```
- Delete the entire `StartReceiver` method (lines ~367–524) and the `ComputeReorderWindow` method + the three `Reorder*` constants (lines ~67–80).
- Delete the receiver-only fields `wasapiOut` (line ~36) and `cts` (line ~34) from `AudioStreamerLogic` (the sender path uses neither — both are receiver-only today).
- **Required for the interim build to compile** (these fields are now gone): in `AudioStreamerLogic.Stop()` (lines ~120–145) **remove** every reference to the deleted fields — the `cts?.Cancel();` line (129), the `wasapiOut?.Stop(); wasapiOut?.Dispose(); wasapiOut = null;` block (133–135), and the `cts?.Dispose(); cts = null;` block (140–141). After editing, the interim `Stop()` body must be exactly:
  ```csharp
  bool wasRunning = IsRunning;
  isRunning = false;
  StopSenderCapture();
  udpClient?.Close();
  udpClient = null;
  receiverSession?.Stop();
  receiverSession = null;
  if (wasRunning)
      LogLine("=== session stopped ===");
  ```
  (Task 4 replaces this whole method with the coordinator `Stop()`; this interim form just has to build and behave correctly for both modes in the meantime.)

> NOTE: After this task `AudioStreamerLogic` still has the sender inline (Task 4 moves it). Keep `udpClient`, `waveSource`, `senderLock`, `isRunning`, and the sender methods as-is. `IsRunning` stays the sender/coordinator flag for now; it already returns `isRunning`, and the `isRunning = true` in `Start()` runs after `StartSender()`/`receiver.Start()` returns for both modes — verify that line still runs for both. (The receiver session sets its *own* `isRunning` internally too; the coordinator's flag is what `IsRunning` returns until Task 4.)

- [ ] **Step 5: Build**

Run: `dotnet build` → expect `0 Warning(s) 0 Error(s)`.

- [ ] **Step 6: Runtime smoke (receiver)**

Run the app as Receiver against a live sender (or loopback from a second instance). Confirm `diagnostics.log` shows `Waiting for audio connection`, the format line, `Reorder window: N packets`, then per-second `[recv]` lines; Stop produces a clean teardown with no error spam.

- [ ] **Step 7: Commit**

```bash
git add IStreamSession.cs ReceiverSession.cs AudioStreamerLogic.cs
git commit -m "Extract ReceiverSession from AudioStreamerLogic"
```

---

## Task 4: Extract `SenderSession`; reduce `AudioStreamerLogic` to a coordinator

**Files:**
- Create: `SenderSession.cs`
- Modify: `AudioStreamerLogic.cs`

**Interfaces:**
- Produces: `SenderSession(AudioStreamerLogic.Config config, Action<string> logLine, Action<DiagnosticsSnapshot> report)` implementing `IStreamSession`.
- Consumes: `WireProtocol.*`, `DiagnosticsSnapshot.ForSender`.

Behaviour-preserving move of the sender (currently lines ~169–309, ~318–365, ~526–540, ~558–586): `StartSender`, `StartSenderCapture`, `OnSenderRecordingStopped`, `RestartSenderCapture`, `StopSenderCapture`, `LogCaptureFormat`, and the nested `TweakedWasapiLoopbackCapture`. Keep the `senderLock` + `volatile isRunning` model exactly. Rename the `Sender`-prefixed methods to plain `StartCapture`/`OnRecordingStopped`/`RestartCapture`/`StopCapture` inside the class.

- [ ] **Step 1: Create `SenderSession.cs`**

Move the sender code verbatim into:

```csharp
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

        private readonly object senderLock = new();
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
            var client = new UdpClient();
            client.Client.SendBufferSize = WireProtocol.SocketBufferBytes;
            client.Client.IOControl(WireProtocol.SIO_UDP_CONNRESET, new byte[4], null);
            client.Connect(IPAddress.Parse(config.HostName), config.Port);
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

        // ... StartCapture / OnRecordingStopped / RestartCapture / StopCapture / LogCaptureFormat
        //     moved from AudioStreamerLogic, with:
        //       - CurrentConfig.* -> config.*
        //       - LogLine(...)    -> logLine(...)
        //       - Report(...)     -> report(...)
        //       - PackWaveFormat / header consts -> WireProtocol.WriteFormatHeader / WireProtocol.*
        //       - DiagnosticsSnapshot for [send] -> DiagnosticsSnapshot.ForSender(sentPackets, sentBytes / 1024)
        //       - the IsRunning checks now read this.isRunning (same volatile field)
        //     TweakedWasapiLoopbackCapture nested class moved here unchanged.
    }
}
```

> The bodies of `StartCapture`, `OnRecordingStopped`, `RestartCapture`, `StopCapture`, `LogCaptureFormat`, and `TweakedWasapiLoopbackCapture` are copied unchanged except for the mechanical substitutions listed above. Do not alter the locking or the publish-after-`StartRecording` ordering.

- [ ] **Step 2: Reduce `AudioStreamerLogic` to a coordinator**

Replace the sender/receiver-specific fields and methods so the class is just:

```csharp
public Config CurrentConfig { get; set; } = new();
public event Action<DiagnosticsSnapshot>? Diagnostics;
private readonly DiagnosticsLog diagnosticsLog = new("diagnostics.log");
private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
private IStreamSession? session;

public bool IsRunning => session?.IsRunning ?? false;

private void LogLine(string line) => diagnosticsLog.Log(line);
private void Report(DiagnosticsSnapshot s) { LogLine(s.ToLogLine()); Diagnostics?.Invoke(s); }

public AudioStreamerLogic() => LoadConfig();

public void Start()
{
    if (IsRunning) return;
    IStreamSession s = CurrentConfig.Mode == ModeType.Sender
        ? new SenderSession(CurrentConfig, LogLine, Report)
        : new ReceiverSession(CurrentConfig, LogLine, Report);
    try
    {
        s.Start();
        session = s;
        LogLine($"=== session started: {CurrentConfig.Mode} on port {CurrentConfig.Port} ===");
    }
    catch
    {
        s.Stop();
        throw;
    }
}

public void Stop()
{
    var s = session;
    session = null;
    if (s == null) return;
    s.Stop();
    LogLine("=== session stopped ===");
}
```

Keep `enum ModeType`, `class Config`, `LoadConfig`, `SaveConfig` exactly as they are. Delete everything else that moved (sender fields/methods, `senderLock`, `isRunning`, `waveSource`, `udpClient`, the `Reorder*`/wire constants already gone, `ComputeReorderWindow`, `PackWaveFormat`/`UnpackWaveFormat`, `LogCaptureFormat`, `TweakedWasapiLoopbackCapture`).

> `ReceiverSession`'s constructor currently takes `AudioStreamerLogic.Config`. With `CurrentConfig` passed in, both sessions share the live config instance — fine, since settings are locked in the UI while running.

- [ ] **Step 3: Build**

Run: `dotnet build` → expect `0 Warning(s) 0 Error(s)`.

- [ ] **Step 4: Runtime smoke (both modes)**

Run as Sender (confirm `[send]` lines + the `Streaming system audio to ...` line) and as Receiver (as Task 3 Step 6). Confirm Start/Stop several times with no port-in-use or disposed-object errors.

- [ ] **Step 5: Commit**

```bash
git add SenderSession.cs AudioStreamerLogic.cs
git commit -m "Extract SenderSession; reduce AudioStreamerLogic to a coordinator"
```

---

## Task 5: R1 — receiver render-device-loss recovery

**Files:**
- Modify: `ReceiverSession.cs`

Symmetric to the sender's capture recovery: subscribe to `WasapiOut.PlaybackStopped`; on an unexpected stop, poll once/sec rebuilding the output until a device returns; flush to the live edge on resume. `BuildAndPlayOutput()` from Task 3 is already the single build point.

- [ ] **Step 1: Subscribe + recover**

In `ReceiverSession`, add a `loggedWaiting`-style guard and these members, and wire `PlaybackStopped` inside `BuildAndPlayOutput` (and unsubscribe in `Stop`):

```csharp
        // In BuildAndPlayOutput, after creating `output` and before Play():
        //     output.PlaybackStopped += OnPlaybackStopped;

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
```

- [ ] **Step 2: Unsubscribe before intentional teardown**

In `Stop()`, change the output teardown block so it unsubscribes first (so an intentional stop isn't mistaken for device loss — mirrors `StopCapture`):

```csharp
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
```

And in `BuildAndPlayOutput`, subscribe before `Play()` — the `if (!isRunning) return;` guard added in Task 3 stays at the top of the lock so the subscribe+Play is atomic against Stop():

```csharp
            lock (outputLock)
            {
                if (!isRunning)
                    return;
                bufferedWaveProvider.ClearBuffer();
                var output = new WasapiOut(AudioClientShareMode.Shared, config.ReceiverAudioLatencyMilliseconds);
                output.Init(underrunMeter);
                output.PlaybackStopped += OnPlaybackStopped;
                output.Play();
                wasapiOut = output;
            }
```

- [ ] **Step 3: Build**

Run: `dotnet build` → expect `0 Warning(s) 0 Error(s)`.

- [ ] **Step 4: Field test (user hardware)**

As Receiver streaming live audio, force the receiver's render device away (let its monitor/HDMI output sleep, or disable the default playback device in Sound settings). Confirm `diagnostics.log` shows `Receiver output stopped: ...` → `waiting for an audio output device...` → on device return `Receiver output restarted.`, and that audio resumes at ~0 backlog (the next `[recv]` line shows low `backlog`/`min`) without a manual Stop/Start. Confirm a normal Stop during an outage logs no spurious "stopped unexpectedly".

Then explicitly exercise the finding-7 race: with the device gone and the 1-second rebuild loop actively retrying, click **Stop** *mid-retry* (not just while idle). Confirm the session stops clean — no `Receiver output restarted.` appears after `=== session stopped ===`, no audio device stays held, and a subsequent **Start** builds a single fresh receiver (no doubled output). This is the case the in-lock `isRunning` guard exists to cover.

- [ ] **Step 5: Commit**

```bash
git add ReceiverSession.cs
git commit -m "Auto-recover receiver output when the render device disappears"
```

---

## Task 6: Documentation

**Files:**
- Modify: `CLAUDE.md`
- Modify: `docs/2026-06-27-streaming-review-followups.md`

- [ ] **Step 1: Update `CLAUDE.md` Architecture section**

- Note the new structure: `AudioStreamerLogic` is a coordinator; `SenderSession`/`ReceiverSession` (`IStreamSession`) own their sides; `WireProtocol` holds the framing.
- Update the `ComputeReorderWindow` reference from `AudioStreamerLogic` to `ReceiverSession`.
- Add the receiver output recovery to the lifecycle note (the receiver now self-heals from render-device loss too — `BuildAndPlayOutput`/`OnPlaybackStopped`/`RestartOutput`, flush-to-live-edge — so the "receiver leaves a stale Running" caveat narrows to genuinely unrecoverable failures).

- [ ] **Step 2: Append a followups entry**

In `docs/2026-06-27-streaming-review-followups.md`, add item **14 — Receiver output device-loss recovery [DONE]** summarizing R1, and note the `WireProtocol`/`SenderSession`/`ReceiverSession` extraction. Fix the `AudioStreamerLogic.ComputeReorderWindow` mention in item 13 → `ReceiverSession.ComputeReorderWindow`.

- [ ] **Step 3: Build + commit**

```bash
dotnet build   # 0/0
git add CLAUDE.md docs/2026-06-27-streaming-review-followups.md
git commit -m "Document session extraction + receiver output recovery"
```

---

## Verification summary

| Task | Verification |
|------|--------------|
| 1 WireProtocol | build 0/0 |
| 2 Snapshot factories | build 0/0 |
| 3 ReceiverSession | build 0/0 + receiver runtime smoke |
| 4 SenderSession + coordinator | build 0/0 + sender & receiver runtime smoke |
| 5 R1 recovery | build 0/0 + receiver device-loss **field test** |
| 6 Docs | build 0/0 |

No automated tests exist in this repo; correctness of the moves rests on build + runtime smoke, and R1's recovery path on the field test (as with the sender auto-recovery). Tasks 1–4 are behaviour-preserving; only Task 5 changes runtime behaviour.

## Out of scope / deferred

- Promoting `Config`/`ModeType` to top-level types (they stay nested in `AudioStreamerLogic`).
- R2 (log-once-then-suppress for per-callback send/receive errors) and R3 (first-packet format sanity check) from the review — not requested here; could fold into a later pass.
- A shared `StreamSession` base class — the two lifecycles differ enough that an interface is the right amount of sharing.
