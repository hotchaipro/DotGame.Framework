// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Numerics;
using System.Threading.Tasks;
using DotGame.Framework;
using DotGame.Framework.Graphics;

namespace Sample.Pages;

/// <summary>
/// A batch's settings: its blend mode says how sprites combine with what's behind them, and its
/// sampler how textures are read. Both are chosen when a batch begins.
/// </summary>
internal sealed class BlendingPage : SamplePage
{
    private static readonly (string Name, BlendMode Mode)[] Blends =
    [
        ("Alpha", BlendMode.Alpha),
        ("Additive", BlendMode.Additive),
        ("Multiply", BlendMode.Multiply),
        ("Screen", BlendMode.Screen),
        ("Opaque", BlendMode.Opaque),
    ];

    private static readonly (string Name, Sampler Sampler)[] Samplers =
    [
        ("LinearClamp", Sampler.LinearClamp),
        ("PointClamp", Sampler.PointClamp),
        ("LinearRepeat", Sampler.LinearRepeat),
        ("PointRepeat", Sampler.PointRepeat),
    ];

    private Texture _circle = null!;
    private Texture _tiny = null!;

    public BlendingPage(
        FrameworkSample game)
        : base(game)
    {
    }

    public override string Title => "Blend modes and samplers";

    public override string Hint => "Chosen when a batch begins: SpriteBatchSettings.Blend and Sampler.";

    public override async Task Load()
    {
        this._circle = await this.Game.Content.LoadAsync<Texture>("Textures/circle");

        // Four pixels, to show how each sampler reads them
        this._tiny = this.Game.Graphics.CreateTexture(
            new Vector2Int(2, 2),
            [Color32.FromRgba(255, 80, 80, 255), Color32.FromRgba(80, 200, 255, 255), Color32.FromRgba(255, 220, 80, 255), Color32.FromRgba(120, 220, 120, 255)]);
    }

    public override void Draw(
        SpriteBatch spriteBatch,
        RectangleInt bounds)
    {
        var game = this.Game;
        float cellWidth = bounds.Width / (float)Blends.Length;
        float size = MathF.Min(cellWidth - 16, 120);

        for (int i = 0; i < Blends.Length; i++)
        {
            var position = new Vector2(bounds.X + (i * cellWidth) + 8, bounds.Y + 12);

            // What's behind: half dark blue, half light
            using (spriteBatch.Begin())
            {
                spriteBatch.Draw(game.Pixel, position, Color.FromRgb(0x28468C), Angle.Zero, default, new Vector2(size / 2, size));
                spriteBatch.Draw(game.Pixel, position + new Vector2(size / 2, 0), Color.FromRgb(0xE1D7BE), Angle.Zero, default, new Vector2(size / 2, size));
                game.Text.Draw(spriteBatch, Blends[i].Name, position + new Vector2(0, size + 6), Color.White, 0.7f);
            }

            // A batch draws with one blend mode
            using var batch = spriteBatch.Begin(new SpriteBatchSettings { Blend = Blends[i].Mode });
            spriteBatch.Draw(this._circle, position + new Vector2(size / 2), Color.FromRgb(0xE66E3C), Angle.Zero, (Vector2)this._circle.Size * 0.5f, new Vector2(size * 0.8f / this._circle.Size.X));
        }

        float sampleTop = bounds.Y + size + 48;
        float sampleWidth = bounds.Width / (float)Samplers.Length;
        float sampleSize = MathF.Min(sampleWidth - 16, 120);
        for (int i = 0; i < Samplers.Length; i++)
        {
            var position = new Vector2(bounds.X + (i * sampleWidth) + 8, sampleTop);

            using (spriteBatch.Begin(new SpriteBatchSettings { Sampler = Samplers[i].Sampler }))
            {

                // A source rectangle larger than the texture shows what happens past its edges
                spriteBatch.Draw(this._tiny, position, Color.White, Angle.Zero, default, new Vector2(sampleSize / 6), new Rectangle(0, 0, 6, 6));
            }

            using var batch = spriteBatch.Begin();
            game.Text.Draw(spriteBatch, Samplers[i].Name, position + new Vector2(0, sampleSize + 6), Color.White, 0.7f);
        }
    }
}
