// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Threading.Tasks;
using DotGame.Framework;
using DotGame.Framework.Graphics;
using DotGame.Framework.Input;

namespace Sample;

/// <summary>
/// One page of the sample: a self-contained example of one part of the framework.
/// </summary>
internal abstract class SamplePage
{
    protected SamplePage(
        FrameworkSample game)
    {
        this.Game = game;
    }

    protected FrameworkSample Game { get; }

    /// <summary>
    /// Gets the page's title, shown in the header.
    /// </summary>
    public abstract string Title { get; }

    /// <summary>
    /// Gets a line of instructions, shown in the footer.
    /// </summary>
    public abstract string Hint { get; }

    /// <summary>
    /// Loads the page's content, once, when the game starts.
    /// </summary>
    public virtual Task Load() => Task.CompletedTask;

    /// <summary>
    /// Called when the page is shown.
    /// </summary>
    public virtual void Enter()
    {
    }

    /// <summary>
    /// Called when another page is shown instead.
    /// </summary>
    public virtual void Leave()
    {
    }

    public virtual void OnPause()
    {
    }

    public virtual void OnResume()
    {
    }

    public virtual void Update()
    {
    }

    /// <summary>
    /// Draws the page inside <paramref name="bounds"/>. The sprite batch isn't begun: a page
    /// begins and ends it as often as it needs to.
    /// </summary>
    public abstract void Draw(
        SpriteBatch spriteBatch,
        RectangleInt bounds);

    public virtual void OnPointerEvent(
        PointerEvent e)
    {
    }

    public virtual void OnKeyEvent(
        KeyEvent e)
    {
    }

    /// <summary>
    /// Gets whether a pointer event is a tap that began inside a rectangle.
    /// </summary>
    protected static bool IsTap(
        in PointerEvent e,
        RectangleInt bounds)
    {
        return (e.Kind == PointerEventKind.Began) && bounds.Contains(e.Position);
    }
}
