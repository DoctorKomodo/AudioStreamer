# Tray Minimize, Start-Minimized & Start-With-Windows Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a system-tray icon with minimize-to-tray, make "Start Minimized" work (docking to the tray and auto-starting the session), and add a "Start with Windows" toggle to both the app and the installer.

**Architecture:** A WinForms `NotifyIcon` (enabled via `UseWindowsForms`) owned by `MainWindow` provides the tray presence; minimize hides the window, the X button exits. A new registry-backed `StartupService` manages the HKCU `Run` value behind a UI checkbox, and the installer writes the same value via a `[Tasks]` opt-in. Start-minimized auto-starts the streaming session, routing errors to a tray balloon instead of a dialog.

**Tech Stack:** C# / .NET 10, WPF + WinForms interop, NAudio, Newtonsoft.Json, Microsoft.Win32.Registry, Inno Setup 6.

## Global Constraints

- Target framework `net10.0-windows`; nullable reference types enabled — keep the build at **0 warnings, 0 errors**.
- This is a git repo: each task ends with `dotnet build` (must report `0 Warning(s) / 0 Error(s)`), the listed **manual check**, then a **commit**. There is no test project.
- WinForms and WPF type names collide (`MessageBox`, `Application`). Keep `MessageBox` as the WPF one (from `using System.Windows;`) and **fully-qualify all WinForms types** (`System.Windows.Forms.NotifyIcon`, `System.Windows.Forms.ToolTipIcon`, etc.). Do **not** add `using System.Windows.Forms;`.
- Registry value name and format must be identical between `StartupService.Enable` and the installer's `[Registry]` entry, both: name `AudioStreamer`, data `"<quoted exe path>"`.
- Do not store the start-with-windows state in `config.json` — it is registry-backed only.

Build command used throughout:
`dotnet build "c:\Users\Andreas\Repos\AudioStreamer\AudioStreamer.csproj" -c Debug`

---

### Task 1: Enable WinForms interop + embed the tray icon

**Files:**
- Modify: `c:\Users\Andreas\Repos\AudioStreamer\AudioStreamer.csproj`

**Interfaces:**
- Produces: WinForms types available to the app; `Resources\icon.ico` embedded in the assembly (logical name ends with `icon.ico`). Consumed by Task 3.

- [ ] **Step 1: Add `UseWindowsForms` and embed the icon**

In `AudioStreamer.csproj`, change the first `<PropertyGroup>` to add `UseWindowsForms`:

```xml
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
```

And add a new `<ItemGroup>` after the `PackageReference` group:

```xml
  <ItemGroup>
    <EmbeddedResource Include="Resources\icon.ico" />
  </ItemGroup>
```

- [ ] **Step 2: Build**

Run: `dotnet build "c:\Users\Andreas\Repos\AudioStreamer\AudioStreamer.csproj" -c Debug`
Expected: `0 Warning(s) / 0 Error(s)`.

- [ ] **Step 3: Manual check**

No behavioural change. Build success is the deliverable.

- [ ] **Step 4: Commit**

```bash
git -C "c:\Users\Andreas\Repos\AudioStreamer" add AudioStreamer.csproj
git -C "c:\Users\Andreas\Repos\AudioStreamer" commit -m "Enable WinForms interop and embed tray icon" -m "Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: `StartupService` (HKCU Run-key wrapper)

**Files:**
- Create: `c:\Users\Andreas\Repos\AudioStreamer\StartupService.cs`

**Interfaces:**
- Produces: `public sealed class StartupService` with `bool IsEnabled { get; }`, `void Enable(string exePath)`, `void Disable()`. Value name `AudioStreamer` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Consumed by Task 5.

- [ ] **Step 1: Create the file**

Create `StartupService.cs`:

```csharp
using Microsoft.Win32;

namespace AudioStreamer
{
    /// <summary>
    /// Manages the HKCU\...\Run registry entry that launches AudioStreamer at login.
    /// The state lives only in the registry (not config.json) so it can't drift from
    /// what the installer's startup task or another tool may have written.
    /// </summary>
    public sealed class StartupService
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "AudioStreamer";

