# UI Mode-Aware Fields, Persistence & Start/Stop — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the settings UI (blanking fields, no persistence, mode-irrelevant fields) and give Start/Stop a real running-state model with crash-proof start.

**Architecture:** All UI inputs live in one `SettingsPanel` (with nested `SenderPanel` / `ReceiverPanel` toggled by mode); the window auto-sizes. `AudioStreamerLogic` gains `SaveConfig()` and an `IsRunning` flag with a guarded, exception-safe `Start()`. The code-behind wires persistence (on Start + on close) and a single `SetRunningState()` helper that drives button/field enablement and a status line.

**Tech Stack:** C# / .NET 10, WPF, NAudio, Newtonsoft.Json.

## Global Constraints

- Target framework `net10.0-windows`; nullable reference types enabled — keep the build at **0 warnings, 0 errors**.
- No test project and **no git repo**: each task ends with `dotnet build` (must report `0 Warning(s) / 0 Error(s)`) followed by the listed **manual check**. There are **no commit steps**.
- `config.json` is read/written relative to the working directory (project root under `dotnet run`).
- Preserve every existing control `x:Name` so the code-behind keeps compiling.
- Do not reintroduce `Console.ReadKey` loops; existing `Console.WriteLine` diagnostics stay.

Build command used throughout:
`dotnet build "c:\Users\Andreas\Repos\AudioStreamer\AudioStreamer.csproj" -c Debug`

---

### Task 1: Logic layer — `SaveConfig()`, `IsRunning`, guarded `Start()`

**Files:**
- Modify: `c:\Users\Andreas\Repos\AudioStreamer\AudioStreamerLogic.cs`

**Interfaces:**
- Produces: `public bool IsRunning { get; private set; }`, `public void SaveConfig()`, and a `Start()` that no-ops when already running and cleans up + rethrows on failure. Consumed by the code-behind in Tasks 4–5.

- [ ] **Step 1: Add the `IsRunning` property**

After the session fields (just below `private WasapiOut? wasapiOut;`), add:

```csharp
        public bool IsRunning { get; private set; }
```

- [ ] **Step 2: Add `SaveConfig()` and reuse it in `LoadConfig()`**

Replace the existing `LoadConfig()` method with:

```csharp
        private void LoadConfig()
        {
            try
            {
                string configText = File.ReadAllText("config.json");
                if (!string.IsNullOrEmpty(configText))
                {
                    CurrentConfig = JsonConvert.DeserializeObject<Config>(configText) ?? new Config();
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
            File.WriteAllText("config.json", JsonConvert.SerializeObject(CurrentConfig, Newtonsoft.Json.Formatting.Indented));
        }
```

- [ ] **Step 3: Guard and harden `Start()`**

Replace the existing `Start()` method with:

```csharp
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
                IsRunning = true;
            }
            catch
            {
                Stop();   // tear down any partially-created session
                throw;    // let the UI surface the failure
            }
        }
```

- [ ] **Step 4: Clear `IsRunning` in `Stop()`**

In `Stop()`, after the final `cts = null;` line, add:

```csharp
            IsRunning = false;
```

- [ ] **Step 5: Build**

Run: `dotnet build "c:\Users\Andreas\Repos\AudioStreamer\AudioStreamer.csproj" -c Debug`
Expected: `0 Warning(s) / 0 Error(s)`.

- [ ] **Step 6: Manual check**

