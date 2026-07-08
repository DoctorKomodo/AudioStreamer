using System;
using NAudio.Wave;

namespace AudioStreamer
{
    /// <summary>Maps a CodecId + PCM format to an encoder/decoder. Opus branch added in Task 5.</summary>
    internal static class CodecFactory
    {
        public static IAudioEncoder CreateEncoder(CodecId codec, WaveFormat format, int opusFrameSamples, Action<string> log)
        {
            if (codec == CodecId.Opus)
            {
                log($"Opus encoder: {format.SampleRate}Hz {format.Channels}ch, {opusFrameSamples} samples/frame");
                return new OpusEncoderAdapter(format.SampleRate, format.Channels, opusFrameSamples);
            }
            return new PcmEncoderAdapter(format.BlockAlign);
        }

        public static IAudioDecoder CreateDecoder(CodecId codec, WaveFormat format, Action<string> log)
        {
            if (codec == CodecId.Opus)
            {
                log($"Opus decoder: {format.SampleRate}Hz {format.Channels}ch");
                return new OpusDecoderAdapter(format.SampleRate, format.Channels);
            }
            return new PcmDecoderAdapter();
        }
    }
}
