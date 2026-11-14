// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using DotGame.Framework;
using DotGame.Framework.Graphics;
using DotGame.Framework.Input;

namespace Sample.Pages;

/// <summary>
/// Drawing textures with <see cref="SpriteBatch"/>: a sprite from a sprite sheet by its source
/// rectangle, color and opacity, rotation around an origin, scale and flipping, and moving a sprite
/// with the pointer: drag it, pinch to scale and rotate it, or scroll the mouse wheel. Tap the sheet
/// to pick the sprite.
/// </summary>
internal sealed class SpritesPage : SamplePage
{
    private static readonly Color[] Tints = [Color.White, Color.FromRgb(0xFF8C8C), Color.FromRgb(0x8CC8FF), Color.FromRgb(0xA0F08C)];

    // The sheet drawn whole, in the top left corner, as a picker: a tap on a sprite selects it
    private const float SheetWidth = 192;

    private Texture _sheet = null!;
    private int _sprite = SpriteSheet.Hero;
    private RectangleInt _sheetBounds;

    private Vector2 _position;
    private float _scale = 1;
    private Angle _rotation;
    private bool _flipX;
    private int _tint;
    private float _opacity = 1;

    // Dragging and pinching
    private readonly Dictionary<int, Vector2> _touches = new();
    private int _dragPointerId = -1;
    private Vector2 _dragOffset;
    private float _pinchStartDistance;
    private float _pinchStartScale;
    private Angle _lastPinchAngle;

    private RectangleInt _flipButton;
    private RectangleInt _tintButton;
    private RectangleInt _fadeButton;

    public SpritesPage(
        FrameworkSample game)
        : base(game)
    {
    }

    public override string Title => "Sprites";

    public override string Hint => "Tap the sheet to pick a sprite. Drag it; pinch, or scroll (+Shift), to scale and turn it.";

    public override async Task Load()
    {
        this._sheet = await this.Game.Content.LoadAsync<Texture>("Textures/spritesheet");
    }

    public override void Enter()
    {
        this._position = this.Game.PageBounds.Center;
    }

    public override void Draw(
        SpriteBatch spriteBatch,
        RectangleInt bounds)
    {
        var game = this.Game;
        using var batch = spriteBatch.Begin();

        // The whole sheet in a corner: a texture drawn with no source rectangle. Every sprite on this
        // page is a rectangle of it, the selected one outlined, and the rectangle is what Draw takes
        var sheetScale = SheetWidth / this._sheet.Size.X;
        var sheetOrigin = new Vector2(bounds.X + 12, bounds.Y + 12);
        this._sheetBounds = new RectangleInt((int)sheetOrigin.X, (int)sheetOrigin.Y, (int)SheetWidth, (int)(this._sheet.Size.Y * sheetScale));
        spriteBatch.Draw(this._sheet, sheetOrigin, Color.White, Angle.Zero, Vector2.Zero, new Vector2(sheetScale));

        var source = SpriteSheet.Source(this._sprite);
        this.Outline(spriteBatch, sheetOrigin + (source.Position * sheetScale), source.Size * sheetScale, Color.FromRgb(0xFFD25A));
        game.Text.Draw(spriteBatch, $"Source {source.X}, {source.Y}, {source.Width}, {source.Height}", new Vector2(this._sheetBounds.X, this._sheetBounds.Bottom + 6), Color.Gray, 0.5f);

        // Copies that turn on their own, in a row: rotation is around the origin, here the
        // sprite's center, and color multiplies the texture's
        var elapsed = (float)game.Time.Total.TotalSeconds;
        var origin = source.Size * 0.5f;
        for (int i = 0; i < 4; i++)
        {
            var spin = Angle.FullCircle * ((elapsed / (3 + i)) % 1);
            var position = new Vector2(bounds.Right - 60, bounds.Y + 70 + (i * 90));
            // Every other copy is flipped to face left (a negative scale; see below)
            var scale = new Vector2(0.32f);
            if ((i % 2) == 1)
            {
                scale.X = -scale.X;
            }

            spriteBatch.Draw(this._sheet, position, Tints[i], spin, origin, scale, source);
        }

        // The sprite the pointer moves

        // Flipping is a negative scale: it mirrors the sprite around its origin (its center here,
        // so it flips in place)
        var spriteScale = new Vector2(this._scale);
        if (this._flipX)
        {
            spriteScale.X = -spriteScale.X;
        }

        spriteBatch.Draw(this._sheet, this._position, Tints[this._tint] * this._opacity, this._rotation, origin, spriteScale, source);

        // Buttons along the bottom
        int y = bounds.Bottom - 60;
        this._flipButton = new RectangleInt(bounds.X + 12, y, 120, 48);
        this._tintButton = new RectangleInt(bounds.X + 144, y, 120, 48);
        this._fadeButton = new RectangleInt(bounds.X + 276, y, 200, 48);
        game.DrawButton(spriteBatch, this._flipButton, "Flip", this._flipX);
        game.DrawButton(spriteBatch, this._tintButton, "Tint");
        game.DrawButton(spriteBatch, this._fadeButton, $"Opacity {this._opacity:0.0}");
    }

