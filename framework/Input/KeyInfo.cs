// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.Input;

/// <summary>
/// Facts about <see cref="Key"/> that the input service relies on.
/// </summary>
internal static class KeyInfo
{
    /// <summary>
    /// The number of keys, which index the input service's keyboard state: one past the last key,
    /// since the keys run from <see cref="Key.None"/> (0) without gaps. A key added after
    /// <see cref="Key.Back"/> must replace it here: Debug builds check the count against the enum
    /// when the input service starts, and throw if they differ.
    /// </summary>
    public const int Count = (int)Key.Back + 1;
}
