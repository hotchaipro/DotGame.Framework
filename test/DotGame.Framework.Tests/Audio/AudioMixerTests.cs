// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Platform;
using DotGame.Framework.Platform.Audio;
using DotGame.Framework.Platform.Common.Audio;
using Xunit;

namespace DotGame.Framework.Tests.Audio;

/// <summary>
/// Android's and desktop's sound-effect mixer, driven the way its audio thread drives
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
        var voice = CreateVoice(mixer, new MixerSoundStrategy([0.25f, -0.5f], 1, TimeSpan.FromMilliseconds(2)));
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
        var voices = new MixerVoiceStrategy[300];
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
        var voice = new MixerVoiceStrategy(mixer, sound, Rate, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2));
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

    [Fact]
    public void At_twice_the_rate_a_voice_plays_every_other_frame_and_ends_in_half_the_time()
    {
        var mixer = new AudioMixer();
        var voice = CreateVoice(mixer, Stereo(0.1f, -0.1f, 0.2f, -0.2f, 0.3f, -0.3f, 0.4f, -0.4f, 0.5f, -0.5f, 0.6f, -0.6f));
        var output = new float[4 * 2];

        voice.SetPlaybackRate(2f);
        voice.Play();

        // Six frames at twice the rate are three: the first, third and fifth, read on whole
        // frames, where the interpolator gives the frame itself
        Assert.False(mixer.Mix(output, 4));
        AssertSamples([0.1f, -0.1f, 0.3f, -0.3f, 0.5f, -0.5f, 0f, 0f], output);
        Assert.False(voice.IsPlaying);
    }

    [Fact]
    public void At_half_the_rate_a_voice_takes_twice_as_long_and_passes_between_its_frames()
    {
        var mixer = new AudioMixer();
        var voice = CreateVoice(mixer, new MixerSoundStrategy([0f, 1f, 2f, 3f, 4f, 5f, 6f, 7f], 1, TimeSpan.FromMilliseconds(8)));
        var output = new float[16 * 2];

        voice.SetPlaybackRate(0.5f);
        voice.Play();

        // Eight frames at half the rate are sixteen
        Assert.True(mixer.Mix(output, 15));
        Assert.True(voice.IsPlaying);
        Assert.False(mixer.Mix(new float[4], 2));
        Assert.False(voice.IsPlaying);

        // On a straight line the interpolator is exact, between frames as on them (away from
        // the ends, where the neighbours repeat the end frames)
        for (int frame = 2; frame < 12; frame++)
        {
            Assert.Equal(frame * 0.5f, output[frame * 2], 5);
            Assert.Equal(frame * 0.5f, output[(frame * 2) + 1], 5);
        }
    }

    [Fact]
    public void A_sine_at_twice_the_rate_has_twice_the_frequency()
    {
        const int Frames = 400;
        var samples = new float[Frames];
        for (int i = 0; i < Frames; i++)
        {
            samples[i] = MathF.Sin(2 * MathF.PI * 10 * i / Frames);     // ten cycles
        }

        int asRecorded = CountRisingCrossings(samples, 1f, Frames);
        int doubled = CountRisingCrossings(samples, 2f, Frames / 2);
        int slowed = CountRisingCrossings(samples, 0.75f, 300);

        // The same ten cycles in half as many frames; three quarters of the rate plays three
        // quarters of them in 300
        Assert.InRange(asRecorded, 9, 10);
        Assert.Equal(asRecorded, doubled);
        Assert.InRange(slowed, 5, 6);

        static int CountRisingCrossings(
            float[] samples,
            float rate,
            int frames)
        {
            var mixer = new AudioMixer();
            var voice = CreateVoice(mixer, new MixerSoundStrategy(samples, 1, TimeSpan.FromSeconds((double)samples.Length / Rate)));
            var output = new float[frames * 2];
            voice.SetPlaybackRate(rate);
            voice.Play();
            mixer.Mix(output, frames);

            int crossings = 0;
            for (int i = 1; i < frames; i++)
            {
                if ((output[(i - 1) * 2] < 0) && (output[i * 2] >= 0))
                {
                    crossings++;
                }
            }

            return crossings;
        }
    }

    [Fact]
    public void A_rate_changed_while_playing_goes_on_from_where_the_voice_was()
    {
        var mixer = new AudioMixer();
        var ramp = new float[64];
        for (int i = 0; i < ramp.Length; i++)
        {
            ramp[i] = i;
        }

        var voice = CreateVoice(mixer, new MixerSoundStrategy(ramp, 1, TimeSpan.FromMilliseconds(64)));
        var output = new float[8 * 2];

        voice.Play();
        mixer.Mix(output, 4);                       // frames 0 to 3
        Assert.Equal(3f, output[6]);

        voice.SetPlaybackRate(1.5f);
        mixer.Mix(output, 4);                       // 4, 5.5, 7, 8.5
        AssertSamples([4f, 4f, 5.5f, 5.5f, 7f, 7f, 8.5f, 8.5f], output[..8]);

        // Back at the recorded rate it carries on from between two frames
        voice.SetPlaybackRate(1f);
        mixer.Mix(output, 3);                       // 10, 11, 12
        AssertSamples([10f, 10f, 11f, 11f, 12f, 12f], output[..6]);
        Assert.True(voice.IsPlaying);
    }

    [Fact]
    public void A_looping_voice_at_a_rate_starts_over_and_keeps_playing()
    {
        var mixer = new AudioMixer();
        var voice = CreateVoice(mixer, new MixerSoundStrategy([1f, 1f, 1f, 1f, 1f, 1f], 1, TimeSpan.FromMilliseconds(6)));
        var output = new float[20 * 2];

        voice.SetLooping(true);
        voice.SetPlaybackRate(4f);
        voice.Play();

        // (A loop's neighbours come from its other end, so a steady sound stays steady across
        // the seam)
        Assert.True(mixer.Mix(output, 20));
        Assert.True(voice.IsPlaying);
        Assert.All(output, sample => Assert.Equal(1f, sample, 5));
    }

    private static void AssertSamples(
        float[] expected,
        float[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], actual[i], 5);
        }
    }

    private static MixerSoundStrategy Stereo(
        params float[] samples)
    {
        return new MixerSoundStrategy(samples, 2, TimeSpan.FromSeconds((double)samples.Length / 2 / Rate));
    }

    private static float[] Ones(
        int count)
    {
        var samples = new float[count];
        Array.Fill(samples, 1f);
        return samples;
    }

    private static MixerVoiceStrategy CreateVoice(
        AudioMixer mixer,
        MixerSoundStrategy sound)
    {
        return new MixerVoiceStrategy(mixer, sound, Rate, TimeSpan.Zero, sound.Duration);
    }
}
