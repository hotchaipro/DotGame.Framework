// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using DotGame.Framework.Content;
using DotGame.Framework.Desktop.Graphics;
using DotGame.Framework.Graphics;
using DotGame.Framework.Platform.Graphics;
using DotGame.Framework.Threading;

namespace DotGame.Framework.Desktop.Content;

internal sealed class ShaderLoader : IContentLoader<Shader>
{
    private readonly ContentServiceStrategy _contentService;
    private readonly GraphicsServiceStrategy _graphicsService;

    public ShaderLoader(
        ContentServiceStrategy contentService,
        GraphicsServiceStrategy graphicsService)
    {
        this._contentService = contentService;
        this._graphicsService = graphicsService;
    }

    public async Task<Shader> LoadAsync(
        string contentKey)
    {
        var format = this._graphicsService.ShaderFormat;

        // Resolve the per-stage shader files via sub-keys. The
        // build pipeline registers one manifest entry per sub-key
        // ("vert.<fmt>", "frag.<fmt>") so the loader stays free of
        // file-naming conventions.
        var runtimeFormat = format switch
        {
            SDL.GPUShaderFormat.MSL => "msl",
            SDL.GPUShaderFormat.DXIL => "dxil",
            _ => "spv",
        };

        var resolvedVertex = this._contentService.ResolveContentPath(contentKey, subKey: $"vert.{runtimeFormat}");
        var resolvedFragment = this._contentService.ResolveContentPath(contentKey, subKey: $"frag.{runtimeFormat}");

        // SPIR-V reflection blobs are always emitted alongside the
        // runtime-format outputs; they are addressed by their own
        // explicit sub-keys.
        var resolvedVertexSpirv = this._contentService.ResolveContentPath(contentKey, subKey: "vert.spv");
        var resolvedFragmentSpirv = this._contentService.ResolveContentPath(contentKey, subKey: "frag.spv");

        var vertexShaderPath = this._contentService.GetFullPath(resolvedVertex);
        var fragmentShaderPath = this._contentService.GetFullPath(resolvedFragment);
        var vertexSpirvPath = this._contentService.GetFullPath(resolvedVertexSpirv);
        var fragmentSpirvPath = this._contentService.GetFullPath(resolvedFragmentSpirv);

        // Background-safe phase: file I/O runs inline on whatever thread
        // invoked the loader (game thread for preload, worker for postload).
        var hasCustomVertex = File.Exists(vertexShaderPath);

        byte[]? vertexCode = null;
        byte[]? vertexSpirv = null;
        if (hasCustomVertex)
        {
            Trace.Write(0xc277de55, $"[ShaderLoader] Loading vertex shader '{vertexShaderPath}'");
            vertexCode = (format == SDL.GPUShaderFormat.MSL)
                ? Encoding.UTF8.GetBytes(File.ReadAllText(vertexShaderPath))
                : File.ReadAllBytes(vertexShaderPath);
            vertexSpirv = File.ReadAllBytes(vertexSpirvPath);
        }

        Trace.Write(0xf67c7584, $"[ShaderLoader] Loading fragment shader '{fragmentShaderPath}'");
        var fragmentCode = (format == SDL.GPUShaderFormat.MSL)
            ? Encoding.UTF8.GetBytes(File.ReadAllText(fragmentShaderPath))
            : File.ReadAllBytes(fragmentShaderPath);

        var fragmentSpirv = File.ReadAllBytes(fragmentSpirvPath);

        // Engine-restricted phase: SDL GPU shader creation on the game thread
        // (synchronous fast path when already on it).
        var shader = await GameThread.InvokeAsync(
            () => this.CreateShader(hasCustomVertex, vertexCode, vertexSpirv, fragmentCode, fragmentSpirv, format));

        Trace.Write(0xceb15397, $"[ShaderLoader] Loaded shader '{contentKey}'");

        return shader;
    }

    private unsafe Shader CreateShader(
        bool hasCustomVertex,
        byte[]? vertexCode,
        byte[]? vertexSpirv,
        byte[] fragmentCode,
        byte[] fragmentSpirv,
        SDL.GPUShaderFormat format)
    {
        var entrypoint = (format == SDL.GPUShaderFormat.MSL) ? "main0" : "main";

        nint vertexShader;
        nint fragmentShader;

        if ((hasCustomVertex) && (vertexCode is not null) && (vertexSpirv is not null))
        {
            fixed (byte* vertexPtr = vertexCode)
            {
                var vertexInfo = new SDL.GPUShaderCreateInfo
                {
                    Code = (nint)vertexPtr,
                    CodeSize = (nuint)vertexCode.Length,
                    Entrypoint = entrypoint,
                    Format = format,
                    Stage = SDL.GPUShaderStage.Vertex,
                    NumSamplers = 0,
                    NumStorageBuffers = 0,
                    NumStorageTextures = 0,
                    NumUniformBuffers = CountSpirVUniformBuffers(vertexSpirv),
                };

                vertexShader = SDL.CreateGPUShader(this._graphicsService.GPUDevice, in vertexInfo);
                if (vertexShader == nint.Zero)
                {
                    throw new InvalidOperationException($"Failed to create vertex shader: {SDL.GetError()}");
                }
            }
        }
        else
        {
            vertexShader = this._graphicsService.DefaultVertexShader;
        }

        fixed (byte* fragmentPtr = fragmentCode)
        {
            var fragmentInfo = new SDL.GPUShaderCreateInfo
            {
                Code = (nint)fragmentPtr,
                CodeSize = (nuint)fragmentCode.Length,
                Entrypoint = entrypoint,
                Format = format,
                Stage = SDL.GPUShaderStage.Fragment,
                NumSamplers = 1,
                NumStorageBuffers = 0,
                NumStorageTextures = 0,
                NumUniformBuffers = CountSpirVUniformBuffers(fragmentSpirv),
            };

            fragmentShader = SDL.CreateGPUShader(this._graphicsService.GPUDevice, in fragmentInfo);
            if (fragmentShader == nint.Zero)
            {
                if (hasCustomVertex)
                {
                    SDL.ReleaseGPUShader(this._graphicsService.GPUDevice, vertexShader);
                }
                throw new InvalidOperationException($"Failed to create fragment shader: {SDL.GetError()}");
            }
        }

        var fragmentUniformBufferCount = CountSpirVUniformBuffers(fragmentSpirv);
        var uniformLayout = ReflectSpirVUniformMembers(fragmentSpirv);
        var sdlShader = new ShaderStrategy(this._graphicsService, vertexShader, fragmentShader, fragmentUniformBufferCount, uniformLayout, hasCustomVertex);
        return new Shader(new Strategy<IShaderStrategy>(sdlShader));
    }