        /// <summary>True when the AudioStreamer Run value exists.</summary>
        public bool IsEnabled
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(AppName) is not null;
            }
        }

        /// <summary>Writes the Run value pointing to <paramref name="exePath"/>.</summary>
        public void Enable(string exePath)
        {
            // Quote the path so spaces parse correctly when Windows runs it at login.
            string quotedPath = exePath.Contains('"') ? exePath : $"\"{exePath}\"";
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key.SetValue(AppName, quotedPath);
        }

        /// <summary>Removes the Run value. Safe to call when already disabled.</summary>
        public void Disable()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build "c:\Users\Andreas\Repos\AudioStreamer\AudioStreamer.csproj" -c Debug`
Expected: `0 Warning(s) / 0 Error(s)`.

- [ ] **Step 3: Manual check**

No UI yet; it's wired in Task 5. Build success is the deliverable.

- [ ] **Step 4: Commit**

```bash
git -C "c:\Users\Andreas\Repos\AudioStreamer" add StartupService.cs
git -C "c:\Users\Andreas\Repos\AudioStreamer" commit -m "Add StartupService for HKCU Run-key management" -m "Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: Tray icon, minimize-to-tray, restore, exit

**Files:**
- Modify: `c:\Users\Andreas\Repos\AudioStreamer\MainWindow.xaml.cs`

**Interfaces:**
- Consumes: embedded `icon.ico` (Task 1).
- Produces: `private System.Windows.Forms.NotifyIcon? trayIcon;`, `SetupTrayIcon()`, `ShowFromTray()`, `MainWindow_StateChanged(...)`. `SetRunningState` updated to refresh the tray tooltip. `Window_Closing` disposes the tray icon. Consumed by Task 4 (`trayIcon` for balloons).

- [ ] **Step 1: Create the tray icon in the constructor**

Replace the constructor body:

```csharp
        public MainWindow()
        {
            InitializeComponent();
            audioStreamerLogic = new AudioStreamerLogic();
            PopulateUIFromConfig();
            this.Closing += Window_Closing;
            SetRunningState(false);
        }
```

with:

```csharp
        private System.Windows.Forms.NotifyIcon? trayIcon;

        public MainWindow()
        {
            InitializeComponent();
            audioStreamerLogic = new AudioStreamerLogic();
            SetupTrayIcon();
            PopulateUIFromConfig();
            this.Closing += Window_Closing;
            this.StateChanged += MainWindow_StateChanged;
            SetRunningState(false);
        }

        private void SetupTrayIcon()
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            string? resName = System.Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith("icon.ico", System.StringComparison.OrdinalIgnoreCase));
            System.Drawing.Icon icon = resName is not null
                ? new System.Drawing.Icon(asm.GetManifestResourceStream(resName)!)
                : System.Drawing.SystemIcons.Application;

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Show AudioStreamer", null, (s, e) => ShowFromTray());
            menu.Items.Add("Exit", null, (s, e) => Close());

            trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = icon,
                Visible = true,
                Text = "AudioStreamer — Idle",
                ContextMenuStrip = menu
            };
            trayIcon.DoubleClick += (s, e) => ShowFromTray();
        }

        private void MainWindow_StateChanged(object? sender, System.EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
                Hide();   // remove the taskbar button; the tray icon remains
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }
```

- [ ] **Step 2: Dispose the tray icon on exit**

Replace `Window_Closing`:

```csharp
        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            UpdateConfigFromUI();
            audioStreamerLogic.SaveConfig();
            audioStreamerLogic.Stop();
        }
```

with:

```csharp
        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            UpdateConfigFromUI();
            audioStreamerLogic.SaveConfig();
            audioStreamerLogic.Stop();
            trayIcon?.Dispose();
            trayIcon = null;
        }
```

- [ ] **Step 3: Update the tray tooltip from `SetRunningState`**

Replace `SetRunningState`:

```csharp
        private void SetRunningState(bool running)
        {
            StartButton.IsEnabled = !running;
            StopButton.IsEnabled = running;
            SettingsPanel.IsEnabled = !running;
            StatusText.Text = running
                ? $"Running ({audioStreamerLogic.CurrentConfig.Mode}) on port {audioStreamerLogic.CurrentConfig.Port}"
                : "Idle";
        }
```

with (adds the tray tooltip line):

```csharp
        private void SetRunningState(bool running)
        {
            StartButton.IsEnabled = !running;
            StopButton.IsEnabled = running;
            SettingsPanel.IsEnabled = !running;
            StatusText.Text = running
                ? $"Running ({audioStreamerLogic.CurrentConfig.Mode}) on port {audioStreamerLogic.CurrentConfig.Port}"
                : "Idle";
            if (trayIcon is not null)
                trayIcon.Text = running
                    ? $"AudioStreamer — Running ({audioStreamerLogic.CurrentConfig.Mode})"
                    : "AudioStreamer — Idle";
        }
```

- [ ] **Step 4: Build**

