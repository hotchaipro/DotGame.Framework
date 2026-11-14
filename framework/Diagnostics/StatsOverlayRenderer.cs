// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
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
/// Uses an embedded 3x5 pixel font and a 1x1 white texture; no content
/// pipeline assets, no allocations after the one-time initialization.
/// Numeric formatting writes into a stack-allocated <see cref="Span{T}"/>
/// to avoid per-frame string allocation.
/// </para>
/// <para>
/// The renderer wraps its draw calls in
/// <see cref="StatsCollector.Suspend"/> / <see cref="StatsCollector.Resume"/>
/// so its own SpriteBatch traffic does not pollute the counters being
/// displayed.
/// </para>
/// </remarks>
internal sealed class StatsOverlayRenderer : IDisposable
{
    // 3x5 font: each glyph is 5 bytes (one row per byte, low 3 bits used).
    // Glyph order matches GlyphIndex().
    private static ReadOnlySpan<byte> Font => new byte[]
    {
        0b111, 0b101, 0b101, 0b101, 0b111, // 0: '0'
        0b010, 0b110, 0b010, 0b010, 0b111, // 1: '1'
        0b111, 0b001, 0b111, 0b100, 0b111, // 2: '2'
        0b111, 0b001, 0b111, 0b001, 0b111, // 3: '3'
        0b101, 0b101, 0b111, 0b001, 0b001, // 4: '4'
        0b111, 0b100, 0b111, 0b001, 0b111, // 5: '5'
        0b111, 0b100, 0b111, 0b101, 0b111, // 6: '6'
        0b111, 0b001, 0b010, 0b010, 0b010, // 7: '7'
        0b111, 0b101, 0b111, 0b101, 0b111, // 8: '8'
        0b111, 0b101, 0b111, 0b001, 0b111, // 9: '9'
        0b000, 0b000, 0b000, 0b000, 0b010, // 10: '.'
        0b000, 0b000, 0b000, 0b000, 0b000, // 11: ' '
        0b111, 0b100, 0b111, 0b100, 0b100, // 12: 'F'
        0b111, 0b101, 0b111, 0b100, 0b100, // 13: 'P'
        0b111, 0b100, 0b111, 0b001, 0b111, // 14: 'S' (same as 5)
        0b101, 0b101, 0b101, 0b101, 0b111, // 15: 'U'
        0b111, 0b101, 0b110, 0b101, 0b101, // 16: 'R'
        0b110, 0b101, 0b101, 0b101, 0b110, // 17: 'D'
        0b101, 0b101, 0b111, 0b111, 0b101, // 18: 'W'
        0b110, 0b101, 0b110, 0b101, 0b110, // 19: 'B'
        0b010, 0b101, 0b111, 0b101, 0b101, // 20: 'A'
        0b111, 0b010, 0b010, 0b010, 0b010, // 21: 'T'
    };

    private const int GlyphWidth = 3;
    private const int GlyphHeight = 5;
    private const int GlyphAdvance = 4;     // 3 px + 1 px tracking
    private const int LineAdvance = 7;      // 5 px + 2 px leading
    private const int Pad = 4;              // panel padding (logical px)

    // Target ~3% of the safe-area height per glyph row, clamped to a
    // sensible range. Re-computed each frame from the supplied bounds
    // so the overlay is readable on both phones and desktop monitors.
    private float _scale = 3f;

    private readonly GraphicsService _graphics;
    private SpriteBatch? _batch;
    private Texture? _whitePixel;

