// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Input;

namespace DotGame.Framework.Platform.Input;

/// <summary>
/// Immutable snapshot of a <c>TextInput</c>'s desired platform state.
/// Pushed by the framework into <see cref="ITextInputHandle.Update"/>
/// whenever any field changes.
/// </summary>
/// <param name="Bounds">
/// Position and size of the on-canvas text field in viewport pixels.
/// <see cref="Rectangle.IsEmpty"/> hides the overlay (when applicable).
/// </param>
/// <param name="Text">Current text value.</param>
/// <param name="InputType">Soft-keyboard / IME hint.</param>
/// <param name="MaxLength">Maximum length, or <c>-1</c> for unbounded.</param>
/// <param name="Multiline">
/// When <c>true</c>, the editor accepts newlines and Enter inserts a
/// line break instead of raising <c>Submitted</c>.
/// </param>
internal readonly record struct TextInputState(
    Rectangle Bounds,
    string Text,
    TextInputType InputType,
    int MaxLength,
    bool Multiline);
