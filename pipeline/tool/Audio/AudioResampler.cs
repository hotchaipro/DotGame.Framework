// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotGame.Framework.ContentPipeline.BuildTool.Audio;

/// <summary>
/// Changes the sample rate of interleaved audio with a Kaiser-windowed sinc filter, the same on every
/// build machine.
/// </summary>
/// <remarks>
/// <para>
/// The settings follow ffmpeg's resampler defaults, which the pipeline used before: 16 zero crossings
/// either side, a cutoff at 97% of the lower rate's Nyquist frequency, and a Kaiser window with beta 9.
/// When downsampling, the filter widens with the ratio, so it still stops what the lower rate can't
/// hold. Each output sample is at the exact position <c>n × from / to</c> in the input, so there's no
/// drift over a long track and no delay: output sample 0 is input sample 0.
/// </para>
/// <para>
/// The rates' ratio is reduced to <c>L / M</c>, and a table holds the filter for each of the
/// <c>L</c> positions between input samples (160 for 44.1 to 48 kHz). Above 4,096 positions, an
/// unusual pair of rates, the filter is worked out for each output sample instead. Each filter is
/// scaled to sum to 1, so a constant stays constant. Coefficients come from
/// <see cref="DeterministicMath"/> and samples are summed in a fixed order, so the output is the
/// same bits everywhere.
/// </para>
/// </remarks>
public static class AudioResampler
{
    private const int ZeroCrossings = 16;
    private const double Cutoff = 0.97;
    private const double KaiserBeta = 9;
    private const int MaxTablePhases = 4096;

    /// <summary>The number of frames <paramref name="frames"/> become at the new rate, rounded to the nearest.</summary>
    public static int OutputFrames(int frames, int fromRate, int toRate) =>
        checked((int)((((long)frames * toRate) + (fromRate / 2)) / fromRate));

    public static float[] Resample(float[] samples, int channels, int fromRate, int toRate)
    {
        if ((fromRate <= 0) || (toRate <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(fromRate));
        }
        if (fromRate == toRate)
        {
            return samples;
        }

        long divisor = Gcd(fromRate, toRate);
        long up = toRate / divisor;
        long down = fromRate / divisor;
        double scale = Math.Min(1.0, (double)toRate / fromRate);
        int half = (int)Math.Ceiling(ZeroCrossings / scale);
        int taps = 2 * half;

        int inFrames = samples.Length / channels;
        int outFrames = OutputFrames(inFrames, fromRate, toRate);
        var output = new float[outFrames * channels];

        double[]? table = up <= MaxTablePhases ? BuildTable((int)up, half, scale) : null;
        var kernel = new double[taps];
        for (int n = 0; n < outFrames; n++)
        {
            // Four outputs at once inside the input: each sum is a chain of dependent adds, so
            // four (eight for stereo) run side by side, each in its own order, the same bits
            if ((table is not null) && (n + 4 <= outFrames) && Interior4(n, down, up, half, taps, inFrames))
            {
                Four(table, samples, output, n, down, up, half, taps, channels);
                n += 3;
                continue;
            }

            long position = n * down;
            long whole = position / up;
            int phase = (int)(position % up);
            ReadOnlySpan<double> filter;
            if (table is not null)
            {
                filter = table.AsSpan(phase * taps, taps);
            }
            else
            {
                Kernel(kernel, (double)phase / up, half, scale);
                filter = kernel;
            }

            // The filter's first tap sits half - 1 samples before the output's position
            long first = whole - half + 1;
            if ((first >= 0) && (first + taps <= inFrames))
            {
                // Inside the input: no edge checks. The sums are in the same order as at the edges,
                // so the output doesn't depend on which path a sample took
                ReadOnlySpan<float> window = samples.AsSpan((int)first * channels, taps * channels);
                if (channels == 2)
                {
                    double left = 0;
                    double right = 0;
                    for (int k = 0; k < filter.Length; k++)
                    {
                        left += filter[k] * window[2 * k];
                        right += filter[k] * window[(2 * k) + 1];
                    }
                    output[2 * n] = (float)left;
                    output[(2 * n) + 1] = (float)right;
                }
                else
                {
                    double sum = 0;
                    for (int k = 0; k < filter.Length; k++)
                    {
                        sum += filter[k] * window[k];
                    }
                    output[n] = (float)sum;
                }
                continue;
            }

            for (int c = 0; c < channels; c++)
            {
                double sum = 0;
                for (int k = 0; k < taps; k++)
                {
                    long index = first + k;
                    if ((index >= 0) && (index < inFrames))
                    {
                        sum += filter[k] * samples[(index * channels) + c];
                    }
                }
                output[(n * channels) + c] = (float)sum;
            }
        }

        return output;
    }

    // Whether outputs n to n + 3 all have every tap inside the input (and so take the fast path)
    private static bool Interior4(int n, long down, long up, int half, int taps, int inFrames)
    {
        long firstStart = ((n * down) / up) - half + 1;
        long lastStart = (((n + 3) * down) / up) - half + 1;
        return (firstStart >= 0) && (lastStart + taps <= inFrames);
    }

