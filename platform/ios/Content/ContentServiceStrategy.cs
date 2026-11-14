// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;
using System.Threading.Tasks;
using DotGame.Framework.Audio;
using DotGame.Framework.Content;
using DotGame.Framework.Graphics;
using DotGame.Framework.iOS.Audio;
using DotGame.Framework.iOS.Graphics;
using DotGame.Framework.Platform.Content;

namespace DotGame.Framework.iOS.Content;

internal sealed class ContentServiceStrategy : IContentServiceStrategy
{
    private readonly string _basePath;
    private readonly ContentManifest _manifest;

    public ContentServiceStrategy(
        GraphicsServiceStrategy graphicsService,
        string basePath,
        ContentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(basePath);
        ArgumentNullException.ThrowIfNull(manifest);

        this._basePath = basePath;
        this._manifest = manifest;

        this.TextureLoader = new TextureLoader(this, graphicsService);
        this.ShaderLoader = new ShaderLoader(this, graphicsService);
        this.SoundLoader = new SoundLoader(this);
        this.MusicLoader = new MusicLoader(this);

        Trace.Write(0xa9bf2566, $"[ContentService] BasePath: {this._basePath}");
    }

    internal string BasePath => this._basePath;

    /// <summary>
    /// The audio service, which observes the playback the game asks of each music track this
    /// service loads (to pause and resume it around the app's lifecycle and audio session
    /// interruptions) and opens the audio output the tracks wait for.
    /// </summary>
    internal AudioServiceStrategy? AudioService { get; set; }

    public ContentManifest Manifest => this._manifest;

    internal string ResolveContentPath(string contentKey, string? subKey = null) =>
        this._manifest.ResolveContentPath(contentKey, subKey);

    public IContentLoader<Texture> TextureLoader { get; }

    public IContentLoader<Shader> ShaderLoader { get; }

    public IContentLoader<Sound> SoundLoader { get; }

    public IContentLoader<Music> MusicLoader { get; }

    public Task<Stream> OpenStreamAsync(
        string contentPath)
    {
        var fullPath = this.GetFullPath(contentPath);

        Trace.Write(0xe229c4ad, $"[ContentService] OpenStream '{fullPath}'");

        var stream = File.OpenRead(fullPath);
        return Task.FromResult<Stream>(stream);
    }

    internal string GetFullPath(
        string contentPath)
    {
        return Path.Combine(this._basePath, contentPath);
    }
}
