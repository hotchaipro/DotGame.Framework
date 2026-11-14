// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Numerics;
using System.Threading.Tasks;
using DotGame.Framework;
using DotGame.Framework.Diagnostics;
using DotGame.Framework.Graphics;
using DotGame.Framework.Input;

namespace Sample.Pages;

/// <summary>
/// Drawing into a <see cref="RenderTexture"/> and then drawing it like any texture, clearing a
/// target, clipping drawing to a rectangle with
/// <see cref="GraphicsService.ScissorRectangle"/>, and reading a render texture's pixels back
/// with <see cref="GraphicsService.ReadPixelsAsync"/> to show them again.
/// </summary>
/// <remarks>
/// A render texture is useful for anything drawn once and shown many times (a card, a
/// minimap), or drawn in pieces and transformed as a whole. Set the render target and the
/// scissor rectangle between batches: a batch draws with the state that's current when it
/// draws its sprites. Reading pixels back is for the occasional snapshot (a picture to share,
/// a video's frames), not every frame: the game waits for the GPU to finish the frame.
/// </remarks>
internal sealed class RenderTexturesPage : SamplePage
{
    private const int TextureSize = 256;

    private Texture _checker = null!;
    private Texture _circle = null!;
    private RenderTexture _canvas = null!;
    private bool _clip = true;
    private RectangleInt _clipButton;
    private Texture? _snapshot;
    private string _snapshotStatus = "No snapshot yet";
    private bool _snapshotRequested;
    private bool _snapshotBusy;
    private RectangleInt _snapshotButton;

    public RenderTexturesPage(
        FrameworkSample game)
        : base(game)
    {
    }

    public override string Title => "Render textures and clipping";

    public override string Hint => "The tiles are one render texture, redrawn each frame; Snapshot reads it back.";

    public override async Task Load()
    {
        this._checker = await this.Game.Content.LoadAsync<Texture>("Textures/checker");
        this._circle = await this.Game.Content.LoadAsync<Texture>("Textures/circle");

        // Render textures are created, not loaded, at the size they're drawn at
        this._canvas = this.Game.Graphics.CreateRenderTexture(new Vector2Int(TextureSize, TextureSize));
    }

    public override void Draw(
        SpriteBatch spriteBatch,
        RectangleInt bounds)
    {
        var game = this.Game;
        var graphics = game.Graphics;
        var elapsed = (float)game.Time.Total.TotalSeconds;

        // 2. Draw the render texture several times, turned and scaled like any texture
        using (spriteBatch.Begin())
        {
            // What's drawn before a change of target stays on the screen
            spriteBatch.Draw(this._circle, new Vector2(bounds.X + 8, bounds.Y + 8), Color.FromRgb(0x2E8B57));

            this.DrawCanvas(spriteBatch, graphics, elapsed);

            var tileScale = MathF.Min(bounds.Width / 4f, 160) / TextureSize;
            for (int i = 0; i < 3; i++)
            {
                var center = new Vector2(bounds.X + ((i + 0.5f) * bounds.Width / 3f), bounds.Y + 120);
                var rotation = Angle.FromRadians((i - 1) * 0.3f);
                spriteBatch.Draw(this._canvas, center, Color.White, rotation, new Vector2(TextureSize / 2f), new Vector2(tileScale));
            }
        }

        // 3. A strip that scrolls a checkerboard past, clipped to its rectangle. The scissor
        // rectangle is in screen pixels and limits everything drawn until it's set back to null.
        // The last snapshot is beside it.
        const int SnapshotSize = 120;
        var strip = new RectangleInt(bounds.X + 40, bounds.Y + 260, bounds.Width - 96 - SnapshotSize, 120);
        var snapshotBounds = new RectangleInt(strip.Right + 16, strip.Y, SnapshotSize, SnapshotSize);
        using var batch = spriteBatch.Begin();
        game.FillRectangle(spriteBatch, new RectangleInt(strip.X - 2, strip.Y - 2, strip.Width + 4, strip.Height + 4), Color.FromRgb(0x5A8CE6));
        game.FillRectangle(spriteBatch, new RectangleInt(snapshotBounds.X - 2, snapshotBounds.Y - 2, snapshotBounds.Width + 4, snapshotBounds.Height + 4), Color.FromRgb(0x5A8CE6));
        game.FillRectangle(spriteBatch, snapshotBounds, Color.FromRgb(0x282E42));
        if (this._snapshot is not null)
        {
            spriteBatch.Draw(this._snapshot, (Vector2)snapshotBounds.Position, Color.White, Angle.Zero, Vector2.Zero, new Vector2((float)SnapshotSize / TextureSize));
        }

        game.Text.Draw(spriteBatch, this._snapshotStatus, new Vector2(snapshotBounds.Right - PixelText.Measure(this._snapshotStatus, 0.5f), snapshotBounds.Bottom + 8), Color.FromRgb(0xBEC8DC), scale: 0.5f);

        // NOTE: The scissor can change during a batch: what's drawn before it keeps the limit it had
        ScissorScope clip = default;
        if (this._clip)
        {
            clip = graphics.UseScissor(strip);
        }

        var scroll = (elapsed * 60) % 128;
        for (int x = -1; x <= (strip.Width / 128) + 1; x++)
        {
            spriteBatch.Draw(this._checker, new Vector2(strip.X + (x * 128) - scroll, strip.Y - 4), Color.White, Angle.Zero, Vector2.Zero, Vector2.One);
        }

        clip.Dispose();

        this._clipButton = new RectangleInt(bounds.X + 12, bounds.Bottom - 60, 200, 48);
        game.DrawButton(spriteBatch, this._clipButton, this._clip ? "Clipping on" : "Clipping off", this._clip);
        this._snapshotButton = new RectangleInt(this._clipButton.Right + 12, bounds.Bottom - 60, 200, 48);
        game.DrawButton(spriteBatch, this._snapshotButton, this._snapshotBusy ? "Reading..." : "Snapshot", this._snapshotBusy);
    }

