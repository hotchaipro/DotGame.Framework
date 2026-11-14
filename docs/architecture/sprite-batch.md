# Sprite batch

`SpriteBatch` is how everything is drawn: textured sprites and textured triangles, in the order they're
submitted, through a transform, a shader, a blend mode and a sampler. It guarantees three things on
every platform:

- **Submission order.** What's drawn later covers what's drawn earlier, across textures, shaders,
  batches and render targets. There's no sorting.
- **The same geometry.** Every platform builds its vertices with the same shared code, so a sprite or a
  triangle lands on the same pixels everywhere.
- **Draws are combined wherever the state allows.** A run of sprites with the same texture and state
  is one draw call, and ending a batch and beginning another with the same state costs nothing.

It also carries four values per sprite to the shader, so a shader can vary from sprite to sprite
without splitting the draw.

## The layers

| Layer | Types | Files |
|---|---|---|
| Public API | `SpriteBatch`, `SpriteBatchScope`, `SpriteBatchSettings`, `ShaderScope`, `RenderTargetScope`, `ScissorScope`, `Vertex`, `ShaderCustomData` | [framework/Graphics](../../framework/Graphics) |
| Platform contract | `ISpriteBatchStrategy` | [framework/Platform/Graphics/ISpriteBatchStrategy.cs](../../framework/Platform/Graphics/ISpriteBatchStrategy.cs) |
| Shared geometry | `SpriteVertex` (internal), `CachedShaderValue` | [platform/shared/Graphics](../../platform/shared/Graphics) |
| Per platform | `SpriteBatchStrategy`, and the recorder or service that draws | `platform/desktop`, `platform/webgl`, `platform/android`, `platform/ios`, each under `Graphics` |

