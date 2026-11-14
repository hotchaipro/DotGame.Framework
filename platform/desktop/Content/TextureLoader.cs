// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;
using System.Threading.Tasks;
using DotGame.Framework.Content;
using DotGame.Framework.Desktop.Graphics;
using DotGame.Framework.Graphics;
using DotGame.Framework.Platform.Common.Content;
using DotGame.Framework.Platform.Graphics;
using DotGame.Framework.Threading;

namespace DotGame.Framework.Desktop.Content;

/// <summary>
/// Loads the desktop's textures: KTX2 files of RGBA8 pixels, as the content pipeline's
/// <c>texture --format ktx2</c> command writes them (read by <see cref="Ktx2Texture"/>).
/// </summary>
internal sealed class TextureLoader : IContentLoader<Texture>
{
    private readonly ContentServiceStrategy _contentService;
    private readonly GraphicsServiceStrategy _graphicsService;

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
        var fullPath = this._contentService.GetFullPath(this._contentService.ResolveContentPath(contentKey));

        Trace.Write(0x2b5dc435, $"[TextureLoader] Loading '{fullPath}'");

        // Background-safe phase: decode runs inline on whatever thread invoked
        // the loader (game thread for preload, worker thread for postload).
        using var ktx2 = Ktx2Texture.ReadFile(fullPath);
        if (ktx2.Format != Ktx2PixelFormat.Rgba8)
        {
            throw new InvalidDataException($"'{fullPath}' is {ktx2.Format}, which desktop textures aren't: rebuild the content for desktop.");
        }

        // Engine-restricted phase: hop to the game thread for GPU upload.
        // Synchronous fast path when already on the game thread.
        var texture = await GameThread.InvokeAsync(
            () => this.CreateTexture(ktx2.Data, ktx2.Size.X, ktx2.Size.Y));

        Trace.Write(0xe11b1f51, $"[TextureLoader] Loaded texture ({ktx2.Size.X}x{ktx2.Size.Y})");

        return texture;
    }

    private Texture CreateTexture(
        ReadOnlySpan<byte> pixels,
        int width,
        int height)
    {
        var texInfo = new SDL.GPUTextureCreateInfo
        {
            Type = SDL.GPUTextureType.TextureType2D,
            Format = SDL.GPUTextureFormat.R8G8B8A8Unorm,
            Width = (uint)width,
            Height = (uint)height,
            LayerCountOrDepth = 1,
            NumLevels = 1,
            SampleCount = SDL.GPUSampleCount.SampleCount1,
            Usage = SDL.GPUTextureUsageFlags.Sampler,
        };

        var gpuTexture = SDL.CreateGPUTexture(this._graphicsService.GPUDevice, in texInfo);
        if (gpuTexture == nint.Zero)
        {
            throw new InvalidOperationException($"Failed to create GPU texture: {SDL.GetError()}");
        }

        this._graphicsService.UploadPixels(gpuTexture, new Vector2Int(width, height), pixels);

        var size = new Vector2Int(width, height);
        var sdlTexture = new TextureStrategy(gpuTexture, size, this._graphicsService);
        return new Texture(new Strategy<ITextureStrategy>(sdlTexture));
    }
}
