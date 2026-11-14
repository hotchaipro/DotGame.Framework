// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DotGame.Framework.Android.Content;
using DotGame.Framework.Content;
using DotGame.Framework.Graphics;
using DotGame.Framework.Platform.Graphics;
using DotGame.Framework.Threading;

namespace DotGame.Framework.Android.Graphics;

internal sealed class ShaderLoader : IContentLoader<Shader>
{
    private readonly ContentServiceStrategy _contentService;
    private readonly GraphicsServiceStrategy _graphicsService;
    private readonly List<(string ContentPath, WeakReference<Shader> WeakRef)> _trackedShaders = new();

    // Guards _trackedShaders: a load can finish on any thread, and a reload runs on the GL thread
    private readonly System.Threading.Lock _trackedShadersLock = new();

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
        var shaderStrategy = await this.LoadShaderStrategy(contentKey).ConfigureAwait(false);

        var shader = new Shader(new Strategy<IShaderStrategy>(shaderStrategy));
        lock (this._trackedShadersLock)
        {
            this._trackedShaders.Add((contentKey, new WeakReference<Shader>(shader)));
        }

        return shader;
    }

    internal async Task ReloadAll()
    {
        (string ContentPath, WeakReference<Shader> WeakRef)[] tracked;
        lock (this._trackedShadersLock)
        {
            tracked = [.. this._trackedShaders];
        }

        for (int i = tracked.Length - 1; i >= 0; i--)
        {
            var (contentKey, weakRef) = tracked[i];

            // NOTE: An unloaded shader can still be here until it's collected
            if (weakRef.TryGetTarget(out var shader) && !shader.IsDisposed)
            {
                var trackedShader = shader.Strategy.ToConcrete<ShaderStrategy>();
                var newShaderStrategy = await this.LoadShaderStrategy(contentKey).ConfigureAwait(false);
                shader.Strategy.Reset(newShaderStrategy);
                newShaderStrategy.CopyShaderParameters(trackedShader);
            }
            else
            {
                lock (this._trackedShadersLock)
                {
                    this._trackedShaders.Remove(tracked[i]);
                }
            }
        }
    }

    private async Task<ShaderStrategy> LoadShaderStrategy(
        string contentKey)
    {
        // Background-safe phase: read shader source from assets on the worker pool.
        // Resolve the logical content key (no codec extension) to the
        // actual relative path produced by the build pipeline.
        var resolvedPath = this._contentService.ResolveContentPath(contentKey);
        string shaderSource;
        using (var stream = await this._contentService.OpenStreamAsync(resolvedPath).ConfigureAwait(false))
        using (var reader = new StreamReader(stream))
        {
            shaderSource = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        // Parse vertex and fragment shaders from the source
        string vertexShaderSource = ExtractShaderSection(shaderSource, "VERTEX")
            ?? DefaultShaderSource.VertexShader;

        string fragmentShaderSource = ExtractShaderSection(shaderSource, "FRAGMENT")
            ?? DefaultShaderSource.FragmentShader;

        // Engine-restricted phase: GL shader compilation on the GL context (game) thread.
        return await GameThread.InvokeAsync(() =>
        {
            int programId = GraphicsServiceStrategy.CompileShaderProgram(vertexShaderSource, fragmentShaderSource);
            return new ShaderStrategy(programId, vertexShaderSource, fragmentShaderSource, this._graphicsService);
        });
    }

    /// <summary>
    /// Extracts a shader section from a combined shader file.
    /// Looks for sections delimited by #ifdef SECTIONNAME and #endif.
    /// </summary>
    private static string? ExtractShaderSection(string shaderSource, string sectionName)
    {
        if (string.IsNullOrWhiteSpace(shaderSource))
        {
            return null;
        }

        ReadOnlySpan<char> source = shaderSource.AsSpan();
        ReadOnlySpan<char> name = sectionName.AsSpan();
        int searchFrom = 0;

        while (searchFrom < source.Length)
        {
            int ifdefPos = source[searchFrom..].IndexOf("#ifdef", StringComparison.OrdinalIgnoreCase);
            if (ifdefPos < 0)
            {
                break;
            }
            ifdefPos += searchFrom;

            int pos = ifdefPos + "#ifdef".Length;

            while (pos < source.Length && source[pos] is ' ' or '\t')
            {
                pos++;
            }

            if (pos + name.Length <= source.Length &&
                source.Slice(pos, name.Length).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                int afterName = pos + name.Length;

                if (afterName >= source.Length || source[afterName] is ' ' or '\t' or '\r' or '\n')
                {
                    int contentStart = afterName;
                    while (contentStart < source.Length && source[contentStart] != '\n')
                    {
                        contentStart++;
                    }
                    if (contentStart < source.Length)
                    {
                        contentStart++;
                    }

                    int endifPos = source[contentStart..].IndexOf("#endif", StringComparison.OrdinalIgnoreCase);
                    if (endifPos >= 0)
                    {
                        return source.Slice(contentStart, endifPos).Trim().ToString();
                    }
                }
            }

            searchFrom = ifdefPos + "#ifdef".Length;
        }

        return null;
    }
}