No behavioural change yet (UI doesn't call the new members). Build success is the deliverable for this task.

---

### Task 2: Layout restructure + remove placeholder style (`MainWindow.xaml`)

**Files:**
- Modify: `c:\Users\Andreas\Repos\AudioStreamer\MainWindow.xaml`

**Interfaces:**
- Produces named containers `SettingsPanel`, `SenderPanel`, `ReceiverPanel`, and `StatusText`; removes `PlaceholderTextBoxStyle`. Consumed by Tasks 3–5. The `ModeComboBox` `SelectionChanged` wiring is intentionally deferred to Task 3 so this task compiles on its own.

- [ ] **Step 1: Replace the entire `MainWindow.xaml` body**

Replace the whole file with:

```xml
<Window x:Class="AudioStreamer.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
        xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
        xmlns:local="clr-namespace:AudioStreamer"
        mc:Ignorable="d"
        Title="AudioStreamer" Width="220" SizeToContent="Height" ResizeMode="CanMinimize">
    <StackPanel Margin="6">
        <StackPanel x:Name="SettingsPanel">

            <TextBlock Text="Mode (Sender/Receiver)" Margin="1"/>
            <ComboBox x:Name="ModeComboBox" Width="200" Margin="1" HorizontalAlignment="Left">
                <ComboBoxItem Content="Sender"/>
                <ComboBoxItem Content="Receiver"/>
            </ComboBox>

            <TextBlock Text="Port" Margin="1"/>
            <TextBox x:Name="PortTextBox" Width="200" Margin="1" HorizontalAlignment="Left"/>

            <StackPanel x:Name="SenderPanel">
                <TextBlock Text="Host Name" Margin="1"/>
                <TextBox x:Name="HostNameTextBox" Width="200" Margin="1" HorizontalAlignment="Left"/>

                <TextBlock Text="Sender Audio Buffer (ms)" Margin="1"/>
                <TextBox x:Name="SenderAudioBufferTextBox" Width="200" Margin="1" HorizontalAlignment="Left"/>

                <TextBlock Text="Sample Rate" Margin="1"/>
                <TextBox x:Name="SampleRateTextBox" Width="200" Margin="1" HorizontalAlignment="Left"/>

                <TextBlock Text="Bits Per Sample" Margin="1"/>
                <TextBox x:Name="BitsPerSampleTextBox" Width="200" Margin="1" HorizontalAlignment="Left"/>

                <TextBlock Text="Channels" Margin="1"/>
                <TextBox x:Name="ChannelsTextBox" Width="200" Margin="1" HorizontalAlignment="Left"/>
            </StackPanel>

            <StackPanel x:Name="ReceiverPanel">
                <TextBlock Text="Receiver Audio Buffer (ms)" Margin="1"/>
                <TextBox x:Name="ReceiverAudioBufferTextBox" Width="200" Margin="1" HorizontalAlignment="Left"/>

                <TextBlock Text="Receiver Audio Latency (ms)" Margin="1"/>
                <TextBox x:Name="ReceiverAudioLatencyTextBox" Width="200" Margin="1" HorizontalAlignment="Left"/>

                <TextBlock Text="Receiver Max Latency (ms)" Margin="1"/>
                <TextBox x:Name="ReceiverMaxLatencyTextBox" Width="200" Margin="1" HorizontalAlignment="Left"/>
            </StackPanel>

            <CheckBox x:Name="StartMinimizedCheckBox" Content="Start Minimized" Margin="1"/>
        </StackPanel>

        <TextBlock x:Name="StatusText" Text="Idle" Margin="1,6"/>

        <StackPanel Orientation="Horizontal" Margin="1">
            <Button x:Name="StartButton" Content="Start" Width="100" Margin="1" Click="StartButton_Click"/>
            <Button x:Name="StopButton" Content="Stop" Width="100" Margin="1" Click="StopButton_Click"/>
        </StackPanel>
    </StackPanel>
</Window>
```

- [ ] **Step 2: Build**

Run: `dotnet build "c:\Users\Andreas\Repos\AudioStreamer\AudioStreamer.csproj" -c Debug`
Expected: `0 Warning(s) / 0 Error(s)`. (`StartButton_Click` / `StopButton_Click` handlers already exist in the code-behind.)

- [ ] **Step 3: Manual check — blanking fixed**

Run: `dotnet run --project "c:\Users\Andreas\Repos\AudioStreamer\AudioStreamer.csproj"`
- All fields show their config values on launch.
- Click into a field, type, click another field: **the typed value stays** (no blanking).
- Both Sender and Receiver fields are visible for now (toggle comes in Task 3).
Close the app.

---

### Task 3: Mode-aware panel visibility (`MainWindow.xaml` + `MainWindow.xaml.cs`)

**Files:**
- Modify: `c:\Users\Andreas\Repos\AudioStreamer\MainWindow.xaml` (add `SelectionChanged` to `ModeComboBox`)
- Modify: `c:\Users\Andreas\Repos\AudioStreamer\MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `SenderPanel`, `ReceiverPanel`, `ModeComboBox` (Task 2).
- Produces: `ModeComboBox_SelectionChanged` handler and `UpdateModePanels()`. The handler name must match the XAML attribute exactly.

- [ ] **Step 1: Wire the `SelectionChanged` event in XAML**

In `MainWindow.xaml`, change the `ModeComboBox` opening tag to:

```xml
            <ComboBox x:Name="ModeComboBox" Width="200" Margin="1" HorizontalAlignment="Left"
                      SelectionChanged="ModeComboBox_SelectionChanged">
```

- [ ] **Step 2: Add the handler and helper in the code-behind**

In `MainWindow.xaml.cs`, add these two methods inside the `MainWindow` class (e.g. after `PopulateUIFromConfig`):

```csharp
        private void ModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateModePanels();
        }

        private void UpdateModePanels()
        {
            bool isSender = (ModeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() == "Sender";
            SenderPanel.Visibility = isSender ? Visibility.Visible : Visibility.Collapsed;
            ReceiverPanel.Visibility = isSender ? Visibility.Collapsed : Visibility.Visible;
        }
```

- [ ] **Step 3: Set initial visibility on load**

At the end of `PopulateUIFromConfig()`, add:

```csharp
            UpdateModePanels();
```

- [ ] **Step 4: Build**

Run: `dotnet build "c:\Users\Andreas\Repos\AudioStreamer\AudioStreamer.csproj" -c Debug`
Expected: `0 Warning(s) / 0 Error(s)`.

- [ ] **Step 5: Manual check — fields toggle by mode**

Run the app. With Mode = Receiver, only Port + the three Receiver fields show; Host Name / Sender Buffer / Sample Rate / Bits / Channels are hidden, and the window is shorter. Switch Mode to Sender: sender fields appear, receiver fields hide, window resizes. Close the app.

---

### Task 4: Input hardening + persistence on close (`MainWindow.xaml.cs`)

**Files:**
- Modify: `c:\Users\Andreas\Repos\AudioStreamer\MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `AudioStreamerLogic.SaveConfig()`, `Stop()` (Task 1).
- Produces: a `ParseOr` helper, a hardened `UpdateConfigFromUI()`, and a `Window_Closing` handler wired in the constructor.

- [ ] **Step 1: Add a safe-parse helper**

In `MainWindow.xaml.cs`, add inside the class:

```csharp
        private static int ParseOr(string text, int fallback) => int.TryParse(text, out int value) ? value : fallback;
```

- [ ] **Step 2: Replace `UpdateConfigFromUI()` to use `TryParse`**

Replace the entire `UpdateConfigFromUI()` method with:

```csharp
        private void UpdateConfigFromUI()
        {
            var cfg = audioStreamerLogic.CurrentConfig;
            cfg.Mode = Enum.Parse<AudioStreamerLogic.ModeType>(((ComboBoxItem)ModeComboBox.SelectedItem).Content?.ToString() ?? nameof(AudioStreamerLogic.ModeType.Receiver));
            cfg.HostName = HostNameTextBox.Text;
            cfg.Port = ParseOr(PortTextBox.Text, cfg.Port);
            cfg.SenderAudioBufferMillisecondsLength = ParseOr(SenderAudioBufferTextBox.Text, cfg.SenderAudioBufferMillisecondsLength);
            cfg.ReceiverAudioBufferMillisecondsLength = ParseOr(ReceiverAudioBufferTextBox.Text, cfg.ReceiverAudioBufferMillisecondsLength);
            cfg.ReceiverAudioLatencyMilliseconds = ParseOr(ReceiverAudioLatencyTextBox.Text, cfg.ReceiverAudioLatencyMilliseconds);
            cfg.ReceiverMaxLatencyMilliseconds = ParseOr(ReceiverMaxLatencyTextBox.Text, cfg.ReceiverMaxLatencyMilliseconds);
            cfg.SampleRate = ParseOr(SampleRateTextBox.Text, cfg.SampleRate);
            cfg.BitsPerSample = ParseOr(BitsPerSampleTextBox.Text, cfg.BitsPerSample);
            cfg.Channels = ParseOr(ChannelsTextBox.Text, cfg.Channels);
            cfg.StartMinimized = StartMinimizedCheckBox.IsChecked ?? false;
        }
```

- [ ] **Step 3: Wire a `Closing` handler in the constructor**

In the `MainWindow()` constructor, after `PopulateUIFromConfig();`, add:

```csharp
            this.Closing += Window_Closing;
```

Then add the handler method to the class:

```csharp
        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            UpdateConfigFromUI();
            audioStreamerLogic.SaveConfig();
            audioStreamerLogic.Stop();
        }
```

- [ ] **Step 4: Build**

Run: `dotnet build "c:\Users\Andreas\Repos\AudioStreamer\AudioStreamer.csproj" -c Debug`
Expected: `0 Warning(s) / 0 Error(s)`.

- [ ] **Step 5: Manual check — persistence across restart**

Run the app. Change Port (e.g. to `6000`) and a Receiver field, then close the window. Reopen with `dotnet run`: the changed values are restored. Confirm `config.json` in the project root contains the new values. (Blank a numeric field then close — it must not crash; the old value is retained.)

---

### Task 5: Start/Stop running-state + crash-proof start (`MainWindow.xaml.cs`)

**Files:**
- Modify: `c:\Users\Andreas\Repos\AudioStreamer\MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `AudioStreamerLogic.Start()` (guarded, may throw), `Stop()`, `SaveConfig()`, `CurrentConfig` (Task 1); `SettingsPanel`, `StatusText`, `StartButton`, `StopButton` (Task 2).
- Produces: `SetRunningState(bool)`, rewritten `StartButton_Click` / `StopButton_Click`.

- [ ] **Step 1: Add the `SetRunningState` helper**

In `MainWindow.xaml.cs`, add inside the class:

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

- [ ] **Step 2: Replace the two button handlers**

Replace `StartButton_Click` and `StopButton_Click` with:

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

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            audioStreamerLogic.Stop();
            SetRunningState(false);
        }
```

- [ ] **Step 3: Set the initial idle state in the constructor**

In the `MainWindow()` constructor, after the `this.Closing += Window_Closing;` line from Task 4, add:

```csharp
            SetRunningState(false);
```

- [ ] **Step 4: Build**

Run: `dotnet build "c:\Users\Andreas\Repos\AudioStreamer\AudioStreamer.csproj" -c Debug`
Expected: `0 Warning(s) / 0 Error(s)`.

- [ ] **Step 5: Manual check — full Start/Stop behaviour**

Run the app (Receiver mode). Then:
- Click **Start**: Start greys out, Stop enables, all settings grey out, status shows `Running (Receiver) on port 5005`.
- Click **Stop**: returns to idle — Start enabled, settings editable, status `Idle`.
- Click **Start** twice quickly (it's disabled after the first, so no second session / no crash).
- Switch to **Sender** mode, set Host Name to an invalid value like `not.an.ip`, click **Start**: a message box explains the failure and the app stays idle (Start still enabled). Set Host Name to `127.0.0.1` and Start succeeds.
Close the app.

---

## Self-Review

**Spec coverage:**
- Defect 1 (blanking) → Task 2 (remove placeholder style). ✓
- Defect 2 (no persistence) → Task 1 (`SaveConfig`) + Task 4 (close) + Task 5 (Start). ✓
- Defect 3 (mode-irrelevant fields) → Task 2 (panels) + Task 3 (toggle). ✓
- Defect 4 (Start/Stop state) → Task 1 (`IsRunning`/guard) + Task 5 (buttons/status/lock/crash-proof). ✓
- Field↔mode mapping → Task 2 panel membership matches the spec table. ✓
- Persist on Start + on close → Task 5 / Task 4. ✓
- Lock config while running → Task 5 (`SettingsPanel.IsEnabled`). ✓
- Status indicator → Task 5 (`StatusText`). ✓

**Placeholder scan:** No TBD/TODO; every code step shows full code. ✓

**Type consistency:** `SaveConfig()`, `IsRunning`, `Start()`/`Stop()`, `UpdateModePanels()`, `ParseOr()`, `SetRunningState()`, and control names (`SettingsPanel`, `SenderPanel`, `ReceiverPanel`, `StatusText`) are used consistently across tasks. `ReceiverMaxLatencyTextBox` already exists from prior work. ✓

**Out-of-scope items** (per spec) deliberately not implemented: per-field validation UI, live save-on-keystroke, separate windows, async-failure recovery, DNS hostname support.
