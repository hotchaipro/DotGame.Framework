// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.ContentPipeline.BuildTool.Graphics;

namespace DotGame.ContentPipeline.Testing;

internal static class Images
{
    /// <summary>A small image with every kind of pixel the pipeline cares about, the same every time.</summary>
    public static RgbaImage Sample(int width = 7, int height = 5)
    {
        var image = new RgbaImage(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = (y * image.Stride) + (x * 4);
                image.Pixels[at] = (byte)(40 * x);
                image.Pixels[at + 1] = (byte)(50 * y);
                image.Pixels[at + 2] = (byte)((x * y * 13) + 7);
                image.Pixels[at + 3] = (byte)((x + y) * 25);
            }
        }

        return image;
    }

    /// <summary>A solid opaque rectangle within a transparent image, for trimming and packing.</summary>
    public static RgbaImage Rectangle(int width, int height, byte r, byte g, byte b, int left = 0, int top = 0, int right = -1, int bottom = -1)
    {
        right = right < 0 ? width : right;
        bottom = bottom < 0 ? height : bottom;
        var image = new RgbaImage(width, height);
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                int at = (y * image.Stride) + (x * 4);
                image.Pixels[at] = r;
                image.Pixels[at + 1] = g;
                image.Pixels[at + 2] = b;
                image.Pixels[at + 3] = 255;
            }
        }

        return image;
    }
}
