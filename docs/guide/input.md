# Input

Input arrives as events before each frame's update, and you can also ask for the keyboard's and
gamepads' current state at any time. Input stays the same across platforms: a tap on a phone and a
click on a desktop are both pointer events.

## Pointers: touch, mouse and pen

Override `OnPointerEvent`:

```csharp
protected override void OnPointerEvent(PointerEvent e)
{
    if ((e.Kind == PointerEventKind.Began) && this.button.Contains(e.Position))
    {
        this.score++;
    }
}
```

[`Position`](../api/Framework/PointerEvent.md#position) is in pixels, with (0, 0) at
the top left of the window. To turn it into your game's coordinates, use the inverse of your
transform.

### Handling a press

| [`State`](../api/Framework/PointerEventKind.md) | What it reports |
|---|---|
| `Began` | A finger came down, or a button was pressed |
| `Moved` | It moved. A mouse moves with no button held too |
| `Ended` | A finger lifted, or a button was released |
| `Canceled` | The press ended without acting: the game lost the focus, or the system took over |

**Always handle `Canceled`.** Treat it as `Ended`, except that the press does nothing. If you ignore
it, a drag sticks after a phone call or an alert, as its `Ended` never comes.

### Several fingers

Each pointer keeps its [`Id`](../api/Framework/PointerEvent.md#id) from `Began` until
`Ended` or `Canceled`. Track them by it:

```csharp
private readonly Dictionary<int, Vector2> pointers = new();

protected override void OnPointerEvent(PointerEvent e)
{
    switch (e.Kind)
    {
        case PointerEventKind.Began:
        case PointerEventKind.Moved:
            this.pointers[e.Id] = e.Position;
            break;

        case PointerEventKind.Ended:
        case PointerEventKind.Canceled:
            this.pointers.Remove(e.Id);
            break;
    }
}
```

`TouchCount` says how many fingers are down, and `TouchIndex` which one this is.

### Mouse buttons and the wheel

`PointerType` tells a mouse from a touch or a pen. `Button` is the button that changed, and
`HeldButtons` those down now. `ScrollWheelDelta` is how far the wheel moved:

```csharp
protected override void OnPointerEvent(PointerEvent e)
{
    if ((e.Kind == PointerEventKind.Began) && (e.Button == PointerButton.Right))
    {
        this.ShowMenu(e.Position);
    }

    this.zoom += e.ScrollWheelDelta.Y * 0.1f;
}
```

Phones have no right button or hover, so make sure a game can be played without them.

For physical sizes, such as a drag threshold of a few millimeters, use
[`DisplayDpi`](../api/Framework/Game.md#displaydpi) to convert to pixels.

## The keyboard

Use events for things that happen once:

```csharp
protected override void OnKeyEvent(KeyEvent e)
{
    if ((e.Kind == KeyEventKind.Pressed) && !e.IsRepeat && (e.Key == Key.Space))
    {
        this.Jump();
    }
}
```

`IsRepeat` is true for the repeats the system sends while a key is held. `Modifiers` has Shift, Control
and the others.

Use the state for things that continue, such as movement:

```csharp
protected override void OnUpdate()
{
    var keyboard = this.Input.GetKeyboardState();
    if (keyboard.IsKeyDown(Key.Left))
    {
        this.position.X -= 200 * this.Time.DeltaSeconds;
    }
}
```

[`Key`](../api/Framework/Key.md) identifies a **physical key**, wherever it is on the player's layout:
`Key.W` is the key above S even on a French keyboard, which is what you want for movement. For
typing, use [text input](#text-entry), not key events.

## Gamepads

Up to [`MaxGamepads`](../api/Framework/InputService.md#maxgamepads) gamepads are read by index. Poll
them for movement:

```csharp
protected override void OnUpdate()
{
    var pad = this.Input.GetGamepadState(0);
    if (pad.IsConnected)
    {
        this.position.X += pad.GetAxis(GamepadAxis.LeftX) * 200 * this.Time.DeltaSeconds;
    }
}
```

And use events for presses, and to notice a pad arriving or leaving:

```csharp
protected override void OnGamepadEvent(GamepadEvent e)
{
    switch (e.Kind)
    {
        case GamepadEventKind.ButtonPressed when e.Button == GamepadButton.South:
            this.Jump();
            break;

        case GamepadEventKind.Disconnected:
            this.ShowPauseMenu();
            break;
    }
}
```

- [`GamepadButton`](../api/Framework/GamepadButton.md) names buttons by where they are rather than
  what's printed on them, so `South` is the bottom face button on any pad.
- Sticks go from -1 to 1, with up and left negative, and triggers from 0 to 1.
- Events that arrive while the game is starting are dropped. So check `GetGamepadState` for pads
  already connected, rather than waiting for `Connected`.

## Text entry

[`TextInput`](../api/Framework/TextInput.md) uses the platform's own editor, so the soft keyboard,
input methods, autocorrect and pasting all work. You draw the field, and the platform does the
editing:

```csharp
protected override Task OnStart()
{
    this.nameInput = this.Input.CreateTextInput();
    this.nameInput.InputType = TextInputType.Text;
    this.nameInput.MaxLength = 24;
    this.nameInput.Bounds = new Rectangle(20, 20, 400, 48);
    this.nameInput.Submitted += (_, _) =>
    {
        this.playerName = this.nameInput.Text;
        this.nameInput.Unfocus();
    };
    this.nameInput.Canceled += (_, _) => this.nameInput.Unfocus();
    return Task.CompletedTask;
}

protected override void OnPointerEvent(PointerEvent e)
{
    // On the web and iOS, focus has to happen during the tap to bring up the keyboard
    if ((e.Kind == PointerEventKind.Began) && this.nameInput.Bounds.Contains(e.Position))
    {
        this.nameInput.Focus();
    }
}
```

Set `Bounds` to where you draw the field, as the platform puts its editor there. Dispose the input
when you're done with it.

`Text` holds what's been committed, and `Composition` any text the player is still composing with an
input method. Draw both.

## See also

- [`InputService`](../api/Framework/InputService.md), [`PointerEvent`](../api/Framework/PointerEvent.md),
  [`KeyEvent`](../api/Framework/KeyEvent.md), [`GamepadEvent`](../api/Framework/GamepadEvent.md)
- The framework sample's Input page
