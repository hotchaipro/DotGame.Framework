// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Globalization;
using DotGame.ContentPipeline.Testing;
using DotGame.Framework.ContentPipeline.BuildTool;
using DotGame.Framework.ContentPipeline.BuildTool.Audio;
using DotGame.Framework.ContentPipeline.BuildTool.Graphics;
using Xunit;

namespace DotGame.Framework.ContentPipeline.Tests.Integration;

/// <summary>
/// The real encoders, where they're installed, against golden hashes. A golden holds for one build
/// of a tool and one version in ContentVersions.props (see <see cref="Goldens"/>), so a machine with
/// another build skips it, saying so, and one output change without a version bump fails it.
/// </summary>
[Trait(Traits.Category, Traits.Slow)]
public sealed class RealToolTests
{
    private static string Project => GoldenFiles.Framework;

    private static CommandLineOptions Options(params string[] args) => CommandLineOptions.Parse(args);

    // One second of a tone, at CD rate, the same every time
    private static string MakeTone(string ffmpeg, TestDirectory directory, string name) => MakeTone(ffmpeg, directory.Path_(name));

    private static string MakeTone(string ffmpeg, string path)
    {
        (int exitCode, string output) = ProcessRunner.Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=1", "-ac", "2", "-bitexact", path]);
        Assert.True(exitCode == 0, output);
        return path;
    }

    // Decodes to raw PCM with one tool build, so a hash doesn't hold a container's version text
    private static byte[] DecodePcm(string ffmpeg, TestDirectory directory, string path)
    {
        string raw = directory.Path_(Path.GetFileName(path) + ".raw");
        (int exitCode, string output) = ProcessRunner.Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-i", path, "-f", "s16le", "-acodec", "pcm_s16le", raw]);
        Assert.True(exitCode == 0, output);
        return File.ReadAllBytes(raw);
    }

    private static string Probe(string ffprobe, string path, string entries)
    {
        (int exitCode, string output) = ProcessRunner.Run(ffprobe, ["-v", "error", "-show_entries", entries, "-of", "csv=p=0", path]);
        Assert.True(exitCode == 0, output);
        return output.Trim();
    }

    [Fact]
    public void Sound_ToWav_KeepsTheSamples_AndHasNoMetadata()
    {
        string ffmpeg = RealTools.Require("ffmpeg");
        using var directory = new TestDirectory();
        string source = MakeTone(ffmpeg, directory, "tone.wav");
        string output = directory.Path_("out.wav");

        SoundCommand.Run(Options("--codec", "pcm_s16le", "--compression", "Lossless", "--input", source, "--output", output));

        Assert.Equal(DecodePcm(ffmpeg, directory, source), DecodePcm(ffmpeg, directory, output));
    }

    [Fact]
    public void Sound_ToFlac_IsLossless()
    {
        string ffmpeg = RealTools.Require("ffmpeg");
        using var directory = new TestDirectory();
        string source = MakeTone(ffmpeg, directory, "tone.wav");
        string output = directory.Path_("out.flac");

        SoundCommand.Run(Options("--codec", "flac", "--compression", "Lossless", "--input", source, "--output", output));

        Assert.Equal(DecodePcm(ffmpeg, directory, source), DecodePcm(ffmpeg, directory, output));
    }

    [Fact]
    public void Flac_IsNoLargerThanFFmpegsLevel12()
    {
        // ffmpeg's encoder was the pipeline's before; its 8 KB padding block is set aside
        string ffmpeg = RealTools.Require("ffmpeg");
        using var directory = new TestDirectory();
        string source = Tones.WriteWav(directory.Path_("noise.wav"), Tones.Noise(44100, 2, 44100, amplitude: 0.05f));
        string ours = directory.Path_("ours.flac");
        string theirs = directory.Path_("theirs.flac");
        SoundCommand.Run(Options("--codec", "flac", "--compression", "Lossless", "--input", source, "--output", ours));
        (int exitCode, string made) = ProcessRunner.Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-i", source, "-c:a", "flac", "-compression_level", "12", "-map_metadata", "-1", "-fflags", "+bitexact", theirs]);
        Assert.True(exitCode == 0, made);

        Assert.True(new FileInfo(ours).Length <= (new FileInfo(theirs).Length - 8192) * 1.01, $"{new FileInfo(ours).Length} against {new FileInfo(theirs).Length}");
        Assert.Equal(DecodePcm(ffmpeg, directory, source), DecodePcm(ffmpeg, directory, ours));
    }

