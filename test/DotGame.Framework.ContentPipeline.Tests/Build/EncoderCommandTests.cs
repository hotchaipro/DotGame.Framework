// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Buffers;
using DotGame.ContentPipeline.Testing;
using DotGame.Framework.ContentPipeline.BuildTool;
using DotGame.Framework.ContentPipeline.BuildTool.Audio;
using DotGame.Framework.ContentPipeline.BuildTool.Graphics;
using DotGame.Framework.Platform.Common.Content;
using DotGame.Imaging;
using DotGame.Imaging.WebP;
using Xunit;

namespace DotGame.Framework.ContentPipeline.Tests.Build;

/// <summary>
/// The build tool's texture, sound and music commands, in process, with the fake tool in place of
/// ffmpeg and astcenc: what they ask the tools for, and what they do with a failure (WebP is encoded in
/// process).
/// </summary>
public sealed class EncoderCommandTests
{
    private static CommandLineOptions Options(params string[] args) => CommandLineOptions.Parse(args);

    private static string SourceImage(TestDirectory directory)
    {
        string path = directory.Path_("hero.png");
        Images.Sample().SavePng(path);
        return path;
    }

    [Fact]
    public void TextureKtx2_PremultipliesByDefault_AndSaysSo()
    {
        using var directory = new TestDirectory();
        string output = directory.Path_("out", "hero.ktx2");

        TextureCommand.Run(Options("--format", "ktx2", "--compression", "Lossless", "--input", SourceImage(directory), "--output", output));

        Ktx2Texture texture = Ktx2Texture.Read(File.ReadAllBytes(output), "hero");
        RgbaImage expected = Images.Sample();
        expected.PremultiplyAlpha();
        Assert.Equal(expected.Pixels, texture.Data);
        Assert.True(texture.Premultiplied);
    }

    [Fact]
    public void TextureKtx2_WithPremultiplyOff_StoresTheChannelsAsTheyAre()
    {
        using var directory = new TestDirectory();
        string output = directory.Path_("hero.ktx2");

        TextureCommand.Run(Options("--format", "ktx2", "--compression", "Lossless", "--premultiply-alpha", "false", "--input", SourceImage(directory), "--output", output));

        Ktx2Texture texture = Ktx2Texture.Read(File.ReadAllBytes(output), "hero");
        Assert.Equal(Images.Sample().Pixels, texture.Data);
        Assert.False(texture.Premultiplied);
    }

    [Fact]
    public void TextureKtx2_LossyItem_IsStoredLosslessly()
    {
        using var directory = new TestDirectory();
        string lossless = directory.Path_("a.ktx2");
        string lossy = directory.Path_("b.ktx2");
        string source = SourceImage(directory);

        TextureCommand.Run(Options("--format", "ktx2", "--compression", "Lossless", "--input", source, "--output", lossless));
        TextureCommand.Run(Options("--format", "ktx2", "--compression", "Lossy", "--quality", "1", "--input", source, "--output", lossy));

        Assert.Equal(File.ReadAllBytes(lossless), File.ReadAllBytes(lossy));
    }

