// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace DotGame.Framework.Platform.Common.Audio;

/// <summary>
/// Converts decoded 16-bit PCM to the float samples
/// <see cref="AudioMixer"/> mixes, at the output rate, once
/// per sound at load.
/// </summary>
/// <remarks>
/// <para>
/// Sounds are built at 48 kHz, the rate most devices mix at (and the
/// rate desktop's output is opened at), so a load is usually only the
/// conversion to float, eight samples at a time. On a Pixel 3a that
/// takes about 22 ms for a 55-second stereo bank.
/// </para>
/// <para>
/// Devices that mix at another rate (some run at 44.1 kHz) get a 4-point
/// cubic Hermite interpolator, far cleaner than linear and cheap enough
/// when it runs once per sound. Sounds with more than two channels keep
/// their first two.
/// </para>
/// </remarks>
internal static class PcmConverter
{
    private const float SampleScale = 1f / 32768f;

    /// <summary>
    /// Returns the sound as interleaved float samples at
    /// <paramref name="targetRate"/>, with 1 or 2 channels.
    /// </summary>
    internal static float[] ToFloat(
        ReadOnlySpan<byte> pcm16,
        int channels,
        int sourceRate,
        int targetRate,
        out int outputChannels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceRate);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetRate);

        outputChannels = Math.Min(channels, 2);
        int sourceFrames = pcm16.Length / (channels * 2);

        // NOTE: Every platform this runs on is little-endian
        var source = MemoryMarshal.Cast<byte, short>(pcm16.Slice(0, sourceFrames * channels * 2));
        float[] samples = Widen(source, channels, outputChannels, sourceFrames);

        return (sourceRate == targetRate)
            ? samples
            : Resample(samples, outputChannels, sourceFrames, sourceRate, targetRate);
    }

    // Every element is written, so the arrays skip zeroing, a large part of the cost for a bank
    private static float[] Widen(
        ReadOnlySpan<short> source,
        int channels,
        int outputChannels,
        int frames)
    {
        var samples = GC.AllocateUninitializedArray<float>(frames * outputChannels);

        if (channels == outputChannels)
        {
            int i = 0;
            if (Vector128.IsHardwareAccelerated)
            {
                var scale = Vector128.Create(SampleScale);
                ref short input = ref MemoryMarshal.GetReference(source);
                ref float output = ref MemoryMarshal.GetArrayDataReference(samples);
                for (; i <= source.Length - Vector128<short>.Count; i += Vector128<short>.Count)
                {
                    var (lower, upper) = Vector128.Widen(Vector128.LoadUnsafe(ref input, (nuint)i));
                    Vector128.StoreUnsafe(Vector128.ConvertToSingle(lower) * scale, ref output, (nuint)i);
                    Vector128.StoreUnsafe(Vector128.ConvertToSingle(upper) * scale, ref output, (nuint)(i + Vector128<int>.Count));
                }
            }

            for (; i < source.Length; i++)
            {
                samples[i] = source[i] * SampleScale;
            }
        }
        else
        {
            for (int frame = 0; frame < frames; frame++)
            {
                samples[frame * 2] = source[frame * channels] * SampleScale;
                samples[(frame * 2) + 1] = source[(frame * channels) + 1] * SampleScale;
            }
        }

        return samples;
    }

    private static float[] Resample(
        float[] input,
        int channels,
        int sourceFrames,
        int sourceRate,
        int targetRate)
    {
        int targetFrames = (int)(((long)sourceFrames * targetRate) / sourceRate);
        var output = GC.AllocateUninitializedArray<float>(targetFrames * channels);
        if (targetFrames == 0)
        {
            return output;
        }

        // The source position advances by sourceRate / targetRate per output frame, kept as a
        // whole index and a fraction in exact integer steps, so it never drifts
        int divisor = GreatestCommonDivisor(sourceRate, targetRate);
        int step = sourceRate / divisor;
        int denominator = targetRate / divisor;
        float inverse = 1f / denominator;
        int lastFrame = sourceFrames - 1;
        int index = 0;
        int fraction = 0;

        ref float source = ref MemoryMarshal.GetArrayDataReference(input);
        ref float target = ref MemoryMarshal.GetArrayDataReference(output);

        for (int frame = 0; frame < targetFrames; frame++)
        {
            float t = fraction * inverse;

            // NOTE: index - 1 through index + 2 are inside the input here, so the reads need
            // no bounds checks
            if ((index >= 1) && (index + 2 <= lastFrame))
            {
                ref float p = ref Unsafe.Add(ref source, (index - 1) * channels);
                if (channels == 2)
                {
                    Unsafe.Add(ref target, frame * 2) = Hermite(
                        p,
                        Unsafe.Add(ref p, 2),
                        Unsafe.Add(ref p, 4),
                        Unsafe.Add(ref p, 6),
                        t);
                    Unsafe.Add(ref target, (frame * 2) + 1) = Hermite(
                        Unsafe.Add(ref p, 1),
                        Unsafe.Add(ref p, 3),
                        Unsafe.Add(ref p, 5),
                        Unsafe.Add(ref p, 7),
                        t);
                }
                else
                {
                    Unsafe.Add(ref target, frame) = Hermite(
                        p,
                        Unsafe.Add(ref p, 1),
                        Unsafe.Add(ref p, 2),
                        Unsafe.Add(ref p, 3),
                        t);
                }
            }
            else
            {
                // The first frame and the last two, where the neighbours repeat the end samples
                int i0 = Math.Max(index - 1, 0);
                int i1 = Math.Min(index, lastFrame);
                int i2 = Math.Min(index + 1, lastFrame);
                int i3 = Math.Min(index + 2, lastFrame);
                for (int c = 0; c < channels; c++)
                {
                    output[(frame * channels) + c] = Hermite(
                        input[(i0 * channels) + c],
                        input[(i1 * channels) + c],
                        input[(i2 * channels) + c],
                        input[(i3 * channels) + c],
                        t);
                }
            }

            fraction += step;
            while (fraction >= denominator)
            {
                fraction -= denominator;
                index++;
            }
        }

        return output;
    }

    private static int GreatestCommonDivisor(
        int a,
        int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

    // Catmull-Rom spline through y1 (t = 0) and y2 (t = 1)
    internal static float Hermite(
        float y0,
        float y1,
        float y2,
        float y3,
        float t)
    {
        float c1 = 0.5f * (y2 - y0);
        float c2 = y0 - (2.5f * y1) + (2f * y2) - (0.5f * y3);
        float c3 = (0.5f * (y3 - y0)) + (1.5f * (y1 - y2));
        return (((((c3 * t) + c2) * t) + c1) * t) + y1;
    }
}
