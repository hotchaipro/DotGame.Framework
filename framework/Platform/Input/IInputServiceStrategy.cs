// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Threading.Tasks;

namespace DotGame.Framework.Platform.Input;

internal interface IInputServiceStrategy
{
    Task OnInitialize();

    /// <summary>
    /// Creates a platform-native text-editor handle. The framework keeps
    /// the handle alive for the lifetime of a corresponding
    /// <c>TextInput</c>, calling <see cref="ITextInputHandle.Update"/>
    /// to reposition / reconfigure / synchronize the editor and
    /// <see cref="ITextInputHandle.Focus"/> /
    /// <see cref="ITextInputHandle.Blur"/> to drive focus. The handle
    /// reports edits, focus changes, submit, and cancel back to the
    /// framework via <paramref name="sink"/>.
    /// </summary>
    /// <remarks>
    /// On overlay platforms (browser) each handle owns its own
    /// transparent <c>&lt;input&gt;</c> overlay so the browser can route
    /// taps to the correct editor synchronously inside the trusted
    /// gesture handler — required by iOS Safari to bring up the soft
    /// keyboard.
    /// </remarks>
    ITextInputHandle CreateTextInputHandle(ITextInputSink sink);
}
