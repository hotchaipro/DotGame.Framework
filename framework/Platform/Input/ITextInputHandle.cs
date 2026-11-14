// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.Framework.Platform.Input;

/// <summary>
/// Per-instance handle to a platform-native text editor. Created by
/// <see cref="IInputServiceStrategy.CreateTextInputHandle"/>. The
/// framework keeps the handle alive for as long as the corresponding
/// <c>TextInput</c> is registered, and pushes state changes via
/// <see cref="Update"/>. Disposing the handle releases the native
/// resource.
/// </summary>
internal interface ITextInputHandle : IDisposable
{
    /// <summary>
    /// Pushes the current desired state into the platform editor:
    /// position/size, text value, input-type hint, max length, and
    /// multiline flag. Implementations should diff against their last
    /// applied state to avoid unnecessary native work, and must avoid
    /// echoing the new text back through
    /// <see cref="ITextInputSink.OnTextChanged"/>.
    /// </summary>
    void Update(in TextInputState state);

    /// <summary>
    /// Programmatically focuses the platform editor. Implementations
    /// should be safe to call when already focused.
    /// </summary>
    void Focus();

    /// <summary>
    /// Programmatically blurs the platform editor. Implementations
    /// should be safe to call when already blurred.
    /// </summary>
    void Blur();
}