    [Fact]
    public void Music_ToOpus_MatchesTheGolden()
    {
        string ffmpeg = RealTools.Require("ffmpeg");
        using var directory = new TestDirectory();
        string source = MakeTone(ffmpeg, directory, "tone.wav");
        string output = directory.Path_("out.opus");

        MusicCommand.Run(Options("--codec", "libopus", "--quality", "5", "--input", source, "--output", output));

        (_, string sampleRate, string channels, _) = AudioFiles.Read(output);
        Assert.Equal("48000,2", $"{sampleRate},{channels}");

        // The file: the encoder is the build tool's own, so the version alone keys it
        Goldens.Check(Project, "music.opus", ContentVersions.Of("ContentPipelineMusicVersion"), File.ReadAllBytes(output));
    }

    [Fact]
    public void Music_WithCoverArt_EncodesOnlyTheAudio()
    {
        // P16: music from a composer or a store has cover art, which ffmpeg would map as video
        string ffmpeg = RealTools.Require("ffmpeg");
        string ffprobe = RealTools.Require("ffprobe");
        using var directory = new TestDirectory();
        string tone = MakeTone(ffmpeg, directory, "tone.wav");
        string cover = directory.Path_("cover.png");
        Images.Sample(32, 32).SavePng(cover);
        string source = directory.Path_("song.flac");
        (int exitCode, string made) = ProcessRunner.Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-i", tone, "-i", cover, "-map", "0:a", "-map", "1:v", "-c:a", "flac", "-c:v", "copy", "-disposition:v", "attached_pic", source]);
        Assert.True(exitCode == 0, made);
        Assert.Contains("video", Probe(ffprobe, source, "stream=codec_type"), StringComparison.Ordinal);
        string output = directory.Path_("out.opus");

        MusicCommand.Run(Options("--codec", "libopus", "--input", source, "--output", output));

        Assert.Equal("audio", Probe(ffprobe, output, "stream=codec_type"));
    }

    [Fact]
    public void Music_ToOpusInCaf_HoldsTheOggFilesPacketsAndLength()
    {
        // iOS's LossyFormat="Opus": the same packets as the Ogg file, priming and length in the packet table
        string ffmpeg = RealTools.Require("ffmpeg");
        using var directory = new TestDirectory();
        string source = MakeTone(ffmpeg, directory, "tone.wav");
        string ogg = directory.Path_("out.opus");
        string caf = directory.Path_("out.caf");

        MusicCommand.Run(Options("--codec", "libopus", "--sample-rate", "48000", "--quality", "5", "--input", source, "--output", ogg));
        MusicCommand.Run(Options("--codec", "libopus", "--sample-rate", "48000", "--quality", "5", "--input", source, "--output", caf));

        (string duration, string sampleRate, string channels, _) = AudioFiles.Read(caf);
        Assert.Equal("48000,2", $"{sampleRate},{channels}");
        Assert.Equal(AudioFiles.Read(ogg).Duration, duration);

        var packets = new DotGame.Framework.ContentPipeline.BuildTool.Audio.OggOpusPackets(File.ReadAllBytes(ogg));
        byte[] file = File.ReadAllBytes(caf);
        Assert.Equal("caff", System.Text.Encoding.ASCII.GetString(file, 0, 4));
        int end = file.Length;
        for (int i = packets.Count - 1; i >= 0; i--)
        {
            var packet = packets.Packet(i);
            end -= packet.Length;
            Assert.True(packet.SequenceEqual(file.AsSpan(end, packet.Length)), $"packet {i}");
        }
    }

