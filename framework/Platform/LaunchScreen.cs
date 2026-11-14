// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Globalization;
using DotGame.Framework.Content;
using DotGame.Framework.Platform.Content;

namespace DotGame.Framework.Platform;

/// <summary>
/// The game's launch screen, as its <c>LaunchScreen</c> project item describes it: shown from
/// launch until the game's first frame is on screen, on every platform. The build generates each
/// platform's native form of it (Android's splash screen, iOS's launch storyboard, the web page's
/// <c>#splash</c>, desktop's launch frame), and the color and sizes reach here through a manifest
/// it registers before the game starts.
/// </summary>
/// <remarks>
/// <para>
/// The layout is the same everywhere: the <see cref="Background"/> color, the logo centered,
/// and an optional branding mark centered horizontally near the bottom. Sizes are in
/// device-independent pixels (dp on Android, points on iOS, CSS pixels on the web), and match
/// Android 12's splash screen, the one platform whose layout is fixed: its branding area
/// (<see cref="BrandingAreaSize"/>, <see cref="BrandingAreaBottomMargin"/> above the bottom).
/// </para>
/// <para>
/// A game without the item has no launch screen: <see cref="IsDefined"/> is false and
/// <see cref="Background"/> is black.
/// </para>
/// </remarks>
internal static class LaunchScreen
{
    // NOTE: These are also in what pipeline\tool\Graphics\LaunchScreenCommand.cs generates. A library compiled
    // into the game reads the background through them too, since it can't see this class.
    internal const string ManifestIdentity = "DotGame.LaunchScreen";
    internal const string SettingsKey = "LaunchScreen/settings";
    internal const string BackgroundProperty = "Background";
    internal const string LogoWidthProperty = "LogoWidth";
    internal const string LogoHeightProperty = "LogoHeight";
    internal const string BrandingWidthProperty = "BrandingWidth";
    internal const string BrandingHeightProperty = "BrandingHeight";

    /// <summary>
    /// The area Android 12's splash screen gives its branding image, which the branding fits in.
    /// </summary>
    private static readonly Vector2 BrandingAreaSize = new Vector2(200, 80);

    /// <summary>
    /// How far the branding area's bottom is above the screen's.
    /// </summary>
    private const float BrandingAreaBottomMargin = 60;

    /// <summary>
    /// The content key of the logo texture, on the platforms that draw the launch screen
    /// themselves (desktop).
    /// </summary>
    internal const string LogoContentKey = "LaunchScreen/logo";

    /// <summary>
    /// The content key of the branding texture, as <see cref="LogoContentKey"/>.
    /// </summary>
    internal const string BrandingContentKey = "LaunchScreen/branding";

    private static readonly Settings s_settings = Read(ContentManifest.Registry);

    /// <summary>What the build says about the launch screen.</summary>
    internal readonly record struct Settings(
        bool IsDefined,
        Color Background,
        Vector2 LogoSize,
        Vector2 BrandingSize);

    /// <summary>
    /// Whether the game has a launch screen.
    /// </summary>
    internal static bool IsDefined => s_settings.IsDefined;

    /// <summary>
    /// The launch screen's color; black if there's no launch screen.
    /// </summary>
    internal static Color Background => s_settings.Background;

    /// <summary>
    /// The logo's drawn size, in device-independent pixels.
    /// </summary>
    internal static Vector2 LogoSize => s_settings.LogoSize;

    /// <summary>
    /// The branding's drawn size, in device-independent pixels; zero if there's none.
    /// </summary>
    internal static Vector2 BrandingSize => s_settings.BrandingSize;

    /// <summary>
    /// Reads the launch screen the build registered in <paramref name="registry"/>, if any.
    /// </summary>
    internal static Settings Read(
        ContentManifestRegistry registry)
    {
        if (!registry.TryGet(ManifestIdentity, out var manifest)
            || !manifest.TryGet(SettingsKey, out var entry)
            || !entry.TryGetProperty(BackgroundProperty, out string background))
        {
            return new Settings(false, Color.Black, Vector2.Zero, Vector2.Zero);
        }

        return new Settings(
            true,
            // NOTE: The build checks it and writes it as #rrggbb, so anything else is a bug and throws
            Color.FromRgb(uint.Parse(background.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
            new Vector2(ReadSize(entry, LogoWidthProperty), ReadSize(entry, LogoHeightProperty)),
            new Vector2(ReadSize(entry, BrandingWidthProperty), ReadSize(entry, BrandingHeightProperty)));
    }

    private static float ReadSize(
        ContentManifestEntry entry,
        string property)
    {
        return entry.TryGetProperty(property, out string value) ? float.Parse(value, CultureInfo.InvariantCulture) : 0f;
    }

    /// <summary>
    /// Where the logo and branding go on a screen of <paramref name="screenSize"/>
    /// device-independent pixels: their top-left corners.
    /// </summary>
    internal static (Vector2 Logo, Vector2 Branding) Layout(
        Vector2 screenSize)
    {
        var logo = (screenSize - LogoSize) * 0.5f;

        // The branding is centered in its area
        var brandingCenter = new Vector2(screenSize.X * 0.5f, screenSize.Y - BrandingAreaBottomMargin - (BrandingAreaSize.Y * 0.5f));
        var branding = brandingCenter - (BrandingSize * 0.5f);
        return (logo, branding);
    }
}
