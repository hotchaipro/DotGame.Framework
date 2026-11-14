// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using DotGame.Framework.Platform.Content;

namespace DotGame.Framework.Content;

/// <summary>
/// Maps content keys, the names the game loads content by, to the files a content pipeline produced
/// for them. The build makes one for each content pipeline it runs; another pipeline
/// makes its own, before the game starts, and registers it.
/// </summary>
/// <remarks>
/// <para>
/// A key can have sub-keys, for an asset that's several related files, such as a shader's stages.
/// What a sub-key means is up to the loader.
/// </para>
/// <para>
/// To load another pipeline's own formats, give <c>Content.RegisterLoader</c> a loader for them,
/// which can replace a built-in one. A pipeline using the built-in loaders has to produce the formats
/// they read on each platform.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var manifest = new ContentManifest("MyStudio.MyPipeline", basePath: "Content/");
/// manifest.Add("Textures/bear", "Textures/bear.dds");
/// ContentManifest.Register(manifest);
/// </code>
/// </example>
public sealed class ContentManifest
{
    /// <summary>
    /// Process-wide registry of <see cref="ContentManifest"/> instances
    /// contributed by build pipelines and (optionally) by game code.
    /// When a <see cref="ContentService"/> is constructed it takes a
    /// snapshot of the currently registered manifests and flattens
    /// them into a single merged lookup. Register additional manifests
    /// (e.g. mods, DLC) before constructing the game that creates
    /// the service.
    /// </summary>
    [PlatformContract]
    internal static ContentManifestRegistry Registry { get; } = new();

    /// <summary>
    /// Internal separator used to compose <c>key</c> and
    /// <c>subKey</c> into a single storage key. Chosen because it
    /// is illegal in Windows filenames and therefore cannot collide
    /// with a legitimate path segment. Not part of the public API:
    /// callers pass key and sub-key as separate arguments.
    /// </summary>
    internal const char SubKeySeparator = ':';

    private readonly ConcurrentDictionary<string, ContentManifestEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a new, empty manifest with the given
    /// <paramref name="identity"/>. The identity uniquely names this
    /// manifest within the registry and is used in diagnostic
    /// messages (e.g. key conflicts during flatten).
    /// </summary>
    /// <param name="identity">
    /// Stable identifier for this manifest. We recommend a
    /// reverse-DNS or organization-prefixed name to avoid clashes
    /// across content packs from different authors — for example
    /// <c>"MyStudio.MyGame"</c> or
    /// <c>"AcmeMods.WinterPack"</c>.
    /// </param>
    /// <param name="basePath">
    /// The build-time content root that the pipeline stripped from
    /// every key/path in this manifest (e.g. <c>"Content/"</c>). This
    /// is a build-time fact emitted
    /// by the content pipeline; the platform combines it with its own
    /// runtime root (application base directory, app bundle path, or
    /// base URL) to locate the physical assets. Pass <see langword="null"/>
    /// or an empty string for hand-authored manifests (mods, DLC) that
    /// do not declare a content root; such manifests do not contribute
    /// a base path when merged into a snapshot. Normalized to forward
    /// slashes with a trailing <c>/</c> and no leading <c>/</c>.
    /// </param>
    public ContentManifest(
        string identity,
        string? basePath = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(identity);
        this.Identity = identity;
        this.BasePath = NormalizeBasePath(basePath);
    }

    /// <summary>
    /// Stable identifier supplied at construction time.
    /// </summary>
    internal string Identity { get; }

    /// <summary>
    /// The build-time content root that the pipeline stripped from
    /// this manifest's keys and paths, normalized to forward slashes
    /// with a trailing <c>/</c> and no leading <c>/</c>. Empty when the
    /// manifest declares no content root (e.g. hand-authored mods). The
    /// platform prepends its own runtime root to this value to locate
    /// physical assets.
    /// </summary>
    [Platform.PlatformContract]
    internal string BasePath { get; }

