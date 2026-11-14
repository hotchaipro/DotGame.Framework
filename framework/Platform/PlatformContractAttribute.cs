// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.Framework.Platform;

/// <summary>
/// Marks an internal member of a public framework type as part of the platform contract, so
/// platform code may use it: the strategy behind a framework object (a texture's, a sound's).
/// Everything else a platform uses of the framework's internals is in this namespace; the
/// analyzers report any other use.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Method | AttributeTargets.Constructor, Inherited = false)]
internal sealed class PlatformContractAttribute : Attribute
{
}
