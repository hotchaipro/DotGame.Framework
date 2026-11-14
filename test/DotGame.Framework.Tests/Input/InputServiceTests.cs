// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Input;
using DotGame.Framework.Platform;
using DotGame.Framework.Platform.Input;
using Xunit;

namespace DotGame.Framework.Tests.Input;

/// <summary>
/// The queues between a platform's input and the game: pointers and their touch counts, the
/// committed keyboard state, and text input. <see cref="DotGame.Framework.Tests.Platform.GameHostTests"/>
/// covers what a pause cancels, as the host drives it.
/// </summary>
public sealed class InputServiceTests
{
    private readonly FakeInputStrategy _platform = new();
    private readonly InputService _input;

    public InputServiceTests()
    {
        this._input = new InputService(new Strategy<IInputServiceStrategy>(this._platform));
    }

    private void Touch(
        PointerEventKind state,
        int pointerId,
        float x = 0,
        float y = 0)
    {
        this._input.QueuePointerEvent(state, pointerId, PointerDevice.Touch, x, y);
    }

    private void Mouse(
        PointerEventKind state,
        float x,
        PointerButton button,
        PointerButton held)
    {
        this._input.QueuePointerEvent(state, 0, PointerDevice.Mouse, x, 0, button, held);
    }

    // ── The pointer queue ──────────────────────────────────────────────

    [Fact]
    public void Dequeue_ReturnsWhatWasQueued_InOrder_Once()
    {
        this.Touch(PointerEventKind.Began, 1, 10, 20);
        this.Touch(PointerEventKind.Moved, 1, 30, 40);

        var events = this._input.DequeuePointerEvents();

        Assert.Equal(2, events.Count);
        Assert.Equal(PointerEventKind.Began, events[0].Kind);
        Assert.Equal(new Vector2(10, 20), events[0].Position);
        Assert.Equal(PointerEventKind.Moved, events[1].Kind);
        Assert.Equal(new Vector2(30, 40), events[1].Position);

        Assert.Empty(this._input.DequeuePointerEvents());
    }

    [Fact]
    public void Dequeue_SeesOnlyWhatArrivedBeforeIt_AndKeepsWhatArrivesAfter()
    {
        this.Touch(PointerEventKind.Began, 1);
        Assert.Single(this._input.DequeuePointerEvents());

        this.Touch(PointerEventKind.Ended, 1);
        var next = this._input.DequeuePointerEvents();

        Assert.Single(next);
        Assert.Equal(PointerEventKind.Ended, next[0].Kind);
    }

    [Fact]
    public void Queue_CarriesEveryField()
    {
        this._input.QueuePointerEvent(PointerEventKind.Moved, 7, PointerDevice.Pen, 1, 2, PointerButton.Right, PointerButton.Left | PointerButton.Right, 3, -4);

        var e = this._input.DequeuePointerEvents()[0];

        Assert.Equal(PointerEventKind.Moved, e.Kind);
        Assert.Equal(7, e.Id);
        Assert.Equal(PointerDevice.Pen, e.Device);
        Assert.Equal(new Vector2(1, 2), e.Position);
        Assert.Equal(PointerButton.Right, e.Button);
        Assert.Equal(PointerButton.Left | PointerButton.Right, e.HeldButtons);
        Assert.Equal(new Vector2(3, -4), e.ScrollWheelDelta);
    }

