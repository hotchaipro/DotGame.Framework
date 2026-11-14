// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading.Tasks;
using DotGame.Framework.Platform.Content;

namespace DotGame.Framework.Content;

/// <summary>Loads content by key, shares what's already loaded, and releases it. Reach it as <c>Game.Content</c>.</summary>
public sealed class ContentService
{
    private readonly Dictionary<Type, IContentLoader> _loaders;
    private readonly ContentBundleCollection _bundles;
    private readonly ConcurrentDictionary<string, Task<Object>> _loadedContent;
    private readonly ContentManifest _manifest;

    internal ContentService(
        Strategy<IContentServiceStrategy> contentServiceStrategy)
    {
        ArgumentNullException.ThrowIfNull(contentServiceStrategy);

        this.Strategy = contentServiceStrategy;

        this._loaders = new();
        this._bundleHolds = new(StringComparer.OrdinalIgnoreCase);
        this._keptContent = new(StringComparer.OrdinalIgnoreCase);
        this._bundles = new ContentBundleCollection(this);
        // Keyed as the manifest keys content: normalized (see ContentManifest.NormalizeKey)
        // and case-insensitive, so every spelling of one content key shares one load
        this._loadedContent = new(StringComparer.OrdinalIgnoreCase);
        this._manifest = contentServiceStrategy.Interface.Manifest
            ?? throw new InvalidOperationException("The content service strategy did not supply a content manifest.");
    }

    private readonly Strategy<IContentServiceStrategy> Strategy;

    // How many loaded bundles use each key, and the keys loaded directly, which stay until they're
    // unloaded. Bundles and direct loads can come from any thread; the lock is on the first.
    private readonly Dictionary<string, int> _bundleHolds;
    private readonly HashSet<string> _keptContent;

