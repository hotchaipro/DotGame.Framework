// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using DotGame.Framework.Content;
using DotGame.Framework.Platform.Graphics;

namespace DotGame.Framework.Graphics;

/// <summary>
/// Represents a 2D texture resource.
/// </summary>
public class Texture : IContent
{
    [PlatformContract]
    internal Texture(
        Strategy<ITextureStrategy> textureStrategy)
    {
        ArgumentNullException.ThrowIfNull(textureStrategy);

        this.Strategy = textureStrategy;
    }

    [PlatformContract]
    internal readonly Strategy<ITextureStrategy> Strategy;

    /// <summary>Whether it has been released.</summary>
    public bool IsDisposed => this.Strategy.IsDisposed;

    /// <summary>
    /// Gets the size of the texture, in pixels.
    /// </summary>
    public Vector2Int Size => this.Strategy.Interface.Size;

    /// <summary>
    /// Replaces the pixels of a texture you created, or a region of them: a texture painted by the
    /// game, or a video frame. Call it before drawing with the texture this frame: what was drawn
    /// with it earlier in the frame may show either the old pixels or the new, by platform.
    /// </summary>
    /// <param name="data">RGBA, the top row first, premultiplied as sprites are.</param>
    /// <param name="region">The pixels to replace, or <c>null</c> for all of them.</param>
    /// <exception cref="InvalidOperationException">
    /// This texture was loaded by key, so it's shared and may be compressed, or it's a render texture.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The region isn't inside the texture.</exception>
    /// <exception cref="ArgumentException">The data isn't one pixel for each of the region's.</exception>
    public void SetData(
        ReadOnlySpan<Color32> data,
        RectangleInt? region = null)
    {
        if (this.IsOwnedByContent || (this is RenderTexture))
        {
            throw new InvalidOperationException("Only a texture you created with CreateTexture can have its pixels replaced.");
        }

        var size = this.Size;
        var area = region ?? new RectangleInt(0, 0, size.X, size.Y);
        if ((area.X < 0) || (area.Y < 0) || (area.Width < 0) || (area.Height < 0)
            || (area.X + area.Width > size.X) || (area.Y + area.Height > size.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(region), area, "The region must be inside the texture.");
        }

        if (data.Length != area.Width * area.Height)
        {
            throw new ArgumentException($"The region has {area.Width * area.Height} pixels, but the data has {data.Length}.", nameof(data));
        }

        if (data.Length > 0)
        {
            this.Strategy.Interface.SetData(area, data);
        }
    }

    // Loaded by key, so shared, and the content service's to release
    internal bool IsOwnedByContent { get; set; }

    /// <summary>
    /// Releases a texture you created. One loaded by key belongs to the content service.
    /// </summary>
    /// <exception cref="InvalidOperationException">This texture was loaded by key.</exception>
    public void Dispose()
    {
        if (this.IsOwnedByContent)
        {
            throw new InvalidOperationException(
                "This texture was loaded by key, and is shared with whoever else loaded it: release it with Content.UnloadAsync.");
        }

        GC.SuppressFinalize(this);
        this.Strategy.Dispose();
    }
}