    [Fact]
    public async Task EventsFromManyThreads_AllArrive_EachThreadsInOrder()
    {
        const int Threads = 8;
        const int EventsPerThread = 2000;

        var seen = new List<PointerEvent>();
        var producers = new Task[Threads];
        for (int t = 0; t < Threads; t++)
        {
            int thread = t;
            producers[t] = Task.Run(
                () =>
                {
                    for (int i = 0; i < EventsPerThread; i++)
                    {
                        this._input.QueuePointerEvent(PointerEventKind.Moved, thread, PointerDevice.Mouse, i, 0);
                    }
                },
                TestContext.Current.CancellationToken);
        }

        var all = Task.WhenAll(producers);
        while (!all.IsCompleted)
        {
            seen.AddRange(this._input.DequeuePointerEvents());
            await Task.Yield();
        }

        await all;
        seen.AddRange(this._input.DequeuePointerEvents());

        Assert.Equal(Threads * EventsPerThread, seen.Count);

        var next = new int[Threads];
        foreach (var e in seen)
        {
            Assert.Equal(next[e.Id]++, (int)e.Position.X);
        }
    }

    // ── Touches ────────────────────────────────────────────────────────

    [Fact]
    public void TouchCount_IsTheTouchesDownAfterTheEvent()
    {
        this.Touch(PointerEventKind.Began, 1);
        this.Touch(PointerEventKind.Began, 2);
        this.Touch(PointerEventKind.Moved, 1);
        this.Touch(PointerEventKind.Ended, 1);
        this.Touch(PointerEventKind.Canceled, 2);

        var events = this._input.DequeuePointerEvents();

        Assert.Equal(1, events[0].TouchCount);
        Assert.Equal(2, events[1].TouchCount);
        Assert.Equal(2, events[2].TouchCount);
        Assert.Equal(1, events[3].TouchCount);
        Assert.Equal(0, events[4].TouchCount);
    }

    [Fact]
    public void TouchIndex_IsTheTouchsPlaceAmongThoseDown()
    {
        this.Touch(PointerEventKind.Began, 10);
        this.Touch(PointerEventKind.Began, 20);
        this.Touch(PointerEventKind.Began, 30);
        this.Touch(PointerEventKind.Moved, 20);
        this.Touch(PointerEventKind.Ended, 10);
        this.Touch(PointerEventKind.Moved, 30);

        var events = this._input.DequeuePointerEvents();

        Assert.Equal(0, events[0].TouchIndex);
        Assert.Equal(1, events[1].TouchIndex);
        Assert.Equal(2, events[2].TouchIndex);
        Assert.Equal(1, events[3].TouchIndex);
        Assert.Equal(0, events[4].TouchIndex);

        // The first touch's lift moves those after it up
        Assert.Equal(1, events[5].TouchIndex);
    }

    [Fact]
    public void NegativePointerIds_AreRealTouches()
    {
        // iOS browsers report them, and gating on a non-negative id once lost every finger
        this.Touch(PointerEventKind.Began, -5);
        this.Touch(PointerEventKind.Began, -6);

        var events = this._input.DequeuePointerEvents();

        Assert.Equal(1, events[0].TouchCount);
        Assert.Equal(2, events[1].TouchCount);
    }

    [Fact]
    public void ADuplicateBegan_DoesNotCountTwice()
    {
        this.Touch(PointerEventKind.Began, 1);
        this.Touch(PointerEventKind.Began, 2);
        this.Touch(PointerEventKind.Began, 1);
        this.Touch(PointerEventKind.Ended, 1);
        this.Touch(PointerEventKind.Ended, 2);

        var events = this._input.DequeuePointerEvents();

        Assert.Equal(2, events[2].TouchCount);
        Assert.Equal(0, events[2].TouchIndex);
        Assert.Equal(1, events[3].TouchCount);
        Assert.Equal(0, events[4].TouchCount);
    }

    [Fact]
    public void AnEndOfATouchNeverSeen_LeavesTheCountAlone()
    {
        this.Touch(PointerEventKind.Began, 1);
        this.Touch(PointerEventKind.Ended, 99);
        this.Touch(PointerEventKind.Moved, 98);

        var events = this._input.DequeuePointerEvents();

        Assert.Equal(1, events[1].TouchCount);
        Assert.Equal(0, events[1].TouchIndex);
        Assert.Equal(1, events[2].TouchCount);
    }