    internal Task Initialize()
    {
        this.RegisterLoader(this.Strategy.Interface.TextureLoader);
        this.RegisterLoader(this.Strategy.Interface.ShaderLoader);
        this.RegisterLoader(this.Strategy.Interface.SoundLoader);
        this.RegisterLoader(this.Strategy.Interface.MusicLoader);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds a loader for a type of content, your own or a library's. It replaces any loader the type
    /// already has, for what's loaded from then on, such as a built-in one that can't read another
    /// content pipeline's textures.
    /// </summary>
    public void RegisterLoader<TContent>(
        IContentLoader<TContent> contentLoader)
        where TContent : class, IContent
    {
        this._loaders[typeof(TContent)] = contentLoader;
    }

    /// <summary>The game's content bundles.</summary>
    public ContentBundleCollection Bundles => this._bundles;

    /// <summary>Gets content that's already loaded, without starting a load.</summary>
    /// <param name="contentKey">The content's key: its path under <c>Content/</c>, with no extension.</param>
    /// <param name="content">The content, if it's loaded.</param>
    public bool TryGet<TContent>(
        string contentKey,
        [NotNullWhen(true)] out TContent? content)
        where TContent : class, IContent
    {
        if ((this._loadedContent.TryGetValue(ContentManifest.NormalizeKey(contentKey), out var contentTask))
            && (contentTask.IsCompletedSuccessfully)
            && (!IsContentDisposed(contentTask.Result))
            && (contentTask.Result is TContent typedContent))
        {
            content = typedContent;
            return true;
        }

        content = null;
        return false;
    }

    /// <summary>Opens a content file's bytes, for a loader or for data the game reads itself.</summary>
    /// <param name="contentPath">The file's path under <c>Content/</c>, with its extension. This is a
    /// path rather than a key, as it names a file rather than a piece of content.</param>
    public Task<Stream> OpenStreamAsync(
        string contentPath)
    {
        return this.Strategy.Interface.OpenStreamAsync(contentPath);
    }

    /// <summary>
    /// Gets the path of the file with a content key, relative to the content root, for a loader to
    /// open with <see cref="OpenStreamAsync"/>.
    /// </summary>
    /// <param name="contentKey">Logical content key.</param>
    /// <param name="subKey">
    /// Optional sub-identifier for asset types whose pipeline
    /// output is a set of related files (e.g. <c>"vert.spv"</c> /
    /// <c>"frag.spv"</c> for a shader).
    /// </param>
    /// <exception cref="KeyNotFoundException">No content has this key.</exception>
    public string ResolveContentPath(
        string contentKey,
        string? subKey = null)
    {
        return this._manifest.ResolveContentPath(contentKey, subKey);
    }

    /// <summary>
    /// Loads content by key, or gets what's already loaded. Every caller of a key shares one
    /// object, which belongs to this service: release it with <see cref="UnloadAsync(string)"/>,
    /// not by disposing it. Content kept this way stays until then, whichever bundles also use it.
    /// </summary>
    /// <param name="contentKey">The content's key: its path under <c>Content/</c>, with no extension.</param>
    public async Task<TContent> LoadAsync<TContent>(
        string contentKey)
        where TContent : class, IContent
    {
        lock (this._bundleHolds)
        {
            this._keptContent.Add(ContentManifest.NormalizeKey(contentKey));
        }

        var result = await this.LoadAsync(typeof(TContent), contentKey).ConfigureAwait(false);

        if (result is not TContent content)
        {
            throw new InvalidOperationException(
                $"Content key '{contentKey}' is already loaded as '{DiagnosticInfo.TypeName(result)}' and cannot be loaded as '{DiagnosticInfo.TypeName<TContent>()}'.");
        }

        return content;
    }

    internal Task<Object> LoadAsync(
        Type contentType,
        string contentKey)
    {
        // "Backgrounds\a" and "Backgrounds/a" name the same content, so they share one load
        contentKey = ContentManifest.NormalizeKey(contentKey);

        while (true)
        {
            // A load that failed is tried again, and whoever was awaiting it has seen the failure
            if (this._loadedContent.TryGetValue(contentKey, out var existingTask)
                && (existingTask.IsFaulted || existingTask.IsCanceled))
            {
                ((ICollection<KeyValuePair<string, Task<Object>>>)this._loadedContent)
                    .Remove(new KeyValuePair<string, Task<Object>>(contentKey, existingTask));
            }

            // NOTE: The entry goes in before the load starts, as GetOrAdd can run a factory on
            // every thread in a race, whose losing load nobody would ever dispose
            if (!this._loadedContent.TryGetValue(contentKey, out var loaderTask))
            {
                var completion = new TaskCompletionSource<Object>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!this._loadedContent.TryAdd(contentKey, completion.Task))
                {
                    continue;
                }

                this.StartLoad(contentType, contentKey, completion);

                return completion.Task;
            }

            if ((loaderTask.IsCompletedSuccessfully)
                && (IsContentDisposed(loaderTask.Result)))
            {
                // Evict the stale entry and retry
                ((ICollection<KeyValuePair<string, Task<Object>>>)this._loadedContent)
                    .Remove(new KeyValuePair<string, Task<Object>>(contentKey, loaderTask));
                continue;
            }

            return loaderTask;
        }
    }

    private void StartLoad(
        Type contentType,
        string contentKey,
        TaskCompletionSource<Object> completion)
    {
        if (this.LoadsOnWorkerThreads)
        {
            _ = Task.Run(() => this.RunLoad(contentType, contentKey, completion));
        }
        else
        {
            _ = this.RunLoad(contentType, contentKey, completion);
        }
    }

