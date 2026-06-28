using System.Windows;
using System.Windows.Controls;
// UseWindowsForms adds a global using for System.Windows.Forms, which makes
// MessageBox ambiguous; pin it to the WPF one.
using MessageBox = System.Windows.MessageBox;

namespace AudioStreamer
{
    public partial class MainWindow : Window
    {
        private AudioStreamerLogic audioStreamerLogic;
        private StartupService startupService;
        private bool suppressStartupToggle;
        private bool reallyExit;   // true only when the user picks Exit from the tray; otherwise X hides to tray
        private System.Windows.Forms.NotifyIcon? trayIcon;

        public MainWindow()
        {
            InitializeComponent();
            audioStreamerLogic = new AudioStreamerLogic();
            audioStreamerLogic.Diagnostics += OnDiagnostics;
            startupService = new StartupService();
            SetupTrayIcon();
            PopulateUIFromConfig();
            this.Closing += Window_Closing;
            this.StateChanged += MainWindow_StateChanged;
            this.Loaded += MainWindow_Loaded;
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
            menu.Items.Add("Exit", null, (s, e) => ExitApp());

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
            // A window first realised while minimized (StartMinimized) comes back from the tray
            // oversized with an unpainted region, because SizeToContent can't measure a minimized
            // window. Re-assert it now that the window is Normal and visible (toggle via Manual so
            // the property actually changes and triggers a fresh measure).
            SizeToContent = SizeToContent.Manual;
            SizeToContent = SizeToContent.WidthAndHeight;
            Activate();
        }

        private void OnDiagnostics(DiagnosticsSnapshot snapshot)
        {
            Dispatcher.BeginInvoke((Action)(() => DiagnosticsText.Text = snapshot.ToCompactLine()));
        }

        private void ExitApp()
        {
            reallyExit = true;
            Close();
        }

        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // The window has no minimize button (NoResize), so the X is the hide-to-tray gesture;
            // a real quit only comes from the tray Exit menu (which sets reallyExit first).
            if (!reallyExit)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            UpdateConfigFromUI();
            audioStreamerLogic.SaveConfig();
            audioStreamerLogic.Stop();
            audioStreamerLogic.Diagnostics -= OnDiagnostics;
            trayIcon?.Dispose();
            trayIcon = null;
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            StartSession(showErrorsAsDialog: true);
        }

        private void StartSession(bool showErrorsAsDialog)
        {
            UpdateConfigFromUI();
            audioStreamerLogic.SaveConfig();

            // Friendly guard for the common first-run mistake: a blank/invalid sender target.
            if (audioStreamerLogic.CurrentConfig.Mode == AudioStreamerLogic.ModeType.Sender
                && !System.Net.IPAddress.TryParse(audioStreamerLogic.CurrentConfig.HostName, out _))
            {
                ReportStartFailure("Enter the receiver's IP address (Host Name).", showErrorsAsDialog);
                return;
            }

            try
            {
                audioStreamerLogic.Start();
                SetRunningState(true);
            }
            catch (Exception ex)
            {
                ReportStartFailure($"Could not start: {ex.Message}", showErrorsAsDialog);
            }
        }

        private void ReportStartFailure(string message, bool showErrorsAsDialog)
        {
            if (showErrorsAsDialog)
                MessageBox.Show(message, "AudioStreamer", MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                trayIcon?.ShowBalloonTip(5000, "AudioStreamer", message, System.Windows.Forms.ToolTipIcon.Warning);
            SetRunningState(false);
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

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            audioStreamerLogic.Stop();
            SetRunningState(false);
        }

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
            DiagnosticsText.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            if (!running)
                DiagnosticsText.Text = string.Empty;
        }

        // Dropdown item: carries the int Config value, displays the friendly label (ToString is what the ComboBox renders).
        private sealed record FormatOption(int Value, string Label) { public override string ToString() => Label; }

        private void PopulateFormatDropdowns()
        {
            SampleRateCombo.ItemsSource = AudioFormats.SampleRates.Select(r => new FormatOption(r, AudioFormats.RateLabel(r))).ToList();
            BitDepthCombo.ItemsSource   = AudioFormats.BitDepths.Select(b => new FormatOption(b, AudioFormats.DepthLabel(b))).ToList();
            ChannelsCombo.ItemsSource   = AudioFormats.ChannelCounts.Select(c => new FormatOption(c, AudioFormats.ChannelLabel(c))).ToList();
        }

        private static void SelectValue(System.Windows.Controls.ComboBox combo, int value, int fallback)
        {
            var options = combo.ItemsSource.Cast<FormatOption>().ToList();
            combo.SelectedItem = options.FirstOrDefault(o => o.Value == value)
                              ?? options.FirstOrDefault(o => o.Value == fallback)
                              ?? options.First();   // fallback is always a catalog default, so this last leg never runs — belt-and-suspenders
        }

        private static int SelectedValue(System.Windows.Controls.ComboBox combo, int fallback) =>
            combo.SelectedItem is FormatOption o ? o.Value : fallback;

        private static int ParseOr(string text, int fallback) => int.TryParse(text, out int value) ? value : fallback;

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
            cfg.SampleRate = SelectedValue(SampleRateCombo, cfg.SampleRate);
            cfg.BitsPerSample = SelectedValue(BitDepthCombo, cfg.BitsPerSample);
            cfg.Channels = SelectedValue(ChannelsCombo, cfg.Channels);
            cfg.StartMinimized = StartMinimizedCheckBox.IsChecked ?? false;
        }

        private void PopulateUIFromConfig()
        {
            PopulateFormatDropdowns();
            ModeComboBox.SelectedItem = ModeComboBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Content.ToString() == audioStreamerLogic.CurrentConfig.Mode.ToString());
            HostNameTextBox.Text = audioStreamerLogic.CurrentConfig.HostName;
            PortTextBox.Text = audioStreamerLogic.CurrentConfig.Port.ToString();
            SenderAudioBufferTextBox.Text = audioStreamerLogic.CurrentConfig.SenderAudioBufferMillisecondsLength.ToString();
            ReceiverAudioBufferTextBox.Text = audioStreamerLogic.CurrentConfig.ReceiverAudioBufferMillisecondsLength.ToString();
            ReceiverAudioLatencyTextBox.Text = audioStreamerLogic.CurrentConfig.ReceiverAudioLatencyMilliseconds.ToString();
            ReceiverMaxLatencyTextBox.Text = audioStreamerLogic.CurrentConfig.ReceiverMaxLatencyMilliseconds.ToString();
            SelectValue(SampleRateCombo, audioStreamerLogic.CurrentConfig.SampleRate, AudioFormats.DefaultSampleRate);
            SelectValue(BitDepthCombo, audioStreamerLogic.CurrentConfig.BitsPerSample, AudioFormats.DefaultBitDepth);
            SelectValue(ChannelsCombo, audioStreamerLogic.CurrentConfig.Channels, AudioFormats.DefaultChannels);
            StartMinimizedCheckBox.IsChecked = audioStreamerLogic.CurrentConfig.StartMinimized;
            suppressStartupToggle = true;
            StartWithWindowsCheckBox.IsChecked = startupService.IsEnabled;
            suppressStartupToggle = false;
            UpdateModePanels();
        }

        private void ModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateModePanels();
        }

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

        private void UpdateModePanels()
        {
            bool isSender = (ModeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() == "Sender";
            SenderPanel.Visibility = isSender ? Visibility.Visible : Visibility.Collapsed;
            ReceiverPanel.Visibility = isSender ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}