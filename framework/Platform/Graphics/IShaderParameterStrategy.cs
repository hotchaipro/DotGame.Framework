// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

namespace DotGame.Framework.Platform.Graphics;

internal interface IShaderParameterStrategy
{
    void SetValue(float value);
    void SetValue(int value);
    void SetValue(Vector2 value);
    void SetValue(Vector4 value);
}
