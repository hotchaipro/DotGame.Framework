// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace DotGame.Framework.Platform.Common.Content;

/// <summary>
/// The pixel formats a <see cref="Ktx2Texture"/> holds.
/// </summary>
internal enum Ktx2PixelFormat
{
    /// <summary>RGBA, a byte each (<c>VK_FORMAT_R8G8B8A8_UNORM</c>).</summary>
    Rgba8 = 0,

    /// <summary>ASTC, LDR, 4x4 blocks of 16 bytes (<c>VK_FORMAT_ASTC_4x4_UNORM_BLOCK</c>).</summary>
    Astc4x4 = 1,

    /// <summary>ASTC, LDR, 6x6 blocks (<c>VK_FORMAT_ASTC_6x6_UNORM_BLOCK</c>).</summary>
    Astc6x6 = 2,

    /// <summary>ASTC, LDR, 8x8 blocks (<c>VK_FORMAT_ASTC_8x8_UNORM_BLOCK</c>).</summary>
    Astc8x8 = 3,
}
