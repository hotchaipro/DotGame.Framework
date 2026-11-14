// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.IO;
using System.Threading.Tasks;
using DotGame.Framework.Content;
using DotGame.Framework.Graphics;
using DotGame.Framework.iOS.Graphics;
using DotGame.Framework.Platform.Graphics;

namespace DotGame.Framework.iOS.Content;

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

    public Task<Shader> LoadAsync(
        string contentKey)
    {
        // Resolve the per-stage shader files via sub-keys. The
        // build pipeline registers one manifest entry per sub-key
        // so the loader stays free of file-naming conventions.
        var resolvedVertex = this._contentService.ResolveContentPath(contentKey, subKey: "vert.msl");
        var resolvedFragment = this._contentService.ResolveContentPath(contentKey, subKey: "frag.msl");

        var vertexPath = this._contentService.GetFullPath(resolvedVertex);
        var fragmentPath = this._contentService.GetFullPath(resolvedFragment);

        // Background-safe phase: file I/O runs inline on whatever thread
        // invoked the loader (game thread for preload, worker for postload).
        string vertexSource;
        if (File.Exists(vertexPath))
        {
            Trace.Write(0x61fe6180, $"[ShaderLoader] Loading vertex shader '{vertexPath}'");
            vertexSource = File.ReadAllText(vertexPath);
        }
        else
        {
            Trace.Write(0xb91d5b34, $"[ShaderLoader] Using default vertex shader for '{contentKey}'");
            vertexSource = DefaultShaderSource.VertexShaderMSL;
        }

        Trace.Write(0xb020e82d, $"[ShaderLoader] Loading fragment shader '{fragmentPath}'");
        var fragmentSource = File.ReadAllText(fragmentPath);

        // NOTE: Metal compiles on any thread, so a load stays on the thread it runs on, where a
        // cold compile doesn't hold up a frame
        var metalShader = this._graphicsService.CompileShaderFromMSL(
            vertexSource,
            fragmentSource);
        var shader = new Shader(new Strategy<IShaderStrategy>(metalShader));

        Trace.Write(0x19b5fc88, $"[ShaderLoader] Loaded shader '{contentKey}'");

        return Task.FromResult(shader);
    }
}
