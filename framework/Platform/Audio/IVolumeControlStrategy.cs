// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using DotGame.Framework.Audio;

namespace DotGame.Framework.Platform.Audio;

internal interface IVolumeControlStrategy
{
    float Volume { get; set; }

    // What this control's volume is multiplied by; null is the master control
    Strategy<IVolumeControlStrategy>? Parent { set; }

    // The curve is never null: VolumeControl.FadeTo substitutes the default
    // before reaching here, so no implementation has to.
    void FadeTo(float toVolume, TimeSpan duration, FadeCurve curve);
}
