# Graphics

Everything you draw goes through [`Game.Graphics`](../api/Framework/GraphicsService.md), usually with
a [`SpriteBatch`](../api/Framework/SpriteBatch.md).

## Drawing sprites

A batch collects sprites from `Begin` until the scope it returns ends, and draws them together. Use the
shared one unless you have a reason not to:

```csharp
protected override void OnDraw()
{
    this.Graphics.Clear(Color.Black);

    var spriteBatch = this.Graphics.SharedSpriteBatch;
    using var batch = spriteBatch.Begin();
    spriteBatch.Draw(this.texture, new Vector2(32, 32), Color.White);
}
```

For more than one batch in a method, give each a block: `using (spriteBatch.Begin()) { ... }`. An
exception ends the batch too, and a scope that's thrown away, `spriteBatch.Begin();`, fails the build
(rule DG0006).

Positions are in pixels, with (0, 0) at the top left of the window and Y going down. Things draw in
the order you draw them, so later sprites cover earlier ones. There are no layers or depth.

`Draw` has one set of optional parameters, so name the ones you use:

```csharp
spriteBatch.Draw(
    this.sheet,
    position,
    Color.White,
    rotation: Angle.FromDegrees(45),
    origin: new Vector2(95, 104),          // The point it's placed, turned and scaled about
    scale: new Vector2(2),
    sourceRectangle: new Rectangle(557, 746, 190, 208));   // Just this part of the texture
```

To face a sprite the other way, flip it: `flip: SpriteFlip.Horizontal` mirrors its image inside the
rectangle it's drawn in, so it stays in place whatever its origin. A negative scale mirrors it about its
origin instead (`scale: new Vector2(-1, 1)`), which moves it unless the origin is at its center.

### Keeping draws cheap

