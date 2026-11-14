// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using DotGame.Framework;
using DotGame.Framework.Diagnostics;
using DotGame.Framework.Graphics;
using DotGame.Framework.Input;
using Sample.Pages;

namespace Sample;

/// <summary>
/// A tour of the DotGame framework: one page for each part of it, using nothing but the
/// framework. Each page is a small, self-contained example; start with the page for the feature
/// you need.
/// </summary>
/// <remarks>
/// <para>
/// A game derives from <see cref="Game"/> and overrides what it needs: <see cref="OnStart"/> to
/// load content, <see cref="OnUpdate"/> and <see cref="OnDraw"/> every frame, and the input and
/// lifecycle callbacks. The platform's engine (<c>DesktopGameEngine</c>, <c>AndroidGameEngine</c>,
/// <c>iOSGameEngine</c>, <c>WebGLGameEngine</c>) runs it; see each platform's project.
/// </para>
/// <para>
/// Move between pages with the arrows at the top, Tab and Shift+Tab, or the number keys. F1
/// cycles the stats overlay.
/// </para>
/// </remarks>
internal sealed class FrameworkSample : Game
{
    public FrameworkSample()
    {
        this.PreferredMultisampleCount = SampleOptions.MultisampleCount;
    }

    private const int HeaderHeight = 56;
    private const int FooterHeight = 40;

    private readonly List<SamplePage> _pages = new();
    private int _pageIndex;
    private bool _isLoaded;

    // Lifecycle counts, shown on the Time and Content page
    internal int PauseCount { get; private set; }

    internal int ResumeCount { get; private set; }

    internal int BoundsChangedCount { get; private set; }

    /// <summary>
    /// Gets a 1x1 white texture, for drawing rectangles in any color.
    /// </summary>
    internal Texture Pixel { get; private set; } = null!;

    /// <summary>
    /// Gets the sample's text drawer. The framework doesn't draw text; see <see cref="PixelText"/>.
    /// </summary>
    internal PixelText Text { get; private set; } = null!;

    /// <summary>
    /// Gets the area pages draw in: the safe area, less the header and footer.
    /// </summary>
    internal RectangleInt PageBounds
    {
        get
        {
            var safe = this.SafeAreaBounds;
            return new RectangleInt(safe.X, safe.Y + HeaderHeight, safe.Width, safe.Height - HeaderHeight - FooterHeight);
        }
    }

    protected override async Task OnStart()
    {
        // A texture made in code: one white pixel, tinted and scaled to draw rectangles
        this.Pixel = this.Graphics.CreateTexture(new Vector2Int(1, 1), [Color32.FromRgba(255, 255, 255, 255)]);

        // Content is loaded by key: the path under Content without its extension
        this.Text = new PixelText(await this.Content.LoadAsync<Texture>("Textures/ascii"));

        this._pages.Add(new SpritesPage(this));
        this._pages.Add(new ShadersPage(this));
        this._pages.Add(new RenderTexturesPage(this));
        this._pages.Add(new BlendingPage(this));
        this._pages.Add(new InputPage(this));
        this._pages.Add(new AudioPage(this));
        this._pages.Add(new TimeAndContentPage(this));

        foreach (var page in this._pages)
        {
            await page.Load();
        }

        this._isLoaded = true;
        this._pages[0].Enter();
    }

    protected override Task OnPause()
    {
        // The app went to the background: a game would pause its music and its simulation here
        this.PauseCount++;
        this.CurrentPage?.OnPause();
        return Task.CompletedTask;
    }

    protected override Task OnResume()
    {
        this.ResumeCount++;
        this.CurrentPage?.OnResume();
        return Task.CompletedTask;
    }

    protected override void OnClientBoundsChanged()
    {
        // The window was resized or the device rotated: ClientBounds and SafeAreaBounds have changed
        this.BoundsChangedCount++;
    }

    private SamplePage? CurrentPage => this._isLoaded ? this._pages[this._pageIndex] : null;

    private void ShowPage(
        int index)
    {
        if (!this._isLoaded)
        {
            return;
        }

        index = ((index % this._pages.Count) + this._pages.Count) % this._pages.Count;
        if (index == this._pageIndex)
        {
            return;
        }

        this._pages[this._pageIndex].Leave();
        this._pageIndex = index;
        this._pages[this._pageIndex].Enter();
    }

    protected override void OnUpdate()
    {
        this.CurrentPage?.Update();
    }

