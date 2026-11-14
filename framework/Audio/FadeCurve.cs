// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;

namespace DotGame.Framework.Audio;

/// <summary>
/// The shape a volume fade follows over its duration.
/// </summary>
/// <remarks>
/// <para>
/// A curve describes only the <em>shape</em> of a fade, on normalized progress
/// from 0 (the moment the fade starts) to 1 (the moment it ends). It is applied
/// the same way whether the volume is rising or falling, so a single instance
/// serves both fade-ins and fade-outs.
/// </para>
/// <para>
/// The built-in curves are <see cref="Linear"/>, <see cref="Exponential"/>,
/// <see cref="Sine"/>, and <see cref="SCurve"/>. Custom shapes are supported:
/// derive from this class and implement <see cref="InterpolateCore"/>. A custom
/// curve works on every platform, including the browser, because the shape is
/// always evaluated by this managed code.
/// </para>
/// <para>
/// Instances must be immutable and safe to share; a single instance is typically
/// reused for the lifetime of the game.
/// </para>
/// </remarks>
public abstract class FadeCurve
{
    // An exponential curve is undefined through zero, so its endpoints are
    // floored to an inaudible epsilon; true silence is reached as a step at the
    // end of the fade, which Interpolate guarantees by returning the target
    // exactly at t >= 1.
    //
    // The floor is what sets the decibel span of a fade to or from silence, and
    // therefore how much of the duration is actually audible. At -80 dB a
    // fade-out passes -30 dB -- effectively gone under a game mix -- barely a
    // third of the way through, so most of the fade is inaudible tail and a
    // two-second fade sounds like it finished in well under one. -60 dB is
    // still far below anything a player can hear over the rest of the mix, but
    // spends noticeably more of the duration in the audible range.
    private const float MinFadeVolume = 0.001f;

    private static readonly FadeCurve s_linear = new LinearFadeCurve();
    private static readonly FadeCurve s_exponential = new ExponentialFadeCurve();
    private static readonly FadeCurve s_sine = new SineFadeCurve();
    private static readonly FadeCurve s_sCurve = new SCurveFadeCurve();

    /// <summary>
    /// A constant-amplitude fade: the volume value changes at a steady rate.
    /// </summary>
    /// <remarks>
    /// Because perceived loudness is logarithmic, a linear amplitude fade sounds
    /// like it holds its level for most of the fade and then falls off a cliff at
    /// the very end. It is included for cases where the mathematical shape matters
    /// more than the perceived one.
    /// </remarks>
    public static FadeCurve Linear => s_linear;

    /// <summary>
    /// A constant-decibel fade, and the default for <see cref="VolumeControl.FadeTo"/>.
    /// </summary>
    /// <remarks>
    /// The volume follows from * (to / from) ^ t, which is a constant number of
    /// decibels per second and is therefore perceived as changing at a steady rate
    /// rather than dropping away abruptly at the end of a fade-out.
    /// </remarks>
    public static FadeCurve Exponential => s_exponential;

    /// <summary>
    /// A sine quarter-wave fade, which moves away from the starting level
    /// immediately and eases gently into the target.
    /// </summary>
    /// <remarks>
    /// A single instance covers both directions: a falling fade through this curve
    /// produces the mirrored (cosine) shape, so no separate curve is needed for
    /// fading out. Two tracks cross-faded with this curve hold a near-constant
    /// combined power -- it is the equal-power cross-fade -- which is what makes it
    /// the usual choice for swapping one track for another.
    /// </remarks>
    public static FadeCurve Sine => s_sine;

    /// <summary>
    /// A symmetric fade that eases at both ends: it leaves the starting level
    /// gradually, moves fastest through the middle, and settles gently onto the
    /// target.
    /// </summary>
    /// <remarks>
    /// The flat start means the fade takes a moment to become audible, which is
    /// what makes it feel unobtrusive for long transitions such as a scene change
    /// or a music swap. For the same reason it is a poor fit for short fades like
    /// ducking, where the delay reads as lag; prefer <see cref="Sine"/> or
    /// <see cref="Exponential"/> there.
    /// </remarks>
    public static FadeCurve SCurve => s_sCurve;