The batch draws in one go until a sprite needs different state: a different texture, shader, blend
or sampler. Ending a batch and beginning another with the same settings doesn't start a new draw. A
thousand sprites from one texture cost about the same as a few. Put sprites drawn together
in one texture, an atlas, and check the result with the
[detailed stats overlay](diagnostics.md#the-stats-overlay), which shows the draw calls per frame.

## Colors

[`Color`](../api/Framework/Color.md) holds floats and is stored with **premultiplied alpha**, as
textures are. One rule covers it: everything that makes a color takes straight alpha, unless its name
says `Premultiplied`:

```csharp
// A palette, with colors written as a design tool shows them
var sky = Color.FromRgb(0x87CEEB);
var accent = Color.FromRgb(0xFFD54F);
var highlight = Color.FromRgb(0xFFD54F, 0.35f);
var scrim = Color.Black.WithAlpha(0.6f);

// Worked out from numbers, from 0 to 1
var pulse = new Color(0.8f, 0.8f, 1f);
var haze = new Color(1f, 1f, 1f, 0.25f);

// Fading whatever a color already is, and blending two
var faded = highlight * 0.5f;
var between = Color.Lerp(Color.Red, Color.Blue, 0.25f);

// Adding to what's behind it: color, but no alpha
var glow = Color.FromPremultiplied(1f, 0.5f, 0f, 0f);
```

`WithAlpha` sets alpha, and `*` multiplies it: above, `faded` has an alpha of 0.175.

- **To fade something, multiply its color.** `Color.White * 0.5f` draws a sprite at half opacity.
- **Pixels you supply must be premultiplied**, in
  [`CreateTexture`](../api/Framework/GraphicsService.md#createtexture) and
  [`SetData`](../api/Framework/Texture.md#setdata). [`Color32.FromRgba`](../api/Framework/Color32.md#fromrgba)
  does that for you, and `Color32.FromPremultiplied` keeps its bytes as they are.

There are only a few named colors, `White`, `Black`, `Gray`, `Red`, `Green`, `Blue`, `Yellow`, `Cyan`,
`Magenta` and `Transparent`. For any other, use its hex value.

## Textures

Most textures come from [content](content.md). You can also make them from pixels, and those you
dispose yourself:

```csharp
var pixels = new Color32[64 * 64];
for (int i = 0; i < pixels.Length; i++)
{
    pixels[i] = Color32.FromRgba((byte)i, 0, 0, 255);
}

var made = this.Graphics.CreateTexture(new Vector2Int(64, 64), pixels);

// Replace the pixels later: a game's own painting, or a video frame
made.SetData(pixels);

made.Dispose();
```

To fill a rectangle with a color, tint and scale a 1x1 white texture.

## Transforms

Pass a matrix to `Begin` to transform everything in the batch, which is how you scroll, zoom or scale
a whole scene:

```csharp
var transform = Matrix3x2.CreateScale(2) * Matrix3x2.CreateTranslation(100, 50);

using var batch = spriteBatch.Begin(transform);
spriteBatch.Draw(this.texture, Vector2.Zero, Color.White);
```

## Batch settings

[`SpriteBatchSettings`](../api/Framework/SpriteBatchSettings.md) chooses how a batch draws. Settings
hold for the whole batch, so begin another batch to change them:

```csharp
using var batch = spriteBatch.Begin(new SpriteBatchSettings
{
    Blend = BlendMode.Additive,
    Sampler = Sampler.PointClamp,
    Transform = Matrix3x2.CreateScale(4),
});
spriteBatch.Draw(this.texture, Vector2.Zero, Color.White);
```

| [`BlendMode`](../api/Framework/BlendMode.md) | What it does |
|---|---|
| `Alpha` | Covers what's behind, as far as the sprite is opaque: the default |
| `Additive` | Adds to what's behind, for light, fire and glows |
| `Multiply` | Multiplies what's behind, which darkens, for shadows |
| `Screen` | Lightens without blowing out |
| `Opaque` | Replaces what's behind, alpha included |

| [`Sampler`](../api/Framework/Sampler.md) | What it does |
|---|---|
| `LinearClamp` | Blends neighboring pixels, and stops at the edges: the default |
| `PointClamp` | Takes the nearest pixel: for crisp pixel art |
| `LinearRepeat`, `PointRepeat` | As above, and the texture repeats past its edges |

## Triangles

[`DrawTriangles`](../api/Framework/SpriteBatch.md#drawtriangles) draws arbitrary shapes in the same
batch and order as sprites: trails, terrain, deformed sprites. Each [`Vertex`](../api/Framework/Vertex.md)
has a position, a texture coordinate from 0 to 1, and a color:

```csharp
ReadOnlySpan<Vertex> vertices =
[
    new Vertex(new Vector2(100, 100), new Vector2(0, 0), Color.Red),
    new Vertex(new Vector2(200, 100), new Vector2(1, 0), Color.Green),
    new Vertex(new Vector2(150, 200), new Vector2(0.5f, 1), Color.Blue),
];

using var batch = spriteBatch.Begin();
spriteBatch.DrawTriangles(this.texture, vertices);
```

Each triangle costs what a sprite costs. To share vertices between triangles, pass indices as well.

## Render textures

A [`RenderTexture`](../api/Framework/RenderTexture.md) is a texture you draw into, and then draw like
any other: for a scene you only redraw when it changes, or effects applied to a whole scene. It's
yours to dispose.

```csharp
protected override Task OnStart()
{
    this.canvas = this.Graphics.CreateRenderTexture(new Vector2Int(512, 512));
    return Task.CompletedTask;
}

protected override void OnDraw()
{
    var spriteBatch = this.Graphics.SharedSpriteBatch;

    using (this.Graphics.UseRenderTarget(this.canvas))
    {
        this.Graphics.Clear(Color.Transparent);
        using (spriteBatch.Begin())
        {
            spriteBatch.Draw(this.texture, Vector2.Zero, Color.White);
        }
    }

    // Back on the screen
    this.Graphics.Clear(Color.Black);
    using var batch = spriteBatch.Begin();
    spriteBatch.Draw(this.canvas, new Vector2(100, 100), Color.White);
}
```

`Graphics.UseRenderTarget` can't change the target while a batch is drawing. Code that's handed a batch,
and doesn't know how it was begun, uses the batch's own:

```csharp
using (spriteBatch.UseRenderTarget(this.canvas))
{
    this.Graphics.Clear(Color.Transparent);
    spriteBatch.Draw(this.texture, Vector2.Zero, Color.White);
}

spriteBatch.Draw(this.canvas, new Vector2(100, 100), Color.White);
```

A target has its own coordinates, so the scope starts clean: no transform, the default shader, default
blending and sampling (or the settings you pass it), and no scissor. All of them come back when it ends.

It allocates nothing, but changing the target is one of the costlier things a GPU does, the more so on a
phone, and this changes it twice. A few a frame are fine; one a sprite isn't. Redrawing a texture only
when it changes, or before the frame's main batch, costs less.

**Render textures can lose their contents.** On mobile and on the web, the graphics device can be
lost and come back. The platform restores textures and shaders loaded through content, but what you
drew is gone, so rebuild it when [`DeviceReset`](../api/Framework/GraphicsService.md#devicereset) fires.

To read the pixels back, use
[`ReadPixelsAsync`](../api/Framework/GraphicsService.md#readpixelsasync). It finishes later, once the
frame's drawing has run. [DotGame.Imaging](https://github.com/hotchaipro/DotGame.Imaging/blob/main/docs/guide.md) can save the result as a PNG.

```csharp
private async Task<Color32[]> TakeSnapshot()
{
    var pixels = new Color32[this.canvas.Size.X * this.canvas.Size.Y];
    await this.Graphics.ReadPixelsAsync(this.canvas, pixels);
    return pixels;
}
```

## Clipping

[`SpriteBatch.UseScissor`](../api/Framework/SpriteBatch.md#usescissor) limits a batch's drawing to an
area, such as a scrolling list. It's in the batch's own coordinates, so its transform applies as it does to sprites, and
code that draws needn't know its caller's camera:

```csharp
using var batch = spriteBatch.Begin(Matrix3x2.CreateTranslation(-this.position));
using (spriteBatch.UseScissor(new Rectangle(20, 20, 300, 200)))
{
    spriteBatch.Draw(this.texture, Vector2.Zero, Color.White);
}
```

It has to stay a rectangle on the screen, so a batch whose transform rotates, other than by quarter
turns, throws. Its edges round outward to whole pixels.

[`Graphics.UseScissor`](../api/Framework/GraphicsService.md#usescissor) is the same limit without the
transform, in the coordinates of sprites with no transform. Use it for a limit fixed on the screen, such
as under a rotating camera, or one that spans batches. Both nest, each limited
by the one around it, and can change during a batch: what's drawn before the change keeps the limit it
had. Each change splits the draw. A scissor belongs to its target: a new one starts with none.

## Smooth edges

Sprites that are rotated, or drawn on a fractional position, can have jagged edges. Ask for
multisampling in the game's constructor, which smooths the edges of rotated quads:

<!-- no-check -->
```csharp
public MyGame()
{
    this.PreferredMultisampleCount = 4;
}
```

It's a request, and a device may give fewer: `Graphics.MultisampleCount` says what it gave. It can't
change later, and render textures aren't multisampled, so a game that draws its scene through one
gets no benefit.

## Rectangles and vectors

[`Rectangle`](../api/Framework/Rectangle.md) and [`RectangleInt`](../api/Framework/RectangleInt.md)
have what you'd expect, such as `Contains` and `Center`. They're immutable, so `Inflate` and
`Offset` are static and return a new rectangle. The build flags code that ignores the result:

```csharp
var bounds = new RectangleInt(10, 10, 100, 50);
var larger = RectangleInt.Inflate(bounds, 4, 4);
bool hit = larger.Contains(new Vector2(12, 12));
```

Positions and sizes use `System.Numerics.Vector2`, or
[`Vector2Int`](../api/Framework/Vector2Int.md) for whole numbers, which converts to `Vector2`
implicitly. [`Angle`](../api/Framework/Angle.md) says which unit it's in by how you make it:
`Angle.FromDegrees(90)`.

## See also

- [Shaders](shaders.md), for custom effects
- [`SpriteBatch`](../api/Framework/SpriteBatch.md) and
  [`GraphicsService`](../api/Framework/GraphicsService.md)
- The framework sample's Sprites, Render textures and Blending pages
