// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace DotGame.Framework.ContentPipeline.BuildTool.Graphics;

/// <summary>
/// Resizes images with a Lanczos-3 filter, in premultiplied color, so transparent pixels don't
/// bleed their (meaningless) color into the edges they border.
/// </summary>
internal static class ImageResampler
{
    private const double Lobes = 3;

    /// <summary>
    /// <paramref name="image"/> (straight alpha) at <paramref name="width"/> by
    /// <paramref name="height"/>, straight alpha.
    /// </summary>
    public static RgbaImage Resize(
        RgbaImage image,
        int width,
        int height)
    {
        if ((width == image.Width) && (height == image.Height))
        {
            return new RgbaImage(width, height, (byte[])image.Pixels.Clone());
        }

        // Premultiplied, in floats
        var source = new float[image.Width * image.Height * 4];
        var pixels = image.Pixels;
        for (int i = 0; i < source.Length; i += 4)
        {
            float a = pixels[i + 3] / 255f;
            source[i] = pixels[i] / 255f * a;
            source[i + 1] = pixels[i + 1] / 255f * a;
            source[i + 2] = pixels[i + 2] / 255f * a;
            source[i + 3] = a;
        }

        var horizontal = ResizeAxis(source, image.Width, image.Height, width, horizontalPass: true);
        var resized = ResizeAxis(horizontal, width, image.Height, height, horizontalPass: false);

        var result = new byte[width * height * 4];
        for (int i = 0; i < result.Length; i += 4)
        {
            float a = Math.Clamp(resized[i + 3], 0f, 1f);
            result[i + 3] = ToByte(a);
            if (a > 0)
            {
                result[i] = ToByte(resized[i] / a);
                result[i + 1] = ToByte(resized[i + 1] / a);
                result[i + 2] = ToByte(resized[i + 2] / a);
            }
        }

        return new RgbaImage(width, height, result);
    }

    // Resamples along one axis: horizontally (to newLength columns) or vertically (rows)
    private static float[] ResizeAxis(
        float[] source,
        int width,
        int height,
        int newLength,
        bool horizontalPass)
    {
        int oldLength = horizontalPass ? width : height;
        int lines = horizontalPass ? height : width;
        int outWidth = horizontalPass ? newLength : width;
        var output = new float[outWidth * (horizontalPass ? height : newLength) * 4];

        // When shrinking, the filter widens with the scale, so every source pixel counts
        double scale = (double)newLength / oldLength;
        double support = (scale < 1) ? Lobes / scale : Lobes;
        double filterScale = (scale < 1) ? scale : 1;

        for (int o = 0; o < newLength; o++)
        {
            double center = ((o + 0.5) / scale) - 0.5;
            int first = Math.Max(0, (int)Math.Floor(center - support));
            int last = Math.Min(oldLength - 1, (int)Math.Ceiling(center + support));

            var weights = new double[last - first + 1];
            double total = 0;
            for (int s = first; s <= last; s++)
            {
                double w = Lanczos((s - center) * filterScale);
                weights[s - first] = w;
                total += w;
            }

            // Each weight divided by the total once, not for every line: the same values
            for (int s = 0; s < weights.Length; s++)
            {
                weights[s] /= total;
            }

            // A pixel's four channels in two vectors of doubles, each channel's sum in the same
            // order as one at a time (multiplies and adds separate), so the same bits
            ref float pixels = ref MemoryMarshal.GetArrayDataReference(source);
            int step = horizontalPass ? 4 : width * 4;
            for (int line = 0; line < lines; line++)
            {
                var redGreen = Vector128<double>.Zero;
                var blueAlpha = Vector128<double>.Zero;
                int index = horizontalPass ? ((line * width) + first) * 4 : ((first * width) + line) * 4;
                for (int s = 0; s < weights.Length; s++, index += step)
                {
                    var w = Vector128.Create(weights[s]);
                    var (rg, ba) = Vector128.Widen(Vector128.LoadUnsafe(ref pixels, (nuint)index));
                    redGreen += rg * w;
                    blueAlpha += ba * w;
                }

                int outIndex = horizontalPass ? ((line * outWidth) + o) * 4 : ((o * outWidth) + line) * 4;
                output[outIndex] = (float)redGreen.GetElement(0);
                output[outIndex + 1] = (float)redGreen.GetElement(1);
                output[outIndex + 2] = (float)blueAlpha.GetElement(0);
                output[outIndex + 3] = (float)blueAlpha.GetElement(1);
            }
        }

        return output;
    }

    private static double Lanczos(
        double x)
    {
        if (x == 0)
        {
            return 1;
        }

        if (Math.Abs(x) >= Lobes)
        {
            return 0;
        }

        double px = Math.PI * x;
        return Lobes * Math.Sin(px) * Math.Sin(px / Lobes) / (px * px);
    }

    private static byte ToByte(
        float value)
    {
        return (byte)Math.Clamp((int)Math.Round(value * 255f), 0, 255);
    }
}
