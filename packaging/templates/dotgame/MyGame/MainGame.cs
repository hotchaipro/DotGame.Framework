using System.Numerics;
using System.Threading.Tasks;
using DotGame.Framework;
using DotGame.Framework.Graphics;
using DotGame.Framework.Input;

namespace MyGame;

/// <summary>
/// The game, shared by every platform. A ball bounces around the screen; tap or click to move it.
/// </summary>
internal sealed class MainGame : Game
{
    private Texture _ball = null!;
    private Vector2 _position = new(32, 32);
    private Vector2 _velocity = new(180, 120);

    protected override async Task OnStart()
    {
        // Runs once, before the first frame. Content is loaded by its path under Content, with no extension.
        this._ball = await this.Content.LoadAsync<Texture>("Textures/ball");
    }

    protected override void OnUpdate()
    {
        // Runs once a frame. Move by time, so the speed is the same at any frame rate.
        this._position += this._velocity * this.Time.DeltaSeconds;

        // Bounce off the edges
        var bounds = new Vector2(this.ClientBounds.Width, this.ClientBounds.Height) - this._ball.Size;
        if ((this._position.X < 0 && this._velocity.X < 0) || (this._position.X > bounds.X && this._velocity.X > 0))
        {
            this._velocity.X = -this._velocity.X;
        }

        if ((this._position.Y < 0 && this._velocity.Y < 0) || (this._position.Y > bounds.Y && this._velocity.Y > 0))
        {
            this._velocity.Y = -this._velocity.Y;
        }
    }

    protected override void OnPointerEvent(PointerEvent e)
    {
        // Touch, mouse and pen alike
        if (e.Kind == PointerEventKind.Began)
        {
            this._position = e.Position - ((Vector2)this._ball.Size / 2);
        }
    }

    protected override void OnDraw()
    {
        this.Graphics.Clear(Color.FromRgb(0x181C28));

        var spriteBatch = this.Graphics.SharedSpriteBatch;
        using var batch = spriteBatch.Begin();
        spriteBatch.Draw(this._ball, this._position, Color.White);
    }
}