    [Theory]
    [InlineData("pcm_s16le", "wav", 44100, 2)]
    [InlineData("flac", "flac", 44100, 1)]
    [InlineData("libopus", "opus", 48000, 2)]
    [InlineData("libopus", "opus", 48000, 1)]
    [InlineData("aac_at", "m4a", 44100, 2)]
    [InlineData("aac_at", "m4a", 48000, 1)]
    public void EncodedAudio_IsReadAsFfprobeReadsIt(string codec, string extension, int sampleRate, int channels)
    {
        // The pipeline reads its outputs' durations and formats itself; ffprobe, where it's installed,
        // is the check. A second and a bit, so no length lines up with a codec's frames
        string ffmpeg = RealTools.Require("ffmpeg");
        string ffprobe = RealTools.Require("ffprobe");
        if (codec == "aac_at")
        {
            Assert.SkipUnless(OperatingSystem.IsMacOS(), "AAC is encoded with AudioToolbox, on macOS only.");
        }
        using var directory = new TestDirectory();
        string source = directory.Path_("tone.wav");
        (int exitCode, string made) = ProcessRunner.Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=1.2345", "-ac", channels.ToString(CultureInfo.InvariantCulture), "-bitexact", source]);
        Assert.True(exitCode == 0, made);
        string output = directory.Path_("out." + extension);

        PcmAudio audio = AudioEncoder.Prepare(AudioReader.Read(source), sampleRate, 0);
        AudioEncoder.Encode(codec, 5, audio, source, output, "afconvert");

        (string duration, string rate, string channelCount, List<string> warnings) = AudioFiles.Read(output);
        Assert.Empty(warnings);
        string[] probed = Probe(ffprobe, output, "stream=sample_rate,channels:format=duration").Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(probed[0].Trim(), rate);
        Assert.Equal(probed[1].Trim(), channelCount);
        Assert.Equal(sampleRate.ToString(CultureInfo.InvariantCulture), rate);
        double read = double.Parse(duration, CultureInfo.InvariantCulture);
        double probedDuration = double.Parse(probed[2], CultureInfo.InvariantCulture);
        if (codec == "libopus")
        {
            // ffprobe counts the pre-skip (312 samples from libopus), which every decoder drops
            Assert.Equal(probedDuration - (312.0 / 48000), read, 2e-6);
            Assert.Equal(1.2345, read, 1.0 / sampleRate);
        }
        else if (codec == "aac_at")
        {
            // Apple's writer gives the length in iTunes' gapless tag (iTunSMPB), which iOS reads and
            // ffprobe doesn't: ffprobe counts the priming and the padding to a whole frame
            Assert.InRange(probedDuration - read, 2112.0 / sampleRate, (2112.0 + 1024) / sampleRate);
            Assert.Equal(1.2345, read, 1.0 / sampleRate);
        }
        else
        {
            Assert.Equal(probedDuration, read, 2e-6);
            Assert.Equal(1.2345, read, 1.0 / sampleRate);
        }
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 5)]
    public void Aac_KeepsTheExactLength_AndEncodesTheSameEachTime(int channels, int quality)
    {
        // ffmpeg's aac_at left up to a frame of padding in the edit list, which iOS would loop through
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "AAC is encoded with AudioToolbox, on macOS only.");
        string ffmpeg = RealTools.Require("ffmpeg");
        using var directory = new TestDirectory();
        string source = directory.Path_("tone.wav");
        (int exitCode, string made) = ProcessRunner.Run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=0.85", "-ac", channels.ToString(CultureInfo.InvariantCulture), source]);
        Assert.True(exitCode == 0, made);

        PcmAudio audio = AudioReader.Read(source);
        AppleAacEncoder.Encode("afconvert", audio, source, directory.Path_("first.m4a"), quality);
        AppleAacEncoder.Encode("afconvert", audio, source, directory.Path_("second.m4a"), quality);

        // The same bytes, and no time in the headers to make the next encode differ
        byte[] first = File.ReadAllBytes(directory.Path_("first.m4a"));
        Assert.Equal(first, File.ReadAllBytes(directory.Path_("second.m4a")));
        foreach (string box in new[] { "mvhd", "tkhd", "mdhd" })
        {
            int at = first.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(box));
            Assert.True(at > 0, box);
            Assert.All(first[(at + 8)..(at + 16)], value => Assert.Equal(0, value));
        }
        (string duration, _, string read, _) = AudioFiles.Read(directory.Path_("first.m4a"));
        Assert.Equal("0.85", duration);
        Assert.Equal(channels.ToString(CultureInfo.InvariantCulture), read);
    }

    [Fact]
    public void Sound_ToOpus_MatchesTheGolden()
    {
        string ffmpeg = RealTools.Require("ffmpeg");
        using var directory = new TestDirectory();
        string source = MakeTone(ffmpeg, directory, "tone.wav");
        string output = directory.Path_("out.opus");

        SoundCommand.Run(Options("--codec", "libopus", "--compression", "Lossy", "--sample-rate", "44100", "--input", source, "--output", output));

        Assert.Equal("48000", AudioFiles.Read(output).SampleRate);
        Goldens.Check(Project, "sound.opus", ContentVersions.Of("ContentPipelineSoundVersion"), File.ReadAllBytes(output));
    }

    [Fact]
    public void Music_EncodedTwice_IsByteIdentical()
    {
        string ffmpeg = RealTools.Require("ffmpeg");
        using var directory = new TestDirectory();
        string source = MakeTone(ffmpeg, directory, "tone.wav");
        string first = directory.Path_("first.opus");
        string second = directory.Path_("second.opus");

        MusicCommand.Run(Options("--codec", "libopus", "--input", source, "--output", first));
        MusicCommand.Run(Options("--codec", "libopus", "--input", source, "--output", second));

        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
    }

    [Theory]
    [InlineData("Lossless", "8")]
    [InlineData("Lossy", "8")]
    public void TextureWebP_MatchesTheGolden(string compression, string quality)
    {
        // No tool now: the encoder is DotGame.Imaging's, whose files are cwebp 1.6.0's (the goldens'
        // hashes are the ones cwebp's files had)
        using var directory = new TestDirectory();
        string source = directory.Path_("hero.png");
        Images.Sample(64, 48).SavePng(source);
        string output = directory.Path_("hero.webp");

        TextureCommand.Run(Options("--compression", compression, "--quality", quality, "--input", source, "--output", output));

        byte[] file = File.ReadAllBytes(output);
        Assert.Equal("RIFF"u8.ToArray(), file[..4]);
        Assert.Equal("WEBP"u8.ToArray(), file[8..12]);
        Goldens.Check(Project, $"texture.webp.{compression}", ContentVersions.Of("ContentPipelineTextureVersion"), file);
    }

    [Theory]
    [InlineData(10, 4)]
    [InlineData(6, 6)]
    [InlineData(2, 8)]
    public void TextureAstc_MatchesTheGolden(int quality, int block)
    {
        string astcenc = RealTools.Require("astcenc", "astcenc-avx2", "astcenc-neon", "astcenc-sse4.1", "astcenc-sse2");
        using var directory = new TestDirectory();
        string source = directory.Path_("hero.png");
        Images.Sample(64, 48).SavePng(source);
        string output = directory.Path_("hero.ktx2");

        TextureCommand.Run(Options("--format", "ktx2", "--gpu-compression", "astc", "--compression", "Lossy", "--quality", quality.ToString(CultureInfo.InvariantCulture), "--astcenc", astcenc, "--intermediate-dir", directory.Path_("obj"), "--input", source, "--output", output));

        var texture = DotGame.Framework.Platform.Common.Content.Ktx2Texture.Read(File.ReadAllBytes(output), "hero");
        Assert.Equal(block, texture.BlockSize);
        Goldens.Check(Project, $"texture.astc.{block}", $"{ContentVersions.Of("ContentPipelineTextureVersion")}+{RealTools.Identity(astcenc)}", texture.Data.ToArray());
    }

    [Fact]
    public void DefaultSpriteShader_CompilesToEveryType_AndMatchesTheGolden()
    {
        string slangc = RealTools.Require("slangc");
        string spirvCross = RealTools.Require("spirv-cross");
        string spirv2dxil = RealTools.Spirv2Dxil();
        using var directory = new TestDirectory();
        string source = Path.Combine(Paths.FrameworkRoot, "platform", "shared", "Shaders", "sprite.slang");
        var compiler = new ShaderCompiler { SlangC = slangc, SpirvCross = spirvCross, Spirv2Dxil = spirv2dxil };

        compiler.Compile(source, directory.Path_("out"), "sprite", directory.Path_("obj"), ShaderTypes.SpirV | ShaderTypes.Msl | ShaderTypes.Dxil | ShaderTypes.Glsl);

        string identity = $"{ContentVersions.Of("ContentPipelineShaderVersion")}+{RealTools.Identity(slangc)}+{RealTools.Identity(spirvCross)}+{RealTools.Identity(spirv2dxil)}";
        foreach (string suffix in new[] { ".frag.spv", ".vert.spv", ".frag.msl", ".vert.msl", ".glsl", ".frag.dxil", ".vert.dxil" })
        {
            Goldens.Check(Project, $"shader.sprite{suffix}", identity, File.ReadAllBytes(directory.Path_("out", "sprite" + suffix)));
        }

        // SPIR-V 1.3, the newest a Vulkan 1.1 device (SDL's) accepts: the header's second word is the version
        foreach (string stage in new[] { "vert", "frag" })
        {
            byte[] spirv = File.ReadAllBytes(directory.Path_("out", $"sprite.{stage}.spv"));
            Assert.Equal(0x00010300u, BitConverter.ToUInt32(spirv, 4));
        }

        // DXIL that D3D12 accepts: signed with the container hash
        foreach (string stage in new[] { "vert", "frag" })
        {
            byte[] dxil = File.ReadAllBytes(directory.Path_("out", $"sprite.{stage}.dxil"));
            byte[] resigned = (byte[])dxil.Clone();
            resigned.AsSpan(4, 16).Clear();
            DxilContainerHash.Sign(resigned, stage);
            Assert.Equal(resigned, dxil);
            Assert.NotEqual(new byte[16], dxil.AsSpan(4, 16).ToArray());
        }
    }

    [Theory]
    [InlineData("", "Content/tone.flac", "fLaC")]
    [InlineData("FLAC", "Content/tone.flac", "fLaC")]
    [InlineData("WAV", "Content/tone.wav", "RIFF")]
    [InlineData("wav", "Content/tone.wav", "RIFF")]
    public void LosslessSounds_AreFlac_OrWavWhenAsked(string format, string output, string marker)
    {
        string ffmpeg = RealTools.Require("ffmpeg");
        using var project = new ContentProject();
        project.Properties.Remove("SoundContentFlacProcessorCommand");
        project.Properties.Remove("SoundContentWavProcessorCommand");
        project.Properties["ContentPipelineBuildTool"] = Path.Combine(AppContext.BaseDirectory, "DotGame.Framework.ContentPipeline.BuildTool.dll");
        project.Properties["FFmpegExe"] = ffmpeg;
        Directory.CreateDirectory(Path.Combine(project.ProjectDir, "src"));
        MakeTone(ffmpeg, Path.Combine(project.ProjectDir, "src", "tone.wav"));
        ContentItem tone = project.AddItem("SoundContent", "src/tone.wav", "Content\\tone.wav");
        if (format.Length > 0)
        {
            tone.With("LosslessFormat", format);
        }

        project.Build().ShouldSucceed();

        Assert.Equal(marker, System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(project.OutputPath(output)), 0, 4));
    }

    [Fact]
    public void Build_WithTheRealCommands_OnDesktop_EndToEnd()
    {
        string ffmpeg = RealTools.Require("ffmpeg");
        string slangc = RealTools.Require("slangc");
        string spirvCross = RealTools.Require("spirv-cross");
        string spirv2dxil = RealTools.Spirv2Dxil();
        string slang = File.ReadAllText(Path.Combine(Paths.FrameworkRoot, "platform", "shared", "Shaders", "sprite.slang"));

        using var project = new ContentProject();
        foreach (string property in new[] { "TextureContentLosslessProcessorCommand", "TextureContentLossyProcessorCommand", "ShaderContentProcessorCommand", "SoundContentFlacProcessorCommand", "SoundContentWavProcessorCommand", "SoundContentLossyProcessorCommand", "MusicContentProcessorCommand" })
        {
            project.Properties.Remove(property);
        }

        project.Properties["ContentPipelineBuildTool"] = Path.Combine(AppContext.BaseDirectory, "DotGame.Framework.ContentPipeline.BuildTool.dll");
        project.Properties["FFmpegExe"] = ffmpeg;
        project.Properties["SlangCExe"] = slangc;
        project.Properties["SpirvCrossExe"] = spirvCross;
        project.Properties["Spirv2DxilExe"] = spirv2dxil;
        string png = Path.Combine(project.ProjectDir, "src", "hero.png");
        project.WriteSource("src/hero.png", "");
        Images.Sample().SavePng(png);
        project.AddItem("TextureContent", "src/hero.png", "Content\\hero.png");
        project.AddContent("ShaderContent", "sprite", ".slang", slang);
        MakeTone(ffmpeg, Path.Combine(project.ProjectDir, "src", "tone.wav"));
        project.AddItem("SoundContent", "src/tone.wav", "Content\\tone.wav");
        MakeTone(ffmpeg, Path.Combine(project.ProjectDir, "src", "theme.flac"));
        project.AddItem("MusicContent", "src/theme.flac", "Content\\theme.flac");

        project.Build().ShouldSucceed();

        Assert.Contains("Content/hero.ktx2", project.OutputFiles());
        Assert.Contains("Content/sprite.frag.dxil", project.OutputFiles());
        Assert.Contains("Content/tone.flac", project.OutputFiles());
        Assert.Contains("Content/theme.opus", project.OutputFiles());
        ManifestEntry theme = Assert.Single(project.Manifest(), entry => entry.Key == "theme");
        Assert.Contains("\"SampleRate\", \"48000\"", theme.Properties, StringComparison.Ordinal);
        Assert.Contains("\"Channels\", \"2\"", theme.Properties, StringComparison.Ordinal);

        // A real no-op and a real restore
        var outputs = project.SnapshotOutputs();
        project.Build().ShouldSucceed();
        Assert.Equal(outputs, project.SnapshotOutputs());

        Dictionary<string, string> hashes = outputs.ToDictionary(file => file.Key, file => file.Value.Hash);
        project.DeleteBuildOutputs();
        BuildResult restored = project.Build().ShouldSucceed();
        Assert.DoesNotContain("cache write", restored.Output, StringComparison.Ordinal);
        Assert.Equal(hashes, project.SnapshotOutputs().ToDictionary(file => file.Key, file => file.Value.Hash));
    }
}
