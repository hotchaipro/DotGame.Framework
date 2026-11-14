// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

#nullable enable
using System;
using AVFoundation;
using Foundation;

namespace DotGame.Framework.iOS.Audio;

/// <summary>
/// Starts and connects AVAudioEngine nodes on every iOS version the framework supports. iOS 27
/// obsoletes <c>AVAudioPlayerNode.Play()</c> and <c>AVAudioEngine.Connect(...)</c>, which raise
/// an Objective-C exception when they fail, for overloads that return an <see cref="NSError"/>.
/// Those overloads exist only on iOS 27 and later, and only in .NET 11's bindings, so older versions
/// and .NET 10 builds keep the old calls. Either
/// way a failure is thrown: an <see cref="ObjCRuntime.ObjCException"/> from the old calls, an
/// <see cref="NSErrorException"/> from the new ones.
/// </summary>
internal static class AudioNodes
{
    public static void Play(
        AVAudioPlayerNode node)
    {
#if NET11_0_OR_GREATER
        if (OperatingSystem.IsIOSVersionAtLeast(27) || OperatingSystem.IsMacCatalystVersionAtLeast(27))
        {
            node.Play(out NSError? error);
            if (error is not null)
            {
                throw new NSErrorException(error);
            }

            return;
        }
#endif

#pragma warning disable CA1422 // Obsoleted on iOS 27, where the branch above runs instead
        node.Play();
#pragma warning restore CA1422
    }

    public static void Connect(
        AVAudioEngine engine,
        AVAudioNode node,
        AVAudioNode to,
        AVAudioFormat? format)
    {
#if NET11_0_OR_GREATER
        if (OperatingSystem.IsIOSVersionAtLeast(27) || OperatingSystem.IsMacCatalystVersionAtLeast(27))
        {
            engine.Connect(node, to, format, out NSError? error);
            if (error is not null)
            {
                throw new NSErrorException(error);
            }

            return;
        }
#endif

#pragma warning disable CA1422 // Obsoleted on iOS 27, where the branch above runs instead
        engine.Connect(node, to, format);
#pragma warning restore CA1422
    }
}
