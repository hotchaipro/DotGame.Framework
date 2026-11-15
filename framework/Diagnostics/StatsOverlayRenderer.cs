// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Diagnostics;
using DotGame.Framework.Graphics;

namespace DotGame.Framework.Diagnostics;

/// <summary>
/// Renders the on-screen stats overlay using the framework's own
/// <see cref="SpriteBatch"/>. Because every platform implements
/// <c>SpriteBatch</c>, this single renderer works everywhere — no
/// per-platform strategy required.
/// </summary>
/// <remarks>
/// <para>
/// The overlay costs as little as it can, so turning it on hardly changes what it measures. It's
/// drawn into a render texture, which every frame draws as one sprite. Four times a second, the
/// numbers are formatted, and only if one has changed is the texture drawn again: the words, drawn
/// once into a texture of their own when the layout changes, as one sprite, and the numbers a
/// sprite a character, from a texture of the embedded 3x5 font. No content pipeline assets, and no
/// allocations after the textures are made.
/// </para>
/// <para>
/// The renderer wraps its draw calls in
/// <see cref="StatsCollector.Suspend"/> / <see cref="StatsCollector.Resume"/>
/// so its own SpriteBatch traffic does not pollute the counters being
/// displayed, takes its draw calls and sprites out of the counts, and its time out of the
/// dropped-frame check.
/// </para>
/// </remarks>
internal sealed class StatsOverlayRenderer : IDisposable
{
    // 3x5 font: each glyph is 5 bytes (one row per byte, low 3 bits used).
    // Glyph order matches GlyphIndex(): the digits, '.', ' ', 'A' to 'Z', then '%'.
    private static ReadOnlySpan<byte> Font => new byte[]
    {
        0b111, 0b101, 0b101, 0b101, 0b111, // '0'
        0b010, 0b110, 0b010, 0b010, 0b111, // '1'
        0b111, 0b001, 0b111, 0b100, 0b111, // '2'
        0b111, 0b001, 0b111, 0b001, 0b111, // '3'
        0b101, 0b101, 0b111, 0b001, 0b001, // '4'
        0b111, 0b100, 0b111, 0b001, 0b111, // '5'
        0b111, 0b100, 0b111, 0b101, 0b111, // '6'
        0b111, 0b001, 0b010, 0b010, 0b010, // '7'
        0b111, 0b101, 0b111, 0b101, 0b111, // '8'
        0b111, 0b101, 0b111, 0b001, 0b111, // '9'
        0b000, 0b000, 0b000, 0b000, 0b010, // '.'
        0b000, 0b000, 0b000, 0b000, 0b000, // ' '
        0b010, 0b101, 0b111, 0b101, 0b101, // 'A'
        0b110, 0b101, 0b110, 0b101, 0b110, // 'B'
        0b011, 0b100, 0b100, 0b100, 0b011, // 'C'
        0b110, 0b101, 0b101, 0b101, 0b110, // 'D'
        0b111, 0b100, 0b110, 0b100, 0b111, // 'E'
        0b111, 0b100, 0b110, 0b100, 0b100, // 'F'
        0b011, 0b100, 0b101, 0b101, 0b011, // 'G'
        0b101, 0b101, 0b111, 0b101, 0b101, // 'H'
        0b111, 0b010, 0b010, 0b010, 0b111, // 'I'
        0b001, 0b001, 0b001, 0b101, 0b010, // 'J'
        0b101, 0b101, 0b110, 0b101, 0b101, // 'K'
        0b100, 0b100, 0b100, 0b100, 0b111, // 'L'
        0b101, 0b111, 0b111, 0b101, 0b101, // 'M'
        0b110, 0b101, 0b101, 0b101, 0b101, // 'N'
        0b010, 0b101, 0b101, 0b101, 0b010, // 'O'
        0b110, 0b101, 0b110, 0b100, 0b100, // 'P'
        0b010, 0b101, 0b101, 0b110, 0b011, // 'Q'
        0b110, 0b101, 0b110, 0b101, 0b101, // 'R'
        0b011, 0b100, 0b010, 0b001, 0b110, // 'S'
        0b111, 0b010, 0b010, 0b010, 0b010, // 'T'
        0b101, 0b101, 0b101, 0b101, 0b111, // 'U'
        0b101, 0b101, 0b101, 0b101, 0b010, // 'V'
        0b101, 0b101, 0b111, 0b111, 0b101, // 'W'
        0b101, 0b101, 0b010, 0b101, 0b101, // 'X'
        0b101, 0b101, 0b010, 0b010, 0b010, // 'Y'
        0b111, 0b001, 0b010, 0b100, 0b111, // 'Z'
        0b101, 0b001, 0b010, 0b100, 0b101, // '%'
    };

