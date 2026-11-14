// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using DotGame.Framework.Audio;
using DotGame.Framework.Content;
using DotGame.Framework.Graphics;
using DotGame.Framework.Platform.Content;
using DotGame.Framework.WebGL.Audio;
using DotGame.Framework.WebGL.Graphics;

namespace DotGame.Framework.WebGL.Content;

/// <summary>Finds a web game's content: the files its build published beside the page.</summary>
[SupportedOSPlatform("browser")]
internal sealed class ContentServiceStrategy : IContentServiceStrategy
{
    // The published name of each content file, with its content's hash in it, from the build
    private const string FingerprintsManifest = "DotGame.ContentFingerprints";

    private readonly string _baseUrl;
    private readonly ContentManifest _manifest;

    internal ContentServiceStrategy(
        WebGLGameEngine engine,
        string basePath,
        ContentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(basePath);
        ArgumentNullException.ThrowIfNull(manifest);

        // Normalize to a relative path so that it is resolved against the
        // base URL (which may include a sub-path) rather than the origin.
        this.BasePath = basePath.Replace('\\', '/').TrimStart('/');
        this._manifest = manifest;

        // Create the content loaders
        this.TextureLoader = new TextureLoader(this, engine.GraphicsService);
        this.ShaderLoader = new ShaderLoader(this, engine.GraphicsService);
        this.SoundLoader = new SoundLoader(this);
        this.MusicLoader = new MusicLoader(this);

        this._baseUrl = ContentBindings.GetBaseUrl();

        Trace.Write(0xa3d186b8, $"[ContentService] ContentUrl: {this._baseUrl}{this.BasePath}");
    }

    /// <summary>The folder content is fetched from, relative to the page.</summary>
    public string BasePath { get; }

    /// <summary>The content the build published.</summary>
    public ContentManifest Manifest => this._manifest;

    internal string ResolveContentPath(string contentKey, string? subKey = null) =>
        this._manifest.ResolveContentPath(contentKey, subKey);

    internal TextureLoader TextureLoader { get; }

    internal ShaderLoader ShaderLoader { get; }

    internal SoundLoader SoundLoader { get; }

    internal MusicLoader MusicLoader { get; }

    IContentLoader<Texture> IContentServiceStrategy.TextureLoader => this.TextureLoader;

    IContentLoader<Shader> IContentServiceStrategy.ShaderLoader => this.ShaderLoader;

    IContentLoader<Sound> IContentServiceStrategy.SoundLoader => this.SoundLoader;

    IContentLoader<Music> IContentServiceStrategy.MusicLoader => this.MusicLoader;

    internal async Task HandleContextRestored()
    {
        await Task.WhenAll(
            this.TextureLoader.ReloadAll(),
            this.ShaderLoader.ReloadAll()).ConfigureAwait(false);
    }

    /// <summary>Fetches a content file.</summary>
    public async Task<Stream> OpenStreamAsync(
        string contentPath)
    {
        var fullPath = this.GetVersionedUrl(contentPath);

        Trace.Write(0xda3560c3, $"[ContentService] OpenStream '{this._baseUrl}{fullPath}'");

        // NOTE: Fetched by the browser rather than with HttpClient, which brought
        // HttpClient's startup cost (and generic code WebAssembly AOT doesn't compile,
        // so it ran in the interpreter) onto the critical path to the first frame.
        // The bytes are wrapped in a MemoryStream whose buffer is publicly visible, so
        // loaders can read it in place (MemoryStream.TryGetBuffer).
        using var content = await ContentBindings.FetchContentAsync(fullPath).ConfigureAwait(false);
        var bytes = GC.AllocateUninitializedArray<byte>(content.GetPropertyAsInt32("length"));
        ContentBindings.CopyContent(content, bytes);
        return new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: true);
    }

    /// <summary>
    /// Returns the full URL (relative to the page origin) for a content
    /// asset. When the build has fingerprinted it, the URL incorporates a
    /// content-hash fingerprint embedded directly in the filename (e.g.
    /// <c>"Content/Sounds/soundbank_0.0123456789abcdef.flac"</c>),
    /// allowing browsers and CDNs to cache the asset indefinitely while
    /// still picking up changes on the next deploy.
    /// </summary>
    /// <param name="contentPath">
    /// Path of the asset relative to <see cref="BasePath"/>, including
    /// any file extension (e.g. <c>"Sounds/soundbank_0.flac"</c>).
    /// </param>
    public string GetVersionedUrl(
        string contentPath)
    {
        // Normalize platform-style separators to URL-style. Some callers
        // build paths with Path.Combine / backslashes; HTTP and the
        // fingerprint map only know forward slashes.
        contentPath = contentPath.Replace('\\', '/');

        if (ContentManifest.Registry.TryGet(FingerprintsManifest, out var fingerprints)
            && fingerprints.TryGet(contentPath, out var entry))
        {
            return this.BasePath + entry.Path;
        }

        return this.BasePath + contentPath;
    }
}