    [Fact]
    public void MouseAndPenEvents_ReportNoTouches()
    {
        this.Touch(PointerEventKind.Began, 1);
        this.Mouse(PointerEventKind.Began, 0, PointerButton.Left, PointerButton.Left);
        this._input.QueuePointerEvent(PointerEventKind.Began, 3, PointerDevice.Pen, 0, 0, PointerButton.Left, PointerButton.Left);

        var events = this._input.DequeuePointerEvents();

        Assert.Equal(0, events[1].TouchCount);
        Assert.Equal(0, events[2].TouchCount);
    }

    // ── Canceling ──────────────────────────────────────────────────────

    [Fact]
    public void Cancel_WithNothingPressed_QueuesNothing()
    {
        this.Mouse(PointerEventKind.Moved, 10, PointerButton.None, PointerButton.None);
        this._input.DequeuePointerEvents();

        this._input.CancelPressedPointers();

        Assert.Empty(this._input.DequeuePointerEvents());
    }

    [Fact]
    public void Cancel_OfAMouseWithAButtonStillHeld_ReportsTheButtons()
    {
        this.Mouse(PointerEventKind.Began, 10, PointerButton.Left, PointerButton.Left);
        this.Mouse(PointerEventKind.Began, 20, PointerButton.Right, PointerButton.Left | PointerButton.Right);
        this.Mouse(PointerEventKind.Ended, 30, PointerButton.Left, PointerButton.Right);
        this._input.DequeuePointerEvents();

        this._input.CancelPressedPointers();

        var canceled = Assert.Single(this._input.DequeuePointerEvents());
        Assert.Equal(PointerEventKind.Canceled, canceled.Kind);
        Assert.Equal(PointerButton.Right, canceled.Button);
        Assert.Equal(PointerButton.None, canceled.HeldButtons);
        Assert.Equal(30, canceled.Position.X);
    }

    [Fact]
    public void Cancel_OfAPen_ReportsThePen()
    {
        this._input.QueuePointerEvent(PointerEventKind.Began, 3, PointerDevice.Pen, 5, 6, PointerButton.Left, PointerButton.Left);
        this._input.DequeuePointerEvents();

        this._input.CancelPressedPointers();

        var canceled = Assert.Single(this._input.DequeuePointerEvents());
        Assert.Equal(PointerDevice.Pen, canceled.Device);
        Assert.Equal(3, canceled.Id);
    }

    [Fact]
    public void Cancel_Twice_CancelsOnce()
    {
        this.Touch(PointerEventKind.Began, 1);
        this._input.DequeuePointerEvents();

        this._input.CancelPressedPointers();
        this._input.CancelPressedPointers();

        Assert.Single(this._input.DequeuePointerEvents());
    }

    [Fact]
    public void AfterACanceledTouch_AnotherTouchIsUnaffected()
    {
        this.Touch(PointerEventKind.Began, 1);
        this._input.DequeuePointerEvents();
        this._input.CancelPressedPointers();
        this._input.DequeuePointerEvents();

        this.Touch(PointerEventKind.Began, 2);
        this.Touch(PointerEventKind.Moved, 2);
        this.Touch(PointerEventKind.Ended, 2);

        Assert.Equal(3, this._input.DequeuePointerEvents().Count);
    }

    [Fact]
    public void Cancel_OfATouch_ReportsWhereItIsNow()
    {
        this.Touch(PointerEventKind.Began, 1, 10, 10);
        this.Touch(PointerEventKind.Moved, 1, 50, 60);
        this._input.DequeuePointerEvents();

        this._input.CancelPressedPointers();

        var canceled = Assert.Single(this._input.DequeuePointerEvents());
        Assert.Equal(new Vector2(50, 60), canceled.Position);
    }

    // ── Keys ───────────────────────────────────────────────────────────

