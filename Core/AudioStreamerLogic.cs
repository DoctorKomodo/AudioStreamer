using System.IO;
using System.Text.Json;

namespace AudioStreamer
{
    public class AudioStreamerLogic
    {
        public enum ModeType
        {
            Sender,
            Receiver
        }
        public class Config
        {
            public ModeType Mode { get; set; } = ModeType.Receiver;
            public string HostName { get; set; } = "";   // blank by default so first-run users must enter the receiver's IP
            public int Port { get; set; } = 5005;
            public int SenderAudioBufferMillisecondsLength { get; set; } = 100;
            public int ReceiverAudioBufferMillisecondsLength { get; set; } = 1000;
            public int ReceiverAudioLatencyMilliseconds { get; set; } = 20;
            public int ReceiverMaxLatencyMilliseconds { get; set; } = 150;   // drift cap; trims backlog to half this. 150 keeps lip-sync tight while leaving jitter headroom (400 was too laggy in the field)
            public int SampleRate { get; set; } = 48000;
            public int BitsPerSample { get; set; } = 16;
            // 0 == AudioFormats.AutoChannels: match the render endpoint's mix-format channel count. Existing
            // config.json files keep their stored explicit value (LoadConfig only adds missing fields).
            public int Channels { get; set; } = 0;
            public bool StartMinimized { get; set; } = false;   // first run shows the window so it can be configured before starting
        }

        public Config CurrentConfig { get; set; } = new();

        // The active session (sender or receiver). Null when idle. IsRunning reflects the session's own flag.
        private IStreamSession? session;
        public bool IsRunning => session?.IsRunning ?? false;

        public event Action<DiagnosticsSnapshot>? Diagnostics;

        // Raised by a session while it is retrying to acquire its audio device (true) and when it resumes
        // streaming (false), so the UI can show "waiting for audio device" instead of a misleading "Running".
        public event Action<bool>? WaitingForDevice;

        // App-data files live next to the executable, NOT in the current working directory. At login via the
        // HKCU \Run key ("start with Windows") the CWD is C:\Windows\system32 — a Run value can't carry a working
        // directory — so a relative "config.json"/"diagnostics.log" would resolve there and the config write would
        // throw UnauthorizedAccessException, crashing startup. AppContext.BaseDirectory is the install dir
        // (writable per-user %LOCALAPPDATA%\AudioStreamer) and equals the CWD for manual / dotnet run launches, so
        // manual behaviour is unchanged.
        private static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        private readonly DiagnosticsLog diagnosticsLog = new(Path.Combine(AppContext.BaseDirectory, "diagnostics.log"));

        // Cached because System.Text.Json recommends reusing options instances. Indented to keep
        // config.json hand-editable; default enum/property handling matches the old Newtonsoft output
        // (PascalCase names, Mode as an integer) so existing config files still load unchanged.
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private void LogLine(string line) => diagnosticsLog.Log(line);

        /// <summary>Lets the UI record startup/decision breadcrumbs to diagnostics.log so an auto-start that
        /// bails (e.g. StartMinimized off, or a start failure) leaves a trace instead of an empty log.</summary>
        public void LogDiagnostic(string line) => LogLine(line);

        private void ReportWaiting(bool waiting) => WaitingForDevice?.Invoke(waiting);

        private void Report(DiagnosticsSnapshot snapshot)
        {
            LogLine(snapshot.ToLogLine());
            Diagnostics?.Invoke(snapshot);
        }

        public AudioStreamerLogic()
        {
            LoadConfig();
        }

        public void Start()
        {
            if (IsRunning)
                return;

            IStreamSession s = CurrentConfig.Mode == ModeType.Sender
                ? new SenderSession(CurrentConfig, LogLine, Report, ReportWaiting)
                : new ReceiverSession(CurrentConfig, LogLine, Report);
            try
            {
                s.Start();
                session = s;
                LogLine($"=== session started: {CurrentConfig.Mode} on port {CurrentConfig.Port} ===");
            }
            catch
            {
                s.Stop();   // tear down any partially-created session
                throw;      // let the UI surface the failure
            }
        }

        public void Stop()
        {
            var s = session;
            session = null;
            if (s == null)
                return;
            s.Stop();
            LogLine("=== session stopped ===");
        }

        private void LoadConfig()
        {
            try
            {
                string configText = File.ReadAllText(ConfigPath);
                if (!string.IsNullOrEmpty(configText))
                {
                    CurrentConfig = JsonSerializer.Deserialize<Config>(configText) ?? new Config();
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
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(CurrentConfig, JsonOptions));
        }
    }
}
