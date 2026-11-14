// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.IO;
using System.Threading.Tasks;
using DotGame.Framework.Audio;
using DotGame.Framework.Content;
using DotGame.Framework.Graphics;

namespace DotGame.Framework.Platform.Content;

internal interface IContentServiceStrategy
{
    ContentManifest Manifest { get; }

    Task<Stream> OpenStreamAsync(string contentPath);

    // Required loaders

    IContentLoader<Texture> TextureLoader { get; }

    IContentLoader<Shader> ShaderLoader { get; }

    IContentLoader<Sound> SoundLoader { get; }

    IContentLoader<Music> MusicLoader { get; }
}