    [Fact]
    public void KeyEvents_ComeBackInOrder_Once_WithTheirFields()
    {
        this._input.QueueKeyEvent(KeyEventKind.Pressed, Key.A, ModifierKeys.Shift, isRepeat: true, virtualKey: 65);
        this._input.QueueKeyEvent(KeyEventKind.Released, Key.A, ModifierKeys.None);

        var events = this._input.DequeueKeyEvents();

        Assert.Equal(2, events.Count);
        Assert.Equal(new KeyEvent(KeyEventKind.Pressed, Key.A, ModifierKeys.Shift, true, 65), events[0]);
        Assert.Equal(new KeyEvent(KeyEventKind.Released, Key.A), events[1]);

        Assert.Empty(this._input.DequeueKeyEvents());
    }

    [Fact]
    public void KeyboardState_IsEmptyAtFirst()
    {
        var state = this._input.GetKeyboardState();

        Assert.False(state.IsKeyDown(Key.A));
        Assert.False(state.IsKeyDown(Key.A));
        Assert.Equal(ModifierKeys.None, state.Modifiers);
    }

    [Fact]
    public void KeyboardState_ChangesOnlyWhenEventsAreDequeued_SoAFrameSeesOneState()
    {
        this._input.QueueKeyEvent(KeyEventKind.Pressed, Key.Space, ModifierKeys.None);
        Assert.False(this._input.GetKeyboardState().IsKeyDown(Key.Space));

        this._input.DequeueKeyEvents();
        Assert.True(this._input.GetKeyboardState().IsKeyDown(Key.Space));

        this._input.QueueKeyEvent(KeyEventKind.Released, Key.Space, ModifierKeys.None);
        Assert.True(this._input.GetKeyboardState().IsKeyDown(Key.Space));

        this._input.DequeueKeyEvents();
        Assert.False(this._input.GetKeyboardState().IsKeyDown(Key.Space));
    }

