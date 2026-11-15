// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Numerics;
using System.Threading.Tasks;
using DotGame.Framework;
using DotGame.Framework.Graphics;
using DotGame.Framework.Input;

namespace Sample.Pages;

/// <summary>
/// A custom shader (<c>Content/Shaders/wave.slang</c>): a parameter shared by a batch, set with
/// <see cref="ShaderParameter.SetValue(float)"/>, and per-sprite values passed with each sprite as
/// <see cref="ShaderCustomData"/>.
/// </summary>
/// <remarks>
/// Every sprite here is a different one from the sprite sheet, with its own wave, yet they're
/// drawn in one batch, and one draw call, as they share the sheet's texture and the shader:
/// turn on the detailed stats overlay (press ` (backtick) twice) to see the draw calls per
/// frame. A shader's parameters apply to the whole batch; per-sprite data is what lets sprites with different
/// settings share it.
/// </remarks>
internal sealed class ShadersPage : SamplePage
{
    private Texture _sheet = null!;
    private Shader _wave = null!;
    private ShaderParameter _time = null!;
    private bool _useShader = true;
    private RectangleInt _toggleButton;
    private RectangleInt _statsButton;

    public ShadersPage(
        FrameworkSample game)
        : base(game)
    {
    }

    public override string Title => "Shaders";

    public override string Hint => "Each sprite's wave is its shader data: one draw. Press ` (backtick) for stats.";

    public override async Task Load()
    {
        this._sheet = await this.Game.Content.LoadAsync<Texture>("Textures/spritesheet");

        // A shader's parameters are looked up once, by their names in the shader
        this._wave = await this.Game.Content.LoadAsync<Shader>("Shaders/wave");
        this._time = this._wave.GetParameter("Time");
    }

    public override void Draw(
        SpriteBatch spriteBatch,
        RectangleInt bounds)
    {
        var game = this.Game;

        // Parameters are set before drawing, and apply to everything drawn with the shader
        this._time.SetValue((float)game.Time.Total.TotalSeconds);
        using (spriteBatch.Begin())
        {

            const int Columns = 4;
            const int Rows = 3;
            var cell = new Vector2(bounds.Width / (float)Columns, (bounds.Height - 80) / (float)Rows);
            var spriteSize = SpriteSheet.Source(0).Size;
            var scale = new Vector2(MathF.Min(cell.X / spriteSize.X, cell.Y / spriteSize.Y) * 0.8f);
            using (spriteBatch.UseShader(this._useShader ? this._wave : null))
            {
                for (int row = 0; row < Rows; row++)
                {
                    for (int column = 0; column < Columns; column++)
                    {
                        int i = (row * Columns) + column;
                        var center = new Vector2(bounds.X + ((column + 0.5f) * cell.X), bounds.Y + ((row + 0.5f) * cell.Y));

                        // Wave size (in the sheet's texture coordinates, so 0.004 is 4 pixels) and phase
                        // (in turns), different for every sprite. At most 12 pixels, less than the clear
                        // space around each character in its source rectangle, so the wave neither cuts
                        // the character off nor shows a neighbor. The colors are left alone: the sprites
                        // page shows tinting
                        var data = new ShaderCustomData((row + 1) * 0.004f, i / (float)(Rows * Columns), 0, 0);

                        spriteBatch.Draw(this._sheet, center, Color.White, origin: spriteSize * 0.5f, scale: scale, sourceRectangle: SpriteSheet.Source(i), customData: data);
                    }
                }
            }
        }

        using var batch = spriteBatch.Begin();
        this._toggleButton = new RectangleInt(bounds.X + 12, bounds.Bottom - 60, 240, 48);
        this._statsButton = new RectangleInt(bounds.X + 264, bounds.Bottom - 60, 200, 48);
        game.DrawButton(spriteBatch, this._toggleButton, this._useShader ? "Shader: wave" : "Shader: default", this._useShader);
        game.DrawButton(spriteBatch, this._statsButton, "Stats overlay");
    }

    public override void OnPointerEvent(
        PointerEvent e)
    {
        if (IsTap(e, this._toggleButton))
        {
            // The default shader ignores the shader data
            this._useShader = !this._useShader;
        }
        else if (IsTap(e, this._statsButton))
        {
            this.Game.CycleStatsOverlay();
        }
    }
}
