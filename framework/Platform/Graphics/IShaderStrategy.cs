// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.Platform.Graphics;

internal interface IShaderStrategy
{
    Strategy<IShaderParameterStrategy> GetParameter(string name);
}