    protected override void OnPointerEvent(
        PointerEvent e)
    {
        // Taps on the header's arrows change pages; everything else goes to the page
        if ((e.Kind == PointerEventKind.Began) && (e.Position.Y < this.SafeAreaBounds.Y + HeaderHeight))
        {
            var safe = this.SafeAreaBounds;
            if (e.Position.X < safe.X + HeaderHeight)
            {
                this.ShowPage(this._pageIndex - 1);
            }
            else if (e.Position.X > safe.Right - HeaderHeight)
            {
                this.ShowPage(this._pageIndex + 1);
            }

            return;
        }

        this.CurrentPage?.OnPointerEvent(e);
    }

    protected override void OnKeyEvent(
        KeyEvent e)
    {
        if (e.Kind == KeyEventKind.Pressed)
        {
            switch (e.Key)
            {
                case Key.Tab:
                    this.ShowPage(this._pageIndex + (((e.Modifiers & ModifierKeys.Shift) != 0) ? -1 : 1));
                    return;

                case >= Key.D1 and <= Key.D6:
                    this.ShowPage(e.Key - Key.D1);
                    return;

                case Key.F1:
                    this.CycleStatsOverlay();
                    return;
            }
        }

        this.CurrentPage?.OnKeyEvent(e);
    }

    /// <summary>
    /// Cycles the stats overlay: off, frames per second, and detailed (update and draw times,
    /// and draw calls and sprites per frame).
    /// </summary>
    internal void CycleStatsOverlay()
    {
        this.Diagnostics.StatsOverlayMode = this.Diagnostics.StatsOverlayMode switch
        {
            StatsOverlayMode.Off => StatsOverlayMode.FpsOnly,
            StatsOverlayMode.FpsOnly => StatsOverlayMode.Detailed,
            _ => StatsOverlayMode.Off,
        };
    }

    protected override void OnDraw()
    {
        // Clear the whole window, then let the page draw inside the safe area: the part of the
        // screen that notches, rounded corners and system bars don't cover
        this.Graphics.Clear(Color.FromRgb(0x181C28));

        var page = this.CurrentPage;
        if (page is null)
        {
            return;
        }

        var spriteBatch = this.Graphics.SharedSpriteBatch;
        page.Draw(spriteBatch, this.PageBounds);

        // The header and footer are drawn last, over the page
        var safe = this.SafeAreaBounds;
        using var batch = spriteBatch.Begin();
        this.FillRectangle(spriteBatch, new RectangleInt(safe.X, safe.Y, safe.Width, HeaderHeight), Color.FromRgb(0x282E42));
        this.Text.Draw(spriteBatch, "<", new Vector2(safe.X + 18, safe.Y + 12), Color.White);
        this.Text.Draw(spriteBatch, ">", new Vector2(safe.Right - 38, safe.Y + 12), Color.White);
        var title = $"{this._pageIndex + 1}/{this._pages.Count}  {page.Title}";
        this.Text.Draw(spriteBatch, title, new Vector2(safe.Center.X - (PixelText.Measure(title) / 2), safe.Y + 12), Color.White);

        var footer = new RectangleInt(safe.X, safe.Bottom - FooterHeight, safe.Width, FooterHeight);
        this.FillRectangle(spriteBatch, footer, Color.FromRgb(0x282E42));
        this.Text.Draw(spriteBatch, page.Hint, new Vector2(footer.X + 12, footer.Y + 10), Color.FromRgb(0xBEC8DC), scale: 0.6f);
    }

    /// <summary>
    /// Fills a rectangle by stretching the 1x1 white texture over it.
    /// </summary>
    internal void FillRectangle(
        SpriteBatch spriteBatch,
        RectangleInt rectangle,
        Color color)
    {
        spriteBatch.Draw(this.Pixel, rectangle.Position, color, scale: rectangle.Size);
    }

    /// <summary>
    /// Draws a labeled button and returns its bounds; pages test taps against them.
    /// </summary>
    internal void DrawButton(
        SpriteBatch spriteBatch,
        RectangleInt bounds,
        string label,
        bool isActive = false)
    {
        this.FillRectangle(spriteBatch, bounds, isActive ? Color.FromRgb(0x5A8CE6) : Color.FromRgb(0x3C4664));
        var scale = 0.7f;
        var width = PixelText.Measure(label, scale);
        this.Text.Draw(spriteBatch, label, new Vector2(bounds.Center.X - (width / 2), bounds.Center.Y - (PixelText.LineHeight * scale / 2)), Color.White, scale);
    }
}
