// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.ContentPipeline.BuildTool.Audio;
using DotGame.Framework.ContentPipeline.BuildTool.Content;
using DotGame.Framework.ContentPipeline.BuildTool.Graphics;

namespace DotGame.Framework.ContentPipeline.BuildTool;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.Out.Write(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        string command = args[0];
        Action<CommandLineOptions>? run = command switch
        {
            TextureCommand.Name => TextureCommand.Run,
            LaunchScreenCommand.Name => LaunchScreenCommand.Run,
            LaunchScreenCommand.PageName => LaunchScreenCommand.RunPage,
            ShaderCommand.Name => ShaderCommand.Run,
            DefaultShadersCommand.Name => DefaultShadersCommand.Run,
            SoundCommand.Name => SoundCommand.Run,
            MusicCommand.Name => MusicCommand.Run,
            ContentCachePruneCommand.Name => ContentCachePruneCommand.Run,
            _ => null,
        };
        string errorCode = command switch
        {
            TextureCommand.Name => TextureCommand.ErrorCode,
            LaunchScreenCommand.Name => LaunchScreenCommand.ErrorCode,
            LaunchScreenCommand.PageName => LaunchScreenCommand.ErrorCode,
            ShaderCommand.Name => ShaderCommand.ErrorCode,
            DefaultShadersCommand.Name => DefaultShadersCommand.ErrorCode,
            SoundCommand.Name => SoundCommand.ErrorCode,
            MusicCommand.Name => MusicCommand.ErrorCode,
            ContentCachePruneCommand.Name => ContentCachePruneCommand.ErrorCode,
            _ => "CPB0001",
        };

        return ContentBuildOutput.Run(errorCode, () =>
        {
            if (run is null)
            {
                throw new ContentBuildException($"Unknown command '{command}'. Run without arguments for usage.");
            }
            run(CommandLineOptions.Parse(args.AsSpan(1)));
        });
    }

    private const string Usage =
        """
        DotGame.Framework.ContentPipeline.BuildTool - builds the DotGame framework content types.
        Normally invoked by the content processor command templates in ContentPipeline.<platform>.targets.

        Usage: DotGame.Framework.ContentPipeline.BuildTool <command> [--option value]...

        Commands:

          texture          Encodes an image to a texture format, premultiplying alpha first:
                           WebP (web, Android, iOS; cwebp's bytes), or KTX2: RGBA8, zlib
                           supercompressed (desktop), or ASTC blocks encoded with astcenc
                           (lossy items on Android and iOS; quality picks the block size).
                             --input <file> --output <file>
                             --compression <Lossless|Lossy>   [--quality <1-10>]   (default 8)
                             [--format <webp|ktx2>]                                (default webp)
                             [--gpu-compression <none|astc>]  ktx2, Lossy only     (default none)
                             [--premultiply-alpha <true|false>]                    (default true)
                             [--intermediate-dir <dir>]       Required for astc
                             [--astcenc <exe>]

          launch-screen    Makes a platform's form of the game's launch screen (its LaunchScreen item):
                           Android's drawables and splash theme, iOS's storyboard and images,
                           desktop's KTX2 or the web's WebP images, and optionally the code that
                           registers it with the framework.
                             --platform <android|ios|desktop|webgl> --output-dir <dir>
                             --logo <file> --background <#rrggbb>   [--logo-size <dp>]   (default 160)
                             [--branding <file>] [--branding-width <dp>]
                             [--cs-output <file>]
                             [--android-theme-parent <style>]       (default @android:style/Theme)

          launch-page      Writes the web page and app manifest with the launch screen filled in.
                             --page-template <file> --page-output <file> --logo-url <url>
                             --logo <file> --background <#rrggbb>   [--logo-size <dp>]
                             [--branding <file> --branding-url <url>] [--branding-width <dp>]
                             [--manifest-template <file> --manifest-output <file>]

          shader           Compiles a Slang shader with slangc, spirv-cross and spirv2dxil.
                             --input <file> --output <file>   Output named <name>[.vert|.frag].<ext>
                             --shader-types <types>           Any of spirv, msl, dxil, glsl, e.g. spirv,msl,dxil
                             [--intermediate-dir <dir>]       (default: the output directory)
                             [--slangc <exe>] [--spirv-cross <exe>] [--spirv2dxil <exe>]

          default-shaders  Compiles the default sprite shader to DefaultShaderSource.g.cs.
                             --input <file> --output-dir <dir> --namespace <namespace>
                             --target <spirv|msl|glsl>
                             [--slangc <exe>] [--spirv-cross <exe>] [--spirv2dxil <exe>]

          sound            Encodes a sound effect from a WAV, FLAC or Ogg Opus source.
                             --input <file> --output <file>
                             --codec <flac|pcm_s16le|libopus|aac_at>
                             --compression <Lossless|Lossy>   Must match the codec.
                             [--quality <1-10>]                (default 8; lossy codecs only)
                             [--sample-rate <hz>]              (default 44100; libopus always 48000)
                             [--channels <1|2>]                (default: preserve the source)
                             [--afconvert <exe>]               (aac_at, macOS only)
                             libopus writes Ogg Opus, or Opus in CAF for an output ending .caf (iOS).

          music            Encodes a music track from a WAV, FLAC or Ogg Opus source, preserving its channel count.
                             --input <file> --output <file>
                             [--codec <libopus|aac_at>]        (default libopus)
                             [--quality <1-10>]                (default 5)
                             [--sample-rate <hz>]              (default 44100; libopus always 48000)
                             [--afconvert <exe>]               (aac_at, macOS only)

          cache-prune      Removes content cache entries no build has used recently.  Never run by a build.
                             --cache-dir <dir>
                             [--unused-days <n>]               Remove entries unused for n days (else report only).
                             [--what-if]                       Report without removing.

        Tools default to the executable name, found on PATH.

        """;
}
