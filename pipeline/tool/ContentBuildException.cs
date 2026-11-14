// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.ContentPipeline.BuildTool;

/// <summary>
/// An expected content build failure, reported without a stack trace.
/// </summary>
public sealed class ContentBuildException(string message) : Exception(message)
{
}
