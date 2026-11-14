// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;
using System.Runtime.InteropServices;
using DotGame.Framework.Desktop.Content;
using DotGame.Framework.Desktop.Graphics;
using DotGame.Framework.Platform.Common.Content;
using DotGame.Framework.Platform.Graphics;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace DotGame.Framework.Desktop;

/// <summary>
/// The desktop's launch screen: the window shows the game's <see cref="LaunchScreen"/> (its
/// color, logo and branding, laid out as on every platform) until the game's first frame is on
/// screen, then it fades out over the game's next frames, as every platform's does. The host draws
/// it each frame: <see cref="Draw()"/> until the game draws, then <see cref="DrawOverGame"/>.
/// </summary>
/// <remarks>
/// It's drawn once into a texture the window's size (again if the window's size changes), with
/// the sprite batch, in the window's logical pixels, which are the launch screen's
/// device-independent pixels; each frame draws that texture. So the launch screen fades as one
/// image: drawing its layers each at the fade's opacity would show the game through the logo
/// less than through the background around it. The logo and branding are the KTX2 textures the content
/// pipeline makes from the <c>LaunchScreen</c> item; a game without one shows black.
/// </remarks>
internal sealed class LaunchScreenStrategy : ILaunchScreenStrategy, IDisposable
{
    private readonly GraphicsServiceStrategy _graphics;
    private readonly nint _window;
    private readonly Strategy<ISpriteBatchStrategy> _batch;
    private readonly (Strategy<ITextureStrategy> Texture, Vector2Int Size)? _logo;
    private readonly (Strategy<ITextureStrategy> Texture, Vector2Int Size)? _branding;
    private Strategy<ITextureStrategy>? _frame;
    private Vector2Int _frameSize;
    private long _fadeStart;
    private bool _isDone;
    private bool _isDisposed;

    public LaunchScreenStrategy(
        GraphicsServiceStrategy graphics,
        nint window,
        ContentServiceStrategy content)
    {
        this._graphics = graphics;
        this._window = window;
        // It draws before the game starts, which is when the sprite pipeline is otherwise made
        graphics.EnsureDefaultResources();
        this._batch = graphics.CreateSpriteBatch();

        if (LaunchScreen.IsDefined)
        {
            this._logo = LoadTexture(graphics, content, LaunchScreen.LogoContentKey);
            if (LaunchScreen.BrandingSize != Vector2.Zero)
            {
                this._branding = LoadTexture(graphics, content, LaunchScreen.BrandingContentKey);
            }
        }
    }

    /// <summary>
    /// Presents a frame of only the launch screen, and records it as shown: the host calls it
    /// once the window is on screen.
    /// </summary>
    public void Show()
    {
        this.PresentFrame();
        StartupTimeline.Mark("launch-shown");
    }

    /// <summary>
    /// The game's first frame is on screen: the launch screen fades out from now, over the frames
    /// <see cref="DrawOverGame"/> draws. On the game thread, as the host reports it after the
    /// frame's submit.
    /// </summary>
    public void Dismiss()
    {
        this._fadeStart = Stopwatch.GetTimestamp();
        StartupTimeline.Mark("first-frame-presented");
    }

    /// <summary>
    /// Presents a frame of only the launch screen, as the window opens: before it's shown, for the
    /// backends that present to a hidden window, and in <see cref="Show"/>.
    /// </summary>
    public void PresentFrame()
    {
        if (this._graphics.AcquireSwapchain())
        {
            this.Draw();
            this._graphics.SubmitFrame();
        }
    }

    /// <summary>
    /// Draws the launch screen over the whole window, at full opacity.
    /// </summary>
    public void Draw()
    {
        // Nothing has drawn this frame: the clear starts it
        this._graphics.Clear(LaunchScreen.Background);
        this.Draw(1f);
    }