    private const int GlyphWidth = 3;
    private const int GlyphHeight = 5;
    private const int GlyphCount = 39;
    private const int GlyphSpace = 11;      // ' ', which isn't drawn
    private const int GlyphPercent = 38;    // '%'
    private const int GlyphAdvance = 4;     // 3 px + 1 px tracking, in the text and in the glyph texture
    private const int LineAdvance = 7;      // 5 px + 2 px leading
    private const int Pad = 4;              // panel padding (logical px)

    // The columns of the detailed overlay, in characters: the labels, then the framework's times
    // under FRAMEWORK and the game's under GAME, then the unit
    private const int ValueColumn = 8;
    private const int GameColumn = 19;
    private const int UnitColumn = 25;

    // The widest line of each mode, in characters, which sizes the panel
    private const int FpsOnlyChars = 10;    // FPS 120.0, with room
    private const int DetailedChars = 31;   // DRAW CALLS 1234 SPRITES 123456

    // Indexes in Colors, the colors a number can be drawn in
    private const byte ValueColor = 0;
    private const byte FrameworkColor = 1;
    private const byte GameColor = 2;

    // How often the numbers are formatted, and drawn if they've changed: four times a second,
    // which is also slow enough to read them
    private static readonly long RefreshTicks = Stopwatch.Frequency / 4;

    private static readonly SpriteBatchSettings PointSampling = new() { Sampler = Sampler.PointClamp };

    // A dark panel, 75% opaque: the text reads over any game, which shows through
    private static readonly Color PanelColor = Color.FromPremultiplied(0f, 0f, 0f, 0.75f);

    // The colors a number can be drawn in, by index in the number list
    private static readonly Color[] Colors =
    [
        Color.White,                                    // A value
        Color.FromPremultiplied(0.45f, 0.82f, 1f, 1f),  // The framework's time (cyan)
        Color.FromPremultiplied(0.8f, 0.6f, 1f, 1f),    // The game's time (violet)
        Color.FromPremultiplied(0.4f, 0.9f, 0.45f, 1f), // Good (green)
        Color.FromPremultiplied(1f, 0.85f, 0.25f, 1f),  // A warning (yellow)
        Color.FromPremultiplied(1f, 0.4f, 0.35f, 1f),   // A problem (red)
    ];

    private static readonly Color LabelColor = Color.FromPremultiplied(0.72f, 0.72f, 0.72f, 1f);

    private readonly GraphicsService _graphics;
    private SpriteBatch? _batch;
    private Texture? _glyphs;

    // The words, drawn when the layout changes, and the whole overlay, drawn when a number does
    private RenderTexture? _labels;
    private RenderTexture? _panel;
    private Vector2Int _panelSize;
    private int _scale = 3;
    private StatsOverlayMode _layoutMode;
    private bool _layoutIsPreferred;
    private bool _labelsChanged = true;
    private long _nextRefreshTicks;

    // The numbers as last formatted, and as the panel shows them: entries of a line, a column, a
    // color, a glyph count and the glyphs. A length of -1 shows nothing yet.
    private readonly byte[] _numbers = new byte[256];
    private readonly byte[] _shown = new byte[256];
    private int _numbersLength;
    private int _shownLength = -1;

    public StatsOverlayRenderer(
        GraphicsService graphics)
    {
        this._graphics = graphics;
    }

