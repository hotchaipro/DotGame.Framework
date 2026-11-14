// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Threading.Tasks;

namespace DotGame.Framework.Platform.Graphics;

internal interface IGraphicsServiceStrategy
{
    Task OnInitialize();

    int MultisampleCount { get; }

    /// <summary>
    /// The frame rate the game would like, or null for the display's own. It can change at any
    /// time, and every platform has to honor it as far as it can: its frame loop reads it each
    /// frame, or the setter passes it on.
    /// </summary>
    int? PreferredFrameRate { get; set; }

    Strategy<ISpriteBatchStrategy> CreateSpriteBatch();

    Strategy<ITextureStrategy> CreateTexture(Vector2Int size, ReadOnlySpan<Color32> data);

    Strategy<ITextureStrategy> CreateRenderTexture(Vector2Int size);

    void SetRenderTarget(Strategy<ITextureStrategy>? textureStrategy);

    /// <summary>
    /// Reads a render texture's pixels, as they are once everything drawn to it so far this
    /// frame has been drawn, into <paramref name="destination"/>: RGBA, premultiplied as drawn,
    /// the top row first. The task completes when the pixels are in, which may be a frame or
    /// more later, on the game thread.
    /// </summary>
    Task ReadPixelsAsync(Strategy<ITextureStrategy> textureStrategy, Memory<Color32> destination);

    void Clear(Color color);

    void SetScissorRectangle(RectangleInt? rectangle);
}