Run: `dotnet build "c:\Users\Andreas\Repos\AudioStreamer\AudioStreamer.csproj" -c Debug`
Expected: `0 Warning(s) / 0 Error(s)`.

- [ ] **Step 5: Manual check**

Run: `dotnet run --project "c:\Users\Andreas\Repos\AudioStreamer\AudioStreamer.csproj"`
- A tray icon appears (notification area). Hovering shows `AudioStreamer — Idle`.
- Click the window's **minimize** button → window disappears from the taskbar; tray icon stays.
- **Double-click** the tray icon (or right-click → Show AudioStreamer) → window returns.
- Click **X** → app exits and the tray icon disappears.

- [ ] **Step 6: Commit**

```bash
git -C "c:\Users\Andreas\Repos\AudioStreamer" add MainWindow.xaml.cs
git -C "c:\Users\Andreas\Repos\AudioStreamer" commit -m "Add tray icon with minimize-to-tray and exit" -m "Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: Refactor Start into `StartSession`; start-minimized auto-start

**Files:**
- Modify: `c:\Users\Andreas\Repos\AudioStreamer\MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `trayIcon` (Task 3), `audioStreamerLogic.IsRunning`, `audioStreamerLogic.CurrentConfig.StartMinimized`.
- Produces: `StartSession(bool showErrorsAsDialog)`, `MainWindow_Loaded(...)`. `StartButton_Click` delegates to `StartSession(true)`.

- [ ] **Step 1: Subscribe to `Loaded` in the constructor**

In the constructor, add after `this.StateChanged += MainWindow_StateChanged;`:

```csharp
            this.Loaded += MainWindow_Loaded;
```

- [ ] **Step 2: Replace `StartButton_Click` with the refactor + add `StartSession` and `MainWindow_Loaded`**

Replace `StartButton_Click`:

```csharp
        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateConfigFromUI();
            audioStreamerLogic.SaveConfig();
            try
            {
                audioStreamerLogic.Start();
                SetRunningState(true);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not start: {ex.Message}", "AudioStreamer", MessageBoxButton.OK, MessageBoxImage.Warning);
                SetRunningState(false);
            }
        }
```

with:

```csharp
        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            StartSession(showErrorsAsDialog: true);
        }

        private void StartSession(bool showErrorsAsDialog)
        {
            UpdateConfigFromUI();
            audioStreamerLogic.SaveConfig();
            try
            {
                audioStreamerLogic.Start();
                SetRunningState(true);
            }
            catch (Exception ex)
            {
                if (showErrorsAsDialog)
                    MessageBox.Show($"Could not start: {ex.Message}", "AudioStreamer", MessageBoxButton.OK, MessageBoxImage.Warning);
                else
                    trayIcon?.ShowBalloonTip(5000, "AudioStreamer", $"Could not start: {ex.Message}", System.Windows.Forms.ToolTipIcon.Warning);
                SetRunningState(false);
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (!audioStreamerLogic.CurrentConfig.StartMinimized)
                return;

            WindowState = WindowState.Minimized;
            Hide();   // dock straight to the tray
            StartSession(showErrorsAsDialog: false);
            if (audioStreamerLogic.IsRunning)
                trayIcon?.ShowBalloonTip(3000, "AudioStreamer", "Streaming started.", System.Windows.Forms.ToolTipIcon.Info);
        }
```

- [ ] **Step 3: Build**

Run: `dotnet build "c:\Users\Andreas\Repos\AudioStreamer\AudioStreamer.csproj" -c Debug`
Expected: `0 Warning(s) / 0 Error(s)`.

- [ ] **Step 4: Manual check**

With `config.json` `Mode` = `Receiver` (1) and `StartMinimized` = `true`, run `dotnet run`:
- No window appears; the tray icon is present.
- A "Streaming started" balloon shows; the receiver has bound its UDP port (verify: `Get-NetUDPEndpoint -OwningProcess <pid>` lists the configured port).
- Double-click the tray icon → window restores showing `Running (Receiver) …` with settings locked.
Then set `StartMinimized` back to `false` and confirm a normal launch shows the window idle.

- [ ] **Step 5: Commit**

```bash
git -C "c:\Users\Andreas\Repos\AudioStreamer" add MainWindow.xaml.cs
git -C "c:\Users\Andreas\Repos\AudioStreamer" commit -m "Auto-start session and dock to tray when Start Minimized is set" -m "Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: "Start with Windows" checkbox + layout move

**Files:**
- Modify: `c:\Users\Andreas\Repos\AudioStreamer\MainWindow.xaml`
- Modify: `c:\Users\Andreas\Repos\AudioStreamer\MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `StartupService` (Task 2).
- Produces: `StartupOptionsPanel` (always-editable), `StartWithWindowsCheckBox` with handler `StartWithWindowsCheckBox_Changed`, `startupService` field, `ExePath()` helper.

