// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.ContentPipeline.BuildTool.Audio;

/// <summary>
/// Decoded audio: interleaved samples in [-1, 1), mono or stereo, at one sample rate.
/// </summary>
public sealed class PcmAudio
{
    public PcmAudio(int sampleRate, int channels, float[] samples)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (channels is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }
        if ((samples.Length % channels) != 0)
        {
            throw new ArgumentException("The samples aren't a whole number of frames.", nameof(samples));
        }

        this.SampleRate = sampleRate;
        this.Channels = channels;
        this.Samples = samples;
    }

    public int SampleRate { get; }

    public int Channels { get; }

    /// <summary>Interleaved, <see cref="Channels"/> to a frame.</summary>
    public float[] Samples { get; }

    public int Frames => this.Samples.Length / this.Channels;

    /// <summary>This audio with <paramref name="channels"/> channels: a mono source is copied to both, and stereo is mixed to mono as (L + R) / 2.</summary>
    public PcmAudio WithChannels(int channels)
    {
        if (channels == this.Channels)
        {
            return this;
        }
        if (channels is < 1 or > 2)
        {
            throw new ContentBuildException($"Audio can be mono or stereo, not {channels} channels.");
        }

        int frames = this.Frames;
        var samples = new float[frames * channels];
        if (channels == 2)
        {
            for (int i = 0; i < frames; i++)
            {
                samples[2 * i] = this.Samples[i];
                samples[(2 * i) + 1] = this.Samples[i];
            }
        }
        else
        {
            for (int i = 0; i < frames; i++)
            {
                samples[i] = (this.Samples[2 * i] + this.Samples[(2 * i) + 1]) * 0.5f;
            }
        }

        return new PcmAudio(this.SampleRate, channels, samples);
    }

    /// <summary>This audio at <paramref name="sampleRate"/>, by <see cref="AudioResampler"/>.</summary>
    public PcmAudio WithSampleRate(int sampleRate) =>
        sampleRate == this.SampleRate ? this : new PcmAudio(sampleRate, this.Channels, AudioResampler.Resample(this.Samples, this.Channels, this.SampleRate, sampleRate));

    /// <summary>
    /// This audio exactly <paramref name="frames"/> long: cut, or followed by silence.
    /// </summary>
    public PcmAudio WithFrames(int frames)
    {
        if (frames == this.Frames)
        {
            return this;
        }

        var samples = new float[frames * this.Channels];
        Array.Copy(this.Samples, samples, Math.Min(samples.Length, this.Samples.Length));
        return new PcmAudio(this.SampleRate, this.Channels, samples);
    }

    /// <summary>
    /// The samples as 16-bit integers: scaled by 32,768, rounded to the nearest (ties to even, as
    /// ffmpeg does), and clipped. No dither, so silence stays silent and a 16-bit source comes
    /// back exactly.
    /// </summary>
    public short[] ToInt16()
    {
        var result = new short[this.Samples.Length];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = ToInt16(this.Samples[i]);
        }

        return result;
    }

    internal static short ToInt16(float sample)
    {
        double scaled = Math.Round(sample * 32768.0, MidpointRounding.ToEven);
        return scaled >= short.MaxValue ? short.MaxValue : scaled <= short.MinValue ? short.MinValue : (short)scaled;
    }
}
