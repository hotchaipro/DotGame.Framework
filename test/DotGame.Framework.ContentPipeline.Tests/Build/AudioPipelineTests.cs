// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Buffers.Binary;
using DotGame.ContentPipeline.Testing;
using DotGame.Framework.ContentPipeline.BuildTool;
using DotGame.Framework.ContentPipeline.BuildTool.Audio;
using Xunit;

namespace DotGame.Framework.ContentPipeline.Tests.Build;

/// <summary>
/// The build tool's own audio code: reading sources, resampling and mixing, and writing WAV and FLAC,
/// with no external tool.
/// </summary>
public sealed class AudioPipelineTests
{
    private static string TestData(params string[] parts) => Path.Combine([Paths.FrameworkRoot, "test", "DotGame.Framework.Tests", "TestData", .. parts]);

    // A WAV of any sample layout, the samples given as integers at full scale or as floats
    private static byte[] Wav(int format, int bits, int channels, int sampleRate, double[] samples, bool extensible = false)
    {
        int bytes = bits / 8;
        int formatSize = extensible ? 40 : 16;
        var file = new byte[12 + 8 + formatSize + 8 + (samples.Length * bytes)];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), file.Length - 8);
        "WAVEfmt "u8.CopyTo(file.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(16), formatSize);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(20), (ushort)(extensible ? 0xFFFE : format));
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(22), (ushort)channels);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(28), sampleRate * channels * bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(32), (ushort)(channels * bytes));
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(34), (ushort)bits);
        if (extensible)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(36), 22);
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(44), (ushort)format);
        }
        int data = 20 + formatSize;
        "data"u8.CopyTo(file.AsSpan(data));
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(data + 4), samples.Length * bytes);
        for (int i = 0; i < samples.Length; i++)
        {
            Span<byte> s = file.AsSpan(data + 8 + (i * bytes), bytes);
            double v = samples[i];
            switch (format, bits)
            {
                case (1, 8): s[0] = (byte)(v + 128); break;
                case (1, 16): BinaryPrimitives.WriteInt16LittleEndian(s, (short)v); break;
                case (1, 24): s[0] = (byte)(int)v; s[1] = (byte)((int)v >> 8); s[2] = (byte)((int)v >> 16); break;
                case (1, _): BinaryPrimitives.WriteInt32LittleEndian(s, (int)v); break;
                case (_, 32): BinaryPrimitives.WriteSingleLittleEndian(s, (float)v); break;
                default: BinaryPrimitives.WriteDoubleLittleEndian(s, v); break;
            }
        }

        return file;
    }

    [Theory]
    [InlineData(1, 8, false, 64, 0.5f)]
    [InlineData(1, 16, false, -16384, -0.5f)]
    [InlineData(1, 24, false, 4194304, 0.5f)]
    [InlineData(1, 32, false, -1073741824, -0.5f)]
    [InlineData(3, 32, false, 0.25, 0.25f)]
    [InlineData(3, 64, false, -0.25, -0.25f)]
    [InlineData(1, 16, true, 8192, 0.25f)]
    [InlineData(3, 32, true, 0.75, 0.75f)]
    public void Wav_EveryLayout_ReadsAsTheSameSamples(int format, int bits, bool extensible, double stored, float expected)
    {
        byte[] file = Wav(format, bits, 2, 22050, [stored, 0, stored, 0], extensible);

        PcmAudio audio = AudioReader.Read(file, "a.wav");

        Assert.Equal(22050, audio.SampleRate);
        Assert.Equal(2, audio.Channels);
        Assert.Equal([expected, 0, expected, 0], audio.Samples);
    }

    [Fact]
    public void Wav_WithADataSizeAStreamingWriterCouldntFill_RunsToTheEnd()
    {
        byte[] file = Wav(1, 16, 1, 8000, [100, 200, 300]);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(40), uint.MaxValue);

        Assert.Equal(3, AudioReader.Read(file, "a.wav").Frames);
    }

    [Fact]
    public void Wav_OfAnotherCodec_FailsNamingIt()
    {
        byte[] file = Wav(1, 16, 1, 8000, [0, 0]);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(20), 2);

        var error = Assert.Throws<ContentBuildException>(() => AudioReader.Read(file, "adpcm.wav"));

        Assert.Contains("format 0x0002", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Wav_WithMoreThanTwoChannels_IsRefused()
    {
        var error = Assert.Throws<ContentBuildException>(() => AudioReader.Read(Wav(1, 16, 6, 48000, new double[12]), "surround.wav"));

        Assert.Contains("6 channels", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OggOpus_IsReadByThePlatformsDecoder()
    {
        byte[] file = File.ReadAllBytes(TestData("Opus", "celt-stereo-112k.opus"));

        PcmAudio audio = AudioReader.Read(file, "a.opus");

        using var decoder = new DotGame.AudioCodecs.Opus.OpusDecoder(file);
        var expected = new float[decoder.Frames!.Value * decoder.Channels];
        decoder.Read(expected);
        Assert.Equal(48000, audio.SampleRate);
        Assert.Equal(decoder.Channels, audio.Channels);
        Assert.Equal(expected, audio.Samples);
    }

    [Theory]
    [InlineData(1, 44100, 44100)]
    [InlineData(2, 44100, 1000)]
    [InlineData(2, 48000, 4096)]
    [InlineData(2, 48000, 4097)]
    [InlineData(1, 22050, 10)]
    [InlineData(2, 11025, 1)]
    [InlineData(1, 44056, 9000)]
    [InlineData(2, 96000, 20000)]
    public void Flac_RoundTripsExactly_ThroughThePlatformsDecoder(int channels, int sampleRate, int frames)
    {
        foreach (PcmAudio audio in new[] { Tones.Sine(sampleRate, channels, frames), Tones.Noise(sampleRate, channels, frames), new PcmAudio(sampleRate, channels, new float[frames * channels]) })
        {
            byte[] file = FlacEncoder.Encode(audio.ToInt16(), channels, sampleRate);

            using var decoder = new DotGame.AudioCodecs.Flac.FlacDecoder(file);
            Assert.Equal(sampleRate, decoder.SampleRate);
            Assert.Equal(channels, decoder.Channels);
            short[] expected = audio.ToInt16();
            var decoded = new short[expected.Length];
            Assert.Equal(frames, decoder.Read(decoded));
            Assert.Equal(expected, decoded);
        }
    }

    [Fact]
    public void Flac_Compresses_AndSilenceToAlmostNothing()
    {
        PcmAudio sine = Tones.Sine(44100, 2, 44100);
        PcmAudio silence = new(44100, 2, new float[88200]);

        Assert.True(FlacEncoder.Encode(sine.ToInt16(), 2, 44100).Length < sine.Samples.Length * 2 / 4);
        Assert.True(FlacEncoder.Encode(silence.ToInt16(), 2, 44100).Length < 400);
    }

    [Fact]
    public void Flac_EncodesTheSameBytes_EveryTime()
    {
        short[] samples = Tones.Noise(44100, 2, 10000).ToInt16();

        Assert.Equal(FlacEncoder.Encode(samples, 2, 44100), FlacEncoder.Encode(samples, 2, 44100));
    }

    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(48000, 44100)]
    [InlineData(22050, 48000)]
    [InlineData(96000, 44100)]
    [InlineData(32000, 48000)]
    public void Resample_KeepsASineInBand_AndTheLength(int from, int to)
    {
        PcmAudio source = Tones.Sine(from, 1, from, frequency: 1000);

        PcmAudio result = source.WithSampleRate(to);
        PcmAudio ideal = Tones.Sine(to, 1, to, frequency: 1000);

        Assert.Equal(to, result.Frames);

        // Away from the ends, where the filter runs past the source, the result is the sine at the new rate
        double signal = 0;
        double error = 0;
        for (int i = to / 10; i < to - (to / 10); i++)
        {
            signal += ideal.Samples[i] * ideal.Samples[i];
            error += (ideal.Samples[i] - result.Samples[i]) * (ideal.Samples[i] - result.Samples[i]);
        }
        Assert.True(10 * Math.Log10(signal / error) > 70, $"SNR {10 * Math.Log10(signal / error):F1} dB");
    }

    [Fact]
    public void Resample_Down_StopsWhatTheLowerRateCantHold()
    {
        // 30 kHz at 96 kHz has no place at 44.1 kHz, whose top is 22.05 kHz
        PcmAudio result = Tones.Sine(96000, 1, 96000, frequency: 30000).WithSampleRate(44100);

        double energy = 0;
        for (int i = 4410; i < 44100 - 4410; i++)
        {
            energy += result.Samples[i] * result.Samples[i];
        }
        Assert.True(10 * Math.Log10((energy / (44100 - 8820)) / 0.125) < -60);
    }

    [Fact]
    public void Resample_KeepsAConstantConstant_AndIsTheSameEachTime()
    {
        var constant = new PcmAudio(44100, 2, Enumerable.Repeat(0.25f, 8820).ToArray());

        float[] first = constant.WithSampleRate(48000).Samples;
        float[] second = constant.WithSampleRate(48000).Samples;

        Assert.Equal(first, second);
        for (int i = 200; i < first.Length - 200; i++)
        {
            Assert.Equal(0.25f, first[i], 1e-6f);
        }
    }

    [Fact]
    public void Resample_OfAnUnusualRatio_WorksTheFilterOutPerSample()
    {
        // 44,099 to 48,000 Hz: 48,000 positions between input samples, past the table's limit
        PcmAudio result = Tones.Sine(44099, 1, 4410, frequency: 500).WithSampleRate(48000);

        Assert.Equal(AudioResampler.OutputFrames(4410, 44099, 48000), result.Frames);
        Assert.InRange(result.Samples.Max(), 0.49f, 0.51f);
    }

    [Fact]
    public void Channels_MixToMonoAsTheAverage_AndCopyToStereo()
    {
        var stereo = new PcmAudio(8000, 2, [0.5f, 0.25f, -1f, 1f]);

        Assert.Equal([0.375f, 0f], stereo.WithChannels(1).Samples);
        Assert.Equal([0.375f, 0.375f, 0f, 0f], stereo.WithChannels(1).WithChannels(2).Samples);
    }

    [Theory]
    [InlineData(0.5f, 16384)]
    [InlineData(-1f, -32768)]
    [InlineData(1.5f, 32767)]
    [InlineData(-1.5f, -32768)]
    [InlineData(1.5f / 32768f, 2)]
    [InlineData(2.5f / 32768f, 2)]
    public void Int16_RoundsTiesToEven_AndClips(float sample, short expected)
    {
        Assert.Equal(expected, new PcmAudio(8000, 1, [sample]).ToInt16()[0]);
    }

    [Fact]
    public void DeterministicMath_AgreesWithTheLibrary()
    {
        for (double x = -60; x <= 60; x += 0.0137)
        {
            Assert.Equal(Math.Sin(x), DeterministicMath.Sin(x), 1e-14);
            Assert.Equal(Math.Cos(x), DeterministicMath.Cos(x), 1e-14);
        }

        // I0(0) = 1, and I0(9) is about 1,093.588
        Assert.Equal(1.0, DeterministicMath.BesselI0(0));
        Assert.Equal(1093.588, DeterministicMath.BesselI0(9), 1e-3);
    }

    [Fact]
    public void ResampledFlac_MatchesTheGolden_OnEveryMachine()
    {
        // No external tool: every build machine must give these bytes, or the same source would get
        // different cache entries and web fingerprints on two machines
        using var directory = new TestDirectory();
        string source = Tones.WriteWav(directory.Path_("tone.wav"), Tones.Sine(44100, 2, 22050));
        string flac = directory.Path_("out.flac");
        string wav = directory.Path_("out.wav");

        SoundCommand.Run(CommandLineOptions.Parse(["--codec", "flac", "--compression", "Lossless", "--sample-rate", "48000", "--input", source, "--output", flac]));
        SoundCommand.Run(CommandLineOptions.Parse(["--codec", "pcm_s16le", "--compression", "Lossless", "--sample-rate", "32000", "--channels", "1", "--input", source, "--output", wav]));

        string version = ContentVersions.Of("ContentPipelineSoundVersion");
        Goldens.Check(GoldenFiles.Framework, "sound.flac.48k", version, File.ReadAllBytes(flac));
        Goldens.Check(GoldenFiles.Framework, "sound.wav.32k.mono", version, File.ReadAllBytes(wav));
    }

    [Fact]
    public void FloatWav_IsWhatTheReaderReadsBack()
    {
        using var directory = new TestDirectory();
        PcmAudio audio = Tones.Noise(48000, 2, 1000);

        WavWriter.WriteFloat32(directory.Path_("a.wav"), audio);

        PcmAudio back = AudioReader.Read(directory.Path_("a.wav"));
        Assert.Equal(48000, back.SampleRate);
        Assert.Equal(audio.Samples, back.Samples);
    }
}
