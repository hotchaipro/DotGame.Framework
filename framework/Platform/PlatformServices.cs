// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using DotGame.Framework.Platform.Audio;
using DotGame.Framework.Platform.Content;
using DotGame.Framework.Platform.Graphics;
using DotGame.Framework.Platform.Input;

namespace DotGame.Framework.Platform;

/// <summary>
/// The services a platform implements, passed to <see cref="GameHost.BeginStartup"/>.
/// </summary>
internal sealed record PlatformServices(
    Strategy<IContentServiceStrategy> Content,
    Strategy<IGraphicsServiceStrategy> Graphics,
    Strategy<IAudioServiceStrategy> Audio,
    Strategy<IInputServiceStrategy> Input);
