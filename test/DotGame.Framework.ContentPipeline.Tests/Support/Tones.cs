// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.ContentPipeline.BuildTool.Audio;

namespace DotGame.ContentPipeline.Testing;

/// <summary>Test audio made in process, so the tests that read and write sound need no tool.</summary>
internal static class Tones
{
    /// <summary>A sine wave, the right channel a quarter turn behind the left so the two differ.</summary>
    public static PcmAudio Sine(int sampleRate, int channels, int frames, double frequency = 440, float amplitude = 0.5f)
    {
        var samples = new float[frames * channels];
        for (int i = 0; i < frames; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                double phase = (2 * Math.PI * frequency * i / sampleRate) + (c * Math.PI / 2);
                samples[(i * channels) + c] = (float)(amplitude * Math.Sin(phase));
            }
        }

        // As a 16-bit source would hold them
        var quantized = new PcmAudio(sampleRate, channels, samples).ToInt16();
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = quantized[i] / 32768f;
        }

        return new PcmAudio(sampleRate, channels, samples);
    }

    /// <summary>White noise from a fixed seed, as 16-bit samples would hold it.</summary>
    public static PcmAudio Noise(int sampleRate, int channels, int frames, int seed = 1, float amplitude = 0.5f)
    {
        var random = new Random(seed);
        var samples = new float[frames * channels];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(((random.NextDouble() * 2) - 1) * amplitude * 32767) / 32768f;
        }

        return new PcmAudio(sampleRate, channels, samples);
    }

    /// <summary>Writes <paramref name="audio"/> as a 16-bit WAV.</summary>
    public static string WriteWav(string path, PcmAudio audio)
    {
        WavWriter.WriteInt16(path, audio);
        return path;
    }
}
