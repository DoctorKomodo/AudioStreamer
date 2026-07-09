using System.Drawing;
using System.IO;
using System.Reflection;

namespace AudioStreamer
{
    /// <summary>
    /// Loads the embedded tray icon at a requested frame size.
    /// </summary>
    internal static class TrayIconLoader
    {
        /// <summary>
        /// Loads the icon frame matching <paramref name="desired"/>. The caller
        /// owns the returned <see cref="Icon"/> on both paths and must dispose it.
        /// </summary>
        /// <remarks>
        /// The parameterless <c>Icon(Stream)</c> constructor selects the .ico's
        /// default frame — 32x32 — leaving NotifyIcon to downscale it to tray
        /// size, which blurs the hand-tuned 16px artwork. Passing the desired
        /// size makes GDI+ pick the matching frame instead.
        /// </remarks>
        internal static Icon Load(Size desired)
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string? resName = System.Array.Find(asm.GetManifestResourceNames(),
                    n => n.EndsWith("icon.ico", System.StringComparison.OrdinalIgnoreCase));

                if (resName is not null)
                {
                    using Stream? stream = asm.GetManifestResourceStream(resName);
                    if (stream is not null)
                        return new Icon(stream, desired);
                }
            }
            catch { /* fall through to the system default */ }

            // A clone, not the shared static: the caller owns and disposes the result.
            return (Icon)SystemIcons.Application.Clone();
        }
    }
}
