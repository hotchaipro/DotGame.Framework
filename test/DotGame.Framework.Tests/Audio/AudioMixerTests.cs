// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Android.Audio;
using DotGame.Framework.Platform;
using DotGame.Framework.Platform.Audio;
using DotGame.Framework.Platform.Common.Audio;
using Xunit;

namespace DotGame.Framework.Tests.Audio;

/// <summary>
/// Android's sound-effect mixer, driven the way its audio thread drives
/// it: voices play, stop and pause from the "game thread" (the test),
/// and each <see cref="AudioMixer.Mix"/> call is one burst.
/// </summary>
[Collection("Master volume")]
public sealed class AudioMixerTests
{
    private const int Rate = 1000;
    private const int Burst = 4;

    [Fact]
    public void A_voice_plays_its_samples_once_then_stops()
    {
        var mixer = new AudioMixer();
        var voice = CreateVoice(mixer, Stereo(0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f));
        var output = new float[Burst * 2];

        Assert.False(voice.IsPlaying);
        voice.Play();
        Assert.True(voice.IsPlaying);

        Assert.True(mixer.Mix(output, 2));
        Assert.Equal([0.1f, 0.2f, 0.3f, 0.4f], output[..4]);

        Assert.False(mixer.Mix(output, Burst));
        Assert.Equal([0.5f, 0.6f, 0f, 0f], output[..4]);
        Assert.False(voice.IsPlaying);

        Assert.False(mixer.Mix(output, Burst));
        Assert.All(output, sample => Assert.Equal(0f, sample));
    }

    private static readonly float[] TwiceAndABit = [0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.1f, 0.2f];
    private static readonly float[] TheRestThenSilence = [0.3f, 0.4f, 0.5f, 0.6f, 0f, 0f];

    [Fact]
    public void A_looping_voice_starts_over_within_a_block_and_keeps_playing()
    {
        var mixer = new AudioMixer();
        var voice = CreateVoice(mixer, Stereo(0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f));
        var output = new float[7 * 2];

        voice.SetLooping(true);
        voice.Play();

        Assert.True(mixer.Mix(output, 7));
        Assert.True(voice.IsPlaying);
        Assert.Equal(TwiceAndABit, output);
    }

    [Fact]
    public void A_voice_no_longer_looping_finishes_the_cue_and_stops()
    {
        var mixer = new AudioMixer();
        var voice = CreateVoice(mixer, Stereo(0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f));
        var output = new float[7 * 2];

        voice.SetLooping(true);
        voice.Play();
        Assert.True(mixer.Mix(output, 1));

        voice.SetLooping(false);
        mixer.Mix(output, 7);

        Assert.Equal(TheRestThenSilence, output[..6]);
        Assert.False(voice.IsPlaying);
    }

    [Fact]
    public void Mono_plays_on_both_channels()
    {
        var mixer = new AudioMixer();
        var voice = CreateVoice(mixer, new SoundStrategy([0.25f, -0.5f], 1, TimeSpan.FromMilliseconds(2)));
        var output = new float[Burst * 2];

        voice.Play();
        mixer.Mix(output, Burst);

        Assert.Equal([0.25f, 0.25f, -0.5f, -0.5f, 0f, 0f, 0f, 0f], output);
    }

    [Fact]
    public void Voices_add_up()
    {
        var mixer = new AudioMixer();
        var output = new float[Burst * 2];

        // Far more voices than Android allows AudioTracks
        var voices = new VoiceStrategy[300];
        for (int i = 0; i < voices.Length; i++)
        {
            voices[i] = CreateVoice(mixer, Stereo(0.001f, 0.002f));
            voices[i].Play();
        }

        mixer.Mix(output, 1);

        Assert.Equal(0.3f, output[0], 4);
        Assert.Equal(0.6f, output[1], 4);
    }

    [Fact]
    public void Stop_silences_and_play_restarts_from_the_top()
    {
        var mixer = new AudioMixer();
        var voice = CreateVoice(mixer, Stereo(1f, 1f, 2f, 2f, 3f, 3f));
        var output = new float[Burst * 2];

        voice.Play();
        mixer.Mix(output, 1);
        voice.Stop();
        Assert.False(voice.IsPlaying);
        Assert.False(mixer.Mix(output, 1));
        Assert.Equal(0f, output[0]);

        voice.Play();
        mixer.Mix(output, 1);
        Assert.Equal(1f, output[0]);
    }

    [Fact]
    public void Play_while_playing_restarts()
    {
        var mixer = new AudioMixer();
        var voice = CreateVoice(mixer, Stereo(1f, 1f, 2f, 2f, 3f, 3f));
        var output = new float[Burst * 2];

        voice.Play();
        mixer.Mix(output, 2);
        voice.Play();
        mixer.Mix(output, 1);

        Assert.Equal(1f, output[0]);
        Assert.True(voice.IsPlaying);
    }

    [Fact]
    public void Pause_holds_the_position_and_play_resumes()
    {
        var mixer = new AudioMixer();
        var voice = CreateVoice(mixer, Stereo(1f, 1f, 2f, 2f, 3f, 3f));
        var output = new float[Burst * 2];

        voice.Play();
        mixer.Mix(output, 1);
        voice.Pause();
        Assert.False(voice.IsPlaying);
        Assert.False(mixer.Mix(output, 1));

        voice.Play();
        Assert.True(voice.IsPlaying);
        mixer.Mix(output, 1);
        Assert.Equal(2f, output[0]);
    }

