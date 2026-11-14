// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;

namespace DotGame.Framework.Content;

/// <summary>The game's content bundles, by name. Reach it as <c>Content.Bundles</c>.</summary>
public sealed class ContentBundleCollection : IEnumerable<ContentBundle>
{
    private readonly ContentService _contentService;
    private readonly List<ContentBundle> _bundles;
    private readonly Dictionary<string, ContentBundle> _bundlesByName;

    internal ContentBundleCollection(
        ContentService contentService)
    {
        ArgumentNullException.ThrowIfNull(contentService);

        this._bundles = new();
        this._bundlesByName = new(StringComparer.Ordinal);
        this._contentService = contentService;
    }

    /// <summary>Makes a bundle with this name.</summary>
    public ContentBundle Add(
        string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var bundle = new ContentBundle(this._contentService, name);
        this._bundlesByName.Add(name, bundle);
        this._bundles.Add(bundle);
        return bundle;
    }

    /// <summary>
    /// Gets a bundle by name.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No bundle has that name.</exception>
    public ContentBundle this[string name] => this._bundlesByName[name];

    /// <summary>
    /// Gets a bundle by name, if there is one.
    /// </summary>
    public bool TryGet(
        string name,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ContentBundle? bundle)
    {
        return this._bundlesByName.TryGetValue(name, out bundle);
    }

    /// <summary>Gets an enumerator over the bundles.</summary>
    public IEnumerator<ContentBundle> GetEnumerator()
    {
        return this._bundles.GetEnumerator();
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
    {
        return this.GetEnumerator();
    }
}
