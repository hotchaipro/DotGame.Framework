// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using DotGame.Framework.Content;
using DotGame.Framework.Graphics;
using DotGame.Framework.Platform.Graphics;
using DotGame.Framework.WebGL.Content;

namespace DotGame.Framework.WebGL.Graphics;

[SupportedOSPlatform("browser")]
internal sealed class ShaderLoader : IContentLoader<Shader>
{
    private readonly ContentServiceStrategy _contentService;
    private readonly GraphicsServiceStrategy _graphicsService;
    private readonly List<(string ContentPath, WeakReference<Shader> WeakRef)> _trackedShaders = new();

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

        this._trackedShaders.Add((contentKey, new WeakReference<Shader>(shader)));

        return shader;
    }

    internal async Task ReloadAll()
    {
        var reloadEntries = new List<(WeakReference<Shader> WeakRef, ShaderStrategy OldStrategy, Task<ShaderStrategy> LoadTask)>();

        for (int i = this._trackedShaders.Count - 1; i >= 0; i--)
        {
            var (contentKey, weakRef) = this._trackedShaders[i];

            // NOTE: An unloaded shader can still be here until it's collected
            if (weakRef.TryGetTarget(out var shader) && !shader.IsDisposed)
            {
                var trackedShader = shader.Strategy.ToConcrete<ShaderStrategy>();
                reloadEntries.Add((weakRef, trackedShader, this.LoadShaderStrategy(contentKey)));
            }
            else
            {
                this._trackedShaders.RemoveAt(i);
            }
        }

        // Wait for all loads to complete in parallel
        var loadTasks = new Task[reloadEntries.Count];
        for (int i = 0; i < reloadEntries.Count; i++)
        {
            loadTasks[i] = reloadEntries[i].LoadTask;
        }

        await Task.WhenAll(loadTasks).ConfigureAwait(false);

        // Apply results
        foreach (var (weakRef, oldStrategy, loadTask) in reloadEntries)
        {
            var newStrategy = loadTask.Result;

            if (weakRef.TryGetTarget(out var shader) && !shader.IsDisposed)
            {

                // Transfer the existing parameter handles before the old shader is
                // disposed, so cached ShaderParameter references stay valid.
                newStrategy.AdoptShaderParameters(oldStrategy);

                shader.Strategy.Reset(newStrategy);
            }
        }
    }

    private async Task<ShaderStrategy> LoadShaderStrategy(
        string contentKey)
    {
        // Resolve the logical content key (no codec extension) to the
        // actual relative path produced by the build pipeline.
        var resolvedPath = this._contentService.ResolveContentPath(contentKey);

        // Load shader source from single file
        var url = this._contentService.GetVersionedUrl(resolvedPath);

        string shaderSource = await WebGLBindings.LoadShaderSource(url).ConfigureAwait(false);

        // Parse vertex and fragment shaders from the source
        string vertexShaderSource = ExtractShaderSection(shaderSource, "VERTEX")
            ?? DefaultShaderSource.VertexShader;

        string? fragmentShaderSource = ExtractShaderSection(shaderSource, "FRAGMENT")
            ?? DefaultShaderSource.FragmentShader;

        // Compile shader program (done once at load time), on a context that isn't lost: one lost
        // part way through fails the compile, and comes back
        while (true)
        {
            await this._graphicsService.WhenContextReady().ConfigureAwait(false);

            var glContext = this._graphicsService.WebGLContext;
            try
            {
                var jsShader = WebGLBindings.CompileShaderProgram(glContext, vertexShaderSource, fragmentShaderSource);

                return new ShaderStrategy(jsShader, this._graphicsService);
            }
            catch (System.Runtime.InteropServices.JavaScript.JSException) when (WebGLBindings.IsContextLost(glContext))
            {
                // The service replaces what WhenContextReady returns as the context's lost event
                // arrives, which is in the next task
                await Task.Yield();
            }
        }
    }

    /// <summary>
    /// Extracts a shader section from a combined shader file.
    /// Looks for sections delimited by #ifdef SECTIONNAME and #endif.
    /// </summary>
    /// <param name="shaderSource">The combined shader source</param>
    /// <param name="sectionName">The section name (e.g., "VERTEX" or "FRAGMENT")</param>
    /// <returns>The shader section content, or null if not found</returns>
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
            // Find next #ifdef
            int ifdefPos = source[searchFrom..].IndexOf("#ifdef", StringComparison.OrdinalIgnoreCase);
            if (ifdefPos < 0)
            {
                break;
            }
            ifdefPos += searchFrom;

            // Advance past "#ifdef"
            int pos = ifdefPos + "#ifdef".Length;

            // Skip horizontal whitespace between #ifdef and the section name
            while (pos < source.Length && source[pos] is ' ' or '\t')
            {
                pos++;
            }

            // Check if the section name matches at this position (case-insensitive)
            if (pos + name.Length <= source.Length &&
                source.Slice(pos, name.Length).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                int afterName = pos + name.Length;

                // Name must be followed by whitespace or end of input (not part of a longer token)
                if (afterName >= source.Length || source[afterName] is ' ' or '\t' or '\r' or '\n')
                {
                    // Skip the rest of the #ifdef line; content begins on the next line
                    int contentStart = afterName;
                    while (contentStart < source.Length && source[contentStart] != '\n')
                    {
                        contentStart++;
                    }
                    if (contentStart < source.Length)
                    {
                        contentStart++; // skip '\n'
                    }

                    // Find the matching #endif
                    int endifPos = source[contentStart..].IndexOf("#endif", StringComparison.OrdinalIgnoreCase);
                    if (endifPos >= 0)
                    {
                        return source.Slice(contentStart, endifPos).Trim().ToString();
                    }
                }
            }

            // Advance past the current "#ifdef" and keep searching
            searchFrom = ifdefPos + "#ifdef".Length;
        }

        return null;
    }
}