    [Fact]
    public void KeyboardState_ReplaysTheFrame_WithTheLastEventWinning()
    {
        this._input.QueueKeyEvent(KeyEventKind.Pressed, Key.A, ModifierKeys.None);
        this._input.QueueKeyEvent(KeyEventKind.Released, Key.A, ModifierKeys.None);
        this._input.QueueKeyEvent(KeyEventKind.Pressed, Key.B, ModifierKeys.None);
        this._input.QueueKeyEvent(KeyEventKind.Released, Key.C, ModifierKeys.None);
        this._input.QueueKeyEvent(KeyEventKind.Pressed, Key.C, ModifierKeys.Control | ModifierKeys.Alt);

        this._input.DequeueKeyEvents();
        var state = this._input.GetKeyboardState();

        Assert.False(state.IsKeyDown(Key.A));
        Assert.True(state.IsKeyDown(Key.B));
        Assert.True(state.IsKeyDown(Key.C));
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Alt, state.Modifiers);
    }

    [Fact]
    public void AKeyRepeat_KeepsTheKeyDown()
    {
        this._input.QueueKeyEvent(KeyEventKind.Pressed, Key.A, ModifierKeys.None);
        this._input.QueueKeyEvent(KeyEventKind.Pressed, Key.A, ModifierKeys.None, isRepeat: true);

        this._input.DequeueKeyEvents();

        Assert.True(this._input.GetKeyboardState().IsKeyDown(Key.A));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100000)]
    public void AKeyOutOfRange_IsDelivered_AndNotInTheState(
        int value)
    {
        var key = (Key)value;

        this._input.QueueKeyEvent(KeyEventKind.Pressed, key, ModifierKeys.None);

        Assert.Single(this._input.DequeueKeyEvents());
        Assert.False(this._input.GetKeyboardState().IsKeyDown(key));
    }

    [Fact]
    public void ADefaultKeyboardState_HasNothingDown()
    {
        Assert.False(default(KeyboardState).IsKeyDown(Key.A));
        Assert.False(default(KeyboardState).IsKeyDown(Key.A));
    }

    [Fact]
    public void Cancel_ReleasesHeldKeys()
    {
        this._input.QueueKeyEvent(KeyEventKind.Pressed, Key.W, ModifierKeys.None);
        this._input.DequeueKeyEvents();

        this._input.CancelPressedPointers();

        var released = Assert.Single(this._input.DequeueKeyEvents());
        Assert.Equal(KeyEventKind.Released, released.Kind);
        Assert.Equal(Key.W, released.Key);
        Assert.False(this._input.GetKeyboardState().IsKeyDown(Key.W));
    }

    [Fact]
    public void ASavedKeyboardState_DoesNotChange()
    {
        this._input.QueueKeyEvent(KeyEventKind.Pressed, Key.A, ModifierKeys.None);
        this._input.DequeueKeyEvents();
        var saved = this._input.GetKeyboardState();

        this._input.QueueKeyEvent(KeyEventKind.Released, Key.A, ModifierKeys.None);
        this._input.DequeueKeyEvents();

        Assert.True(saved.IsKeyDown(Key.A));
    }

    [Fact]
    public async Task Initialize_InitializesThePlatformOnce()
    {
        await this._input.Initialize();
        await this._input.Initialize();

        Assert.Equal(1, this._platform.Initializations);
    }

    // ── Text input ─────────────────────────────────────────────────────

    [Fact]
    public void CreateTextInput_PushesItsStateToANewHandle()
    {
        using var input = this._input.CreateTextInput();

        Assert.Single(this._platform.Handles);
        var state = Assert.Single(this._platform.Handles[0].Updates);
        Assert.Equal(string.Empty, state.Text);
        Assert.Equal(-1, state.MaxLength);
    }

    [Fact]
    public void TextInputProperties_PushOnlyWhatChanged()
    {
        using var input = this._input.CreateTextInput();
        var handle = this._platform.Handles[0];
        handle.Updates.Clear();

        input.Text = "abc";
        input.Text = "abc";
        input.MaxLength = 10;
        input.MaxLength = 10;
        input.IsMultiline = true;
        input.IsMultiline = true;
        input.InputType = TextInputType.Email;
        input.InputType = TextInputType.Email;
        input.Bounds = new Rectangle(1, 2, 3, 4);
        input.Bounds = new Rectangle(1, 2, 3, 4);

        Assert.Equal(5, handle.Updates.Count);
        Assert.Equal(new TextInputState(new Rectangle(1, 2, 3, 4), "abc", TextInputType.Email, 10, true), handle.Updates[^1]);
    }

    [Fact]
    public void Text_SetToNull_IsEmpty()
    {
        using var input = this._input.CreateTextInput();
        input.Text = "abc";

        input.Text = null!;

        Assert.Equal(string.Empty, input.Text);
    }

    [Fact]
    public void TextInputEvents_WaitForTheDispatch()
    {
        using var input = this._input.CreateTextInput();
        var sink = this._platform.Sinks[0];
        var seen = new List<string>();
        input.TextChanged += (_, _) => seen.Add(input.Text);

        sink.OnTextChanged("a");
        Assert.Empty(seen);
        Assert.Equal(string.Empty, input.Text);

        this._input.DispatchTextInputEvents();
        Assert.Equal(["a"], seen);
        Assert.Equal("a", input.Text);

        this._input.DispatchTextInputEvents();
        Assert.Equal(["a"], seen);
    }

    [Fact]
    public void TextFromThePlatform_IsNotEchoedBack_AndAChangeBackIsStillPushed()
    {
        using var input = this._input.CreateTextInput();
        var handle = this._platform.Handles[0];
        input.Text = "game";
        handle.Updates.Clear();

        this._platform.Sinks[0].OnTextChanged("typed");
        this._input.DispatchTextInputEvents();

        Assert.Equal("typed", input.Text);
        Assert.Empty(handle.Updates);

        // Back to what the game last pushed, but not what the editor holds now
        input.Text = "game";

        Assert.Equal("game", Assert.Single(handle.Updates).Text);
    }

    [Fact]
    public void TheSameTextAgain_RaisesNothing()
    {
        using var input = this._input.CreateTextInput();
        int changes = 0;
        input.TextChanged += (_, _) => changes++;

        this._platform.Sinks[0].OnTextChanged("a");
        this._platform.Sinks[0].OnTextChanged("a");
        this._input.DispatchTextInputEvents();

        Assert.Equal(1, changes);
    }

    [Fact]
    public void Focus_IsReportedOncePerChange()
    {
        using var input = this._input.CreateTextInput();
        var sink = this._platform.Sinks[0];
        var seen = new List<string>();
        input.GotFocus += (_, _) => seen.Add("got");
        input.LostFocus += (_, _) => seen.Add("lost");

        sink.OnLostFocus();
        sink.OnGotFocus();
        sink.OnGotFocus();
        this._input.DispatchTextInputEvents();
        Assert.True(input.IsFocused);

        sink.OnLostFocus();
        sink.OnLostFocus();
        this._input.DispatchTextInputEvents();

        Assert.False(input.IsFocused);
        Assert.Equal(["got", "lost"], seen);
    }

    [Fact]
    public void SubmitAndCancel_AreRaised_InOrder()
    {
        using var input = this._input.CreateTextInput();
        var seen = new List<string>();
        input.Submitted += (_, _) => seen.Add("submitted");
        input.Canceled += (_, _) => seen.Add("canceled");
        input.TextChanged += (_, _) => seen.Add("text");

        this._platform.Sinks[0].OnTextChanged("a");
        this._platform.Sinks[0].OnSubmit();
        this._platform.Sinks[0].OnCancel();
        this._input.DispatchTextInputEvents();

        Assert.Equal(["text", "submitted", "canceled"], seen);
    }

    [Fact]
    public void FocusAndBlur_ReachThePlatform_UntilDisposed()
    {
        var input = this._input.CreateTextInput();
        var handle = this._platform.Handles[0];

        input.Focus();
        input.Unfocus();
        Assert.Equal(["focus", "blur"], handle.Calls);

        input.Dispose();
        input.Dispose();
        input.Focus();
        input.Unfocus();
        input.Text = "ignored by the platform";

        Assert.Equal(["focus", "blur", "dispose"], handle.Calls);
    }

    [Fact]
    public void EventsForADisposedInput_AreDropped()
    {
        var input = this._input.CreateTextInput();
        var other = this._input.CreateTextInput();
        int changes = 0;
        int otherChanges = 0;
        input.TextChanged += (_, _) => changes++;
        other.TextChanged += (_, _) => otherChanges++;

        this._platform.Sinks[0].OnTextChanged("a");
        this._platform.Sinks[1].OnTextChanged("b");
        input.Dispose();
        this._input.DispatchTextInputEvents();

        Assert.Equal(0, changes);
        Assert.Equal(1, otherChanges);
        other.Dispose();
    }

    private sealed class FakeInputStrategy : IInputServiceStrategy
    {
        public int Initializations { get; private set; }

        public List<FakeHandle> Handles { get; } = [];

        public List<ITextInputSink> Sinks { get; } = [];

        public Task OnInitialize()
        {
            this.Initializations++;
            return Task.CompletedTask;
        }

        public ITextInputHandle CreateTextInputHandle(
            ITextInputSink sink)
        {
            var handle = new FakeHandle();
            this.Handles.Add(handle);
            this.Sinks.Add(sink);
            return handle;
        }
    }

    private sealed class FakeHandle : ITextInputHandle
    {
        public List<TextInputState> Updates { get; } = [];

        public List<string> Calls { get; } = [];

        public void Update(
            in TextInputState state)
        {
            this.Updates.Add(state);
        }

        public void Focus()
        {
            this.Calls.Add("focus");
        }

        public void Blur()
        {
            this.Calls.Add("blur");
        }

        public void Dispose()
        {
            this.Calls.Add("dispose");
        }
    }
}
