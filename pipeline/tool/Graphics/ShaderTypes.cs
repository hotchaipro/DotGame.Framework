// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.ContentPipeline.BuildTool.Graphics;

/// <summary>
/// The shader output types a Slang shader can be compiled to.
/// </summary>
[Flags]
internal enum ShaderTypes
{
    None = 0,

    /// <summary>SPIR-V for Vulkan: .vert.spv + .frag.spv.</summary>
    SpirV = 1,

    /// <summary>MSL 2.1 for Metal: .vert.msl + .frag.msl.</summary>
    Msl = 2,

    /// <summary>DXIL for Direct3D 12: .vert.dxil + .frag.dxil.</summary>
    Dxil = 4,

    /// <summary>GLSL ES 300 for OpenGL ES / WebGL: one .glsl with #ifdef VERTEX / FRAGMENT sections.</summary>
    Glsl = 8,
}