    /// <summary>
    /// Draws the launch screen over a frame the game drew: opaque until it's dismissed, then fading
    /// to clear over <see cref="GameHost.LaunchScreenFadeDuration"/>, after which it's gone.
    /// </summary>
    public void DrawOverGame()
    {
        if (this._isDone)
        {
            return;
        }

        if (this._fadeStart == 0)
        {
            this.Draw(1f);
            return;
        }

        float t = (float)(Stopwatch.GetElapsedTime(this._fadeStart) / GameHost.LaunchScreenFadeDuration);
        if (t >= 1f)
        {
            this._isDone = true;
            this.Dispose();
            return;
        }

        this.Draw(1f - GameHost.EaseLaunchScreenFade(t));
    }

    public void Dispose()
    {
        if (this._isDisposed)
        {
            return;
        }

        this._isDisposed = true;
        this._batch.Dispose();
        this._frame?.Dispose();
        this._logo?.Texture.Dispose();
        this._branding?.Texture.Dispose();
    }

    // The launch screen's texture, over the window at an opacity
    private void Draw(
        float opacity)
    {
        float scale = this._graphics.DisplayScale;
        var frame = this.EnsureFrame(scale);

        this._graphics.SetRenderTarget(null);
        var batch = this._batch.Interface;
        batch.Begin();
        batch.Draw(frame, Vector2.Zero, Color.White * opacity, default, Vector2.Zero, new Vector2(1f / scale), null, DotGame.Framework.Graphics.SpriteFlip.None, default);
        batch.End();
    }

    // The launch screen drawn into a texture of the window's pixels, made again when they change
    private Strategy<ITextureStrategy> EnsureFrame(
        float scale)
    {
        var size = new Vector2Int((int)this._graphics.SwapchainWidth, (int)this._graphics.SwapchainHeight);
        if ((this._frame is { } existing) && (this._frameSize == size))
        {
            return existing;
        }

        this._frame?.Dispose();
        var frame = this._graphics.CreateRenderTexture(size);
        this._frame = frame;
        this._frameSize = size;

        SDL.GetWindowSize(this._window, out int width, out int height);
        var (logoPosition, brandingPosition) = LaunchScreen.Layout(new Vector2(width, height));

        this._graphics.SetRenderTarget(frame);
        this._graphics.Clear(LaunchScreen.Background);
        var batch = this._batch.Interface;
        batch.Begin(System.Numerics.Matrix4x4.CreateScale(scale, scale, 1));
        DrawImage(batch, this._logo, logoPosition, LaunchScreen.LogoSize);
        DrawImage(batch, this._branding, brandingPosition, LaunchScreen.BrandingSize);
        batch.End();
        this._graphics.SetRenderTarget(null);

        return frame;
    }

    // An image at its launch screen size (its texture is made at a higher scale)
    private static void DrawImage(
        ISpriteBatchStrategy batch,
        (Strategy<ITextureStrategy> Texture, Vector2Int Size)? image,
        Vector2 position,
        Vector2 size)
    {
        if (image is not { } loaded)
        {
            return;
        }

        var scale = new Vector2(size.X / loaded.Size.X, size.Y / loaded.Size.Y);
        batch.Draw(loaded.Texture, position, Color.White, default, Vector2.Zero, scale, null, DotGame.Framework.Graphics.SpriteFlip.None, default);
    }

    // A launch screen texture, or nothing (and a trace) if it can't be read: the launch screen
    // then shows without it
    private static (Strategy<ITextureStrategy>, Vector2Int)? LoadTexture(
        GraphicsServiceStrategy graphics,
        ContentServiceStrategy content,
        string contentKey)
    {
        try
        {
            string path = content.GetFullPath(content.ResolveContentPath(contentKey));
            using var texture = Ktx2Texture.ReadFile(path);
            if (texture.Format != Ktx2PixelFormat.Rgba8)
            {
                throw new InvalidDataException($"'{path}' is {texture.Format}, not RGBA8.");
            }

            return (graphics.CreateTexture(texture.Size, MemoryMarshal.Cast<byte, Color32>(texture.Data)), texture.Size);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Trace.Write(0x5c2e9a71, $"[LaunchScreen] '{contentKey}' wasn't loaded, so the launch screen shows without it: {ex.Message}");
            return null;
        }
    }
}