    [Fact]
    public void Texture_FailsOnAMissingSource()
    {
        using var directory = new TestDirectory();

        var error = Assert.Throws<ContentBuildException>(() => TextureCommand.Run(Options("--format", "ktx2", "--compression", "Lossless", "--input", directory.Path_("none.png"), "--output", directory.Path_("o.ktx2"))));

        Assert.Contains("none.png", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--format", "webp", "Lossy")]
    [InlineData("--format", "ktx2", "Lossless")]
    public void Texture_RefusesGpuCompressionOutsideLossyKtx2(string option, string format, string compression)
    {
        using var directory = new TestDirectory();

        Assert.Throws<ContentBuildException>(() => TextureCommand.Run(Options(option, format, "--gpu-compression", "astc", "--compression", compression, "--intermediate-dir", directory.Root, "--input", SourceImage(directory), "--output", directory.Path_("o.ktx2"))));
    }

    [Fact]
    public void Texture_RefusesAMisspelledOption()
    {
        using var directory = new TestDirectory();

        Assert.Throws<ContentBuildException>(() => TextureCommand.Run(Options("--format", "ktx2", "--compression", "Lossless", "--qualty", "5", "--input", SourceImage(directory), "--output", directory.Path_("o.ktx2"))));
    }

    [Fact]
    public void TextureWebP_Lossless_KeepsTransparentColors_AndStaysLossless()
    {
        using var directory = new TestDirectory();
        string output = directory.Path_("hero.webp");

        TextureCommand.Run(Options("--compression", "Lossless", "--input", SourceImage(directory), "--output", output));

        // The premultiplied pixels exactly, transparent ones' colors (zeros, premultiplied) and all
        byte[] webp = File.ReadAllBytes(output);
        Assert.True(WebPCodec.TryGetDecodedSize(webp, PixelLayout.Rgba32, out _, out _, out int byteCount));
        var decoded = new byte[byteCount];
        WebPCodec.Decode(webp, decoded, PixelLayout.Rgba32, premultiplyAlpha: false);
        RgbaImage expected = Images.Sample();
        expected.PremultiplyAlpha();
        Assert.Equal(expected.Pixels, decoded);
    }

    [Theory]
    [InlineData("1", 10)]
    [InlineData("8", 80)]
    [InlineData("10", 100)]
    public void TextureWebP_Lossy_IsCwebpsSettings(string quality, int webpQuality)
    {
        using var directory = new TestDirectory();
        string output = directory.Path_("hero.webp");

        TextureCommand.Run(Options("--compression", "Lossy", "--quality", quality, "--input", SourceImage(directory), "--output", output));

        // What cwebp -q <quality x 10> -exact -m 6 -alpha_q 100 writes, from the premultiplied pixels
        RgbaImage image = Images.Sample();
        image.PremultiplyAlpha();
        var expected = new ArrayBufferWriter<byte>();
        WebPCodec.Encode(expected, image.Width, image.Height, image.Pixels, PixelLayout.Rgba32, unpremultiplyAlpha: false, new WebPEncoderOptions { Quality = webpQuality, Method = 6, Exact = true, AlphaQuality = 100 });
        Assert.Equal(expected.WrittenSpan.ToArray(), File.ReadAllBytes(output));
    }

    // Each quality gets libwebp's nearest level at or above it: never further from the source
    [Theory]
    [InlineData(1, 20)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(4, 40)]
    [InlineData(5, 60)]
    [InlineData(6, 60)]
    [InlineData(7, 80)]
    [InlineData(8, 80)]
    [InlineData(9, 100)]
    [InlineData(10, 100)]
    public void NearLosslessLevel_RoundsTowardTheSource(int quality, int level)
    {
        Assert.Equal(level, TextureCommand.NearLosslessLevel(quality));
    }

    [Theory]
    [InlineData("6", 60)]
    [InlineData("7", 80)]
    public void TextureWebP_LosslessBelowQuality9_IsCwebpsNearLosslessSettings(string quality, int nearLossless)
    {
        using var directory = new TestDirectory();
        string output = directory.Path_("hero.webp");

        TextureCommand.Run(Options("--compression", "Lossless", "--quality", quality, "--input", SourceImage(directory), "--output", output));

        // What cwebp -lossless -near_lossless <level> -exact -m 6 -q 100 writes, from the premultiplied pixels
        RgbaImage image = Images.Sample();
        image.PremultiplyAlpha();
        var expected = new ArrayBufferWriter<byte>();
        WebPCodec.Encode(expected, image.Width, image.Height, image.Pixels, PixelLayout.Rgba32, unpremultiplyAlpha: false, new WebPEncoderOptions { Lossless = true, NearLossless = nearLossless, Quality = 100, Method = 6, Exact = true });
        Assert.Equal(expected.WrittenSpan.ToArray(), File.ReadAllBytes(output));
    }

    [Theory]
    [InlineData("9")]
    [InlineData("10")]
    public void TextureWebP_LosslessAtQuality9And10_IsExact(string quality)
    {
        using var directory = new TestDirectory();
        string source = SourceImage(directory);
        string exact = directory.Path_("exact.webp");
        string output = directory.Path_("hero.webp");

        TextureCommand.Run(Options("--compression", "Lossless", "--input", source, "--output", exact));
        TextureCommand.Run(Options("--compression", "Lossless", "--quality", quality, "--input", source, "--output", output));

        Assert.Equal(File.ReadAllBytes(exact), File.ReadAllBytes(output));
    }

    [Fact]
    public void TextureKtx2_LosslessBelowQuality10_IsStoredExactly()
    {
        using var directory = new TestDirectory();
        string source = SourceImage(directory);
        string lossless = directory.Path_("lossless.ktx2");
        string nearLossless = directory.Path_("near.ktx2");

        TextureCommand.Run(Options("--format", "ktx2", "--compression", "Lossless", "--input", source, "--output", lossless));
        TextureCommand.Run(Options("--format", "ktx2", "--compression", "Lossless", "--quality", "1", "--input", source, "--output", nearLossless));

        Assert.Equal(File.ReadAllBytes(lossless), File.ReadAllBytes(nearLossless));
    }

    [Fact]
    public void TextureWebP_NeedsNoTool()
    {
        using var directory = new TestDirectory();

        // cwebp is gone: its option is an error now, so a stale command line says so
        var error = Assert.Throws<ContentBuildException>(() => TextureCommand.Run(Options("--compression", "Lossless", "--cwebp", "cwebp", "--input", SourceImage(directory), "--output", directory.Path_("hero.webp"))));

        Assert.Contains("cwebp", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Texture_ReportsAToolThatWontStart()
    {
        using var directory = new TestDirectory();
        string missing = directory.Path_("no-such-astcenc");

        var error = Assert.Throws<ContentBuildException>(() => TextureCommand.Run(Options("--format", "ktx2", "--gpu-compression", "astc", "--compression", "Lossy", "--astcenc", missing, "--intermediate-dir", directory.Path_("obj"), "--input", SourceImage(directory), "--output", directory.Path_("hero.ktx2"))));

        Assert.Contains("failed to start", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(10, 4)]
    [InlineData(8, 4)]
    [InlineData(7, 6)]
    [InlineData(5, 6)]
    [InlineData(4, 8)]
    [InlineData(1, 8)]
    public void AstcBlockSize_FollowsQuality(int quality, int block)
    {
        Assert.Equal(block, TextureCommand.AstcBlockSize(quality));
    }

    // A sine at 440 Hz, half a second, as a 16-bit WAV
    private static string Tone(TestDirectory directory, string name, int sampleRate = 44100, int channels = 2)
    {
        string path = directory.Path_(name);
        WavWriter.WriteInt16(path, Tones.Sine(sampleRate, channels, sampleRate / 2));
        return path;
    }

    [Theory]
    [InlineData(1, 48000)]
    [InlineData(5, 112000)]
    [InlineData(10, 256000)]
    public void OpusBitrate_FollowsTheLadder(int quality, int bitrate)
    {
        Assert.Equal(bitrate, AudioEncoder.OpusBitrate(quality));
    }

    [Theory]
    [InlineData(1, 2, 64)]
    [InlineData(5, 2, 160)]
    [InlineData(10, 2, 320)]
    [InlineData(8, 1, 256)]
    [InlineData(10, 1, 256)]
    [InlineData(5, 1, 160)]
    public void AacBitrate_FollowsTheLadder_UpToAudioToolboxsMonoLimit(int quality, int channels, int kbps)
    {
        Assert.Equal(kbps, AppleAacEncoder.Bitrate(quality, channels));
    }

    [Theory]
    [InlineData("speex")]
    [InlineData("libvorbis")]
    [InlineData("libmp3lame")]
    public void Encode_RefusesACodecItDoesntHave(string codec)
    {
        using var directory = new TestDirectory();
        PcmAudio audio = AudioReader.Read(Tone(directory, "a.wav"));

        Assert.Throws<ContentBuildException>(() => AudioEncoder.Encode(codec, 5, audio, "a.wav", directory.Path_("out"), "afconvert"));
    }

    [Theory]
    [InlineData("flac", true)]
    [InlineData("pcm_s16le", true)]
    [InlineData("libopus", false)]
    [InlineData("aac_at", false)]
    public void IsLossless_KnowsEachCodec(string codec, bool lossless)
    {
        Assert.Equal(lossless, AudioEncoder.IsLossless(codec));
    }

    [Fact]
    public void Opus_IsEncodedInProcess_At48Khz_ToTheSourcesLength()
    {
        using var directory = new TestDirectory();
        string output = directory.Path_("out", "a.opus");

        SoundCommand.Run(Options("--codec", "libopus", "--compression", "Lossy", "--channels", "1", "--sample-rate", "44100", "--input", Tone(directory, "a.wav"), "--output", output));

        // Opus is always 48 kHz; the build tool resampled, mixed and encoded, with no tool
        Assert.Empty(directory.ToolCalls());
        PcmAudio decoded = AudioReader.Read(output);
        Assert.Equal(48000, decoded.SampleRate);
        Assert.Equal(1, decoded.Channels);
        Assert.Equal(24000, decoded.Frames);
    }

    [Fact]
    public void Opus_IsTheSameBytesEveryTime()
    {
        using var directory = new TestDirectory();
        string source = Tone(directory, "a.wav");

        MusicCommand.Run(Options("--input", source, "--output", directory.Path_("first.opus")));
        MusicCommand.Run(Options("--input", source, "--output", directory.Path_("second.opus")));

        Assert.Equal(File.ReadAllBytes(directory.Path_("first.opus")), File.ReadAllBytes(directory.Path_("second.opus")));
    }

    [Fact]
    public void Sound_FailsOnAMissingSource()
    {
        using var directory = new TestDirectory();

        var error = Assert.Throws<ContentBuildException>(() => SoundCommand.Run(Options("--codec", "pcm_s16le", "--compression", "Lossless", "--input", directory.Path_("none.wav"), "--output", directory.Path_("o.wav"))));

        Assert.Contains("none.wav", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sound_ToWavAndFlac_NeedsNoTool_AndKeepsTheSamples()
    {
        using var directory = new TestDirectory();
        string source = Tone(directory, "a.wav");

        SoundCommand.Run(Options("--codec", "pcm_s16le", "--compression", "Lossless", "--input", source, "--output", directory.Path_("out.wav")));
        SoundCommand.Run(Options("--codec", "flac", "--compression", "Lossless", "--input", source, "--output", directory.Path_("out.flac")));

        Assert.Empty(directory.ToolCalls());
        float[] expected = AudioReader.Read(source).Samples;
        Assert.Equal(expected, AudioReader.Read(directory.Path_("out.wav")).Samples);
        Assert.Equal(expected, AudioReader.Read(directory.Path_("out.flac")).Samples);
    }

    [Fact]
    public void Sound_ResamplesToTheRateItsGiven()
    {
        using var directory = new TestDirectory();

        SoundCommand.Run(Options("--codec", "pcm_s16le", "--compression", "Lossless", "--sample-rate", "48000", "--input", Tone(directory, "a.wav"), "--output", directory.Path_("out.wav")));

        PcmAudio result = AudioReader.Read(directory.Path_("out.wav"));
        Assert.Equal(48000, result.SampleRate);
        Assert.Equal(24000, result.Frames);
    }

    [Fact]
    public void Aac_NeedsAMac()
    {
        using var directory = new TestDirectory();
        string input = Tone(directory, "a.wav");

        if (OperatingSystem.IsMacOS())
        {
            // The build tool writes a float WAV for afconvert, which is left behind by neither
            MusicCommand.Run(Options("--codec", "aac_at", "--input", input, "--output", directory.Path_("a.m4a")));
            Assert.True(File.Exists(directory.Path_("a.m4a")));
            Assert.False(File.Exists(directory.Path_("a.m4a.decoded.wav")));
        }
        else
        {
            var error = Assert.Throws<ContentBuildException>(() => MusicCommand.Run(Options("--codec", "aac_at", "--input", input, "--output", directory.Path_("a.m4a"))));
            Assert.Contains("only on a Mac", error.Message, StringComparison.Ordinal);
            Assert.Throws<ContentBuildException>(() => SoundCommand.Run(Options("--codec", "aac_at", "--compression", "Lossy", "--input", input, "--output", directory.Path_("a.m4a"))));
        }
    }

    [Fact]
    public void Sound_RefusesACompressionTheCodecCantDo()
    {
        using var directory = new TestDirectory();
        string input = Tone(directory, "a.wav");

        Assert.Throws<ContentBuildException>(() => SoundCommand.Run(Options("--codec", "libopus", "--compression", "Lossless", "--input", input, "--output", directory.Path_("a.opus"))));
        Assert.Throws<ContentBuildException>(() => SoundCommand.Run(Options("--codec", "flac", "--compression", "Lossy", "--input", input, "--output", directory.Path_("a.flac"))));
    }

    [Fact]
    public void Sound_KeepsAMonoSourceMono_UnlessToldOtherwise()
    {
        using var directory = new TestDirectory();
        string input = Tone(directory, "a.wav", channels: 1);

        SoundCommand.Run(Options("--codec", "pcm_s16le", "--compression", "Lossless", "--input", input, "--output", directory.Path_("mono.wav")));
        SoundCommand.Run(Options("--codec", "pcm_s16le", "--compression", "Lossless", "--channels", "2", "--input", input, "--output", directory.Path_("stereo.wav")));

        Assert.Equal(1, AudioReader.Read(directory.Path_("mono.wav")).Channels);
        Assert.Equal(2, AudioReader.Read(directory.Path_("stereo.wav")).Channels);
    }

    [Fact]
    public void Music_DefaultsToOpusQuality5()
    {
        using var directory = new TestDirectory();
        string source = Tone(directory, "a.wav");

        MusicCommand.Run(Options("--input", source, "--output", directory.Path_("default.opus")));
        MusicCommand.Run(Options("--codec", "libopus", "--quality", "5", "--input", source, "--output", directory.Path_("q5.opus")));

        Assert.Equal(File.ReadAllBytes(directory.Path_("q5.opus")), File.ReadAllBytes(directory.Path_("default.opus")));
    }

    [Fact]
    public void Music_WithOpus_AlwaysEncodesAt48Khz()
    {
        using var directory = new TestDirectory();

        MusicCommand.Run(Options("--codec", "libopus", "--sample-rate", "44100", "--input", Tone(directory, "a.wav"), "--output", directory.Path_("a.opus")));

        Assert.Equal(48000, AudioReader.Read(directory.Path_("a.opus")).SampleRate);
    }

    [Theory]
    [InlineData("vorbis")]
    [InlineData("mp3")]
    [InlineData("m4a")]
    public void Sound_RefusesASourceFormatItDoesntRead_NamingIt(string format)
    {
        using var directory = new TestDirectory();
        byte[] header = format switch
        {
            "vorbis" => AudioFiles.OggVorbis(44100, 2, 1000),
            "mp3" => [0x49, 0x44, 0x33, 4, 0, 0, 0, 0, 0, 0],
            _ => [0, 0, 0, 0x20, 0x66, 0x74, 0x79, 0x70, 0x4D, 0x34, 0x41, 0x20],
        };
        string input = directory.Path_("a." + format);
        File.WriteAllBytes(input, header);

        var error = Assert.Throws<ContentBuildException>(() => SoundCommand.Run(Options("--codec", "pcm_s16le", "--compression", "Lossless", "--input", input, "--output", directory.Path_("o.wav"))));

        Assert.Contains(format switch { "vorbis" => "Ogg Vorbis", "mp3" => "MP3", _ => "MP4" }, error.Message, StringComparison.Ordinal);
        Assert.Contains(AudioReader.SupportedFormats, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpandCommandTemplate_ReplacesTheTokens_AndTrimsTheFolder()
    {
        Assert.Equal(
            "enc \"in\" \"out\" \"dir\" {Quality}",
            ExternalProcess.ExpandCommandTemplate("enc \"{InputFile}\" \"{OutputFile}\" \"{IntermediateDir}\" {Quality}", "in", "out", "dir" + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void RunShellCommand_HonorsTheTemplatesQuoting_WithSpacesInPaths()
    {
        using var directory = new TestDirectory(" with space");
        string input = directory.WriteFile("in put.png", "x");
        string output = directory.Path_("out put.bin");

        ExternalProcess.RunShellCommand($"\"{Paths.FakeTool}\" texture --input \"{input}\" --output \"{output}\"", "encode");

        Assert.True(File.Exists(output));
    }

    [Fact]
    public void RunShellCommand_ReportsAFailureWithItsCommand()
    {
        using var directory = new TestDirectory();
        directory.SetToolConfig("fail.texture", "exit");
        string input = directory.WriteFile("in.png", "x");

        var error = Assert.Throws<ContentBuildException>(() => ExternalProcess.RunShellCommand($"\"{Paths.FakeTool}\" texture --input \"{input}\" --output \"{directory.Path_("o")}\"", "encode"));

        Assert.Contains("exit code 3", error.Message, StringComparison.Ordinal);
        Assert.Contains("in.png", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_ReportsAnExitCode()
    {
        using var directory = new TestDirectory();
        string input = directory.WriteFile("in.png", "x");
        directory.SetToolConfig("fail.texture", "exit");

        var error = Assert.Throws<ContentBuildException>(() => ExternalProcess.Run(Paths.FakeTool, ["texture", "--input", input, "--output", directory.Path_("o")], "the encode"));

        Assert.Equal("the encode failed with exit code 3.", error.Message);
    }

    [Fact]
    public void ContentBuildOutputRun_TurnsAFailureIntoACanonicalError()
    {
        // No stack trace on an expected failure, and a non-zero exit either way
        Assert.Equal(1, ContentBuildOutput.Run("TEX0001", () => throw new ContentBuildException("expected")));
        Assert.Equal(1, ContentBuildOutput.Run("TEX0001", () => throw new InvalidOperationException("a bug")));
        Assert.Equal(0, ContentBuildOutput.Run("TEX0001", () => { }));
    }

    [Fact]
    public void TextureKtx2_DecodedPixels_MatchTheGolden()
    {
        using var directory = new TestDirectory();
        string output = directory.Path_("hero.ktx2");

        TextureCommand.Run(Options("--format", "ktx2", "--compression", "Lossless", "--input", SourceImage(directory), "--output", output));

        // The pixels, not the file: zlib's bytes can differ between machines and runtimes
        Goldens.Check(GoldenFiles.Framework, "texture.ktx2.pixels", ContentVersions.Of("ContentPipelineTextureVersion"), Ktx2Texture.Read(File.ReadAllBytes(output), "hero").Data.ToArray());
    }
}