- [ ] **Step 1: Move Start Minimized out of `SettingsPanel` and add the new checkbox (XAML)**

In `MainWindow.xaml`, delete the Start Minimized checkbox line from inside `SettingsPanel`:

```xml
            <CheckBox x:Name="StartMinimizedCheckBox" Content="Start Minimized" Margin="1"/>
```

(it is the last child of `SettingsPanel`, just before that panel's closing `</StackPanel>`).

Then, between `SettingsPanel`'s closing `</StackPanel>` and the `StatusText` line, insert:

```xml
        <StackPanel x:Name="StartupOptionsPanel">
            <CheckBox x:Name="StartMinimizedCheckBox" Content="Start Minimized" Margin="1"/>
            <CheckBox x:Name="StartWithWindowsCheckBox" Content="Start with Windows" Margin="1"
                      Checked="StartWithWindowsCheckBox_Changed" Unchecked="StartWithWindowsCheckBox_Changed"/>
        </StackPanel>
```

- [ ] **Step 2: Add the `startupService` field and create it in the constructor**

In `MainWindow.xaml.cs`, change the field declaration:

```csharp
        private AudioStreamerLogic audioStreamerLogic;
```

to:

```csharp
        private AudioStreamerLogic audioStreamerLogic;
        private StartupService startupService;
        private bool suppressStartupToggle;
```

And in the constructor, add `startupService = new StartupService();` immediately after `audioStreamerLogic = new AudioStreamerLogic();` (it must exist before `PopulateUIFromConfig`):

```csharp
            audioStreamerLogic = new AudioStreamerLogic();
            startupService = new StartupService();
            SetupTrayIcon();
```

- [ ] **Step 3: Initialise the checkbox from the registry (suppressing the write-back)**

In `PopulateUIFromConfig()`, replace the existing Start Minimized line:

```csharp
            StartMinimizedCheckBox.IsChecked = audioStreamerLogic.CurrentConfig.StartMinimized;
```

with:

```csharp
            StartMinimizedCheckBox.IsChecked = audioStreamerLogic.CurrentConfig.StartMinimized;
            suppressStartupToggle = true;
            StartWithWindowsCheckBox.IsChecked = startupService.IsEnabled;
            suppressStartupToggle = false;
```

- [ ] **Step 4: Add the toggle handler and exe-path helper**

Add to the `MainWindow` class:

```csharp
        private void StartWithWindowsCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (suppressStartupToggle)
                return;
            if (StartWithWindowsCheckBox.IsChecked == true)
                startupService.Enable(ExePath());
            else
                startupService.Disable();
        }

        private static string ExePath() =>
            System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
```

- [ ] **Step 5: Build**

Run: `dotnet build "c:\Users\Andreas\Repos\AudioStreamer\AudioStreamer.csproj" -c Debug`
Expected: `0 Warning(s) / 0 Error(s)`.

- [ ] **Step 6: Manual check**

Run `dotnet run`. Tick **Start with Windows**, then verify the registry value exists:
`(Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run').AudioStreamer` → prints the quoted exe path.
Untick it → the property is gone. Start a session and confirm both startup checkboxes remain **editable** while `SettingsPanel` is greyed out.

- [ ] **Step 7: Commit**

```bash
git -C "c:\Users\Andreas\Repos\AudioStreamer" add MainWindow.xaml MainWindow.xaml.cs
git -C "c:\Users\Andreas\Repos\AudioStreamer" commit -m "Add Start with Windows toggle and move startup options out of the lock" -m "Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: Installer startup task + documentation

**Files:**
- Modify: `c:\Users\Andreas\Repos\AudioStreamer\installer\AudioStreamer.iss`
- Modify: `c:\Users\Andreas\Repos\AudioStreamer\CLAUDE.md`
- Modify: `c:\Users\Andreas\Repos\AudioStreamer\README.md`

**Interfaces:**
- Consumes: nothing (final task). The `[Registry]` value must match `StartupService` (name `AudioStreamer`, quoted exe path).

- [ ] **Step 1: Add the startup task and registry value to the installer**

In `installer\AudioStreamer.iss`, after the `[Languages]` section and before `[Files]`, insert:

```
[Tasks]
; Final-page checkbox (checked by default). The app's "Start with Windows" toggle
; manages the same registry value, so it stays in sync after installation.
Name: "startup"; \
  Description: "Start {#MyAppName} automatically when Windows starts"; \
  GroupDescription: "Startup:"
```

Then, after the `[Icons]` section, insert:

```
[Registry]
; Matches exactly what StartupService.Enable() writes, so the app's toggle reads
; the correct state immediately after the first launch.
Root: HKCU; \
  Subkey:    "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; \
  ValueName: "{#MyAppName}"; \
  ValueData: """{app}\{#MyAppExeName}"""; \
  Flags:     uninsdeletevalue; \
  Tasks:     startup
```

- [ ] **Step 2: Verify the installer compiles**

Run (publish already exists from prior builds; this compiles just the script):
`& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" "c:\Users\Andreas\Repos\AudioStreamer\installer\AudioStreamer.iss"`
Expected: `Successful compile`.

- [ ] **Step 3: Update CLAUDE.md**

In `CLAUDE.md`, append to the `MainWindow` bullet (after its existing text) the sentence:

```
A WinForms `NotifyIcon` (enabled by `<UseWindowsForms>` in the csproj, icon loaded from the embedded `Resources\icon.ico`) provides the tray: the minimize button hides the window (`StateChanged` → `Hide()`), the X button / tray **Exit** quits, and double-click / **Show** restores. `StartButton_Click` delegates to `StartSession(showErrorsAsDialog)`; on `Loaded`, if `Config.StartMinimized` the app docks to the tray and auto-starts the session, routing failures to a tray balloon instead of a dialog. **Start with Windows** is a registry-backed toggle (`StartupService`, HKCU `…\Run`, value `AudioStreamer`) — not in `config.json`; it sits with Start Minimized in `StartupOptionsPanel`, which stays editable while a session runs.
```

- [ ] **Step 4: Update README.md**

In `README.md`, under the install/usage area, add a short subsection after the "On the machine whose sound you want to send (Sender)" block (before the firewall note) — or at the end of "Using AudioStreamer":

```markdown
### Running in the background

Minimizing the window sends AudioStreamer to the **system tray** (double-click the tray icon, or right-click → **Show**, to bring it back; **Exit** quits). Tick **Start Minimized** to have it launch straight to the tray and begin streaming with your saved settings, and **Start with Windows** to launch it automatically at login. During installation you can also tick "Start AudioStreamer automatically when Windows starts" to set the same option.
```

- [ ] **Step 5: Build (sanity) and commit**

Run: `dotnet build "c:\Users\Andreas\Repos\AudioStreamer\AudioStreamer.csproj" -c Debug`
Expected: `0 Warning(s) / 0 Error(s)`.

```bash
git -C "c:\Users\Andreas\Repos\AudioStreamer" add installer/AudioStreamer.iss CLAUDE.md README.md
git -C "c:\Users\Andreas\Repos\AudioStreamer" commit -m "Add installer startup task and document tray/startup behaviour" -m "Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Self-Review

**Spec coverage:**
- §1 csproj (`UseWindowsForms`, embedded icon) → Task 1. ✓
- §2 `StartupService` → Task 2. ✓
- §3 tray icon + window state (minimize→hide, restore, X→exit, dispose) → Task 3. ✓
- §4 start-minimized + auto-start + error routing (`StartSession`) → Task 4. ✓
- §5 Start-with-Windows checkbox (init from `IsEnabled`, immediate write, `ExePath`) → Task 5. ✓
- §6 layout move to `StartupOptionsPanel` + tray tooltip in `SetRunningState` → Task 5 (panel) + Task 3 (tooltip). ✓
- §7 installer `[Tasks]`/`[Registry]` → Task 6. ✓
- Testing items 1–6 → distributed across Task 3/4/5/6 manual checks. ✓

**Placeholder scan:** No TBD/TODO; every code step shows full code.

**Type consistency:** `trayIcon` (`System.Windows.Forms.NotifyIcon?`), `StartSession(bool)`, `ShowFromTray()`, `MainWindow_StateChanged`, `MainWindow_Loaded`, `startupService` (`StartupService`), `StartWithWindowsCheckBox_Changed`, `ExePath()`, `StartupOptionsPanel`, `StartWithWindowsCheckBox` are used consistently across tasks. WinForms types fully-qualified throughout; `MessageBox` left as the WPF type. Registry value name `AudioStreamer` matches between `StartupService` (Task 2) and the installer `[Registry]` (Task 6).