    /// <summary>
    /// Registers (or overwrites) the entry for the given
    /// <paramref name="key"/> (and optional <paramref name="subKey"/>).
    /// The last registration wins, which allows mods / DLC layered
    /// on top of the build-pipeline manifest to override base
    /// content.
    /// </summary>
    /// <param name="key">
    /// Logical content key. Backslashes are normalized to forward
    /// slashes; any leading <c>/</c> is trimmed. Must not contain
    /// <c>:</c> (reserved for sub-key delimiting).
    /// </param>
    /// <param name="path">
    /// Physical relative path. Same normalization as
    /// <paramref name="key"/>.
    /// </param>
    /// <param name="subKey">
    /// Optional single-token sub-identifier for asset types that
    /// produce a set of related files (e.g. <c>"vert.spv"</c> /
    /// <c>"frag.spv"</c> for a shader). When non-null, must be
    /// non-empty and must not contain <c>:</c>, <c>/</c>, or
    /// <c>\</c>.
    /// </param>
    /// <param name="properties">
    /// What a loader should know before it opens the file, read with <see cref="TryGetProperty"/>,
    /// or <c>null</c> for nothing.
    /// </param>
    public void Add(
        string key,
        string path,
        string? subKey = null,
        IEnumerable<KeyValuePair<string, string>>? properties = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var normalizedKey = NormalizeKey(key);
        ValidateKey(normalizedKey, nameof(key));

        var normalizedSubKey = NormalizeSubKey(subKey);
        ValidateSubKey(normalizedSubKey, nameof(subKey));

        var normalizedPath = NormalizePath(path);
        var storageKey = ComposeStorageKey(normalizedKey, normalizedSubKey);
        var normalizedProperties = NormalizeProperties(properties);

        var entry = new ContentManifestEntry(normalizedKey, normalizedSubKey, normalizedPath, normalizedProperties);
        this._entries[storageKey] = entry;
    }

    /// <summary>
    /// Folds every entry from <paramref name="other"/> into this
    /// manifest. Throws <see cref="InvalidOperationException"/> if
    /// an incoming entry collides with an existing entry under the
    /// same key/sub-key but with a different path, so layering
    /// conflicts surface immediately rather than as silent
    /// overrides. Identical re-adds are silently allowed
    /// (idempotent), which makes it safe to layer the same mod
    /// twice or to re-build a snapshot after an unchanged
    /// registration.
    /// </summary>
    /// <param name="other">
    /// Source manifest. Its <see cref="Identity"/> is used only in
    /// the conflict message.
    /// </param>
    internal void Add(
        ContentManifest other)
    {
        ArgumentNullException.ThrowIfNull(other);

        foreach (var entry in other.GetEntries())
        {
            if (this.TryGet(entry.Key, out var existing, entry.SubKey))
            {
                if (!string.Equals(existing.Path, entry.Path, StringComparison.OrdinalIgnoreCase))
                {
                    var storageKey = ComposeStorageKey(entry.Key, entry.SubKey);
                    throw new InvalidOperationException(
                        $"Content key '{storageKey}' is already registered as " +
                        $"'{existing.Path}' and manifest '{other.Identity}' attempted " +
                        $"to register it as '{entry.Path}'. Resolve the conflict before " +
                        $"merging the manifests.");
                }
                continue;
            }

            this.Add(entry.Key, entry.Path, entry.SubKey, entry.Properties);
        }
    }

    /// <summary>
    /// Removes the entry for the given <paramref name="key"/> (and
    /// optional <paramref name="subKey"/>). Returns
    /// <see langword="true"/> if an entry was removed.
    /// </summary>
    internal bool Remove(
        string key,
        string? subKey = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var normalizedKey = NormalizeKey(key);
        var normalizedSubKey = NormalizeSubKey(subKey);
        var storageKey = ComposeStorageKey(normalizedKey, normalizedSubKey);

        return this._entries.TryRemove(storageKey, out _);
    }

