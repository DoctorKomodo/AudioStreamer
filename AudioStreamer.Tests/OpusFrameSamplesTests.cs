using Xunit;

namespace AudioStreamer.Tests
{
    public class OpusFrameSamplesTests
    {
        [Theory]
        [InlineData(120, 120)]
        [InlineData(240, 240)]
        [InlineData(480, 480)]
        [InlineData(960, 960)]
        [InlineData(0, 120)]      // below range -> smallest
        [InlineData(300, 240)]    // nearest
        [InlineData(700, 480)]    // nearest (700-480=220 < 960-700=260)
        [InlineData(5000, 960)]   // above range -> largest
        public void Clamp_SnapsToNearestValidOption(int input, int expected)
        {
            Assert.Equal(expected, AudioStreamerLogic.ClampOpusFrameSamples(input));
        }

        [Fact]
        public void Config_DefaultsAreOpusFriendly()
        {
            var c = new AudioStreamerLogic.Config();
            Assert.Equal(CodecId.Pcm, c.Codec);
            Assert.Equal(480, c.OpusFrameSamples);
        }
    }
}
