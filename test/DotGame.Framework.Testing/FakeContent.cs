// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using DotGame.Framework.Audio;
using DotGame.Framework.Content;
using DotGame.Framework.Graphics;
using DotGame.Framework.Platform;
using DotGame.Framework.Platform.Content;

namespace DotGame.Framework.Testing;

/// <summary>
/// A <see cref="ContentService"/> over files in memory, with fake textures, shaders and sounds.
/// </summary>
public sealed class FakeContent : IContentServiceStrategy
{
    private readonly FakeTextureLoader _textures;
    private readonly FakeSoundLoader _sounds;
    private ContentService? _service;

    public FakeContent()
    {
        this._textures = new FakeTextureLoader(this);
        this._sounds = new FakeSoundLoader(this);
    }

    /// <summary>
    /// A service over these files by itself, started on first use. A <see cref="TestHost"/>'s
    /// game makes its own instead.
    /// </summary>
    public ContentService Service
    {
        get
        {
            if (this._service is null)
            {
                this._service = new ContentService(this.ToStrategy());
                _ = this._service.Initialize();
            }

            return this._service;
        }
    }

    /// <summary>
    /// The files that can be opened, by path.
    /// </summary>
    public Dictionary<string, byte[]> Files { get; } = [];

    /// <summary>
    /// The texture paths and sound paths loaded, in the order they were asked for.
    /// </summary>
    public List<string> LoadedTextures { get; } = [];

    public List<string> LoadedSounds { get; } = [];

    /// <summary>
    /// The textures loaded, by path.
    /// </summary>
    public Dictionary<string, Texture> Textures { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A content path whose load fails, as a missing file's does.
    /// </summary>
    public string? MissingContent { get; set; }

    /// <summary>
    /// What the content is: <see cref="AddFile(string, byte[])"/> adds to it, and a test can add
    /// entries with no file (a shader, say, which needs none).
    /// </summary>
    public ContentManifest Manifest { get; } = new ContentManifest("tests");

    public void AddFile(
        string path,
        string text)
    {
        this.AddFile(path, Encoding.UTF8.GetBytes(text));
    }

    public void AddFile(
        string path,
        byte[] bytes)
    {
        this.Files[path] = bytes;
        this.Manifest.Add(path, path);
    }

    internal Strategy<IContentServiceStrategy> ToStrategy() => new(this);

    IContentLoader<Texture> IContentServiceStrategy.TextureLoader => this._textures;

    IContentLoader<Shader> IContentServiceStrategy.ShaderLoader { get; } = new FakeShaderLoader();

    IContentLoader<Sound> IContentServiceStrategy.SoundLoader => this._sounds;

    IContentLoader<Music> IContentServiceStrategy.MusicLoader { get; } = new UnusedLoader<Music>();

    Task<Stream> IContentServiceStrategy.OpenStreamAsync(
        string contentPath)
    {
        if (!this.Files.TryGetValue(contentPath, out var bytes))
        {
            throw new FileNotFoundException($"No test file '{contentPath}'.");
        }

        return Task.FromResult<Stream>(new MemoryStream(bytes));
    }

    private sealed class FakeTextureLoader(FakeContent content) : IContentLoader<Texture>
    {
        public Task<Texture> LoadAsync(
            string contentPath)
        {
            content.LoadedTextures.Add(contentPath);
            if (contentPath == content.MissingContent)
            {
                return Task.FromException<Texture>(new FileNotFoundException($"No test texture '{contentPath}'."));
            }

            var texture = TestTextures.Create();
            content.Textures[contentPath] = texture;
            return Task.FromResult(texture);
        }
    }

    private sealed class FakeShaderLoader : IContentLoader<Shader>
    {
        public Task<Shader> LoadAsync(
            string contentPath)
        {
            return Task.FromResult(TestShaders.Create());
        }
    }

    private sealed class FakeSoundLoader(FakeContent content) : IContentLoader<Sound>
    {
        public Task<Sound> LoadAsync(
            string contentPath)
        {
            content.LoadedSounds.Add(contentPath);
            if (contentPath == content.MissingContent)
            {
                return Task.FromException<Sound>(new FileNotFoundException($"No test sound '{contentPath}'."));
            }

            return Task.FromResult(FakeAudio.CreateSound());
        }
    }

    private sealed class UnusedLoader<TContent> : IContentLoader<TContent>
        where TContent : class, IContent
    {
        public Task<TContent> LoadAsync(string contentPath) => throw new NotSupportedException();
    }
}
