using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Xunit;

namespace AudioStreamer.Tests
{
    public class IconAssetTests
    {
        // The prototype scores 47 near-white pixels; the icon this replaced scored 11.
        private const int MinimumNearWhitePixels = 30;

        // A crumbled arc leaves a fleck. The prototype's components are 25/23/21.
        private const int MinimumComponentPixels = 8;

        private sealed record Frame(int Width, int Height, byte[] Payload);

        private static byte[] ReadIcon()
        {
            var asm = typeof(TrayIconLoader).Assembly;
            string resName = System.Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith("icon.ico", System.StringComparison.OrdinalIgnoreCase))!;

            using var stream = asm.GetManifestResourceStream(resName)!;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }

        /// <summary>
        /// Parses the ICO directory. Frame payloads are decoded here rather than
        /// via Icon.ToBitmap(), which is unreliable for PNG-compressed frames.
        /// </summary>
        private static List<Frame> ReadFrames()
        {
            byte[] ico = ReadIcon();
            int count = System.BitConverter.ToUInt16(ico, 4);
            var frames = new List<Frame>(count);

            for (int i = 0; i < count; i++)
            {
                int entry = 6 + i * 16;
                int width = ico[entry] == 0 ? 256 : ico[entry];
                int height = ico[entry + 1] == 0 ? 256 : ico[entry + 1];
                int length = System.BitConverter.ToInt32(ico, entry + 8);
                int offset = System.BitConverter.ToInt32(ico, entry + 12);

                if (offset < 0 || length < 0 || (long)offset + length > ico.Length)
                    throw new System.InvalidOperationException(
                        $"icon.ico frame {i} is out of bounds: offset={offset}, length={length}, file={ico.Length}");

                frames.Add(new Frame(width, height, ico.AsSpan(offset, length).ToArray()));
            }

            return frames;
        }

        private static Bitmap Decode(Frame frame)
        {
            Assert.True(
                frame.Payload.Length > 8 && frame.Payload[1] == 'P' &&
                frame.Payload[2] == 'N' && frame.Payload[3] == 'G',
                $"expected a PNG-encoded {frame.Width}px frame; regenerate with tools/generate-icon.py");

            using var stream = new MemoryStream(frame.Payload);
            using var decoded = new Bitmap(stream);

            // A Bitmap built from a stream needs that stream to outlive it, so
            // hand back a copy that owns its own pixel data.
            return new Bitmap(decoded);
        }

        private static double Luminance(Color c) =>
            (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

        /// <summary>
        /// Sizes of the 8-connected components of bright (glyph) pixels, largest first.
        /// </summary>
        /// <remarks>
        /// 8-connectivity, not 4: a 1px-wide curved stroke steps diagonally, and
        /// under 4-connectivity each diagonal step severs it. The two arcs would
        /// report as a handful of specks even when the artwork is correct.
        /// </remarks>
        private static List<int> BrightComponentSizes(Bitmap frame)
        {
            bool IsBright(int x, int y)
            {
                Color c = frame.GetPixel(x, y);
                return c.A >= 128 && Luminance(c) > 0.80;
            }

            var seen = new bool[16, 16];
            var sizes = new List<int>();

            for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                if (seen[x, y] || !IsBright(x, y)) continue;

                int count = 0;
                var stack = new Stack<(int X, int Y)>();
                stack.Push((x, y));

                while (stack.Count > 0)
                {
                    var (cx, cy) = stack.Pop();
                    if (cx < 0 || cx > 15 || cy < 0 || cy > 15) continue;
                    if (seen[cx, cy] || !IsBright(cx, cy)) continue;

                    seen[cx, cy] = true;
                    count++;

                    for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        if (dx != 0 || dy != 0)
                            stack.Push((cx + dx, cy + dy));
                }

                sizes.Add(count);
            }

            sizes.Sort();
            sizes.Reverse();
            return sizes;
        }

        [Fact]
        public void Icon_ContainsAllSixSizes()
        {
            int[] sizes = ReadFrames().Select(f => f.Width).OrderBy(w => w).ToArray();

            Assert.Equal(new[] { 16, 32, 48, 64, 128, 256 }, sizes);
        }

        [Fact]
        public void SmallFrame_HasCrispWhitePixels()
        {
            using Bitmap frame = Decode(ReadFrames().Single(f => f.Width == 16));

            int nearWhite = 0;
            for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                Color c = frame.GetPixel(x, y);
                if (c.A >= 128 && Luminance(c) > 0.95) nearWhite++;
            }

            Assert.True(
                nearWhite >= MinimumNearWhitePixels,
                $"only {nearWhite} near-white pixels at 16px (need >= {MinimumNearWhitePixels}); " +
                "the glyph has dissolved into antialiased grey — is it being downscaled?");
        }

        /// <summary>
        /// The speaker and the two arcs must survive as three separate shapes.
        /// Merging means the icon reads as a blob; fragmenting means it has
        /// dissolved into the specks that made the old icon illegible.
        /// </summary>
        [Fact]
        public void SmallFrame_ResolvesIntoExactlyThreeMasses()
        {
            using Bitmap frame = Decode(ReadFrames().Single(f => f.Width == 16));

            Assert.Equal(3, BrightComponentSizes(frame).Count);
        }

        /// <summary>
        /// An exact mass count is not enough on its own: three masses of which one
        /// is a three-pixel fleck would still count as three.
        /// </summary>
        [Fact]
        public void SmallFrame_HasNoCrumbledComponent()
        {
            using Bitmap frame = Decode(ReadFrames().Single(f => f.Width == 16));

            List<int> sizes = BrightComponentSizes(frame);

            // Not sizes.Min() inline: xUnit builds the message eagerly, so an
            // empty list would throw InvalidOperationException instead of failing.
            int smallest = sizes.Count == 0 ? 0 : sizes[^1];

            Assert.True(
                sizes.Count > 0 && smallest >= MinimumComponentPixels,
                $"smallest bright component is {smallest} pixels (need >= {MinimumComponentPixels}); " +
                $"an arc has crumbled. Component sizes: [{string.Join(", ", sizes)}]");
        }
    }
}
