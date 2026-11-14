// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Audio;
using DotGame.Framework.Platform.Common.Audio;
using Xunit;

namespace DotGame.Framework.Tests.Audio;

/// <summary>
/// The volume control the native platforms' audio threads poll while a
/// voice or music plays, so a change or a fade reaches it immediately.
/// </summary>
public sealed class PolledVolumeControlStrategyTests
{
    [Fact]
    public void Setting_the_volume_clamps_it_and_ends_a_fade()
    {
        var volume = new PolledVolumeControlStrategy();
        Assert.Equal(1f, volume.Volume);

        volume.FadeTo(0f, TimeSpan.FromHours(1), FadeCurve.Linear);
        volume.Volume = 1.5f;
        Assert.Equal(1f, volume.Volume);

        volume.Volume = -1f;
        Assert.Equal(0f, volume.Volume);
    }

    [Fact]
    public void A_fade_changes_the_volume_as_it_runs()
    {
        var volume = new PolledVolumeControlStrategy();

        volume.FadeTo(0f, TimeSpan.FromMilliseconds(200), FadeCurve.Linear);
        float start = volume.Volume;
        Assert.InRange(start, 0.5f, 1f);

        Thread.Sleep(100);
        float middle = volume.Volume;
        Assert.InRange(middle, 0f, start);

        Thread.Sleep(200);
        Assert.Equal(0f, volume.Volume);
    }

    [Fact]
    public void A_fade_starts_from_where_a_running_fade_has_got_to()
    {
        var volume = new PolledVolumeControlStrategy();

        volume.FadeTo(0f, TimeSpan.Zero, FadeCurve.Linear);
        Assert.Equal(0f, volume.Volume);

        volume.FadeTo(1f, TimeSpan.FromHours(1), FadeCurve.Linear);
        volume.FadeTo(0.5f, TimeSpan.FromHours(1), FadeCurve.Linear);
        Assert.InRange(volume.Volume, 0f, 0.01f);
    }

    [Fact]
    public void Another_thread_reads_whole_fades_while_the_game_thread_writes()
    {
        // Each write is a fade from 0.25 or 0.75 to itself, so a read
        // that mixed two writes' fields would see a value in between
        var volume = new PolledVolumeControlStrategy();
        volume.Volume = 0.25f;
        bool done = false;
        float torn = -1f;

        var reader = new Thread(() =>
        {
            while (!Volatile.Read(ref done))
            {
                float value = volume.Volume;
                if ((value != 0.25f) && (value != 0.75f))
                {
                    torn = value;
                }
            }
        });

        reader.Start();
        for (int i = 0; i < 200_000; i++)
        {
            volume.Volume = ((i & 1) == 0) ? 0.75f : 0.25f;
            volume.FadeTo(volume.Volume, TimeSpan.FromHours(1), FadeCurve.Linear);
        }

        Volatile.Write(ref done, true);
        reader.Join();

        Assert.Equal(-1f, torn);
    }
}