    // NOTE: Takes everything through the completion, including a load that throws before its
    // first await (no loader for the type), so it never faults itself
    private async Task RunLoad(
        Type contentType,
        string contentKey,
        TaskCompletionSource<Object> completion)
    {
        try
        {
            var loader = this.GetLoaderForType(contentType);

            Trace.Write(0x7c8b1041, $"[ContentService] Loading '{contentKey}' using '{DiagnosticInfo.FullTypeName(loader)}'");

            var stopWatch = System.Diagnostics.Stopwatch.StartNew();

            var result = await loader.LoadAsync(contentKey).ConfigureAwait(false);

            stopWatch.Stop();
            Trace.Write(0x7c8b1042, $"[ContentService] Loaded '{contentKey}' in {stopWatch.ElapsedMilliseconds}ms");

            // NOTE: A texture is the one type that's also made directly, and those are the
            // caller's to dispose
            if (result is Graphics.Texture texture)
            {
                texture.IsOwnedByContent = true;
            }

            completion.TrySetResult(result);
        }
        // NOTE: A cancellation too: no one cancels a load (it takes no token), so it's a failure
        // of the loader's, and the exception it threw says where. A canceled task would end in a
        // new TaskCanceledException with no frames below this one.
        catch (Exception e)
        {
            completion.TrySetException(e);
        }
    }

    /// <summary>
    /// Whether loads run on worker threads, so a load during play doesn't hold up frames. Set
    /// by the host once the game has started, except where there's only one thread (the web).
    /// During startup loads run on the calling thread, with no frame to protect.
    /// </summary>
    internal bool LoadsOnWorkerThreads { get; set; }

    /// <summary>
    /// Releases content, for every caller that shares it, unless a loaded bundle
    /// still uses it, in which case it goes when the last such bundle is unloaded.
    /// </summary>
    /// <returns>A task that completes once it's released, at once unless it's still loading.</returns>
    public Task UnloadAsync(
        string contentKey)
    {
        string key = ContentManifest.NormalizeKey(contentKey);
        lock (this._bundleHolds)
        {
            this._keptContent.Remove(key);
            if (this._bundleHolds.ContainsKey(key))
            {
                return Task.CompletedTask;
            }
        }

        return this.ReleaseAsync(key);
    }

    /// <inheritdoc cref="UnloadAsync(string)"/>
    public Task UnloadAsync(
        IContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        foreach (var entry in this._loadedContent)
        {
            if (entry.Value.IsCompletedSuccessfully && ReferenceEquals(entry.Value.Result, content))
            {
                return this.UnloadAsync(entry.Key);
            }
        }

        return Task.CompletedTask;
    }

    internal void HoldForBundle(
        string contentKey)
    {
        string key = ContentManifest.NormalizeKey(contentKey);
        lock (this._bundleHolds)
        {
            this._bundleHolds[key] = this._bundleHolds.GetValueOrDefault(key) + 1;
        }
    }

    internal Task ReleaseForBundle(
        string contentKey)
    {
        string key = ContentManifest.NormalizeKey(contentKey);
        lock (this._bundleHolds)
        {
            int holds = this._bundleHolds.GetValueOrDefault(key) - 1;
            if (holds > 0)
            {
                this._bundleHolds[key] = holds;
                return Task.CompletedTask;
            }

            this._bundleHolds.Remove(key);
            if (this._keptContent.Contains(key))
            {
                return Task.CompletedTask;
            }
        }

        return this.ReleaseAsync(key);
    }

    private async Task ReleaseAsync(
        string key)
    {
        if (this._loadedContent.TryRemove(key, out var contentTask))
        {
            // NOTE: A load that failed left nothing to dispose, and its failure was reported to
            // whoever awaited it
            await ((Task)contentTask).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            if (contentTask.IsCompletedSuccessfully)
            {
                if (contentTask.Result is Graphics.Texture texture)
                {
                    texture.IsOwnedByContent = false;
                }

                ((IContent)contentTask.Result).Dispose();
            }
        }
    }

    private IContentLoader GetLoaderForType(
        Type contentType)
    {
        if (!this._loaders.TryGetValue(contentType, out var loader))
        {
            throw new InvalidOperationException(
                $"No content loader is registered for '{contentType}'. Register one with Content.RegisterLoader, or call the method of the library that provides the type that registers its loaders.");
        }

        return loader;
    }

    private static bool IsContentDisposed(
        Object content)
    {
        return ((IContent)content).IsDisposed;
    }
}
