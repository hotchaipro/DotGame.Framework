// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DotGame.Framework.Content;

/// <summary>A named set of content that loads and unloads together, such as a level's. Bundles count their holds, so what another bundle uses stays loaded.</summary>
public sealed class ContentBundle
{
    private readonly ContentService _contentService;
    private readonly List<(Type ContentType, string ContentKey)> _entries;

    // The keys this bundle holds, from its load until its unload
    private readonly HashSet<string> _held = new(StringComparer.OrdinalIgnoreCase);

    internal ContentBundle(
        ContentService contentService,
        string name)
    {
        ArgumentNullException.ThrowIfNull(contentService);

        this._entries = new();
        this._contentService = contentService;
        this.Name = name;
    }

    /// <summary>The bundle's name.</summary>
    public string Name
    {
        get;
    }

    /// <summary>Adds content to the bundle by its key, to load with the rest.</summary>
    /// <param name="contentKey">The content's key: its path under <c>Content/</c>, with no extension.</param>
    public void Add<TContent>(
        string contentKey)
        where TContent : class, IContent
    {
        this._entries.Add((typeof(TContent), contentKey));
    }

    /// <summary>
    /// Loads everything in the bundle. What another bundle also uses stays loaded until that
    /// bundle is unloaded too.
    /// </summary>
    public async Task LoadAsync()
    {
        var tasks = new Task[this._entries.Count];

        for (int i = 0; i < this._entries.Count; i++)
        {
            var (contentType, contentKey) = this._entries[i];
            if (this._held.Add(ContentManifest.NormalizeKey(contentKey)))
            {
                this._contentService.HoldForBundle(contentKey);
            }

            tasks[i] = this._contentService.LoadAsync(contentType, contentKey);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases what the bundle loaded, except what another loaded bundle uses or was loaded
    /// directly with <see cref="ContentService.LoadAsync{TContent}"/>.
    /// </summary>
    public Task UnloadAsync()
    {
        return this.ReleaseHeldAsync();
    }

    private async Task ReleaseHeldAsync()
    {
        foreach (string contentKey in this._held)
        {
            await this._contentService.ReleaseForBundle(contentKey).ConfigureAwait(false);
        }

        this._held.Clear();
    }
}
