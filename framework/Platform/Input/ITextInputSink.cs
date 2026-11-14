// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.Platform.Input;

/// <summary>
/// Callbacks invoked by an <see cref="IInputServiceStrategy"/> to report
/// platform text-editor events back to the framework. Implementations
/// must be safe to call from any thread; the framework dispatches the
/// observable session events on the game thread.
/// </summary>
internal interface ITextInputSink
{
    /// <summary>
    /// Reports the full current value of the platform editor (matches
    /// the semantics of HTML <c>'input'</c>, iOS <c>EditingChanged</c>,
    /// and Android <c>OnTextChanged</c>). Replaces, not appends.
    /// </summary>
    void OnTextChanged(string fullText);

    /// <summary>Editor gained keyboard focus.</summary>
    void OnGotFocus();

    /// <summary>Editor lost keyboard focus.</summary>
    void OnLostFocus();

    /// <summary>
    /// The text an input method is composing, not yet part of the text; empty when it ends. A
    /// platform whose editor keeps composing text in the text itself doesn't report this.
    /// </summary>
    void OnCompositionChanged(string composition);

    /// <summary>Commit (Enter / IME Done).</summary>
    void OnSubmit();

    /// <summary>Dismiss (Escape / Back).</summary>
    void OnCancel();
}
