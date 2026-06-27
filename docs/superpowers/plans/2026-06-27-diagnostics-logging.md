# Diagnostics File Log + Live UI Readout Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist the per-second streaming diagnostics to a rolling file and show them live in the window, without ever doing disk/UI work on the audio threads.

**Architecture:** A new `DiagnosticsLog` enqueues timestamped lines and writes them on a dedicated background thread (with 1 MB rotation). `AudioStreamerLogic` builds a `DiagnosticsSnapshot` once per second, logs its detailed form, and raises a `Diagnostics` event; `MainWindow` marshals that event to a dim readout line via `Dispatcher.BeginInvoke`.

**Tech Stack:** C# / .NET 10, WPF, `System.Collections.Concurrent.BlockingCollection`, `System.IO`.

## Global Constraints

- Target framework `net10.0-windows`; nullable enabled — keep the build at **0 warnings, 0 errors**.
- Git repo: each task ends with `dotnet build` (must report `0 Warning(s) / 0 Error(s)`), the listed **manual check**, then a **commit**. No test project.
- **Logging must never block or stall the audio threads**: callers only enqueue; all disk + console IO happens on the background writer thread.
- All file IO in `DiagnosticsLog` is wrapped in try/catch and silently swallowed — diagnostics must never crash or stall streaming.
- The log file is opened via a **relative path** (`"diagnostics.log"`) so it lands next to the executable, like `config.json`.
- Keep `MessageBox` as the WPF type and fully-qualify WinForms types (the `UseWindowsForms` global using is active).

Build command used throughout:
`dotnet build "c:\Users\Andreas\Repos\AudioStreamer\AudioStreamer.csproj" -c Debug`

---

### Task 1: `DiagnosticsLog` — off-thread rolling file writer

**Files:**
- Create: `c:\Users\Andreas\Repos\AudioStreamer\DiagnosticsLog.cs`
- Modify: `c:\Users\Andreas\Repos\AudioStreamer\.gitignore`

**Interfaces:**
- Produces: `public sealed class DiagnosticsLog { public DiagnosticsLog(string path); public void Log(string line); }`. `Log` timestamps + enqueues, never touches disk on the caller's thread. Consumed by Task 3.

- [ ] **Step 1: Create `DiagnosticsLog.cs`**

```csharp
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace AudioStreamer
{
    /// <summary>
    /// Thread-safe diagnostic logger that never blocks the caller on disk IO.
    /// Callers enqueue lines (cheap); a single background thread does all file
    /// writes, console mirroring, and rotation. All IO failures are swallowed —
    /// logging must never affect streaming.
    /// </summary>
    public sealed class DiagnosticsLog
    {
        private const long MaxBytes = 1024 * 1024;   // rotate after ~1 MB

        private readonly string _path;
        private readonly string _rolledPath;
        private readonly BlockingCollection<string> _queue = new();
        private long _bytesWritten;

        public DiagnosticsLog(string path)
        {
            _path = path;
            _rolledPath = Path.Combine(
                Path.GetDirectoryName(path) ?? string.Empty,
                Path.GetFileNameWithoutExtension(path) + ".1" + Path.GetExtension(path)); // diagnostics.1.log

            try { _bytesWritten = File.Exists(_path) ? new FileInfo(_path).Length : 0; }
            catch { _bytesWritten = 0; }

            var writer = new Thread(WriteLoop) { IsBackground = true, Name = "DiagnosticsLog" };
            writer.Start();
        }

        /// <summary>Timestamps and enqueues a line. Returns immediately; no disk IO here.</summary>
        public void Log(string line)
        {
            string stamped = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}";
            try { _queue.Add(stamped); } catch { /* queue completed — ignore */ }
        }

        private void WriteLoop()
        {
            foreach (string line in _queue.GetConsumingEnumerable())
            {
                Console.WriteLine(line);   // mirror to console off the audio thread (visible under dotnet run)
                try
                {
                    int size = line.Length + Environment.NewLine.Length;
                    RotateIfNeeded(size);
                    File.AppendAllText(_path, line + Environment.NewLine);
                    _bytesWritten += size;
                }
                catch { /* disk unavailable / locked — drop this line */ }
            }
        }

        private void RotateIfNeeded(int incomingBytes)
        {
            if (_bytesWritten + incomingBytes <= MaxBytes)
                return;
            try
            {
                if (File.Exists(_rolledPath)) File.Delete(_rolledPath);
                if (File.Exists(_path)) File.Move(_path, _rolledPath);
            }
            catch { /* best effort */ }
            _bytesWritten = 0;
        }
    }
}
```

