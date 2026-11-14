// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;
using System.Threading.Tasks;
using DotGame.Framework.Android.Audio;
using DotGame.Framework.Android.Graphics;
using DotGame.Framework.Audio;
using DotGame.Framework.Content;
using DotGame.Framework.Graphics;
using DotGame.Framework.Platform.Common.Audio;
using DotGame.Framework.Platform.Content;

namespace DotGame.Framework.Android.Content;

internal sealed class ContentServiceStrategy : IContentServiceStrategy
{
    private readonly string _basePath;
    private readonly ContentManifest _manifest;

    public ContentServiceStrategy(
        AndroidGameEngine engine,
        string basePath,
        ContentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(basePath);
        ArgumentNullException.ThrowIfNull(manifest);

        this._basePath = basePath;
        this._manifest = manifest;

        this.TextureLoader = new TextureLoader(this, engine.GraphicsService);
        this.ShaderLoader = new ShaderLoader(this, engine.GraphicsService);
        this.SoundLoader = new SoundLoader(this);
        this.MusicLoader = new MusicLoader(this);

        Trace.Write(0x0e9112d0, $"[ContentService] BasePath: {this._basePath}");
    }

    internal string BasePath => this._basePath;

    /// <summary>
    /// Observes the playback the game asks of each music track this service loads: the audio
    /// service, which pauses and resumes music around the activity's lifecycle and audio focus.
    /// </summary>
    internal IMusicPlaybackListener? MusicPlaybackListener { get; set; }

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
        await this.TextureLoader.ReloadAll();
        await this.ShaderLoader.ReloadAll();
    }

    public Task<Stream> OpenStreamAsync(
        string contentPath)
    {
        var fullPath = this.GetFullPath(contentPath);

        Trace.Write(0x23197b9c, $"[ContentService] OpenStream '{fullPath}'");

        nint nativeAssetManager = AssetStream.GetNativeAssetManager();
        var stream = new AssetStream(
            nativeAssetManager, fullPath, AssetStream.AASSET_MODE_RANDOM);

        return Task.FromResult<Stream>(stream);
    }

    internal string GetFullPath(
        string contentPath)
    {
        return (this._basePath + contentPath).Replace('\\', '/');
    }
}
