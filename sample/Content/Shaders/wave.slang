// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

// A custom sprite shader for the framework sample's Shaders page.
//
// Shaders are written in Slang, as a pixel shader only: the sprite batch's own vertex shader runs
// before it, and passes the color, texture coordinate and shader data of each sprite. The
// content pipeline compiles the shader for every platform (SPIR-V, DXIL, MSL and GLSL ES).
//
//   Uniforms, set with Shader.GetParameter(name).SetValue(value), are shared by every sprite
//   in a batch: here, the time.
//
//   Custom data, passed with each sprite as SpriteBatch.Draw's customData, can differ from
//   sprite to sprite in one batch: here, x is the size of the wave (in texture coordinates) and
//   y its phase (in turns), so sprites sway by different amounts and out of step.

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
    p.x += sin((p.y * 40.0f) + (Time * 4.0f) + (input.CustomData.y * 6.2831853f)) * input.CustomData.x;

    return SpriteTextureSampler.Sample(p) * input.Color;
}
