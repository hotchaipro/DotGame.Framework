// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.Framework.Content;

/// <summary>
/// Defines the contract for content types that are loaded and cached by the
/// <see cref="ContentService"/>, which owns them, since everyone who loads a key shares one
/// object: release them with <see cref="ContentService.UnloadAsync(string)"/>. Extends
/// <see cref="IDisposable"/> for the content service to release resources with, and exposes
/// <see cref="IsDisposed"/> so the cache can detect and evict stale entries.
/// </summary>
public interface IContent : IDisposable
{
    /// <summary>Whether it has been released.</summary>
    bool IsDisposed { get; }
}