    public override void OnPointerEvent(
        PointerEvent e)
    {
        if (IsTap(e, this._flipButton))
        {
            this._flipX = !this._flipX;
            return;
        }

        if (IsTap(e, this._tintButton))
        {
            this._tint = (this._tint + 1) % Tints.Length;
            return;
        }

        if (IsTap(e, this._fadeButton))
        {
            this._opacity = (this._opacity <= 0.25f) ? 1 : this._opacity - 0.25f;
            return;
        }

        // A tap on the sheet picks the sprite in the cell under it
        if (IsTap(e, this._sheetBounds))
        {
            var cell = (e.Position - this._sheetBounds.Position) / (this._sheetBounds.Size.X / (float)SpriteSheet.Columns);
            this._sprite = Math.Clamp(((int)cell.Y * SpriteSheet.Columns) + (int)cell.X, 0, SpriteSheet.Count - 1);
            return;
        }

        // The mouse wheel scales; with Shift held, it rotates
        if (e.ScrollWheelDelta.Y != 0)
        {
            var keyboard = this.Game.Input.GetKeyboardState();
            if ((keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                this._rotation += Angle.FromDegrees(e.ScrollWheelDelta.Y * 10);
            }
            else
            {
                this._scale = Math.Clamp(this._scale * (1 + (e.ScrollWheelDelta.Y * 0.1f)), 0.2f, 5);
            }

            return;
        }

        switch (e.Kind)
        {
            case PointerEventKind.Began:
                this._touches[e.Id] = e.Position;
                if (this._touches.Count == 1)
                {
                    this._dragPointerId = e.Id;
                    this._dragOffset = this._position - e.Position;
                }
                else
                {
                    this._pinchStartDistance = 0;
                }

                break;

            case PointerEventKind.Moved:
                if (!this._touches.ContainsKey(e.Id))
                {
                    break;
                }

                this._touches[e.Id] = e.Position;
                if (this._touches.Count >= 2)
                {
                    this.Pinch();
                }
                else if (e.Id == this._dragPointerId)
                {
                    this._position = e.Position + this._dragOffset;
                }

                break;

            case PointerEventKind.Ended:
            case PointerEventKind.Canceled:
                this._touches.Remove(e.Id);
                this._pinchStartDistance = 0;

                // Carry on dragging with a finger that's still down
                this._dragPointerId = -1;
                foreach (var (id, position) in this._touches)
                {
                    this._dragPointerId = id;
                    this._dragOffset = this._position - position;
                    break;
                }

                break;
        }
    }

    // Two fingers: their distance scales the sprite, their angle turns it, and their midpoint moves it
    private void Pinch()
    {
        using var touches = this._touches.Values.GetEnumerator();
        touches.MoveNext();
        var p1 = touches.Current;
        touches.MoveNext();
        var p2 = touches.Current;

        var distance = Vector2.Distance(p1, p2);
        var midpoint = (p1 + p2) * 0.5f;
        var angle = Angle.FromVector(p2 - p1);

        if (this._pinchStartDistance == 0)
        {
            this._pinchStartDistance = distance;
            this._pinchStartScale = this._scale;
            this._lastPinchAngle = angle;
            this._dragOffset = this._position - midpoint;
            return;
        }

        this._scale = Math.Clamp(this._pinchStartScale * (distance / this._pinchStartDistance), 0.2f, 5);

        // Wrapped to [-180°, 180°], so crossing the angle's boundary doesn't spin the sprite around
        this._rotation += Angle.Wrap(angle - this._lastPinchAngle);
        this._lastPinchAngle = angle;
        this._position = midpoint + this._dragOffset;
    }

    // A rectangle's outline, as four thin bars
    private void Outline(
        SpriteBatch spriteBatch,
        Vector2 position,
        Vector2 size,
        Color color)
    {
        const int Thickness = 2;
        var x = (int)position.X;
        var y = (int)position.Y;
        var w = (int)size.X;
        var h = (int)size.Y;
        this.Game.FillRectangle(spriteBatch, new RectangleInt(x - Thickness, y - Thickness, w + (2 * Thickness), Thickness), color);
        this.Game.FillRectangle(spriteBatch, new RectangleInt(x - Thickness, y + h, w + (2 * Thickness), Thickness), color);
        this.Game.FillRectangle(spriteBatch, new RectangleInt(x - Thickness, y, Thickness, h), color);
        this.Game.FillRectangle(spriteBatch, new RectangleInt(x + w, y, Thickness, h), color);
    }
}