    // Draws into the render texture, from inside a batch this method knows nothing about. Its
    // coordinates are its own pixels, from its top left, and the batch comes back as it was
    private void DrawCanvas(
        SpriteBatch spriteBatch,
        GraphicsService graphics,
        float elapsed)
    {
        using (spriteBatch.UseRenderTarget(this._canvas))
        {
            graphics.Clear(Color.FromRgb(0xFFECC8));
            for (int i = 0; i < 6; i++)
            {
                var angle = (elapsed * 0.8f) + (i * MathF.Tau / 6);
                var position = new Vector2(TextureSize / 2f) + (new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 80);
                var color = new Color(1f - (i / 6f), 0.4f, i / 6f);
                spriteBatch.Draw(this._circle, position, color, Angle.Zero, (Vector2)this._circle.Size * 0.5f, new Vector2(0.6f));
            }

            // A snapshot is read here, after the texture is drawn: a read gets what's been drawn to
            // the texture so far this frame
            if (this._snapshotRequested)
            {
                this._snapshotRequested = false;
                _ = this.TakeSnapshotAsync();
            }
        }
    }

    // Reads the render texture back and shows its pixels as a texture of their own, as a game
    // would before sharing a picture of its board
    private async Task TakeSnapshotAsync()
    {
        this._snapshotBusy = true;
        try
        {
            // The pixels come back once the frame's drawing has run, on the game thread: RGBA,
            // premultiplied as drawn, the top row first. The array mustn't change until then.
            var size = this._canvas.Size;
            var pixels = new Color32[size.X * size.Y];
            await this.Game.Graphics.ReadPixelsAsync(this._canvas, pixels);

            // Textures take premultiplied pixels, so they go straight into one. A game would
            // encode them as a PNG to share (DotGame.Imaging's PngCodec.Encode) or feed them to a video.
            this._snapshot?.Dispose();
            this._snapshot = this.Game.Graphics.CreateTexture(size, pixels);

            this._snapshotStatus = $"{size.X}x{size.Y} read back";
        }
        catch (Exception e)
        {
            // The task is let go, so show what went wrong
            this._snapshotStatus = $"Snapshot failed: {e.Message}";
            Trace.Write(0x4d1c7a2e, exception: e);
        }
        finally
        {
            this._snapshotBusy = false;
        }
    }

    public override void OnPointerEvent(
        PointerEvent e)
    {
        if (IsTap(e, this._clipButton))
        {
            this._clip = !this._clip;
        }
        else if (IsTap(e, this._snapshotButton) && !this._snapshotBusy)
        {
            this._snapshotRequested = true;
        }
    }
}
