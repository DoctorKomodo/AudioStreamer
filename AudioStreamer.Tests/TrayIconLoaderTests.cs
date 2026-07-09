using System.Drawing;
using Xunit;

namespace AudioStreamer.Tests
{
    public class TrayIconLoaderTests
    {
        [Theory]
        [InlineData(16)]
        [InlineData(32)]
        [InlineData(48)]
        public void Load_SelectsTheFrameMatchingTheRequestedSize(int px)
        {
            using var icon = TrayIconLoader.Load(new Size(px, px));

            Assert.Equal(new Size(px, px), icon.Size);
        }

        /// <summary>
        /// Documents why TrayIconLoader exists: the parameterless Icon(Stream)
        /// constructor selects the .ico's default frame (32x32), not the 16x16
        /// frame the tray needs. NotifyIcon then downscales it, blurring the
        /// pixel-tuned small artwork.
        /// </summary>
        [Fact]
        public void DefaultIconConstructor_DoesNotSelectTheSmallestFrame()
        {
            var asm = typeof(TrayIconLoader).Assembly;
            string resName = System.Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith("icon.ico", System.StringComparison.OrdinalIgnoreCase))!;

            using var stream = asm.GetManifestResourceStream(resName)!;
            using var icon = new Icon(stream);

            Assert.NotEqual(new Size(16, 16), icon.Size);
        }
    }
}
