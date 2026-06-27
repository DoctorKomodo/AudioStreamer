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
