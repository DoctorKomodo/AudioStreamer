using System.Windows;
using System.Windows.Controls;

namespace AudioStreamer
{
    public partial class MainWindow : Window
    {
        private AudioStreamerLogic audioStreamerLogic;

        public MainWindow()
        {
            InitializeComponent();
            audioStreamerLogic = new AudioStreamerLogic();
            PopulateUIFromConfig();
            this.Closing += Window_Closing;
            SetRunningState(false);
        }

        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            UpdateConfigFromUI();
            audioStreamerLogic.SaveConfig();
            audioStreamerLogic.Stop();
        }

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

        private void SetRunningState(bool running)
        {
            StartButton.IsEnabled = !running;
            StopButton.IsEnabled = running;
            SettingsPanel.IsEnabled = !running;
            StatusText.Text = running
                ? $"Running ({audioStreamerLogic.CurrentConfig.Mode}) on port {audioStreamerLogic.CurrentConfig.Port}"
                : "Idle";
        }

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
            cfg.SampleRate = ParseOr(SampleRateTextBox.Text, cfg.SampleRate);
            cfg.BitsPerSample = ParseOr(BitsPerSampleTextBox.Text, cfg.BitsPerSample);
            cfg.Channels = ParseOr(ChannelsTextBox.Text, cfg.Channels);
            cfg.StartMinimized = StartMinimizedCheckBox.IsChecked ?? false;
        }

        private void PopulateUIFromConfig()
        {
            ModeComboBox.SelectedItem = ModeComboBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Content.ToString() == audioStreamerLogic.CurrentConfig.Mode.ToString());
            HostNameTextBox.Text = audioStreamerLogic.CurrentConfig.HostName;
            PortTextBox.Text = audioStreamerLogic.CurrentConfig.Port.ToString();
            SenderAudioBufferTextBox.Text = audioStreamerLogic.CurrentConfig.SenderAudioBufferMillisecondsLength.ToString();
            ReceiverAudioBufferTextBox.Text = audioStreamerLogic.CurrentConfig.ReceiverAudioBufferMillisecondsLength.ToString();
            ReceiverAudioLatencyTextBox.Text = audioStreamerLogic.CurrentConfig.ReceiverAudioLatencyMilliseconds.ToString();
            ReceiverMaxLatencyTextBox.Text = audioStreamerLogic.CurrentConfig.ReceiverMaxLatencyMilliseconds.ToString();
            SampleRateTextBox.Text = audioStreamerLogic.CurrentConfig.SampleRate.ToString();
            BitsPerSampleTextBox.Text = audioStreamerLogic.CurrentConfig.BitsPerSample.ToString();
            ChannelsTextBox.Text = audioStreamerLogic.CurrentConfig.Channels.ToString();
            StartMinimizedCheckBox.IsChecked = audioStreamerLogic.CurrentConfig.StartMinimized;
            UpdateModePanels();
        }

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
    }
}