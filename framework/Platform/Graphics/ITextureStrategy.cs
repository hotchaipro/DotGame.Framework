// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.Framework.Platform.Graphics;

internal interface ITextureStrategy : IDisposable
{
    Vector2Int Size { get; }

    // Replaces a region's pixels; the framework has checked the region and the length
    void SetData(RectangleInt region, ReadOnlySpan<Color32> data);
}