    public void Draw(
        in StatsSnapshot snapshot,
        RectangleInt safeBounds,
        StatsOverlayMode mode)
    {
        long start = Stopwatch.GetTimestamp();
        StatsCollector.Suspend();
        try
        {
            this.EnsureInitialized();
            var batch = this._batch!;

            // FpsOnly: one line. Detailed: six (FPS and the target rate, the columns' headings,
            // update and draw time, dropped frames and the game thread's share of each frame,
            // draw calls and sprites).
            bool detailed = mode == StatsOverlayMode.Detailed;
            int lines = detailed ? 6 : 1;
            int panelUnitsW = (Pad * 2) + ((detailed ? DetailedChars : FpsOnlyChars) * GlyphAdvance) - 1;
            int panelUnitsH = (Pad * 2) + (LineAdvance * lines) - 2;

            // A whole number of pixels per font pixel, so every glyph is drawn alike: about 1/220
            // of the safe area's height, so it reads across screen densities, but no wider than
            // the safe area
            int scale = Math.Max(3, (int)MathF.Round(safeBounds.Height / 220f));
            scale = Math.Max(2, Math.Min(scale, (safeBounds.Width - 16) / panelUnitsW));
            var size = new Vector2Int(panelUnitsW * scale, panelUnitsH * scale);

            // The layout: the panel's size, and the words, which change with the mode, and when the
            // target is the game's preferred rate rather than the display's
            bool isPreferred = detailed && (snapshot.TargetHz < snapshot.DisplayHz - 0.5f);
            if ((this._panel is null) || (size != this._panelSize))
            {
                this._labels?.Dispose();
                this._panel?.Dispose();
                this._labels = this._graphics.CreateRenderTexture(size);
                this._panel = this._graphics.CreateRenderTexture(size);
                this._panelSize = size;
                this._labelsChanged = true;
            }

            if ((scale != this._scale) || (mode != this._layoutMode) || (isPreferred != this._layoutIsPreferred))
            {
                this._scale = scale;
                this._layoutMode = mode;
                this._layoutIsPreferred = isPreferred;
                this._labelsChanged = true;
            }

            // The numbers, four times a second, and drawn only if one has changed
            bool redraw = this._labelsChanged;
            if (redraw || (start >= this._nextRefreshTicks))
            {
                this._nextRefreshTicks = start + RefreshTicks;
                this._numbersLength = this.FormatNumbers(snapshot, detailed);
                redraw |= (this._shownLength < 0)
                    || !this._numbers.AsSpan(0, this._numbersLength).SequenceEqual(this._shown.AsSpan(0, this._shownLength));
            }

            int drawCalls = 1;
            int sprites = 1;
            int originX = safeBounds.X + 8;
            int originY = safeBounds.Y + 8;
            using (batch.Begin(PointSampling))
            {
                if (redraw)
                {
                    if (this._labelsChanged)
                    {
                        using (batch.UseRenderTarget(this._labels!, PointSampling))
                        {
                            this._graphics.Clear(Color.Transparent);
                            sprites += this.DrawLabels(batch, detailed, isPreferred);
                        }

                        drawCalls++;
                        this._labelsChanged = false;
                    }

                    using (batch.UseRenderTarget(this._panel!, PointSampling))
                    {
                        this._graphics.Clear(PanelColor);
                        batch.Draw(this._labels!, Vector2.Zero, Color.White);
                        int numbers = this.DrawNumbers(batch);
                        sprites += 1 + numbers;
                        drawCalls += (numbers > 0) ? 2 : 1;
                    }

                    this._numbers.AsSpan(0, this._numbersLength).CopyTo(this._shown);
                    this._shownLength = this._numbersLength;
                }

                batch.Draw(this._panel!, new Vector2(originX, originY), Color.White);
            }

            // The overlay's draws are counted with the rest of the frame when they're submitted,
            // so they're taken out of this frame's counts
            StatsCollector.ExcludeOverlayDraw(drawCalls, sprites);
        }
        finally
        {
            StatsCollector.Resume();
            StatsCollector.ExcludeOverlayTime(Stopwatch.GetTimestamp() - start);
        }
    }

    private void EnsureInitialized()
    {
        if (this._glyphs is null)
        {
            // The font's glyphs side by side, white on clear, a column apart
            const int Width = GlyphCount * GlyphAdvance;
            var pixels = new Color32[Width * GlyphHeight];
            var white = Color32.FromRgba(255, 255, 255, 255);
            var font = Font;
            for (int glyph = 0; glyph < GlyphCount; glyph++)
            {
                for (int row = 0; row < GlyphHeight; row++)
                {
                    byte bits = font[(glyph * GlyphHeight) + row];
                    for (int column = 0; column < GlyphWidth; column++)
                    {
                        if ((bits & (1 << (GlyphWidth - 1 - column))) != 0)
                        {
                            pixels[(row * Width) + (glyph * GlyphAdvance) + column] = white;
                        }
                    }
                }
            }

            this._glyphs = this._graphics.CreateTexture(new Vector2Int(Width, GlyphHeight), pixels);
            this._graphics.DeviceReset += this.OnDeviceReset;
        }

        if (this._batch is null)
        {
            this._batch = this._graphics.CreateSpriteBatch();
        }
    }

    // A reset loses what was drawn into the render textures, so both are drawn again
    private void OnDeviceReset(
        object? sender,
        EventArgs e)
    {
        this._labelsChanged = true;
    }