    // Outputs n to n + 3, each summed as the single-output loop sums it
    private static void Four(double[] table, float[] samples, float[] output, int n, long down, long up, int half, int taps, int channels)
    {
        ref double filters = ref MemoryMarshal.GetArrayDataReference(table);
        ref float input = ref MemoryMarshal.GetArrayDataReference(samples);
        Span<int> starts = stackalloc int[4];
        Span<int> phases = stackalloc int[4];
        for (int j = 0; j < 4; j++)
        {
            long position = (n + j) * down;
            starts[j] = (int)((position / up) - half + 1) * channels;
            phases[j] = (int)(position % up) * taps;
        }

        ref double f0 = ref Unsafe.Add(ref filters, phases[0]);
        ref double f1 = ref Unsafe.Add(ref filters, phases[1]);
        ref double f2 = ref Unsafe.Add(ref filters, phases[2]);
        ref double f3 = ref Unsafe.Add(ref filters, phases[3]);
        ref float x0 = ref Unsafe.Add(ref input, starts[0]);
        ref float x1 = ref Unsafe.Add(ref input, starts[1]);
        ref float x2 = ref Unsafe.Add(ref input, starts[2]);
        ref float x3 = ref Unsafe.Add(ref input, starts[3]);
        if (channels == 2)
        {
            double l0 = 0, r0 = 0, l1 = 0, r1 = 0, l2 = 0, r2 = 0, l3 = 0, r3 = 0;
            for (int k = 0; k < taps; k++)
            {
                double c0 = Unsafe.Add(ref f0, k), c1 = Unsafe.Add(ref f1, k), c2 = Unsafe.Add(ref f2, k), c3 = Unsafe.Add(ref f3, k);
                l0 += c0 * Unsafe.Add(ref x0, 2 * k);
                r0 += c0 * Unsafe.Add(ref x0, (2 * k) + 1);
                l1 += c1 * Unsafe.Add(ref x1, 2 * k);
                r1 += c1 * Unsafe.Add(ref x1, (2 * k) + 1);
                l2 += c2 * Unsafe.Add(ref x2, 2 * k);
                r2 += c2 * Unsafe.Add(ref x2, (2 * k) + 1);
                l3 += c3 * Unsafe.Add(ref x3, 2 * k);
                r3 += c3 * Unsafe.Add(ref x3, (2 * k) + 1);
            }

            Span<float> o = output.AsSpan(2 * n, 8);
            (o[0], o[1], o[2], o[3], o[4], o[5], o[6], o[7]) = ((float)l0, (float)r0, (float)l1, (float)r1, (float)l2, (float)r2, (float)l3, (float)r3);
        }
        else if (channels == 1)
        {
            double s0 = 0, s1 = 0, s2 = 0, s3 = 0;
            for (int k = 0; k < taps; k++)
            {
                s0 += Unsafe.Add(ref f0, k) * Unsafe.Add(ref x0, k);
                s1 += Unsafe.Add(ref f1, k) * Unsafe.Add(ref x1, k);
                s2 += Unsafe.Add(ref f2, k) * Unsafe.Add(ref x2, k);
                s3 += Unsafe.Add(ref f3, k) * Unsafe.Add(ref x3, k);
            }

            Span<float> o = output.AsSpan(n, 4);
            (o[0], o[1], o[2], o[3]) = ((float)s0, (float)s1, (float)s2, (float)s3);
        }
        else
        {
            for (int j = 0; j < 4; j++)
            {
                ref double f = ref Unsafe.Add(ref filters, phases[j]);
                for (int c = 0; c < channels; c++)
                {
                    double sum = 0;
                    for (int k = 0; k < taps; k++)
                    {
                        sum += Unsafe.Add(ref f, k) * Unsafe.Add(ref input, starts[j] + (k * channels) + c);
                    }
                    output[((n + j) * channels) + c] = (float)sum;
                }
            }
        }
    }

    private static double[] BuildTable(int phases, int half, double scale)
    {
        int taps = 2 * half;
        var table = new double[phases * taps];
        for (int p = 0; p < phases; p++)
        {
            Kernel(table.AsSpan(p * taps, taps), (double)p / phases, half, scale);
        }

        return table;
    }

    // The filter for an output sample frac (0 <= frac < 1) after an input sample: taps at
    // offsets -half + 1 .. half from that sample
    private static void Kernel(Span<double> kernel, double frac, int half, double scale)
    {
        double cutoff = Cutoff * scale;
        double window = DeterministicMath.BesselI0(KaiserBeta);
        double sum = 0;
        for (int k = 0; k < kernel.Length; k++)
        {
            double x = (k - half + 1) - frac;
            double t = x / half;
            double w = (t is >= -1 and <= 1) ? DeterministicMath.BesselI0(KaiserBeta * Math.Sqrt(1 - (t * t))) / window : 0;
            double arg = Math.PI * cutoff * x;
            double sinc = x == 0 ? 1 : DeterministicMath.Sin(arg) / arg;
            kernel[k] = cutoff * sinc * w;
            sum += kernel[k];
        }

        for (int k = 0; k < kernel.Length; k++)
        {
            kernel[k] /= sum;
        }
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }
}