    // Sprites the overlay drew this frame, removed from the counts it shows
    private int _spriteCount;

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
        StatsCollector.Suspend();
        try
        {
            this.EnsureInitialized();

            var batch = this._batch!;
            var pixel = this._whitePixel!;

            // Pick a scale that keeps the overlay readable across
            // device DPIs. 5 logical glyph rows should occupy ~3% of
            // the safe-area height; clamp so it never disappears or
            // dominates the screen.
            float scale = MathF.Max(3f, MathF.Round(safeBounds.Height / 220f));
            this._scale = scale;

            // FpsOnly: 1 line.  Detailed: 4 lines (FPS, UPD, DRW, BAT).
            int lines = mode == StatsOverlayMode.Detailed ? 4 : 1;
            const int contentWidth = GlyphAdvance * 14; // up to ~14 chars per line
            int panelW = (int)(((Pad * 2) + contentWidth) * scale);
            int panelH = (int)(((Pad * 2) + (LineAdvance * lines)) * scale);

            int originX = safeBounds.X + 8;
            int originY = safeBounds.Y + 8;

            this._spriteCount = 0;
            using (batch.Begin())
            {

                // Translucent black panel.
                this.DrawRect(batch, pixel, originX, originY, panelW, panelH, Color.FromPremultiplied(0f, 0f, 0f, 0.55f));

                int textX = originX + (int)(Pad * scale);
                int textY = originY + (int)(Pad * scale);
                var fg = Color.White;
                var fgFw = Color.FromPremultiplied(0.55f, 0.85f, 1f, 1f);   // framework portion (cyan)
                var fgApp = Color.FromPremultiplied(1f, 0.85f, 0.4f, 1f);   // app portion (amber)
                var fgDim = Color.FromPremultiplied(0.7f, 0.7f, 0.7f, 1f);  // secondary count (gray)

                Span<byte> buf = stackalloc byte[16];

                // Line 1: FPS <value>
                int x = textX;
                x = this.WriteLabel(batch, pixel, x, textY, "FPS ", fg);
                int len = FormatFixed(snapshot.Fps, 1, buf);
                x = this.WriteGlyphs(batch, pixel, x, textY, buf[..len], fg);
                x = this.WriteLabel(batch, pixel, x, textY, " WORST ", fg);
                len = FormatFixed(snapshot.WorstFrameMs, 1, buf);
                this.WriteGlyphs(batch, pixel, x, textY, buf[..len], fg);

                if (mode == StatsOverlayMode.Detailed)
                {
                    int y = textY + (int)(LineAdvance * scale);

                    // Line 2: UPD <fw> <app>
                    x = textX;
                    x = this.WriteLabel(batch, pixel, x, y, "UPD ", fg);
                    len = FormatFixed(snapshot.UpdateFrameworkMs, 1, buf);
                    x = this.WriteGlyphs(batch, pixel, x, y, buf[..len], fgFw);
                    x = this.WriteLabel(batch, pixel, x, y, " ", fg);
                    len = FormatFixed(snapshot.UpdateAppMs, 1, buf);
                    this.WriteGlyphs(batch, pixel, x, y, buf[..len], fgApp);

                    y += (int)(LineAdvance * scale);

                    // Line 3: DRW <fw> <app>
                    x = textX;
                    x = this.WriteLabel(batch, pixel, x, y, "DRW ", fg);
                    len = FormatFixed(snapshot.DrawFrameworkMs, 1, buf);
                    x = this.WriteGlyphs(batch, pixel, x, y, buf[..len], fgFw);
                    x = this.WriteLabel(batch, pixel, x, y, " ", fg);
                    len = FormatFixed(snapshot.DrawAppMs, 1, buf);
                    this.WriteGlyphs(batch, pixel, x, y, buf[..len], fgApp);

                    y += (int)(LineAdvance * scale);

                    // Line 4: BAT <draw calls> <sprites>
                    x = textX;
                    x = this.WriteLabel(batch, pixel, x, y, "BAT ", fg);
                    len = FormatFixed(snapshot.DrawCalls, 0, buf);
                    x = this.WriteGlyphs(batch, pixel, x, y, buf[..len], fg);
                    x = this.WriteLabel(batch, pixel, x, y, " ", fg);
                    len = FormatFixed(snapshot.Sprites, 0, buf);
                    this.WriteGlyphs(batch, pixel, x, y, buf[..len], fgDim);
                }
            }

            // The overlay's batch is one draw call, counted with the rest of the
            // frame when it's submitted, so take it out of this frame's counts
            StatsCollector.ExcludeOverlayDraw(this._spriteCount);
        }
        finally
        {
            StatsCollector.Resume();
        }
    }

    private void EnsureInitialized()
    {
        if (this._whitePixel is null)
        {
            this._whitePixel = this._graphics.CreateTexture(
                new Vector2Int(1, 1),
                new[] { Color32.FromRgba(255, 255, 255, 255) });
        }

        if (this._batch is null)
        {
            this._batch = this._graphics.CreateSpriteBatch();
        }
    }

    private int WriteLabel(
        SpriteBatch batch,
        Texture pixel,
        int x,
        int y,
        string text,
        Color color)
    {
        Span<byte> ids = stackalloc byte[16];
        int n = 0;
        for (int i = 0; i < text.Length && n < ids.Length; i++)
        {
            ids[n++] = GlyphIndex(text[i]);
        }
        return this.WriteGlyphs(batch, pixel, x, y, ids[..n], color);
    }

    private int WriteGlyphs(
        SpriteBatch batch,
        Texture pixel,
        int x,
        int y,
        ReadOnlySpan<byte> ids,
        Color color)
    {
        var font = Font;
        float scale = this._scale;
        int px = (int)scale;
        for (int i = 0; i < ids.Length; i++)
        {
            int g = ids[i];
            int baseIdx = g * GlyphHeight;
            for (int row = 0; row < GlyphHeight; row++)
            {
                byte bits = font[baseIdx + row];
                for (int col = 0; col < GlyphWidth; col++)
                {
                    if ((bits & (1 << (GlyphWidth - 1 - col))) != 0)
                    {
                        this.DrawRect(batch, pixel,
                            x + (int)(col * scale),
                            y + (int)(row * scale),
                            px,
                            px,
                            color);
                    }
                }
            }
            x += (int)(GlyphAdvance * scale);
        }
        return x;
    }

    private void DrawRect(
        SpriteBatch batch,
        Texture pixel,
        int x,
        int y,
        int w,
        int h,
        Color color)
    {
        this._spriteCount++;
        batch.Draw(
            pixel,
            new Vector2(x, y),
            color,
            Angle.Zero,
            Vector2.Zero,
            new Vector2(w, h));
    }

    private static byte GlyphIndex(
        char c)
    {
        if (c >= '0' && c <= '9')
        {
            return (byte)(c - '0');
        }

        return c switch
        {
            '.' => 10,
            ' ' => 11,
            'F' => 12,
            'P' => 13,
            'S' => 14,
            'U' => 15,
            'R' => 16,
            'D' => 17,
            'W' => 18,
            'B' => 19,
            'A' => 20,
            'T' => 21,
            _ => 11, // unknown -> blank
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
        this._batch?.Dispose();
        this._batch = null;
        this._whitePixel?.Dispose();
        this._whitePixel = null;
    }
}
