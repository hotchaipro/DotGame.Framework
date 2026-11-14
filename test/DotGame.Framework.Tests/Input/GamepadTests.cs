// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Input;
using DotGame.Framework.Platform;
using DotGame.Framework.Platform.Input;
using Xunit;

namespace DotGame.Framework.Tests.Input;

public sealed class GamepadTests
{
    private sealed class NoInput : IInputServiceStrategy
    {
        public System.Threading.Tasks.Task OnInitialize() => System.Threading.Tasks.Task.CompletedTask;

        public ITextInputHandle CreateTextInputHandle(ITextInputSink sink) => throw new System.NotSupportedException();
    }

    private static InputService NewInput() => new InputService(new Strategy<IInputServiceStrategy>(new NoInput()));

    [Fact]
    public void A_gamepad_nobody_connected_reads_as_nothing()
    {
        var state = NewInput().GetGamepadState(0);

        Assert.False(state.IsConnected);
        Assert.False(state.IsButtonDown(GamepadButton.South));
        Assert.Equal(0f, state.GetAxis(GamepadAxis.LeftX));
    }

    [Fact]
    public void State_follows_the_events_once_they_are_dequeued()
    {
        var input = NewInput();
        input.QueueGamepadEvent(new GamepadEvent(GamepadEventKind.Connected, 1));
        input.QueueGamepadEvent(new GamepadEvent(GamepadEventKind.ButtonPressed, 1, GamepadButton.DPadRight));
        input.QueueGamepadEvent(new GamepadEvent(GamepadEventKind.AxisMoved, 1, axis: GamepadAxis.RightTrigger, value: 0.5f));

        Assert.False(input.GetGamepadState(1).IsConnected);

        Assert.Equal(3, input.DequeueGamepadEvents().Count);

        var state = input.GetGamepadState(1);
        Assert.True(state.IsConnected);
        Assert.True(state.IsButtonDown(GamepadButton.DPadRight));
        Assert.False(state.IsButtonDown(GamepadButton.South));
        Assert.Equal(0.5f, state.GetAxis(GamepadAxis.RightTrigger));
        Assert.False(input.GetGamepadState(0).IsConnected);
    }

    [Fact]
    public void A_release_clears_the_button()
    {
        var input = NewInput();
        input.QueueGamepadEvent(new GamepadEvent(GamepadEventKind.ButtonPressed, 0, GamepadButton.North));
        input.QueueGamepadEvent(new GamepadEvent(GamepadEventKind.ButtonReleased, 0, GamepadButton.North));
        input.DequeueGamepadEvents();

        Assert.False(input.GetGamepadState(0).IsButtonDown(GamepadButton.North));
    }

    [Fact]
    public void A_disconnect_releases_what_it_held()
    {
        var input = NewInput();
        input.QueueGamepadEvent(new GamepadEvent(GamepadEventKind.Connected, 0));
        input.QueueGamepadEvent(new GamepadEvent(GamepadEventKind.ButtonPressed, 0, GamepadButton.South));
        input.QueueGamepadEvent(new GamepadEvent(GamepadEventKind.AxisMoved, 0, axis: GamepadAxis.LeftX, value: 1f));
        input.DequeueGamepadEvents();

        input.QueueGamepadEvent(new GamepadEvent(GamepadEventKind.Disconnected, 0));
        var events = input.DequeueGamepadEvents();

        Assert.Equal(GamepadEventKind.ButtonReleased, events[0].Kind);
        Assert.Equal(GamepadEventKind.AxisMoved, events[1].Kind);
        Assert.Equal(GamepadEventKind.Disconnected, events[2].Kind);
        var state = input.GetGamepadState(0);
        Assert.False(state.IsConnected);
        Assert.False(state.IsButtonDown(GamepadButton.South));
        Assert.Equal(0f, state.GetAxis(GamepadAxis.LeftX));
    }

    [Fact]
    public void Losing_focus_releases_what_every_gamepad_holds_and_keeps_it_connected()
    {
        var input = NewInput();
        input.QueueGamepadEvent(new GamepadEvent(GamepadEventKind.Connected, 2));
        input.QueueGamepadEvent(new GamepadEvent(GamepadEventKind.ButtonPressed, 2, GamepadButton.Start));
        input.DequeueGamepadEvents();

        input.CancelPressedPointers();
        input.DequeueGamepadEvents();

        var state = input.GetGamepadState(2);
        Assert.True(state.IsConnected);
        Assert.False(state.IsButtonDown(GamepadButton.Start));
    }

    [Fact]
    public void An_event_for_an_index_out_of_range_is_dropped()
    {
        var input = NewInput();
        input.QueueGamepadEvent(new GamepadEvent(GamepadEventKind.Connected, InputService.MaxGamepads));

        Assert.Empty(input.DequeueGamepadEvents());
    }
}
