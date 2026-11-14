// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

uniform float4x4 MatrixTransform;

Sampler2D SpriteTextureSampler;

struct VertexShaderOutput
{
	float4 Position : SV_POSITION;
	float4 Color : COLOR0;
	float2 TextureCoordinates : TEXCOORD0;
	float4 CustomData : TEXCOORD1;
};

[shader("vertex")]
VertexShaderOutput vsMain(
	float4 position : POSITION0,
	float4 color : COLOR0,
	float2 texCoord : TEXCOORD0,
	float4 customData : TEXCOORD1)
{
	VertexShaderOutput output;
	output.Position = mul(position, MatrixTransform);
	output.Color = color;
	output.TextureCoordinates = texCoord;
	output.CustomData = customData;
	return output;
}

[shader("pixel")]
float4 psMain(VertexShaderOutput input) : SV_Target
{
	float4 texColor = SpriteTextureSampler.Sample(input.TextureCoordinates);
	return texColor * input.Color;
}
