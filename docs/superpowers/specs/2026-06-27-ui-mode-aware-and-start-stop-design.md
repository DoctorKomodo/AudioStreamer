# UI Mode-Aware Fields, Persistence & Start/Stop Overhaul — Design

**Date:** 2026-06-27
**Component:** `MainWindow.xaml`, `MainWindow.xaml.cs`, `AudioStreamerLogic.cs`

## Problem

The settings UI has three defects, plus the Start/Stop buttons lack state handling:

1. **Fields blank on focus loss.** `PlaceholderTextBoxStyle` drives the `TextBox.Text` property from a style setter plus an `IsKeyboardFocused` trigger that sets `Text=""`. This placeholder-via-style-setter pattern fights user input, so typed values are lost when a field loses focus.
2. **Settings never persisted.** `UpdateConfigFromUI()` runs only on Start and only mutates the in-memory `CurrentConfig`. Nothing writes `config.json` except `LoadConfig()`'s error-fallback path, so edits never survive a restart.
3. **Mode-irrelevant fields shown.** The receiver derives the audio format from the incoming packet header, so sample rate / bit depth / channels / host name / sender buffer are meaningless in Receiver mode but still displayed.
4. **Start/Stop have no state model.** Both buttons are always enabled. Clicking Start twice starts a second session (receiver: crash on port-in-use; sender: duplicate capture + leak). Bad input (e.g. malformed Host Name) crashes on the UI thread. No indication of whether a session is running.

## Field ↔ Mode mapping

Derived from actual usage in `StartSender()` / `StartReceiver()`:

| Setting | Sender | Receiver |
|---|---|---|
| Mode | ✓ | ✓ |
| Port | ✓ | ✓ |
| Start Minimized | ✓ | ✓ |
| Host Name | ✓ (destination) | — (receiver binds `IPAddress.Any`) |
| Sender Audio Buffer | ✓ | — |
| Sample Rate / Bits Per Sample / Channels | ✓ (packed into header) | — (read from header) |
| Receiver Audio Buffer | — | ✓ |
| Receiver Audio Latency | — | ✓ |
| Receiver Max Latency | — | ✓ |

## Design

### 1. Layout restructure (`MainWindow.xaml`)

Replace the fixed `Grid` + 22 `RowDefinition`s with a vertical `StackPanel`; set the window `SizeToContent="Height"` (fixed width) so it resizes as fields show/hide. Structure:

```
StackPanel (root)
  StackPanel x:Name="SettingsPanel"
    Mode (label + ComboBox)
    Port (label + TextBox)
    StackPanel x:Name="SenderPanel"     // Host Name, Sender Audio Buffer, Sample Rate, Bits Per Sample, Channels
    StackPanel x:Name="ReceiverPanel"   // Receiver Audio Buffer, Receiver Audio Latency, Receiver Max Latency
    CheckBox StartMinimized
  TextBlock x:Name="StatusText"
  StackPanel (horizontal): StartButton, StopButton
```

Wrapping all inputs in `SettingsPanel` makes "lock config while running" a single `IsEnabled` toggle.

### 2. Remove the placeholder style

Delete `PlaceholderTextBoxStyle` and its `Style="{StaticResource ...}"` / `Tag="..."` references. Fields are always pre-filled from config by `PopulateUIFromConfig()`, so no watermark is needed. Typed text becomes an ordinary local value that survives focus loss — fixing defect #1.

### 3. Mode-aware visibility (`MainWindow.xaml.cs`)

`ModeComboBox.SelectionChanged` handler sets `SenderPanel.Visibility` / `ReceiverPanel.Visibility` (`Visible` / `Collapsed`) from the selected mode. Collapsed children take zero height; `SizeToContent` shrinks the window. `PopulateUIFromConfig()` sets the initial visibility to match the loaded mode. Hidden fields retain their text, so `UpdateConfigFromUI()` still reads them and switching modes is non-destructive.

### 4. Persistence

Add `AudioStreamerLogic.SaveConfig()` — serialize `CurrentConfig` to `config.json` (same writer as `LoadConfig`'s fallback: `JsonConvert.SerializeObject(..., Formatting.Indented)`). Wire it to:
- **Start button:** `UpdateConfigFromUI()` → `SaveConfig()` → `Start()`.
- **Window `Closing`:** `UpdateConfigFromUI()` → `SaveConfig()` → `Stop()` (tidy socket teardown).

Harden `UpdateConfigFromUI()` to use `int.TryParse` (keep the existing config value when a field is blank/invalid) so saving mid-edit cannot throw.

### 5. Running-state model (`AudioStreamerLogic.cs`)

Add `public bool IsRunning { get; private set; }`.

```
Start():
  if (IsRunning) return;                       // guard: ignore double-start
  try   { StartSender()/StartReceiver();
          IsRunning = true; }
  catch { Stop();                              // clean up partial session
          throw; }                             // surface to UI
Stop():  // existing teardown, plus:
  IsRunning = false;
```

Synchronous failure points (receiver port-in-use, sender `IPAddress.Parse`, `StartRecording` with no device) throw before any background task starts, so they are caught by `Start()`.

### 6. Start/Stop UI behaviour (`MainWindow.xaml.cs`)

`StartButton_Click`:
```
UpdateConfigFromUI();
SaveConfig();
try { audioStreamerLogic.Start(); SetRunningState(true); }
catch (Exception ex) {
    MessageBox.Show($"Could not start: {ex.Message}", "AudioStreamer",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
    SetRunningState(false);
}
```

`StopButton_Click`: `audioStreamerLogic.Stop(); SetRunningState(false);`

`SetRunningState(bool running)` helper:
- `StartButton.IsEnabled = !running`
- `StopButton.IsEnabled = running`
- `SettingsPanel.IsEnabled = !running`
- `StatusText.Text = running ? $"Running ({CurrentConfig.Mode}) on port {CurrentConfig.Port}" : "Idle"`

Called from the constructor/`PopulateUIFromConfig()` with `false`, after a successful Start with `true`, and from Stop with `false`.

## Out of scope

- Per-field validation UI (beyond the parse-failure / start-failure messages).
- Live save-on-keystroke.
- Separate Sender/Receiver windows.
- Recovery from *background-thread* session failure (e.g. audio device removed mid-stream): the UI will still show "Running". Surfacing async failures is deferred.
- Hostname (DNS) support for the sender target — `IPAddress.Parse` still requires an IP literal; a non-IP host produces a friendly start-failure message rather than resolving.

## Testing

No automated test project exists. Verify by running the app (`dotnet run`, console attached for diagnostics):
1. Edit a field, click away → value persists (no blanking).
2. Edit fields, click Start, close, reopen → values restored from `config.json`.
3. Switch Mode → sender-only / receiver-only fields show/hide; window resizes.
4. Click Start → Start disabled, Stop enabled, settings greyed, status shows "Running (…)". Click Stop → reverts to idle.
5. Click Start twice → no second session / no crash.
6. Enter an invalid Host Name in Sender mode, click Start → message box, app stays idle.