    [Fact]
    public void Pause_after_stop_does_nothing()
    {
        var mixer = new AudioMixer();
        var voice = CreateVoice(mixer, Stereo(1f, 1f, 2f, 2f));
        var output = new float[Burst * 2];

        voice.Play();
        mixer.Mix(output, 1);
        voice.Stop();
        voice.Pause();
        voice.Play();
        mixer.Mix(output, 1);

        Assert.Equal(1f, output[0]);
    }

    [Fact]
    public void A_segment_plays_only_its_frames()
    {
        var mixer = new AudioMixer();
        var sound = Stereo(1f, 1f, 2f, 2f, 3f, 3f, 4f, 4f);
        var voice = new VoiceStrategy(mixer, sound, Rate, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2));
        var output = new float[Burst * 2];

        voice.Play();
        mixer.Mix(output, Burst);

        Assert.Equal([2f, 2f, 3f, 3f, 0f, 0f, 0f, 0f], output);
        Assert.False(voice.IsPlaying);
    }

    [Fact]
    public void Volume_changes_ramp_across_a_burst()
    {
        var mixer = new AudioMixer();
        var voice = CreateVoice(mixer, Stereo(Ones(Burst * 2 * 4)));
        var volume = new PolledVolumeControlStrategy();
        voice.SetVolumeControl(new Strategy<IVolumeControlStrategy>(volume));
        var output = new float[Burst * 2];

        voice.Play();
        mixer.Mix(output, Burst);
        Assert.All(output, sample => Assert.Equal(1f, sample));

        volume.Volume = 0.5f;
        mixer.Mix(output, Burst);
        Assert.Equal([1f, 0.875f, 0.75f, 0.625f], new[] { output[0], output[2], output[4], output[6] });

        mixer.Mix(output, Burst);
        Assert.All(output, sample => Assert.Equal(0.5f, sample));
    }

    [Fact]
    public void A_disposed_voice_is_no_longer_mixed()
    {
        var mixer = new AudioMixer();
        var voice = CreateVoice(mixer, Stereo(1f, 1f, 1f, 1f));
        var output = new float[Burst * 2];

        voice.Play();
        voice.Dispose();
        voice.Play();

        Assert.False(mixer.Mix(output, Burst));
        Assert.False(voice.IsPlaying);
    }

    [Fact]
    public void Starting_a_voice_wakes_the_output()
    {
        var mixer = new AudioMixer();
        int wakes = 0;
        mixer.SetVoiceStartedHandler(() => wakes++);
        var voice = CreateVoice(mixer, Stereo(1f, 1f));

        voice.Play();

        Assert.Equal(1, wakes);
    }

    [Fact]
    public void Same_rate_conversion_scales_to_float()
    {
        byte[] pcm = [0x00, 0x40, 0x00, 0xC0, 0xFF, 0x7F, 0x00, 0x80];

        var samples = PcmConverter.ToFloat(pcm, 2, 44100, 44100, out int channels);

        Assert.Equal(2, channels);
        Assert.Equal([0.5f, -0.5f, 32767f / 32768f, -1f], samples);
    }

    [Fact]
    public void Resampling_keeps_the_duration_and_the_waveform()
    {
        const int SourceRate = 44100;
        const int TargetRate = 48000;
        const double Frequency = 440;
        int sourceFrames = SourceRate / 10;
        var pcm = new byte[sourceFrames * 2];
        for (int i = 0; i < sourceFrames; i++)
        {
            short value = (short)(Math.Sin(2 * Math.PI * Frequency * i / SourceRate) * 16000);
            pcm[i * 2] = (byte)value;
            pcm[(i * 2) + 1] = (byte)(value >> 8);
        }

        var samples = PcmConverter.ToFloat(pcm, 1, SourceRate, TargetRate, out int channels);

        Assert.Equal(1, channels);
        Assert.Equal(TargetRate / 10, samples.Length);
        for (int i = 2; i < samples.Length - 2; i++)
        {
            double expected = Math.Sin(2 * Math.PI * Frequency * i / TargetRate) * 16000 / 32768;
            Assert.True(Math.Abs(expected - samples[i]) < 0.001, $"Sample {i}: {samples[i]}, expected {expected}");
        }
    }

    [Fact]
    public void More_than_two_channels_keep_the_first_two()
    {
        byte[] pcm = [0x00, 0x40, 0x00, 0xC0, 0x00, 0x20];

        var samples = PcmConverter.ToFloat(pcm, 3, 48000, 48000, out int channels);

        Assert.Equal(2, channels);
        Assert.Equal([0.5f, -0.5f], samples);
    }

    private static SoundStrategy Stereo(
        params float[] samples)
    {
        return new SoundStrategy(samples, 2, TimeSpan.FromSeconds((double)samples.Length / 2 / Rate));
    }

    private static float[] Ones(
        int count)
    {
        var samples = new float[count];
        Array.Fill(samples, 1f);
        return samples;
    }

    private static VoiceStrategy CreateVoice(
        AudioMixer mixer,
        SoundStrategy sound)
    {
        return new VoiceStrategy(mixer, sound, Rate, TimeSpan.Zero, sound.Duration);
    }
}
