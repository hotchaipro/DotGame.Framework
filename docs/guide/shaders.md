# Shaders

A [`Shader`](../api/Framework/Shader.md) changes how sprites draw. You write it once in
[Slang](https://shader-slang.org/), and the content pipeline compiles it for every platform: SPIR-V,
DXIL and Metal on desktop, Metal on iOS, and GLSL ES on the web and Android.

Most effects need only a pixel shader. The sprite batch's own vertex shader runs before it.

## Writing one

Add the file to your project:

```xml
<ItemGroup>
  <ShaderContent Include="Content\Shaders\*.slang" />
</ItemGroup>
```

A pixel shader receives each pixel's sprite color, texture coordinate and
[per-sprite data](#values-that-differ-per-sprite). This one, `Content/Shaders/wave.slang`, makes
sprites wave:

```slang
uniform float Time;

Sampler2D SpriteTextureSampler;

struct VertexShaderOutput
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 TextureCoordinate : TEXCOORD0;
    float4 CustomData : TEXCOORD1;
};

[shader("pixel")]
float4 psMain(VertexShaderOutput input) : SV_Target
{
    float2 p = input.TextureCoordinate;
    p.x += sin((p.y * 40.0f) + (Time * 4.0f)) * 0.01f;

    return SpriteTextureSampler.Sample(p) * input.Color;
}
```

Textures are premultiplied, so a shader that changes a pixel's color works on its color divided by
its alpha, and multiplies the alpha back in.

## Using one

Load it like any content, and draw with it in a `UseShader` scope, which goes back to the shader before
it when it ends. Look up parameters once, by their names in the shader, and set them before drawing, as
they apply to everything drawn with it:

```csharp
protected override async Task OnStart()
{
    this.wave = await this.Content.LoadAsync<Shader>("Shaders/wave");
    this.time = this.wave.GetParameter("Time");
}

protected override void OnDraw()
{
    var spriteBatch = this.Graphics.SharedSpriteBatch;

    this.time.SetValue((float)this.Time.Total.TotalSeconds);

    using var batch = spriteBatch.Begin();
    using (spriteBatch.UseShader(this.wave))
    {
        spriteBatch.Draw(this.texture, Vector2.Zero, Color.White);
    }
}
```

Scopes nest, and cost nothing until a sprite needs the change. Sprites either
side of a change can't share a draw, as with a change of texture.

## Values that differ per sprite

A shader's parameters apply to everything drawn with it, so giving sprites different settings that way
would take a draw for each. Instead, pass [`ShaderCustomData`](../api/Framework/ShaderCustomData.md) with
each sprite: four values the shader reads as `TEXCOORD1`. These sprites each have their own settings,
and still draw in one draw call:

```csharp
using var batch = spriteBatch.Begin();
using (spriteBatch.UseShader(this.wave))
{
    for (int i = 0; i < 12; i++)
    {
        // The sample's wave.slang reads x as the wave's size and y as its phase
        var data = new ShaderCustomData(0.01f, i / 12f, 0, 0);
        spriteBatch.Draw(this.texture, new Vector2(i * 40, 0), Color.White, customData: data);
    }
}
```

The default sprite shader ignores this data.

## A trap: derivatives on the web

**Measure derivatives before any branch or early `return`.** The shader tools compile an early
`return` into a `do { ... } while (false)` loop for GLSL, and WebGL's `dFdx` and `dFdy` (Slang's `ddx`
and `ddy`) can return zero inside that loop, as they're undefined in non-uniform control flow. Any
derivative-based antialiasing then collapses to a hard edge on the web alone, while desktop draws
correctly.

So compute `ddx`, `ddy` and anything that samples with implicit derivatives at the top of the pixel
shader, and give the shader one `return` at the end, assigning a result in branches rather than
returning from them.

## See also

- [`Shader`](../api/Framework/Shader.md), [`ShaderParameter`](../api/Framework/ShaderParameter.md)
- The framework sample's Shaders page
