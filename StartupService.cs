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
