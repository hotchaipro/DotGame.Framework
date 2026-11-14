// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace DotGame.Framework.iOS;

/// <summary>
/// An autorelease pool without a managed object, for loops that run for good: a thread has only
/// one pool of its own for its whole life, and the main run loop's drains only when it goes idle.
/// </summary>
internal static class ObjCAutoreleasePool
{
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_autoreleasePoolPush")]
    public static extern nint Push();

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_autoreleasePoolPop")]
    public static extern void Pop(
        nint pool);
}
