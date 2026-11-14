// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using DotGame.Framework.Content;

namespace DotGame.Framework.Platform.Content;

/// <summary>
/// Process-wide collection of <see cref="ContentManifest"/> instances
/// contributed by build pipelines and (optionally) by game code.
/// Accessed through <see cref="ContentManifest.Registry"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each build pipeline (the framework's, or any other content
/// pipeline) emits a generated
/// <c>[ModuleInitializer]</c> that constructs a
/// <see cref="ContentManifest"/> with a stable identity and calls
/// <see cref="Register"/>. Game code may also register hand-built
/// manifests (mods, DLC, downloaded user content) before
/// the game starts.
/// </para>
/// <para>
/// When the game starts, it takes a <em>snapshot</em> of the
/// manifests registered so far, which every content loader uses. Each
/// <c>Game</c> instance therefore owns its own immutable view of
/// the content map; subsequent mutations of the registry do not
/// affect already-running games.
/// </para>
/// </remarks>
internal sealed class ContentManifestRegistry : IEnumerable<ContentManifest>
{
    private readonly ConcurrentDictionary<string, ContentManifest> _byIdentity =
        new(StringComparer.Ordinal);

    // Registration order. Guarded by _orderLock so that BuildSnapshot
    // can take a deterministic snapshot regardless of dictionary
    // enumeration order.
    private readonly List<ContentManifest> _order = new();
    private readonly Lock _orderLock = new();

    internal ContentManifestRegistry()
    {
    }

    /// <summary>
    /// Registers <paramref name="manifest"/>. Throws
    /// <see cref="InvalidOperationException"/> if another manifest is
    /// already registered with the same
    /// <see cref="ContentManifest.Identity"/>.
    /// </summary>
    public void Register(
        ContentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (!this._byIdentity.TryAdd(manifest.Identity, manifest))
        {
            throw new InvalidOperationException(
                $"A content manifest with identity '{manifest.Identity}' is already registered. " +
                $"Call Unregister first to replace it.");
        }

        lock (this._orderLock)
        {
            this._order.Add(manifest);
        }
    }

    /// <summary>
    /// Unregisters the manifest with the given <paramref name="identity"/>.
    /// Returns <see langword="true"/> if a manifest was removed.
    /// </summary>
    internal bool Unregister(
        string identity)
    {
        ArgumentException.ThrowIfNullOrEmpty(identity);

        if (!this._byIdentity.TryRemove(identity, out var removed))
        {
            return false;
        }

        lock (this._orderLock)
        {
            this._order.Remove(removed);
        }
        return true;
    }

    /// <summary>
    /// Gets a property of a key's entry, from the last manifest registered with that entry.
    /// </summary>
    public bool TryGetProperty(
        string key,
        string name,
        [NotNullWhen(true)] out string? value)
    {
        lock (this._orderLock)
        {
            for (int i = this._order.Count - 1; i >= 0; i--)
            {
                if (this._order[i].TryGet(key, out var entry)
                    && entry.TryGetProperty(name, out var found))
                {
                    value = found;
                    return true;
                }
            }
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Looks up a registered manifest by its identity.
    /// </summary>
    public bool TryGet(
        string identity,
        [NotNullWhen(true)] out ContentManifest? manifest)
    {
        ArgumentException.ThrowIfNullOrEmpty(identity);
        return this._byIdentity.TryGetValue(identity, out manifest);
    }

    // What the build registers about content rather than content: the launch screen, and the
    // published names of web content. NOTE: Exact names, as a game's own manifest is named for its
    // assembly, which can start the same way
    private static bool IsAboutContent(
        string identity)
    {
        return (identity == "DotGame.LaunchScreen") || (identity == "DotGame.ContentFingerprints");
    }

    /// <summary>
    /// Number of currently registered manifests.
    /// </summary>
    public int Count
    {
        get
        {
            lock (this._orderLock)
            {
                return this._order.Count;
            }
        }
    }

    /// <summary>
    /// Enumerates registered manifests in registration order.
    /// </summary>
    public IEnumerator<ContentManifest> GetEnumerator()
    {
        ContentManifest[] snapshot;
        lock (this._orderLock)
        {
            snapshot = this._order.ToArray();
        }
        return ((IEnumerable<ContentManifest>)snapshot).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

    /// <summary>
    /// Builds a new <see cref="ContentManifest"/> that merges every
    /// currently registered manifest, in registration order. Throws if two source manifests register the same key
    /// with different paths, so content conflicts surface at
    /// start-up rather than as silent overrides.
    /// </summary>
    /// <param name="snapshotIdentity">
    /// Identity to assign to the returned merged manifest. Defaults
    /// to <c>"ContentManifestRegistry.Snapshot"</c>.
    /// </param>
    [PlatformContract]
    internal ContentManifest BuildSnapshot(
        string snapshotIdentity = "default")
    {
        ArgumentException.ThrowIfNullOrEmpty(snapshotIdentity);

        ContentManifest[] registered;
        lock (this._orderLock)
        {
            registered = this._order.ToArray();
        }

        // The content root is a build-time fact contributed by the
        // content pipeline. Resolve the single common non-empty base
        // path across the registered manifests; hand-authored manifests
        // (mods, DLC) that declare no base path do not participate.
        string basePath = string.Empty;
        ContentManifest? basePathSource = null;
        foreach (var manifest in registered)
        {
            if (manifest.BasePath.Length == 0)
            {
                continue;
            }

            if (basePathSource is null)
            {
                basePath = manifest.BasePath;
                basePathSource = manifest;
            }
            else if (!string.Equals(basePath, manifest.BasePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Content manifest '{manifest.Identity}' declares base path " +
                    $"'{manifest.BasePath}' but manifest '{basePathSource.Identity}' " +
                    $"already declared base path '{basePath}'. Every build-pipeline " +
                    $"manifest must share the same content root.");
            }
        }

        var snapshot = new ContentManifest(snapshotIdentity, basePath);

        foreach (var manifest in registered)
        {
            if (!IsAboutContent(manifest.Identity))
            {
                snapshot.Add(manifest);
            }
        }

        return snapshot;
    }
}