    // Draws the words into the labels texture, and returns the sprites it drew
    private int DrawLabels(
        SpriteBatch batch,
        bool detailed,
        bool isPreferred)
    {
        int sprites = this.DrawText(batch, 0, 0, "FPS", LabelColor);
        if (!detailed)
        {
            return sprites;
        }

        // FPS is judged by the target: the display's rate, or the game's preferred one
        sprites += this.DrawText(batch, 0, 11, isPreferred ? "TARGET" : "DISPLAY", LabelColor);
        sprites += this.DrawText(batch, 0, GameColumn + 4, "HZ", LabelColor);

        // The time columns' headings, in their values' colors
        sprites += this.DrawText(batch, 1, ValueColumn, "FRAMEWORK", Colors[FrameworkColor]);
        sprites += this.DrawText(batch, 1, GameColumn, "GAME", Colors[GameColor]);
        sprites += this.DrawText(batch, 2, 0, "UPDATE", LabelColor);
        sprites += this.DrawText(batch, 2, UnitColumn, "MS", LabelColor);
        sprites += this.DrawText(batch, 3, 0, "DRAW", LabelColor);
        sprites += this.DrawText(batch, 3, UnitColumn, "MS", LabelColor);
        sprites += this.DrawText(batch, 4, 0, "DROPPED", LabelColor);
        sprites += this.DrawText(batch, 4, GameColumn - 4, "CPU", LabelColor);
        sprites += this.DrawText(batch, 5, 0, "DRAW CALLS", LabelColor);
        sprites += this.DrawText(batch, 5, 17, "SPRITES", LabelColor);
        return sprites;
    }

    // Formats the numbers into the number list, each at its place and in its color, and returns
    // the list's length
    private int FormatNumbers(
        in StatsSnapshot snapshot,
        bool detailed)
    {
        // FPS, green at the target rate, yellow a little under, red well under
        float fpsShare = (snapshot.TargetHz > 0f) ? snapshot.Fps / snapshot.TargetHz : 1f;
        int length = this.AddNumber(0, 0, 4, Status(fpsShare >= 0.95f, fpsShare >= 0.75f), snapshot.Fps, 1);
        if (!detailed)
        {
            return length;
        }

        length = this.AddNumber(length, 0, GameColumn, ValueColor, snapshot.TargetHz, 0);

        // UPDATE and DRAW: the framework's time and the game's
        length = this.AddNumber(length, 2, ValueColumn, FrameworkColor, snapshot.UpdateFrameworkMs, 1);
        length = this.AddNumber(length, 2, GameColumn, GameColor, snapshot.UpdateAppMs, 1);
        length = this.AddNumber(length, 3, ValueColumn, FrameworkColor, snapshot.DrawFrameworkMs, 1);
        length = this.AddNumber(length, 3, GameColumn, GameColor, snapshot.DrawAppMs, 1);

        // DROPPED, green at none and red at any; CPU, the game thread's share of a frame at the
        // target rate, green under half, yellow to 80% and red above, where the game nears not
        // keeping up
        length = this.AddNumber(length, 4, ValueColumn, Status(snapshot.DroppedFrames == 0, false), snapshot.DroppedFrames, 0);
        length = this.AddNumber(length, 4, GameColumn, Status(snapshot.CpuPercent < 50f, snapshot.CpuPercent < 80f), snapshot.CpuPercent, 0, isPercent: true);

        // DRAW CALLS and SPRITES
        length = this.AddNumber(length, 5, 11, ValueColor, snapshot.DrawCalls, 0);
        return this.AddNumber(length, 5, UnitColumn, ValueColor, snapshot.Sprites, 0);
    }

    // Adds a number to the number list, and returns the list's new length
    private int AddNumber(
        int length,
        int line,
        int column,
        byte color,
        float value,
        int fractionDigits,
        bool isPercent = false)
    {
        Span<byte> glyphs = stackalloc byte[16];
        int count = FormatFixed(value, fractionDigits, glyphs);
        if (isPercent)
        {
            glyphs[count++] = GlyphPercent;
        }

        var numbers = this._numbers;
        if (length + 4 + count > numbers.Length)
        {
            return length;
        }

        numbers[length] = (byte)line;
        numbers[length + 1] = (byte)column;
        numbers[length + 2] = color;
        numbers[length + 3] = (byte)count;
        glyphs[..count].CopyTo(numbers.AsSpan(length + 4));
        return length + 4 + count;
    }

