# Tray Minimize, Start-Minimized & Start-With-Windows — Design

**Date:** 2026-06-27
**Component:** `AudioStreamer.csproj`, `MainWindow.xaml`, `MainWindow.xaml.cs`, new `StartupService.cs`, `installer/AudioStreamer.iss`

## Problem

- The **Start Minimized** checkbox is persisted to `config.json` but has no effect.
- There is no system-tray presence; minimizing only goes to the taskbar.
- There is no way to launch the app at Windows login.

## Decisions (from brainstorming)

- **Close (X)** exits the app; the **minimize** button hides to the tray.
- When the app launches **minimized**, it **auto-starts** the streaming session using saved settings.
- Tray menu is minimal: **Show AudioStreamer** + **Exit** (double-click also shows).
- Single-instance enforcement and tray Start/Stop items are **out of scope**.

## Design

### 1. Project changes (`AudioStreamer.csproj`)

- Add `<UseWindowsForms>true</UseWindowsForms>` alongside `<UseWPF>true</UseWPF>` so `System.Windows.Forms.NotifyIcon` is available (no NuGet dependency).
- Add `<EmbeddedResource Include="Resources\icon.ico" />` so the tray icon can be loaded from the assembly manifest stream at runtime. (`<ApplicationIcon>` stays — it's the exe/window icon.)

### 2. `StartupService.cs` (new, UI-independent)

Adapted from AudioLeash. Thin wrapper over `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value name `AudioStreamer`:

```csharp
public sealed class StartupService
{
    public bool IsEnabled { get; }            // value exists?
    public void Enable(string exePath);       // writes "exePath" (quoted)
    public void Disable();                    // removes value (safe if absent)
}
```

`Enable` quotes the path so spaces parse correctly at login. `IsEnabled` checks only for the value's existence, so it agrees with whatever the installer wrote.

### 3. Tray icon + window state (`MainWindow.xaml.cs`)

`MainWindow` owns a `NotifyIcon` (created in the constructor, disposed in `Window_Closing`):
- Icon loaded from the embedded `icon.ico`; `Text` reflects state (see §6).
- `ContextMenuStrip` with **Show AudioStreamer** (→ `ShowFromTray()`) and **Exit** (→ `this.Close()`).
- `DoubleClick` → `ShowFromTray()`.

Window behaviour:
- `StateChanged`: when `WindowState == Minimized`, call `Hide()` (removes the taskbar button; tray icon remains).
- `ShowFromTray()`: `Show()`; `WindowState = WindowState.Normal`; `Activate()`.
- **Close (X)** uses the default WPF path: closing the only window shuts the app down. `Window_Closing` already does `UpdateConfigFromUI` → `SaveConfig` → `Stop`; it gains `notifyIcon.Dispose()`. Tray **Exit** calls `this.Close()` so the same teardown runs.
- `ShutdownMode` stays the default `OnLastWindowClose`. Minimize *hides* (does not close) the window, so the app keeps running while docked.

### 4. Start-minimized + auto-start

Refactor the current `StartButton_Click` body into:

```csharp
private void StartSession(bool showErrorsAsDialog)
{
    UpdateConfigFromUI();
    audioStreamerLogic.SaveConfig();
    try { audioStreamerLogic.Start(); SetRunningState(true); }
    catch (Exception ex)
    {
        if (showErrorsAsDialog)
            MessageBox.Show($"Could not start: {ex.Message}", "AudioStreamer",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            notifyIcon.ShowBalloonTip(5000, "AudioStreamer",
                            $"Could not start: {ex.Message}", ToolTipIcon.Warning);
        SetRunningState(false);
    }
}
```

- `StartButton_Click` → `StartSession(showErrorsAsDialog: true)`.
- `Window_Loaded`: if `CurrentConfig.StartMinimized`, set `WindowState = Minimized` (→ hides to tray) and call `StartSession(showErrorsAsDialog: false)`. Otherwise show normally. On auto-start success, an optional `"Streaming started"` balloon.

### 5. Start-with-Windows toggle (UI)

A new **Start with Windows** `CheckBox`:
- Initialized in `PopulateUIFromConfig()` from `startupService.IsEnabled` (suppressing the change handler during init so it doesn't write back).
- On `Checked`/`Unchecked`, calls `startupService.Enable(ExePath())` / `Disable()` immediately, where `ExePath()` is `Process.GetCurrentProcess().MainModule!.FileName`.
- Registry-backed, **not** stored in `config.json`.

### 6. Layout (`MainWindow.xaml`)

Move **Start Minimized** out of `SettingsPanel` into a new `StartupOptionsPanel` (a `StackPanel` placed between `SettingsPanel` and `StatusText`) together with the new **Start with Windows** checkbox. `SetRunningState` continues to lock only `SettingsPanel`, so both startup options stay editable while a session runs.

`SetRunningState` also updates the tray tooltip:
`notifyIcon.Text = running ? $"AudioStreamer — Running ({Mode})" : "AudioStreamer — Idle"` (kept under the 63-char `NotifyIcon.Text` limit).

### 7. Installer (`installer/AudioStreamer.iss`)

Add, mirroring AudioLeash:

```
[Tasks]
Name: "startup"; Description: "Start {#MyAppName} automatically when Windows starts"; \
  GroupDescription: "Startup:"

[Registry]
Root: HKCU; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "{#MyAppName}"; \
  ValueData: """{app}\{#MyAppExeName}"""; \
  Flags: uninsdeletevalue; Tasks: startup
```

The written value matches `StartupService.Enable`'s format, so the app's checkbox reflects the correct state on first launch.

## Out of scope

- Single-instance enforcement (a second launch can still start a duplicate process).
- Start/Stop items in the tray menu.
- Tray balloon notifications beyond auto-start success/failure.

## Testing

Manual (no test project), via `dotnet run` and Windows UI Automation where feasible:

1. **Start Minimized off:** launch → window visible, idle. (unchanged)
2. **Minimize → tray:** click minimize → window hidden, no taskbar button, tray icon present. Double-click tray → window restored.
3. **Close (X):** exits the app; tray icon removed; `config.json` saved.
4. **Start Minimized on + auto-start:** set the checkbox, close, relaunch → no visible window, tray icon present, session running (receiver: UDP port bound). Restore from tray shows the running state.
5. **Start with Windows toggle:** check → `HKCU\…\Run\AudioStreamer` value present; uncheck → value removed. Verify via registry.
6. **Installer task:** running setup with the startup task ticked writes the same Run value; the app's checkbox shows checked on first launch.
