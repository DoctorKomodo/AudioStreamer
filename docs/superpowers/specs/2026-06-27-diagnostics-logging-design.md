# Diagnostics: File Log + Live UI Readout — Design

**Date:** 2026-06-27
**Component:** new `DiagnosticsLog.cs`, `AudioStreamerLogic.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`

## Problem

The sync-diagnostic metrics (`[recv] backlog=…`, `[send] pkts/s=…`) are emitted only via `Console.WriteLine`. AudioStreamer is a `WinExe` GUI app with no console, so for a normally-launched / installed app the output goes nowhere — useless for diagnosing a real lip-sync drift in the field. We want the metrics (a) written to a file and (b) shown live in the window.

## Decisions (from brainstorming)

- **UI readout:** a compact dim line under the status text, shown only while running.
- **File logging:** always-on, size-capped with rollover.
- **Decoupled from streaming:** logging must never do disk IO on the audio threads. The streaming threads only enqueue; a background thread does all file IO. The UI event uses non-blocking `Dispatcher.BeginInvoke`.

## Design

### 1. `DiagnosticsLog` (new, thread-safe, off-thread writer)

Owns the log file and a background writer so callers never block on disk.

```csharp
public sealed class DiagnosticsLog
{
    public DiagnosticsLog(string path);   // e.g. "diagnostics.log" (relative → next to the exe)
    public void Log(string line);         // timestamp + enqueue; returns immediately
}
```

- Backed by a `BlockingCollection<string>` and one `IsBackground` writer thread (started in the constructor) that loops `GetConsumingEnumerable()` and writes each line.
- `Log` stamps `yyyy-MM-dd HH:mm:ss.fff ` onto the line and enqueues — no disk access on the calling (audio) thread.
- The writer appends to `diagnostics.log`, tracking bytes written (seeded from the existing file length). When the size passes **1 MB**, it rotates: delete `diagnostics.1.log`, move `diagnostics.log` → `diagnostics.1.log`, reopen fresh (≤ ~2 MB on disk total).
- All file IO is wrapped in try/catch and **silently swallowed** — diagnostics must never crash or stall the app. The writer thread also mirrors each line to `Console` (so `dotnet run` still shows it) off the audio thread.
- On a hard process kill, queued-but-unwritten lines (≤ ~1–2) may be lost — acceptable for diagnostics. No explicit dispose/join required (background thread dies with the process).

### 2. `AudioStreamerLogic` — structured snapshot + event + routing

- New record carrying one second of metrics, with both output formats:

```csharp
public readonly record struct DiagnosticsSnapshot(
    bool IsReceiver, double BacklogMs, int PacketsPerSec, long KbPerSec, int OverflowPerSec, int ResyncPerSec)
{
    public string ToLogLine();      // "[recv] backlog={BacklogMs:F0}ms pkts/s=… KB/s=… overflow/s=… resync/s=…"
                                    // or "[send] pkts/s=… KB/s=…"
    public string ToCompactLine();  // "backlog {BacklogMs:F0} ms · {PacketsPerSec} pkt/s · {ResyncPerSec} resync/s"
                                    // or "{PacketsPerSec} pkt/s · {KbPerSec} KB/s"
}
```

- New `public event Action<DiagnosticsSnapshot>? Diagnostics;`.
- A `DiagnosticsLog` instance is created in the constructor (path `"diagnostics.log"`).
- A private `LogLine(string line)` helper calls `diagnosticsLog.Log(line)` (which handles the console mirror). All existing `Console.WriteLine` diagnostic calls (errors, "Streaming…/Receiving…/Waiting…", format line) are routed through `LogLine`.
- The once-per-second blocks in `StartSender` (`[send]`) and `ReceiveAudio` (`[recv]`) build a `DiagnosticsSnapshot` from the existing counters, then call `Report(snapshot)`:

```csharp
private void Report(DiagnosticsSnapshot s)
{
    LogLine(s.ToLogLine());      // off-thread file + console
    Diagnostics?.Invoke(s);      // UI (handler marshals via Dispatcher)
}
```

- `Start()` logs a session header via `LogLine` (`=== session started: {Mode} on port {Port} ===`); `Stop()` logs `=== session stopped ===` (only when a session was running, to avoid noise on idle Stop).

### 3. `MainWindow` — live readout

- A dim `DiagnosticsText` `TextBlock` directly under `StatusText` (e.g. `Foreground="Gray"`, small), empty by default.
- The constructor subscribes: `audioStreamerLogic.Diagnostics += OnDiagnostics;`. The handler marshals to the UI thread:

```csharp
private void OnDiagnostics(DiagnosticsSnapshot s) =>
    Dispatcher.BeginInvoke(() => DiagnosticsText.Text = s.ToCompactLine());
```

- `SetRunningState(running)` shows `DiagnosticsText` while running and clears + collapses it when idle.
- `Window_Closing` unsubscribes (`audioStreamerLogic.Diagnostics -= OnDiagnostics;`) before teardown.

## Data flow

```
audio thread → build DiagnosticsSnapshot → Report()
   ├─ LogLine → DiagnosticsLog.Log()  → [enqueue] → writer thread → file + console
   └─ Diagnostics event → MainWindow.OnDiagnostics → Dispatcher.BeginInvoke → UI text
```

Both branches are non-blocking on the audio thread (enqueue; post message).

## Testing

Manual (no test project), via loopback (`dotnet run` / built exe) and inspection:
1. Run a receiver session fed by a sender; confirm `diagnostics.log` appears next to the exe with timestamped session header + `[recv]`/`[send]` lines, appended ~1/sec.
2. With the window visible, confirm `DiagnosticsText` updates ~1/sec while running and is cleared/hidden when stopped.
3. Confirm a second consecutive session appends (not truncates) and writes a new session header.
4. Rotation (1 MB) is verified by reasoning about the byte counter; a full-size run isn't forced in a quick test.

## Out of scope

- Configurable log level / verbosity, and an in-app log viewer.
- Cross-process file contention (two instances sharing one working directory — only arises in same-machine loopback testing, not normal use).
- Flushing the final queued line on a hard kill.