- [ ] **Step 2: Ignore the log files**

In `.gitignore`, under the runtime-generated section, add:

```
## Runtime-generated diagnostics log (written next to the executable)
diagnostics.log
diagnostics.1.log
```

- [ ] **Step 3: Build**

Run: `dotnet build "c:\Users\Andreas\Repos\AudioStreamer\AudioStreamer.csproj" -c Debug`
Expected: `0 Warning(s) / 0 Error(s)`.

- [ ] **Step 4: Manual check**

No behaviour yet (wired in Task 3). Build success is the deliverable.

- [ ] **Step 5: Commit**

```bash
git -C "c:\Users\Andreas\Repos\AudioStreamer" add DiagnosticsLog.cs .gitignore
git -C "c:\Users\Andreas\Repos\AudioStreamer" commit -m "Add off-thread rolling DiagnosticsLog" -m "Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: `DiagnosticsSnapshot` record + formatters

**Files:**
- Create: `c:\Users\Andreas\Repos\AudioStreamer\DiagnosticsSnapshot.cs`

**Interfaces:**
- Produces: `public readonly record struct DiagnosticsSnapshot(bool IsReceiver, double BacklogMs, int PacketsPerSec, long KbPerSec, int OverflowPerSec, int ResyncPerSec)` with `string ToLogLine()` and `string ToCompactLine()`. Consumed by Tasks 3 and 4.

- [ ] **Step 1: Create `DiagnosticsSnapshot.cs`**

```csharp
namespace AudioStreamer
{
    /// <summary>One second of streaming diagnostics, with both output formats.</summary>
    public readonly record struct DiagnosticsSnapshot(
        bool IsReceiver,
        double BacklogMs,
        int PacketsPerSec,
        long KbPerSec,
        int OverflowPerSec,
        int ResyncPerSec)
    {
        /// <summary>Detailed line for the log file / console.</summary>
        public string ToLogLine() => IsReceiver
            ? $"[recv] backlog={BacklogMs:F0}ms pkts/s={PacketsPerSec} KB/s={KbPerSec} overflow/s={OverflowPerSec} resync/s={ResyncPerSec}"
            : $"[send] pkts/s={PacketsPerSec} KB/s={KbPerSec}";

        /// <summary>Compact line for the live UI readout.</summary>
        public string ToCompactLine() => IsReceiver
            ? $"backlog {BacklogMs:F0} ms · {PacketsPerSec} pkt/s · {ResyncPerSec} resync/s"
            : $"{PacketsPerSec} pkt/s · {KbPerSec} KB/s";
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build "c:\Users\Andreas\Repos\AudioStreamer\AudioStreamer.csproj" -c Debug`
Expected: `0 Warning(s) / 0 Error(s)`.

- [ ] **Step 3: Manual check**

No behaviour yet. Build success is the deliverable.

- [ ] **Step 4: Commit**

```bash
git -C "c:\Users\Andreas\Repos\AudioStreamer" add DiagnosticsSnapshot.cs
git -C "c:\Users\Andreas\Repos\AudioStreamer" commit -m "Add DiagnosticsSnapshot with log/compact formatters" -m "Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: Wire diagnostics into `AudioStreamerLogic`

**Files:**
- Modify: `c:\Users\Andreas\Repos\AudioStreamer\AudioStreamerLogic.cs`

**Interfaces:**
- Consumes: `DiagnosticsLog` (Task 1), `DiagnosticsSnapshot` (Task 2).
- Produces: `public event Action<DiagnosticsSnapshot>? Diagnostics;`, plus a `diagnosticsLog` field and private `LogLine`/`Report` helpers. All former `Console.WriteLine` diagnostics now route through `LogLine`. Consumed by Task 4 (the event).

- [ ] **Step 1: Add the field, event, and helpers**

In `AudioStreamerLogic.cs`, just after the `public bool IsRunning { get; private set; }` line, add:

```csharp
        public event Action<DiagnosticsSnapshot>? Diagnostics;
        private readonly DiagnosticsLog diagnosticsLog = new("diagnostics.log");

        private void LogLine(string line) => diagnosticsLog.Log(line);

        private void Report(DiagnosticsSnapshot snapshot)
        {
            LogLine(snapshot.ToLogLine());
            Diagnostics?.Invoke(snapshot);
        }
```

- [ ] **Step 2: Log session start/stop**

In `Start()`, change the success line:

```csharp
                IsRunning = true;
```

to:

```csharp
                IsRunning = true;
                LogLine($"=== session started: {CurrentConfig.Mode} on port {CurrentConfig.Port} ===");
```

In `Stop()`, capture the prior state at the very top (before `cts?.Cancel();`):

```csharp
            bool wasRunning = IsRunning;
```

and at the very end of `Stop()` (after `IsRunning = false;`), add:

```csharp
            if (wasRunning)
                LogLine("=== session stopped ===");
```

- [ ] **Step 3: Route the sender's metric + messages**

In `StartSender`, replace the periodic log block:

```csharp
                    if (sendLogTimer.ElapsedMilliseconds >= 1000)
                    {
                        Console.WriteLine($"[send] pkts/s={sentPackets} KB/s={sentBytes / 1024}");
                        sentPackets = 0; sentBytes = 0; sendLogTimer.Restart();
                    }
```

with:

```csharp
                    if (sendLogTimer.ElapsedMilliseconds >= 1000)
                    {
                        Report(new DiagnosticsSnapshot(false, 0, sentPackets, sentBytes / 1024, 0, 0));
                        sentPackets = 0; sentBytes = 0; sendLogTimer.Restart();
                    }
```

Then change the sender's two remaining `Console.WriteLine` calls to `LogLine`:

```csharp
                    Console.WriteLine("Error sending audio: " + ex.Message);
```
→
```csharp
                    LogLine("Error sending audio: " + ex.Message);
```

and

```csharp
            Console.WriteLine("Streaming system audio to " + CurrentConfig.HostName + "...");
```
→
```csharp
            LogLine("Streaming system audio to " + CurrentConfig.HostName + "...");
```

- [ ] **Step 4: Route the receiver's metric + messages**

In `ReceiveAudio`, replace the periodic log block:

```csharp
                        if (logTimer.ElapsedMilliseconds >= 1000)
                        {
                            Console.WriteLine(
                                $"[recv] backlog={bufferedWaveProvider.BufferedDuration.TotalMilliseconds:F0}ms " +
                                $"pkts/s={packets} KB/s={payloadBytes / 1024} overflow/s={overflows} resync/s={resyncs}");
                            packets = 0; payloadBytes = 0; overflows = 0; resyncs = 0;
                            logTimer.Restart();
                        }
```

with:

```csharp
                        if (logTimer.ElapsedMilliseconds >= 1000)
                        {
                            Report(new DiagnosticsSnapshot(true,
                                bufferedWaveProvider.BufferedDuration.TotalMilliseconds,
                                packets, payloadBytes / 1024, overflows, resyncs));
                            packets = 0; payloadBytes = 0; overflows = 0; resyncs = 0;
                            logTimer.Restart();
                        }
```

Then change the receiver's remaining `Console.WriteLine` calls to `LogLine` (four of them):

```csharp
                        Console.WriteLine("Error receiving audio: " + ex.Message);
```
→ `LogLine("Error receiving audio: " + ex.Message);`

```csharp
                Console.WriteLine("Waiting for audio connection from sender");
```
→ `LogLine("Waiting for audio connection from sender");`

```csharp
                            Console.WriteLine($"Sample rate: {sampleRate}, Bit depth: {bitDepth}, Channels: {channels} received from sender");
```
→ `LogLine($"Sample rate: {sampleRate}, Bit depth: {bitDepth}, Channels: {channels} received from sender");`

```csharp
                        Console.WriteLine("Error receiving connection: " + ex.Message);
```
→ `LogLine("Error receiving connection: " + ex.Message);`

```csharp
            Console.WriteLine("Receiving audio...");
```
→ `LogLine("Receiving audio...");`

- [ ] **Step 5: Build**

Run: `dotnet build "c:\Users\Andreas\Repos\AudioStreamer\AudioStreamer.csproj" -c Debug`
Expected: `0 Warning(s) / 0 Error(s)`. (There should be no `Console.WriteLine` left in `AudioStreamerLogic.cs`.)

- [ ] **Step 6: Manual check — file is written**

Two-instance loopback. Create a sender config in a temp dir (`Mode` 0, `HostName` `127.0.0.1`, port 5005) and a receiver config in another, launch both built exes (each with its own `WorkingDir`), play audio briefly, then stop both. Confirm a `diagnostics.log` appears in each working dir containing a `=== session started …` header and timestamped `[recv]` / `[send]` lines. (During inline execution this is driven with the same `Start-Process` + UI-Automation flow used earlier in the project.)

- [ ] **Step 7: Commit**

```bash
git -C "c:\Users\Andreas\Repos\AudioStreamer" add AudioStreamerLogic.cs
git -C "c:\Users\Andreas\Repos\AudioStreamer" commit -m "Route diagnostics through DiagnosticsLog and a Diagnostics event" -m "Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: Live readout in `MainWindow`

**Files:**
- Modify: `c:\Users\Andreas\Repos\AudioStreamer\MainWindow.xaml`
- Modify: `c:\Users\Andreas\Repos\AudioStreamer\MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `audioStreamerLogic.Diagnostics` event and `DiagnosticsSnapshot.ToCompactLine()` (Tasks 2–3).
- Produces: `DiagnosticsText` readout, `OnDiagnostics` handler.

- [ ] **Step 1: Add the readout TextBlock (XAML)**

In `MainWindow.xaml`, immediately after the `StatusText` line:

```xml
        <TextBlock x:Name="StatusText" Text="Idle" Margin="1,6"/>
```

insert:

```xml
        <TextBlock x:Name="DiagnosticsText" Foreground="Gray" FontSize="11" Margin="1,0,1,4"/>
```

- [ ] **Step 2: Subscribe and handle the event (code-behind)**

In `MainWindow.xaml.cs`, in the constructor, immediately after `audioStreamerLogic = new AudioStreamerLogic();`, add:

```csharp
            audioStreamerLogic.Diagnostics += OnDiagnostics;
```

Add the handler method to the class:

```csharp
        private void OnDiagnostics(DiagnosticsSnapshot snapshot)
        {
            Dispatcher.BeginInvoke((Action)(() => DiagnosticsText.Text = snapshot.ToCompactLine()));
        }
```

- [ ] **Step 3: Show/clear the readout with running state**

In `SetRunningState`, at the end of the method (after the tray tooltip block), add:

```csharp
            DiagnosticsText.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            if (!running)
                DiagnosticsText.Text = string.Empty;
```

- [ ] **Step 4: Unsubscribe on close**

In `Window_Closing`, before `trayIcon?.Dispose();`, add:

```csharp
            audioStreamerLogic.Diagnostics -= OnDiagnostics;
```

- [ ] **Step 5: Build**

Run: `dotnet build "c:\Users\Andreas\Repos\AudioStreamer\AudioStreamer.csproj" -c Debug`
Expected: `0 Warning(s) / 0 Error(s)`.

- [ ] **Step 6: Manual check — live readout**

Run a visible receiver (`StartMinimized` false) and a separate sender targeting `127.0.0.1` while audio plays. Confirm the dim `DiagnosticsText` under the status line updates ~once/sec (e.g. `backlog 10 ms · 101 pkt/s · 0 resync/s`) while running, and is cleared/hidden after **Stop**.

- [ ] **Step 7: Commit**

```bash
git -C "c:\Users\Andreas\Repos\AudioStreamer" add MainWindow.xaml MainWindow.xaml.cs
git -C "c:\Users\Andreas\Repos\AudioStreamer" commit -m "Show live diagnostics readout under the status line" -m "Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Documentation

**Files:**
- Modify: `c:\Users\Andreas\Repos\AudioStreamer\CLAUDE.md`

**Interfaces:** none (docs only).

- [ ] **Step 1: Document the diagnostics path**

In `CLAUDE.md`, replace the existing diagnostics sentence in the Latency-cap bullet:

```
The receive loop and the sender's `DataAvailable` handler emit once-per-second `[recv]`/`[send]` diagnostics (backlog, pkts/s, KB/s, overflow/s, resync/s) — a steadily climbing `backlog` is the signature of clock drift.
```

with:

```
The receive loop and the sender's `DataAvailable` handler build a `DiagnosticsSnapshot` once per second (backlog, pkts/s, KB/s, overflow/s, resync/s) and call `Report()`, which logs the detailed line and raises the `Diagnostics` event — a steadily climbing `backlog` is the signature of clock drift. Diagnostics are decoupled from streaming: `DiagnosticsLog` (constructed with the relative path `"diagnostics.log"`, so it sits next to the exe / `config.json`) timestamps and **enqueues** each line; a single background thread does all file IO + console mirroring and rotates at 1 MB → `diagnostics.1.log`. The audio threads never touch disk. `MainWindow` subscribes to `Diagnostics` and shows `snapshot.ToCompactLine()` in the dim `DiagnosticsText` readout under the status line (via `Dispatcher.BeginInvoke`), visible only while running.
```

- [ ] **Step 2: Build (sanity) and commit**

Run: `dotnet build "c:\Users\Andreas\Repos\AudioStreamer\AudioStreamer.csproj" -c Debug`
Expected: `0 Warning(s) / 0 Error(s)`.

```bash
git -C "c:\Users\Andreas\Repos\AudioStreamer" add CLAUDE.md
git -C "c:\Users\Andreas\Repos\AudioStreamer" commit -m "Document diagnostics logging and live readout" -m "Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Self-Review

**Spec coverage:**
- §1 `DiagnosticsLog` (enqueue + background writer, 1 MB rotation, swallowed IO, console mirror off-thread, relative path) → Task 1. ✓
- §2 `DiagnosticsSnapshot` (record + `ToLogLine`/`ToCompactLine`) → Task 2. ✓
- §2 event + `LogLine`/`Report`, routing all `Console.WriteLine`, session headers → Task 3. ✓
- §3 `MainWindow` readout (dim TextBlock, `Dispatcher.BeginInvoke`, show/hide with running state, unsubscribe on close) → Task 4. ✓
- Decoupling-from-streaming constraint → Task 1 (queue) + Task 3 (`Report` only enqueues / invokes) + Task 4 (`BeginInvoke`). ✓
- Testing items → Task 3 (file) + Task 4 (readout) manual checks. ✓
- Docs → Task 5. ✓

**Placeholder scan:** No TBD/TODO; every code step shows full code.

**Type consistency:** `DiagnosticsLog(string).Log(string)`, `DiagnosticsSnapshot(bool, double, int, long, int, int)` with `ToLogLine()`/`ToCompactLine()`, `Diagnostics` event of `Action<DiagnosticsSnapshot>`, `LogLine`/`Report`, `OnDiagnostics`, and `DiagnosticsText` are used consistently across tasks. The snapshot field order (`IsReceiver, BacklogMs, PacketsPerSec, KbPerSec, OverflowPerSec, ResyncPerSec`) matches every `new DiagnosticsSnapshot(...)` call in Task 3.
