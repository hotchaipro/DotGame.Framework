// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Audio;
using DotGame.Framework.Platform;
using DotGame.Framework.Platform.Audio;
using DotGame.Framework.Platform.Common.Audio;
using Xunit;

namespace DotGame.Framework.Tests.Audio;

/// <summary>
/// Volume controls chain: what plays follows its control, the control's parents and the master.
/// </summary>
[Collection("Master volume")]
public sealed class VolumeHierarchyTests : IDisposable
{
    public VolumeHierarchyTests()
    {
        PolledVolumeControlStrategy.Master.Volume = 1f;
    }

    public void Dispose()
    {
        PolledVolumeControlStrategy.Master.Volume = 1f;
    }

    private static VolumeControl Control(
        out PolledVolumeControlStrategy polled,
        VolumeControl? parent = null)
    {
        polled = new PolledVolumeControlStrategy();
        return new VolumeControl(new Strategy<IVolumeControlStrategy>(polled), parent);
    }

    private static VolumeControl Master()
    {
        return new VolumeControl(new Strategy<IVolumeControlStrategy>(PolledVolumeControlStrategy.Master), isMaster: true);
    }

    [Theory]
    [InlineData(1.0000001f, 1f)]
    [InlineData(1.5f, 1f)]
    [InlineData(-0.5f, 0f)]
    [InlineData(float.PositiveInfinity, 1f)]
    [InlineData(0.25f, 0.25f)]
    public void A_volume_is_clamped_to_its_range(
        float volume,
        float expected)
    {
        var control = Control(out _);

        control.Volume = volume;

        Assert.Equal(expected, control.Volume);
    }

    [Fact]
    public void A_volume_that_is_not_a_number_is_refused()
    {
        var control = Control(out _);

        Assert.Throws<ArgumentOutOfRangeException>(() => control.Volume = float.NaN);
        Assert.Throws<ArgumentOutOfRangeException>(() => control.FadeTo(float.NaN, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_control_multiplies_by_its_parents_and_the_master()
    {
        var group = Control(out var groupPolled);
        var voice = Control(out var voicePolled, group);

        voicePolled.Volume = 0.5f;
        groupPolled.Volume = 0.5f;
        PolledVolumeControlStrategy.Master.Volume = 0.5f;

        Assert.Equal(0.125f, PolledVolumeControlStrategy.ReadEffectiveVolume(voicePolled));
    }

    [Fact]
    public void Nothing_playing_without_a_control_escapes_the_master()
    {
        PolledVolumeControlStrategy.Master.Volume = 0.25f;

        Assert.Equal(0.25f, PolledVolumeControlStrategy.ReadEffectiveVolume(null));
    }

    [Fact]
    public void A_parents_fade_reaches_what_is_under_it()
    {
        var group = Control(out var groupPolled);
        var voice = Control(out var voicePolled, group);

        group.FadeTo(0f, TimeSpan.Zero);

        Assert.Equal(0f, PolledVolumeControlStrategy.ReadEffectiveVolume(voicePolled));
    }

    [Fact]
    public void The_master_survives_dispose()
    {
        var master = Master();

        master.Dispose();
        master.Volume = 0.5f;

        Assert.Equal(0.5f, PolledVolumeControlStrategy.ReadEffectiveVolume(null));
    }

    [Fact]
    public void Parenting_to_the_master_is_the_same_as_no_parent()
    {
        var master = Master();
        master.Volume = 0.5f;

        Control(out var voicePolled, master);

        Assert.Equal(0.5f, PolledVolumeControlStrategy.ReadEffectiveVolume(voicePolled));
    }

    [Fact]
    public void Disposing_a_parent_silences_what_is_under_it()
    {
        var group = Control(out _);
        Control(out var voicePolled, group);

        group.Dispose();

        Assert.Equal(0f, PolledVolumeControlStrategy.ReadEffectiveVolume(voicePolled));
    }

    [Fact]
    public void Disposing_a_parent_silences_all_the_way_down()
    {
        var group = Control(out _);
        var middle = Control(out _, group);
        Control(out var voicePolled, middle);

        group.Dispose();

        Assert.Equal(0f, PolledVolumeControlStrategy.ReadEffectiveVolume(voicePolled));
    }

    [Fact]
    public void Disposing_a_parent_silences_even_during_a_fade_up()
    {
        var group = Control(out var groupPolled);
        Control(out var voicePolled, group);
        groupPolled.Volume = 0f;
        group.FadeTo(1f, TimeSpan.FromHours(1));

        group.Dispose();

        Assert.Equal(0f, PolledVolumeControlStrategy.ReadEffectiveVolume(voicePolled));
    }

    [Fact]
    public void A_disposed_parent_is_released_only_with_its_last_child()
    {
        var group = Control(out _);
        var first = Control(out _, group);
        var second = Control(out _, group);

        group.Dispose();
        Assert.False(group.Strategy.IsDisposed);

        first.Dispose();
        Assert.False(group.Strategy.IsDisposed);

        second.Dispose();
        Assert.True(group.Strategy.IsDisposed);
    }

    [Fact]
    public void A_parent_with_no_children_is_released_at_once()
    {
        var group = Control(out _);

        group.Dispose();

        Assert.True(group.Strategy.IsDisposed);
    }

    [Fact]
    public void Disposing_twice_does_nothing_more()
    {
        var group = Control(out _);
        var child = Control(out _, group);

        child.Dispose();
        child.Dispose();

        Assert.False(group.Strategy.IsDisposed);
    }

    [Fact]
    public void A_disposed_control_can_not_be_used()
    {
        var group = Control(out _);
        group.Dispose();

        Assert.Throws<ObjectDisposedException>(() => group.Volume);
        Assert.Throws<ObjectDisposedException>(() => group.Volume = 1f);
        Assert.Throws<ObjectDisposedException>(() => group.FadeTo(1f, TimeSpan.FromSeconds(1)));
        Assert.Throws<ObjectDisposedException>(() => Control(out _, group));
    }
}