    /// <summary>
    /// Counts OpVariable instructions with Uniform storage class in a SPIR-V module.
    /// </summary>
    private static uint CountSpirVUniformBuffers(
        byte[] spirv)
    {
        const ushort OpVariable = 59;
        const uint StorageClassUniform = 2;

        var words = MemoryMarshal.Cast<byte, uint>(spirv);
        uint count = 0;
        int i = 5; // skip SPIR-V header (5 words)

        while (i < words.Length)
        {
            uint word0 = words[i];
            ushort opcode = (ushort)(word0 & 0xFFFF);
            ushort wordCount = (ushort)(word0 >> 16);

            if (wordCount == 0)
            {
                break; // malformed
            }

            if (opcode == OpVariable && wordCount >= 4)
            {
                uint storageClass = words[i + 3];
                if (storageClass == StorageClassUniform)
                {
                    count++;
                }
            }

            i += wordCount;
        }

        return count;
    }

    /// <summary>
    /// Reflects the SPIR-V module to extract uniform buffer member names and
    /// their byte offsets from OpMemberName and OpMemberDecorate instructions.
    /// </summary>
    private static Dictionary<string, uint> ReflectSpirVUniformMembers(
        byte[] spirv)
    {
        const ushort OpTypePointer = 32;
        const ushort OpVariable = 59;
        const ushort OpMemberName = 6;
        const ushort OpMemberDecorate = 72;
        const uint StorageClassUniform = 2;
        const uint DecorationOffset = 35;

        var words = MemoryMarshal.Cast<byte, uint>(spirv);

        var typePointers = new Dictionary<uint, (uint storageClass, uint elementType)>();
        var memberNames = new Dictionary<(uint typeId, uint memberIndex), string>();
        var memberOffsets = new Dictionary<(uint typeId, uint memberIndex), uint>();
        uint firstUniformPointerTypeId = 0;

        int i = 5; // skip SPIR-V header (5 words)

        while (i < words.Length)
        {
            uint word0 = words[i];
            ushort opcode = (ushort)(word0 & 0xFFFF);
            ushort wordCount = (ushort)(word0 >> 16);

            if (wordCount == 0)
            {
                break; // malformed
            }

            switch (opcode)
            {
                case OpTypePointer when wordCount >= 4:
                    {
                        uint resultId = words[i + 1];
                        uint storageClass = words[i + 2];
                        uint elementType = words[i + 3];
                        typePointers[resultId] = (storageClass, elementType);
                        break;
                    }

                case OpVariable when wordCount >= 4:
                    {
                        uint resultTypeId = words[i + 1];
                        uint storageClass = words[i + 3];
                        if (storageClass == StorageClassUniform &&
                            firstUniformPointerTypeId == 0)
                        {
                            firstUniformPointerTypeId = resultTypeId;
                        }
                        break;
                    }

                case OpMemberName when wordCount >= 4:
                    {
                        uint typeId = words[i + 1];
                        uint memberIndex = words[i + 2];
                        var nameBytes = MemoryMarshal.Cast<uint, byte>(
                            words.Slice(i + 3, wordCount - 3));
                        int nullIndex = nameBytes.IndexOf((byte)0);
                        var name = Encoding.UTF8.GetString(
                            nameBytes[..(nullIndex >= 0 ? nullIndex : nameBytes.Length)]);
                        memberNames[(typeId, memberIndex)] = name;
                        break;
                    }

                case OpMemberDecorate when wordCount >= 5:
                    {
                        uint typeId = words[i + 1];
                        uint memberIndex = words[i + 2];
                        uint decoration = words[i + 3];
                        if (decoration == DecorationOffset)
                        {
                            memberOffsets[(typeId, memberIndex)] = words[i + 4];
                        }
                        break;
                    }
            }

            i += wordCount;
        }

        // Resolve: uniform pointer type -> struct type -> member names + offsets
        var result = new Dictionary<string, uint>();

        if (firstUniformPointerTypeId != 0 &&
            typePointers.TryGetValue(firstUniformPointerTypeId, out var pointerInfo))
        {
            uint structTypeId = pointerInfo.elementType;

            foreach (var ((typeId, memberIndex), name) in memberNames)
            {
                if (typeId == structTypeId &&
                    memberOffsets.TryGetValue((typeId, memberIndex), out uint offset))
                {
                    result[name] = offset;
                }
            }
        }

        return result;
    }
}
