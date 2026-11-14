// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.Graphics;

/// <summary>
/// How a sprite batch's sprites combine with what's behind them. Colors are premultiplied, so a
/// sprite's transparent parts leave what's behind alone in every mode but <see cref="Opaque"/>.
/// </summary>
public enum BlendMode
{
    /// <summary>The sprite covers what's behind it, as far as it's opaque.</summary>
    Alpha,

    /// <summary>The sprite's color adds to what's behind it, for light, fire and glows.</summary>
    Additive,

    /// <summary>The sprite's color multiplies what's behind it, which darkens: for shadows.</summary>
    Multiply,

    /// <summary>The inverse of multiplying the inverses, which lightens without blowing out.</summary>
    Screen,

    /// <summary>The sprite replaces what's behind it, alpha included.</summary>
    Opaque,
}