    /// <summary>
    /// Removes every entry contributed by <paramref name="other"/>.
    /// An entry is removed only when this manifest holds the same
    /// key/sub-key with the same path; entries missing from this
    /// manifest are ignored. Throws
    /// <see cref="InvalidOperationException"/> when an entry exists
    /// under the same key/sub-key but with a different path, which
    /// indicates that something has layered over the contribution
    /// since it was added and removing it would corrupt the merged
    /// view.
    /// </summary>
    /// <remarks>
    /// Pairs with <see cref="Add(ContentManifest)"/> to support
    /// runtime mod / DLC unload: build a snapshot, layer the mod's
    /// manifest in with <c>Add(modManifest)</c>, and later strip it
    /// back out with <c>Remove(modManifest)</c>.
    /// </remarks>
    /// <returns>The number of entries actually removed.</returns>
    internal int Remove(
        ContentManifest other)
    {
        ArgumentNullException.ThrowIfNull(other);

        int removed = 0;
        foreach (var entry in other.GetEntries())
        {
            if (!this.TryGet(entry.Key, out var existing, entry.SubKey))
            {
                continue;
            }

            if (!string.Equals(existing.Path, entry.Path, StringComparison.OrdinalIgnoreCase))
            {
                var storageKey = ComposeStorageKey(entry.Key, entry.SubKey);
                throw new InvalidOperationException(
                    $"Content key '{storageKey}' was contributed by manifest " +
                    $"'{other.Identity}' as '{entry.Path}' but is now registered as " +
                    $"'{existing.Path}'. Refusing to remove the entry because the " +
                    $"current value did not come from '{other.Identity}'.");
            }

            var storage = ComposeStorageKey(entry.Key, entry.SubKey);
            if (this._entries.TryRemove(storage, out _))
            {
                removed++;
            }
        }
        return removed;
    }

    /// <summary>
    /// Returns <see langword="true"/> when an entry is registered
    /// for <paramref name="key"/> (and optional
    /// <paramref name="subKey"/>).
    /// </summary>
    [PlatformContract]
    internal bool TryGet(
        string key,
        out ContentManifestEntry entry,
        string? subKey = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        var storageKey = ComposeStorageKey(NormalizeKey(key), NormalizeSubKey(subKey));
        return this._entries.TryGetValue(storageKey, out entry);
    }

