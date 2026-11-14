// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Android.Audio;
using Xunit;

namespace DotGame.Framework.Tests.Audio;

/// <summary>
/// Android's load-time conversion to float at the output rate, against a
/// plain per-sample version of the same interpolation.
/// </summary>
public sealed class PcmConverterTests
{
    public static TheoryData<int, int, int, int> Cases()
    {
        var data = new TheoryData<int, int, int, int>();
        int[] lengths = [0, 1, 2, 3, 4, 7, 8, 9, 15, 16, 17, 1001];
        (int Source, int Target)[] rates = [(44100, 44100), (48000, 48000), (44100, 48000), (48000, 44100), (22050, 48000), (48000, 22050), (32000, 44100)];
        foreach (int channels in new[] { 1, 2, 3 })
        {
            foreach (int frames in lengths)
            {
                foreach (var (source, target) in rates)
                {
                    data.Add(channels, frames, source, target);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Matches_the_per_sample_conversion(
        int channels,
        int frames,
        int sourceRate,
        int targetRate)
    {
        byte[] pcm = Noise(frames * channels, seed: (frames * 31) + channels);

        var samples = PcmConverter.ToFloat(pcm, channels, sourceRate, targetRate, out int outputChannels);
        var expected = Reference(pcm, channels, sourceRate, targetRate, out int expectedChannels);

        Assert.Equal(expectedChannels, outputChannels);
        Assert.Equal(expected.Length, samples.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            // NOTE: the converter keeps its position in integer steps, the reference in doubles
            Assert.True(Math.Abs(expected[i] - samples[i]) < 1e-5f, $"Sample {i}: {samples[i]}, expected {expected[i]}");
        }
    }

    [Fact]
    public void Same_rate_conversion_is_exact()
    {
        byte[] pcm = Noise(1003 * 2, seed: 7);

        var samples = PcmConverter.ToFloat(pcm, 2, 48000, 48000, out _);

        for (int i = 0; i < samples.Length; i++)
        {
            Assert.Equal(BitConverter.ToInt16(pcm, i * 2) / 32768f, samples[i]);
        }
    }

    [Fact]
    public void A_trailing_partial_frame_is_ignored()
    {
        byte[] pcm = [0x00, 0x40, 0x00, 0xC0, 0x00];

        var samples = PcmConverter.ToFloat(pcm, 2, 48000, 48000, out int channels);

        Assert.Equal(2, channels);
        Assert.Equal([0.5f, -0.5f], samples);
    }

    [Fact]
    public void Rates_must_be_positive()
    {
        byte[] pcm = [0, 0];

        Assert.Throws<ArgumentOutOfRangeException>(() => PcmConverter.ToFloat(pcm, 0, 48000, 48000, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => PcmConverter.ToFloat(pcm, 1, 0, 48000, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => PcmConverter.ToFloat(pcm, 1, 48000, 0, out _));
    }

    private static byte[] Noise(
        int samples,
        int seed)
    {
        var random = new Random(seed);
        var pcm = new byte[samples * 2];
        random.NextBytes(pcm);
        return pcm;
    }

    // The converter before it was made faster: one sample at a time, the position in doubles
    private static float[] Reference(
        byte[] pcm,
        int channels,
        int sourceRate,
        int targetRate,
        out int outputChannels)
    {
        outputChannels = Math.Min(channels, 2);
        int sourceFrames = pcm.Length / (channels * 2);

        if (sourceRate == targetRate)
        {
            var samples = new float[sourceFrames * outputChannels];
            for (int frame = 0; frame < sourceFrames; frame++)
            {
                for (int c = 0; c < outputChannels; c++)
                {
                    samples[(frame * outputChannels) + c] = Read(pcm, channels, frame, c);
                }
            }

            return samples;
        }

        int targetFrames = (int)(((long)sourceFrames * targetRate) / sourceRate);
        var resampled = new float[targetFrames * outputChannels];
        double step = (double)sourceRate / targetRate;
        int lastFrame = sourceFrames - 1;

        for (int frame = 0; frame < targetFrames; frame++)
        {
            double position = frame * step;
            int index = (int)position;
            float t = (float)(position - index);

            int i0 = Math.Max(index - 1, 0);
            int i1 = Math.Min(index, lastFrame);
            int i2 = Math.Min(index + 1, lastFrame);
            int i3 = Math.Min(index + 2, lastFrame);

            for (int c = 0; c < outputChannels; c++)
            {
                float y0 = Read(pcm, channels, i0, c);
                float y1 = Read(pcm, channels, i1, c);
                float y2 = Read(pcm, channels, i2, c);
                float y3 = Read(pcm, channels, i3, c);
                float c1 = 0.5f * (y2 - y0);
                float c2 = y0 - (2.5f * y1) + (2f * y2) - (0.5f * y3);
                float c3 = (0.5f * (y3 - y0)) + (1.5f * (y1 - y2));
                resampled[(frame * outputChannels) + c] = (((((c3 * t) + c2) * t) + c1) * t) + y1;
            }
        }

        return resampled;
    }

    private static float Read(
        byte[] pcm,
        int channels,
        int frame,
        int channel)
    {
        return BitConverter.ToInt16(pcm, ((frame * channels) + channel) * 2) / 32768f;
    }
}
