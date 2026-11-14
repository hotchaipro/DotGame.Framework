// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Runtime.InteropServices;
using DotGame.AudioCodecs.Opus;
using DotGame.Framework.Platform.Common.Audio;
using Xunit;

namespace DotGame.Framework.Tests.Audio;

/// <summary>
/// The music reader desktop and Android stream tracks through, and the sound decoder they load sounds
/// with, against DotGame.AudioCodecs's decode of the whole file, on Ogg Opus files libopus encoded
/// (FixtureSources).
/// </summary>
public sealed class OpusMusicReaderTests
{
    public static TheoryData<string> Fixtures => new()
    {
        "celt-stereo-112k", "celt-mono-64k", "celt-2_5ms", "celt-60ms", "silk-nb-mono",
        "silk-wb-mono", "silk-stereo", "silk-60ms", "hybrid-mono", "hybrid-stereo",
    };

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", "Opus", name + ".opus");

    private static OpusMusicReader Open(string name, int channels = 1, long totalFrames = 0) =>
        new(() => File.OpenRead(Fixture(name)), channels, totalFrames);

    private static short[] Whole(string name)
    {
        using var decoder = new OpusDecoder(File.ReadAllBytes(Fixture(name)));
        var all = new short[decoder.Frames!.Value * decoder.Channels];
        Assert.Equal(decoder.Frames.Value, decoder.Read(all));
        return all;
    }

    private static short[] ReadAll(OpusMusicReader reader, int chunkFrames)
    {
        var all = new short[checked((int)reader.TotalFrames * reader.Channels)];
        var chunk = new short[chunkFrames * reader.Channels];
        int frames = 0;
        int read;
        while ((read = reader.Read(chunk)) > 0)
        {
            chunk.AsSpan(0, read * reader.Channels).CopyTo(all.AsSpan(frames * reader.Channels));
            frames += read;
        }
        Assert.Equal(reader.TotalFrames, frames);
        return all;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Reads_TheWholeFilesSamples(string name)
    {
        var reader = Open(name);
        reader.SeekTo(0);

        // An odd chunk, so reads end mid-packet
        Assert.Equal(Whole(name), ReadAll(reader, 1013));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void SoundDecoder_GivesTheWholeFilesSamples(string name)
    {
        var (pcm, sampleRate, channels) = SoundDecoder.Decode(File.ReadAllBytes(Fixture(name)));

        Assert.Equal(48000, sampleRate);
        Assert.Equal(Whole(name), MemoryMarshal.Cast<byte, short>(pcm.AsSpan()).ToArray());
        Assert.Equal(Whole(name).Length / channels * channels, pcm.Length / 2);
    }

    [Fact]
    public void Probe_ReadsTheChannelsAndLengthFromTheFile()
    {
        var (channels, frames) = OpusMusicReader.Probe(File.ReadAllBytes(Fixture("hybrid-stereo")));

        Assert.Equal(2, channels);
        Assert.Equal(144000, frames);
    }

    [Fact]
    public void TheFilesChannelsAndLength_ReplaceTheManifests_OnceItsRead()
    {
        var reader = Open("celt-stereo-112k", channels: 1, totalFrames: 5);
        Assert.Equal(5, reader.TotalFrames);

        reader.SeekTo(0);

        Assert.Equal(2, reader.Channels);
        Assert.Equal(192000, reader.TotalFrames);
    }

    [Theory]
    [InlineData("celt-stereo-112k", 48123)]
    [InlineData("celt-60ms", 70001)]
    [InlineData("silk-wb-mono", 20000)]
    [InlineData("hybrid-stereo", 100000)]
    public void SeekTo_LandsOnTheFrame(string name, int frame)
    {
        short[] whole = Whole(name);
        var reader = Open(name);
        reader.SeekTo(frame);
        Assert.Equal(frame, reader.FramePosition);

        int channels = reader.Channels;
        var chunk = new short[4800 * channels];
        Assert.Equal(4800, reader.Read(chunk));

        // After the pre-roll the decoder has nearly converged on the samples of a decode from the
        // start: what's left is far below the signal, and a sample off would be heard as a seam
        double signal = 0;
        double error = 0;
        for (int i = 0; i < chunk.Length; i++)
        {
            double expected = whole[(frame * channels) + i];
            signal += expected * expected;
            error += (chunk[i] - expected) * (chunk[i] - expected);
        }
        Assert.True(signal > 0);
        double snr = 10 * Math.Log10(signal / Math.Max(error, 1));
        Assert.True(snr > 80, $"{snr:F1} dB");
    }

    [Fact]
    public void SeekTo_TheStart_IsExact_AfterReading()
    {
        var reader = Open("hybrid-mono");
        reader.SeekTo(0);
        var chunk = new short[30000];
        reader.Read(chunk);

        reader.SeekTo(0);

        Assert.Equal(Whole("hybrid-mono"), ReadAll(reader, 4800));
    }

    [Fact]
    public void Read_StopsAtTheEnd()
    {
        var reader = Open("celt-mono-64k");
        reader.SeekTo(143990);
        var chunk = new short[4800];

        Assert.Equal(10, reader.Read(chunk));
        Assert.Equal(0, reader.Read(chunk));

        reader.SeekTo(long.MaxValue);
        Assert.Equal(0, reader.Read(chunk));
    }

    [Fact]
    public void Release_KeepsThePosition_AndTheNextReadLoadsTheFileAgain()
    {
        short[] whole = Whole("celt-stereo-112k");
        var reader = Open("celt-stereo-112k");
        reader.SeekTo(0);
        var chunk = new short[4800 * 2];
        reader.Read(chunk);

        // Reloading resumes where it left off (by a seek, so within the pre-roll's precision)
        reader.Release();
        Assert.Equal(4800, reader.FramePosition);
        Assert.Equal(4800, reader.Read(chunk));
        Assert.Equal(9600, reader.FramePosition);

        reader.Release();
        reader.SeekTo(0);
        Assert.Equal(4800, reader.Read(chunk));
        Assert.Equal(whole.AsSpan(0, chunk.Length).ToArray(), chunk);
    }
}