`SpriteBatch` checks arguments and scopes, and passes each sprite straight to its strategy. The
strategy writes the sprite's vertices with the shared code and decides where its draw splits. What
happens to the vertices after that differs per platform (see [Platforms](#platforms)).

## The public API

### Batches and scopes

`Begin` returns a `SpriteBatchScope`; disposing it ends the batch. Discarding the scope fails the build
(rule DG0006). A batch begins with `SpriteBatchSettings`: an optional `Matrix3x2` transform, a
`BlendMode` and a `Sampler`, defaulting to no transform, alpha blending and linear clamped sampling.

Within a batch, three scopes change state and nest, each restoring what was there before it:

- **`UseShader(shader)`** draws with a shader until the scope ends; `null` is the default sprite shader.
  Each batch starts with the default. `SpriteBatch` tells the strategy only when the shader object
  actually changes, and the strategies don't split a draw until a sprite is drawn with the new shader,
  so a scope that draws nothing, or one that sets the shader already in use, costs nothing.
- **`UseRenderTarget(texture, settings)`** ends the platform batch, switches the render target through
  `GraphicsService`, and begins again with `settings`, the default shader and no scissor. When the
  scope ends it does the reverse and restores the outer batch's settings and shader. Only one batch may
  be drawing while it does: changing the target under another open batch throws.
  `GraphicsService.UseRenderTarget` throws while any batch is drawing; this scope is the way to change
  targets from inside a batch.
- **`UseScissor(rectangle)`** converts a rectangle in the batch's coordinates through its transform,
  rounds the edges outward, and calls `GraphicsService.UseScissor`. The transform has to keep the
  rectangle a rectangle (no skew, rotation only by quarter turns), or it throws.

Scopes that end out of order are reported as non-fatal errors and recovered from, rather than thrown,
because they're usually discovered in a `Dispose` during unwinding.

### Sprites

```csharp
public void Draw(Texture texture, Vector2 position, Color color, Angle rotation = default,
    Vector2 origin = default, Vector2? scale = null, Rectangle? sourceRectangle = null,
    SpriteFlip flip = SpriteFlip.None, in ShaderCustomData customData = default);
```

The sprite is a rectangle the size of the texture or the source rectangle, placed at `position`,
rotated and scaled about `origin`. A negative scale mirrors it about the origin; `flip` mirrors the
image inside the rectangle by swapping texture coordinates, so it stays in place.

### Triangles

```csharp
public void DrawTriangles(Texture texture, ReadOnlySpan<Vertex> vertices);
public void DrawTriangles(Texture texture, ReadOnlySpan<Vertex> vertices, ReadOnlySpan<ushort> indices);

public readonly record struct Vertex(Vector2 Position, Vector2 TextureCoordinate, Color Color);
```

For geometry a rectangle can't describe: trails and ribbons that bend and fade, tapered or skewed
sprites, gradients, terrain, shapes. Positions are in the batch's space, before its transform, as a
sprite's position is. Texture coordinates run 0 to 1 across the whole texture. Each corner has its own
color, interpolated across the triangle. Triangles go through the batch's transform, shader, blend and
sampler, and draw in order with the sprites around them. Nothing is culled, so they can wind either
way. A vertex count, or an index count, that isn't a multiple of three throws.

The indexed overload shares vertices in the caller's data only: each triangle is expanded into its own
vertices when it's drawn.

### Per-sprite shader data

`ShaderCustomData` is four values (x, y, z, w) that travel with one sprite to its shader. A shader reads
them as a `float4` with the semantic `TEXCOORD1`. The default sprite shader ignores them, and sprites
drawn without them, and all triangles, pass zeros.

Shader parameters (`ShaderParameter`) hold values shared by every sprite drawn with a shader, and a
change of value splits the draw. Shader data holds values that differ per sprite and splits nothing, so
sprites with different data draw together: a dissolve amount per sprite, a wave phase, or a game's
text shader that reads each glyph's outline width and font settings from it.

The values travel as half floats: about three significant digits, from about 6×10⁻⁵ to 65504. That
covers fractions, phases, and distances in texels, which is what per-sprite settings usually are.

## The vertex

Every platform draws with one vertex layout, `SpriteVertex` in
[platform/shared/Graphics/SpriteVertex.cs](../../platform/shared/Graphics/SpriteVertex.cs):

| Offset | Field | Format |
|---|---|---|
| 0 | Position | 2 × float |
| 8 | Color | 4 × unsigned byte, normalized (R, G, B, A in memory order) |
| 12 | Texture coordinate | 2 × float |
| 20 | Shader data | 4 × half float (`TEXCOORD1`) |
| | **Total** | **28 bytes** |

- **Color as four bytes**, as XNA and MonoGame sprite batches store it. Shaders still read a `float4`
  from 0 to 1, as the GPU normalizes it. `PackColor` clamps each component to 0–1 and rounds to the
  nearest 1/255; NaN packs as 0. Colors are premultiplied, so a color brighter than 1 can't be
  expressed in the vertex.
- **Positions are transformed on the CPU.** The vertex holds the final position in the batch's
  target coordinates, and the vertex shader only applies the target's projection. So a batch's
  transform never splits a draw: sprites drawn with different transforms share one.
- **The public `Vertex` is a different type** from the internal `SpriteVertex` because they do
  different jobs. `Vertex` is the API for building geometry, with a `Color` and no shader data;
  `SpriteVertex` is the upload layout, with the color packed. The shared code converts one into the
  other.

### The shared geometry

`SpriteVertex` holds the only geometry code, used by every platform's strategy:

- **`WriteSprite`** folds the sprite's rotation and position into the batch transform's 2D affine part
  (only `M11`, `M12`, `M21`, `M22`, `M41` and `M42` are used), works out the source rectangle's texture
  coordinates and the flip, and writes the four corners.
- **`WriteQuad`** writes four corners, top-left, top-right, bottom-right and bottom-left, each with its
  own position and texture coordinates, one packed color and the shader data.
- **`WriteTriangle`** writes a triangle's three corners through the transform, each with its own color
  and zero shader data, and repeats the third as the fourth.

Every platform draws four vertices as two triangles with one index pattern: (0, 1, 2) and (0, 2, 3),
split along the top-left to bottom-right diagonal. A triangle fits the same pattern because its fourth
vertex repeats its third, making the second triangle (0, 2, 2) degenerate. So sprites and triangles
share one vertex buffer, one index buffer and one draw, and a triangle costs what a sprite costs; the
draw statistics count each as one sprite.

The geometry is unit-tested without a device in
[test/DotGame.Framework.Tests/Graphics/SpriteVertexTests.cs](../../test/DotGame.Framework.Tests/Graphics/SpriteVertexTests.cs),
against `Matrix4x4` transforms.

## The platform contract

`ISpriteBatchStrategy` is what each platform implements:

| Member | Contract |
|---|---|
| `Begin(transform, blend, sampler)` | Starts a batch with the default shader. A change of blend or sampler from the previous batch is handled here, so drawing a sprite needn't compare them. |
| `SetShader(shader)` | The shader for the sprites drawn after it, `null` for the default. Doesn't draw or split anything by itself. |
| `Draw(texture, position, color, rotation, origin, scale, source, flip, customData)` | Writes one sprite with `WriteSprite`, continuing the current run when its texture and shader match, or starting a new one. |
| `DrawTriangle(texture, a, b, c)` | The same, with `WriteTriangle`. |
| `End()` | Ends the batch. **It doesn't draw.** The sprites wait, and a later batch with the same state continues them. |

The rule every strategy follows: **a draw splits when a sprite needs different state, never at `Begin`,
`End` or `SetShader` alone.** A batch that draws nothing, a shader scope around nothing, or a `Begin`
right after an `End` with the same settings leaves the run of sprites intact.

What ends a run: a different texture, a different shader, a different blend or sampler, a changed
shader parameter value, a render target change, a clear, a scissor change, a pixel readback, and on
the platforms that draw as they go, the end of the frame or a capacity limit. Parameter values are
compared, not just set: the platforms keep each parameter's last value (`CachedShaderValue` on Android
and the web, the uniform bytes on desktop and iOS) and skip a set that changes nothing.

## Platforms

All four platforms share the vertex, the geometry and the splitting rule. They differ in when vertices
reach the GPU and when draws are issued.

| | Desktop (SDL GPU) | Web (WebGL 2) | Android (OpenGL ES 3) | iOS (Metal) |
|---|---|---|---|---|
| Who holds the sprites | The service's `DrawRecorder`, shared by every batch | The service's `FrameRecorder`, shared by every batch | Each batch's strategy | Each batch's strategy |
| Vertex upload | Once per frame: one copy into a transfer buffer and one copy pass | Once per frame: one `bufferData` in one call into JavaScript | Once per submit, with `glBufferData` | None: written in place in shared Metal buffers |
| When draws are issued | When the frame is submitted | When the frame is executed | When state changes, another batch begins, or the frame ends | When state changes, another batch begins, or the frame ends, into the frame's render encoder |
| Indices | 16-bit; a longer run draws in parts of 16,384 sprites with a base vertex | 32-bit; no limit | 16-bit; 16,384 sprites per submit | 16-bit; 2,048 sprites per draw |

### Desktop

[platform/desktop/Graphics/DrawRecorder.cs](../../platform/desktop/Graphics/DrawRecorder.cs) records the
whole frame and draws it when the frame is submitted. `AddSprite` returns where to write the next
sprite's vertices, extending the last segment or starting a new one. A **segment** is a run of sprites
with the same texture, pipeline (shader and blend), sampler, render target, scissor rectangle and
fragment uniform values. Each segment captures the state it was recorded with, including any pending
clear, because by the time it's drawn the service has moved on.

Each shader counts the changes to its parameters' values (setting a value a parameter already has
isn't one), and a segment records the count it started with, so a value set between any two sprites
starts a new segment, as on the other platforms, for the cost of comparing two integers per sprite. A
segment stores its own copy of the values, since a shader's parameters can change before the segment
is drawn.

At submit, the recorder uploads every vertex of the frame in one copy pass, then walks the segments:
one render pass per render target (a clear starts a pass that clears; a readback ends the pass and
copies between passes), binding only the pipeline, texture, sampler and scissor that differ from the
previous segment. A sampler change closes the segment at `Begin`; the blend is part of the pipeline.

Why record the frame: SDL's GPU API, like Vulkan and Metal, has explicit render passes, and uploads
happen outside them. Drawing at each flush would mean a copy pass and a new render pass every time,
and a render pass's load and store of the target is the expensive part on most GPUs. Recording and
replaying after one upload is the arrangement that fits the API: render passes per frame are one per
render target, however many textures and shaders the frame uses.

### Web

[platform/webgl/Graphics/FrameRecorder.cs](../../platform/webgl/Graphics/FrameRecorder.cs) records the
frame's vertices into one byte array and its state changes into a command list of floats: draw, set
shader, set uniform, set render state (blend and sampler), set render target, clear, set or disable the
scissor, and readback. A draw command covers a run of sprites with the same texture and shader. At the
end of the frame, one call into JavaScript
([platform/webgl/wwwroot/_dotgame/graphics.js](../../platform/webgl/wwwroot/_dotgame/graphics.js))
uploads the vertices from the WebAssembly heap and runs the commands, skipping a program bind,
projection upload or texture bind that's already current.

Why: crossing into JavaScript and copying data across is the web's main per-frame cost, so a frame
crosses once, and JavaScript does no work per sprite. Every state change is a command in order with
the sprites, so sprites drawn before a change get the state they were drawn with.

A uniform command names its program. A shader's parameters are usually set before the batch that uses
it switches to it, and a WebGL uniform location only applies to its own program, so the command can't
rely on whichever program is current.

### Android

Each `SpriteBatchStrategy`
([platform/android/Graphics/SpriteBatchStrategy.cs](../../platform/android/Graphics/SpriteBatchStrategy.cs))
records segments of sprites with the same texture, program and render state into a managed vertex
array. `End` doesn't draw. The service keeps one active batch and submits it before anything that
changes GL state its sprites depend on: a render target change, a clear, a scissor change, a uniform
change, a readback, a texture upload, another batch beginning, and the end of the frame. A submit
uploads the vertices with one `glBufferData` and replays the segments, changing only the GL state that
differs from the last.

`glBufferData` gives the driver fresh storage to copy into, so nothing waits on a frame the GPU is still
reading. It's cheaper than a mapped ring buffer or client-side vertex arrays on mobile drivers, which
copy client arrays during every draw call, so their cost grows with the number of draws.

A submit holds at most 16,384 sprites, the most 16-bit indices address. A frame that draws more
submits what it has and carries on.

### iOS

Each `SpriteBatchStrategy`
([platform/ios/Graphics/SpriteBatchStrategy.cs](../../platform/ios/Graphics/SpriteBatchStrategy.cs))
writes vertices straight into shared-storage Metal buffers, so there's no copy. Sprites wait until the
texture or shader changes, the blend or sampler changes at `Begin`, 2,048 sprites are waiting, or the
service draws them before a render target change, a clear, a scissor change, a shader parameter change,
a readback, another batch beginning, a texture in use being released, or the end of the frame. A flush
encodes one draw into the frame's render encoder; the service tracks what the encoder has bound and
sets only what changed.

The buffers form a ring of slots, each holding 16,384 sprites. Each flush takes the next region of the
current slot, so draws already encoded keep their own vertices; the ring moves to the next slot when
another 2,048 sprites wouldn't fit. Each slot records the last frame that read it, and a command
buffer completion handler counts finished frames. A slot the GPU may still be reading is never
overwritten: a new slot is inserted before it instead. The ring starts with one slot and grows to what
the game's frames need.

## Several batches

`GraphicsService.SharedSpriteBatch` is the usual batch, and `CreateSpriteBatch` makes more. Submission
order holds across them. On desktop and the web every batch adds to the service's one recorder. On
Android and iOS the service draws the active batch's waiting sprites before a different batch begins.

## Statistics

Each platform counts draw calls and sprites where it issues its draws (`GameHost.CountDraws`), so the
stats overlay's `BAT` line (`DiagnosticsService.StatsOverlayMode = Detailed`) shows how a frame
batches on any platform.

## Why this design

**Deferring draws and merging identical batches.** XNA-style batches, such as MonoGame's and KNI's,
draw at every `End` and at every texture change. That's cheap where a draw call restarts nothing, but
not on APIs with explicit render passes. It also matters for libraries built on the framework, which
naturally end and begin a batch, or open a shader scope, around each piece of content that needs
different settings. Here those boundaries cost nothing unless the state actually changes.

**Shader data in the vertex.** Without it, every sprite whose shader settings differ needs its own
draw, with uniforms set in between. A generic field covers per-sprite uses that other engines give fixed
fields (an added color, a channel swizzle) and any a game's shader invents. Four half floats keep the
vertex at 28 bytes, with the color in four bytes, smaller than a vertex with a float color and no
shader data (32 bytes).

**Triangles in the batch, not an immediate draw.** A general immediate draw, such as XNA's
`DrawUserPrimitives`, takes any vertex format with its own render state, one draw call per call. In a
deferred batch it would have to draw everything recorded before it to keep the order, which on desktop
is another copy pass and render pass, and it would need vertex layouts and render state on every
platform. Triangles that fit the sprite path stay in order with sprites, share their draws, and need
nothing new on any platform.

**Submission order only.** XNA-style batches offer sort modes (by texture, back to front, front to
back). Sorting by texture draws overlapping sprites out of order, which breaks drawing a scene in
order. A game that wants sorting sorts before it draws.

## Limits

- **No sorting and no depth.** Later draws cover earlier ones.
- **Colors are clamped to 0–1** and rounded to 1/255 in the vertex.
- **Shader data is half precision**, and only `Draw` carries it: triangles always pass zeros.
- **Texture coordinates are interpolated per triangle, without perspective.** A quad drawn as two
  triangles that isn't a parallelogram (a ribbon segment that narrows) shows a slight bend in its
  texture along the diagonal, as in most 2D libraries' quad and primitive drawing.