    /// <summary>
    /// Indexer that throws <see cref="KeyNotFoundException"/> when
    /// the key (and optional sub-key) is not registered.
    /// </summary>
    internal ContentManifestEntry this[string key, string? subKey = null]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(key);
            var storageKey = ComposeStorageKey(NormalizeKey(key), NormalizeSubKey(subKey));
            if (!this._entries.TryGetValue(storageKey, out var entry))
            {
                throw new KeyNotFoundException(
                    $"Content key '{storageKey}' is not registered in the manifest.");
            }
            return entry;
        }
    }

    /// <summary>
    /// Resolves a logical content key (with optional sub-key) to
    /// its physical relative path.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No content has this key: a key is never taken as a
    /// path, so a mistaken one is reported as one, the same on every platform.</exception>
    [PlatformContract]
    internal string ResolveContentPath(
        string key,
        string? subKey = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!this.TryGet(key, out var entry, subKey))
        {
            throw new KeyNotFoundException(subKey is null
                ? $"No content has the key '{key}'."
                : $"No content has the key '{key}' with the sub-key '{subKey}'.");
        }

        return entry.Path;
    }

    /// <summary>How many entries the manifest has.</summary>
    internal int Count => this._entries.Count;

    // NOTE: Indexes a snapshot array. Enumerating _entries.Values (a read-only collection
    // over an array of structs) or _entries itself uses enumerators that WebAssembly AOT
    // doesn't compile, since they're only reached through interface calls, so they ran in
    // the interpreter.
    internal IEnumerable<ContentManifestEntry> GetEntries()
    {
        var entries = this._entries.ToArray();
        for (var i = 0; i < entries.Length; i++)
        {
            yield return entries[i].Value;
        }
    }

    /// <summary>
    /// Adds a manifest to the content the game can load by key, before the game starts. A key in
    /// a manifest registered later replaces the same key in one registered earlier.
    /// </summary>
    /// <exception cref="InvalidOperationException">A manifest with the same identity is already registered.</exception>
    public static void Register(
        ContentManifest manifest)
    {
        Registry.Register(manifest);
    }

    /// <summary>
    /// Gets a property that a pipeline gave a key's entry when adding it, such as what a custom
    /// loader needs to know before it opens the file. It works before the game's services exist.
    /// </summary>
    /// <param name="key">The content's key.</param>
    /// <param name="name">The property's name.</param>
    /// <param name="value">The property's value, if the entry has it.</param>
    /// <returns>Whether the entry has it.</returns>
    public static bool TryGetProperty(
        string key,
        string name,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
    {
        return Registry.TryGetProperty(key, name, out value);
    }

    /// <summary>
    /// Normalizes a logical content key to the canonical form used
    /// for storage and lookup: forward slashes, no leading slash.
    /// Case is preserved (comparison is case-insensitive at lookup
    /// time). Shared with <see cref="ContentService"/>, whose cache of
    /// loaded content must key the same content the same way.
    /// </summary>
    internal static string NormalizeKey(
        string key)
    {
        var normalized = key.Replace('\\', '/');
        if (normalized.Length > 0 && normalized[0] == '/')
        {
            normalized = normalized[1..];
        }
        return normalized;
    }

    /// <summary>
    /// Normalizes a physical relative path. Same rules as
    /// <see cref="NormalizeKey"/>; kept as a distinct method so
    /// that future divergence (e.g. percent-decoding) only touches
    /// one side.
    /// </summary>
    private static string NormalizePath(
        string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.Length > 0 && normalized[0] == '/')
        {
            normalized = normalized[1..];
        }
        return normalized;
    }

    /// <summary>
    /// Normalizes a content-root base path to the canonical form:
    /// forward slashes, no leading slash, and a single trailing slash.
    /// Returns the empty string when no base path is supplied.
    /// </summary>
    private static string NormalizeBasePath(
        string? basePath)
    {
        if (string.IsNullOrEmpty(basePath))
        {
            return string.Empty;
        }

        var normalized = basePath.Replace('\\', '/');
        if (normalized.Length > 0 && normalized[0] == '/')
        {
            normalized = normalized[1..];
        }
        if (normalized.Length == 0)
        {
            return string.Empty;
        }
        if (normalized[^1] != '/')
        {
            normalized += "/";
        }
        return normalized;
    }

    private static string? NormalizeSubKey(
        string? subKey)
    {
        if (subKey is null)
        {
            return null;
        }
        return (subKey.Length == 0) ? null : subKey;
    }

    private static Dictionary<string, string>? NormalizeProperties(
        IEnumerable<KeyValuePair<string, string>>? properties)
    {
        if (properties is null)
        {
            return null;
        }

        Dictionary<string, string>? map = null;

        // NOTE: The generated manifests pass arrays. Enumerated as arrays, not through
        // IEnumerable<T>: an array of structs enumerated through IEnumerable<T> uses
        // generic instances that WebAssembly AOT doesn't compile, so registering the
        // manifest at startup ran in the interpreter.
        if (properties is KeyValuePair<string, string>[] array)
        {
            foreach (var pair in array)
            {
                AddProperty(ref map, pair);
            }
        }
        else
        {
            foreach (var pair in properties)
            {
                AddProperty(ref map, pair);
            }
        }
        return map;
    }

    private static void AddProperty(
        ref Dictionary<string, string>? map,
        KeyValuePair<string, string> pair)
    {
        if (string.IsNullOrEmpty(pair.Key) || pair.Value is null)
        {
            return;
        }
        map ??= new Dictionary<string, string>(StringComparer.Ordinal);
        map[pair.Key] = pair.Value;
    }

    private static void ValidateKey(
        string normalizedKey,
        string parameterName)
    {
        if (normalizedKey.Contains(SubKeySeparator))
        {
            throw new ArgumentException(
                $"Content key '{normalizedKey}' must not contain the reserved character '{SubKeySeparator}'. Pass the sub-key as a separate argument instead.",
                parameterName);
        }
    }

    private static void ValidateSubKey(
        string? normalizedSubKey,
        string parameterName)
    {
        if (normalizedSubKey is null)
        {
            return;
        }
        if (normalizedSubKey.Contains(SubKeySeparator)
            || normalizedSubKey.Contains('/')
            || normalizedSubKey.Contains('\\'))
        {
            throw new ArgumentException(
                $"Sub-key '{normalizedSubKey}' must not contain '{SubKeySeparator}', '/', or '\\'.",
                parameterName);
        }
    }

    private static string ComposeStorageKey(
        string normalizedKey,
        string? normalizedSubKey)
    {
        return normalizedSubKey is null
            ? normalizedKey
            : string.Concat(normalizedKey, SubKeySeparator.ToString(), normalizedSubKey);
    }
}
