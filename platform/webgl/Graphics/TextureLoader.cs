// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using DotGame.Framework.Content;
using DotGame.Framework.Graphics;
using DotGame.Framework.Platform.Graphics;
using DotGame.Framework.WebGL.Content;

namespace DotGame.Framework.WebGL.Graphics;

[SupportedOSPlatform("browser")]
internal sealed class TextureLoader : IContentLoader<Texture>
{
    private readonly ContentServiceStrategy _contentService;
    private readonly GraphicsServiceStrategy _graphicsService;
    private readonly List<(string ContentPath, WeakReference<Texture> WeakRef)> _trackedTextures = new();

    public TextureLoader(
        ContentServiceStrategy contentService,
        GraphicsServiceStrategy graphicsService)
    {
        this._contentService = contentService;
        this._graphicsService = graphicsService;
    }

    public async Task<Texture> LoadAsync(
        string contentKey)
    {
        var textureStrategy = await this.LoadTextureStrategy(contentKey).ConfigureAwait(false);

        var texture = new Texture(new Strategy<ITextureStrategy>(textureStrategy));

        this._trackedTextures.Add((contentKey, new WeakReference<Texture>(texture)));

        return texture;
    }

    internal async Task ReloadAll()
    {
        var reloadEntries = new List<(WeakReference<Texture> WeakRef, Task<TextureStrategy> LoadTask)>();

        for (int i = this._trackedTextures.Count - 1; i >= 0; i--)
        {
            var (contentKey, weakRef) = this._trackedTextures[i];

            // NOTE: An unloaded texture can still be here until it's collected
            if (weakRef.TryGetTarget(out var texture) && !texture.IsDisposed)
            {
                // Invalidate old GL texture (already freed by context loss)
                texture.Strategy.ToConcrete<TextureStrategy>().Invalidate();

                reloadEntries.Add((weakRef, this.LoadTextureStrategy(contentKey)));
            }
            else
            {
                this._trackedTextures.RemoveAt(i);
            }
        }

        // Wait for all loads to complete in parallel
        var loadTasks = new Task[reloadEntries.Count];
        for (int i = 0; i < reloadEntries.Count; i++)
        {
            loadTasks[i] = reloadEntries[i].LoadTask;
        }

        await Task.WhenAll(loadTasks).ConfigureAwait(false);

        // Apply results
        foreach (var (weakRef, loadTask) in reloadEntries)
        {
            // NOTE: Unloaded while it was reloading: the reload goes too
            if (weakRef.TryGetTarget(out var texture) && !texture.IsDisposed)
            {
                texture.Strategy.Reset(loadTask.Result);
            }
            else
            {
                loadTask.Result.Dispose();
            }
        }
    }

    private async Task<TextureStrategy> LoadTextureStrategy(
        string contentKey)
    {
        // NOTE: A lost context comes back; JS also waits for it if it's lost while this decodes
        await this._graphicsService.WhenContextReady().ConfigureAwait(false);

        var glContext = this._graphicsService.WebGLContext;

        // Create a new texture object
        var jsTexture = WebGLBindings.CreateTexture2D();

        // Load texture from content. Loader is extension-agnostic;
        // ResolveContentPath maps the logical key to the actual relative
        // path with codec extension based on the build-time manifest.
        // The stored values are uploaded unchanged: premultiplication, where
        // wanted, is done by the content pipeline.
        var url = this._contentService.GetVersionedUrl(this._contentService.ResolveContentPath(contentKey));

        await WebGLBindings.LoadTexture2DFromContent(
            jsTexture,
            glContext,
            url).ConfigureAwait(false);

        var width = jsTexture.GetPropertyAsInt32(@"width");

        var height = jsTexture.GetPropertyAsInt32(@"height");

        return new TextureStrategy(jsTexture, new Vector2Int(width, height));
    }
}