    // Draws the number list, and returns the sprites it drew
    private int DrawNumbers(
        SpriteBatch batch)
    {
        int sprites = 0;
        var numbers = this._numbers;
        for (int i = 0; i < this._numbersLength;)
        {
            int line = numbers[i];
            int column = numbers[i + 1];
            var color = Colors[numbers[i + 2]];
            int count = numbers[i + 3];
            for (int glyph = 0; glyph < count; glyph++)
            {
                sprites += this.DrawGlyph(batch, line, column + glyph, numbers[i + 4 + glyph], color);
            }

            i += 4 + count;
        }

        return sprites;
    }

    // Draws text at a line and column, and returns the sprites it drew
    private int DrawText(
        SpriteBatch batch,
        int line,
        int column,
        string text,
        Color color)
    {
        int sprites = 0;
        for (int i = 0; i < text.Length; i++)
        {
            sprites += this.DrawGlyph(batch, line, column + i, GlyphIndex(text[i]), color);
        }

        return sprites;
    }

    // Draws a glyph at a line and column, a sprite from the glyph texture, and returns the sprites
    // it drew: none for a space
    private int DrawGlyph(
        SpriteBatch batch,
        int line,
        int column,
        int glyph,
        Color color)
    {
        if (glyph == GlyphSpace)
        {
            return 0;
        }

        int scale = this._scale;
        var position = new Vector2((Pad + (column * GlyphAdvance)) * scale, (Pad + (line * LineAdvance)) * scale);
        var source = new Rectangle(glyph * GlyphAdvance, 0, GlyphWidth, GlyphHeight);
        batch.Draw(this._glyphs!, position, color, Angle.Zero, Vector2.Zero, new Vector2(scale), source);
        return 1;
    }

    // A judged value's color: green when good, yellow when it's a warning, red otherwise
    private static byte Status(
        bool isGood,
        bool isWarning)
    {
        return isGood ? (byte)3 : isWarning ? (byte)4 : (byte)5;
    }

    private static byte GlyphIndex(
        char c)
    {
        if (c >= '0' && c <= '9')
        {
            return (byte)(c - '0');
        }

        if (c >= 'A' && c <= 'Z')
        {
            return (byte)(12 + (c - 'A'));
        }

        return c switch
        {
            '.' => 10,
            '%' => GlyphPercent,
            _ => GlyphSpace, // anything else -> blank
        };
    }

    /// <summary>
    /// Formats a non-negative float into ASCII digit indices with a
    /// fixed number of fractional digits. Avoids string allocation.
    /// Caller passes a buffer large enough (16 bytes is plenty for the
    /// values we display).
    /// </summary>
    private static int FormatFixed(
        float value,
        int fractionDigits,
        Span<byte> buffer)
    {
        if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f)
        {
            value = 0f;
        }

        // Round to the requested precision.
        float scale = 1f;
        for (int i = 0; i < fractionDigits; i++)
        {
            scale *= 10f;
        }
        long scaled = (long)MathF.Round(value * scale);

        // Split integer / fractional.
        long intPart = scaled / (long)scale;
        long fracPart = scaled - (intPart * (long)scale);

        // Write integer part (at least one digit).
        Span<byte> tmp = stackalloc byte[12];
        int t = 0;
        if (intPart == 0)
        {
            tmp[t++] = 0;
        }
        else
        {
            while (intPart > 0 && t < tmp.Length)
            {
                tmp[t++] = (byte)(intPart % 10);
                intPart /= 10;
            }
        }

        int n = 0;
        for (int i = t - 1; i >= 0 && n < buffer.Length; i--)
        {
            buffer[n++] = tmp[i];
        }

        if (fractionDigits > 0 && n < buffer.Length)
        {
            buffer[n++] = 10; // '.'
            for (int i = fractionDigits - 1; i >= 0 && n < buffer.Length; i--)
            {
                long div = 1;
                for (int j = 0; j < i; j++)
                {
                    div *= 10;
                }

                buffer[n++] = (byte)((fracPart / div) % 10);
            }
        }

        return n;
    }

    public void Dispose()
    {
        if (this._glyphs is not null)
        {
            this._graphics.DeviceReset -= this.OnDeviceReset;
        }

        this._batch?.Dispose();
        this._batch = null;
        this._glyphs?.Dispose();
        this._glyphs = null;
        this._labels?.Dispose();
        this._labels = null;
        this._panel?.Dispose();
        this._panel = null;
    }
}