    /// <summary>
    /// Interpolates between two volume levels at normalized fade progress
    /// <paramref name="t"/>.
    /// </summary>
    /// <param name="fromVolume">The volume at the start of the fade.</param>
    /// <param name="toVolume">The volume at the end of the fade.</param>
    /// <param name="t">Fade progress, from 0 (start) to 1 (end).</param>
    /// <remarks>
    /// The endpoints are returned exactly, so a fade always lands precisely on its
    /// target -- including true silence -- regardless of the shape in between.
    /// </remarks>
    [PlatformContract]
    internal float Interpolate(
        float fromVolume,
        float toVolume,
        float t)
    {
        if (t <= 0f)
        {
            return fromVolume;
        }

        if (t >= 1f)
        {
            return toVolume;
        }

        return this.InterpolateCore(fromVolume, toVolume, t);
    }

    /// <summary>
    /// Fills <paramref name="destination"/> with the fade evaluated at evenly
    /// spaced points, the first being <paramref name="fromVolume"/> and the last
    /// being <paramref name="toVolume"/>.
    /// </summary>
    /// <remarks>
    /// This is how a curve reaches platforms that schedule a fade ahead of time
    /// rather than polling it, notably Web Audio's automation timeline. Sampling
    /// here rather than reimplementing shapes per platform is what allows a custom
    /// curve to work everywhere.
    /// </remarks>
    [PlatformContract]
    internal void Sample(
        float fromVolume,
        float toVolume,
        Span<float> destination)
    {
        if (destination.Length == 0)
        {
            return;
        }

        if (destination.Length == 1)
        {
            destination[0] = toVolume;
            return;
        }

        float lastIndex = destination.Length - 1;

        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = this.Interpolate(fromVolume, toVolume, i / lastIndex);
        }
    }

    /// <summary>
    /// Computes the volume at normalized fade progress <paramref name="t"/>.
    /// </summary>
    /// <param name="fromVolume">The volume at the start of the fade.</param>
    /// <param name="toVolume">The volume at the end of the fade.</param>
    /// <param name="t">Fade progress, strictly between 0 and 1 exclusive.</param>
    /// <remarks>
    /// The endpoints are handled by <see cref="Interpolate"/> and are never passed
    /// here, so an implementation only has to describe the shape in between and
    /// need not special-case t = 0 or t = 1.
    /// </remarks>
    protected abstract float InterpolateCore(
        float fromVolume,
        float toVolume,
        float t);

    private sealed class LinearFadeCurve : FadeCurve
    {
        protected override float InterpolateCore(
            float fromVolume,
            float toVolume,
            float t)
        {
            return fromVolume + ((toVolume - fromVolume) * t);
        }
    }

    private sealed class ExponentialFadeCurve : FadeCurve
    {
        protected override float InterpolateCore(
            float fromVolume,
            float toVolume,
            float t)
        {
            var from = MathF.Max(fromVolume, MinFadeVolume);
            var to = MathF.Max(toVolume, MinFadeVolume);

            return from * MathF.Pow(to / from, t);
        }
    }

    private sealed class SineFadeCurve : FadeCurve
    {
        protected override float InterpolateCore(
            float fromVolume,
            float toVolume,
            float t)
        {
            return fromVolume + ((toVolume - fromVolume) * MathF.Sin(t * (MathF.PI / 2f)));
        }
    }

    private sealed class SCurveFadeCurve : FadeCurve
    {
        protected override float InterpolateCore(
            float fromVolume,
            float toVolume,
            float t)
        {
            // Raised cosine, the sigmoid counterpart to the quarter-wave used by
            // Sine: its slope is zero at both ends, so the fade eases in and out
            // rather than only settling onto the target.
            float s = (1f - MathF.Cos(t * MathF.PI)) / 2f;

            return fromVolume + ((toVolume - fromVolume) * s);
        }
    }
}
