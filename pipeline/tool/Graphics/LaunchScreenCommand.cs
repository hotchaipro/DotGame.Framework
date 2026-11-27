// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace DotGame.Framework.ContentPipeline.BuildTool.Graphics;

/// <summary>
/// Makes a platform's native form of a game's launch screen (its <c>LaunchScreen</c> item:
/// a logo, a color, an optional branding mark), and the code that registers it with the
/// framework (<c>DotGame.Framework.LaunchScreen</c>). See the framework's
/// <c>docs/architecture/launch-screen.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// The layout is Android 12's splash screen, the one platform whose layout is fixed, so every
/// platform matches it: the logo centered, its longer side <c>--logo-size</c> device-independent
/// pixels, which must fit the icon's visible circle; the branding centered in a 200 x 80 area
/// 60 above the bottom. The framework's <c>LaunchScreen</c> has the same constants.
/// </para>
/// <para>
/// <c>launch-screen</c> writes the images and resources: Android's drawables (per density), its
/// splash screen theme (API 31 and later) and window background (earlier); iOS's launch
/// storyboard and its @1x to @3x images; desktop's KTX2 textures; the web's WebP images (lossless,
/// or lossy with <c>--compression Lossy</c> and <c>--lossy-quality</c>). Then
/// <c>launch-page</c> writes the web page and app manifest from their templates, once the
/// images' fingerprinted URLs are known.
/// </para>
/// </remarks>
internal static class LaunchScreenCommand
{
    public const string Name = "launch-screen";
    public const string PageName = "launch-page";
    public const string ErrorCode = "LSC0001";

    // Android 12's splash screen (dp), as the framework's LaunchScreen has them
    private const float IconAreaSize = 288;
    private const float IconVisibleDiameter = 192;
    private const float BrandingAreaWidth = 200;
    private const float BrandingAreaHeight = 80;
    private const float BrandingAreaBottomMargin = 60;

    // The scale the desktop and web images are made at: sharp up to three device pixels a dp
    private const int ImageScale = 3;

    private static readonly (string Folder, float Scale)[] AndroidDensities =
    [
        ("drawable-mdpi", 1f),
        ("drawable-hdpi", 1.5f),
        ("drawable-xhdpi", 2f),
        ("drawable-xxhdpi", 3f),
        ("drawable-xxxhdpi", 4f),
    ];

    public static void Run(CommandLineOptions options)
    {
        string platform = options.GetChoice("platform", null, "android", "ios", "desktop", "webgl");
        var screen = Description.Read(options);
        string outputDir = options.GetPath("output-dir");
        string? csOutput = options.GetString("cs-output") is { Length: > 0 } cs ? ContentPaths.Normalize(cs) : null;
        string themeParent = options.GetString("android-theme-parent", "@android:style/Theme");

        // The web's images: lossless by default, or lossy at a quality of 1 to 10 (as textures)
        string compression = options.GetChoice("compression", "Lossless", "Lossless", "Lossy");
        int lossyQuality = options.GetInt32("lossy-quality", 8, 1, 10);
        options.ThrowIfUnrecognized();

        Directory.CreateDirectory(outputDir);
        switch (platform)
        {
            case "android":
                WriteAndroid(screen, outputDir, themeParent);
                break;

            case "ios":
                WriteIos(screen, outputDir);
                break;

            case "desktop":
                WriteKtx2(screen.Logo, screen.LogoSize, Path.Combine(outputDir, "logo.ktx2"));
                if (screen.Branding is not null)
                {
                    WriteKtx2(screen.Branding, screen.BrandingSize, Path.Combine(outputDir, "branding.ktx2"));
                }
                break;

            case "webgl":
                WriteWebP(screen.Logo, screen.LogoSize, Path.Combine(outputDir, "logo.webp"), compression, lossyQuality);
                if (screen.Branding is not null)
                {
                    WriteWebP(screen.Branding, screen.BrandingSize, Path.Combine(outputDir, "branding.webp"), compression, lossyQuality);
                }
                break;
        }

        if (csOutput is not null)
        {
            WriteRegistration(screen, csOutput);
        }
    }

    /// <summary>
    /// <c>launch-page</c>: the web page and app manifest, from their templates.
    /// </summary>
    public static void RunPage(CommandLineOptions options)
    {
        var screen = Description.Read(options);
        string pageTemplate = options.GetPath("page-template");
        string pageOutput = options.GetPath("page-output");
        string logoUrl = options.GetRequired("logo-url");
        string? brandingUrl = options.GetString("branding-url");
        string? manifestTemplate = options.GetString("manifest-template") is { Length: > 0 } m ? ContentPaths.Normalize(m) : null;
        string? manifestOutput = (manifestTemplate is null) ? null : options.GetPath("manifest-output");
        options.ThrowIfUnrecognized();

        if ((screen.Branding is not null) && string.IsNullOrEmpty(brandingUrl))
        {
            throw new ContentBuildException("The launch screen has branding, but no '--branding-url' was given.");
        }

        WritePage(screen, pageTemplate, pageOutput, logoUrl, brandingUrl);
        if (manifestTemplate is not null)
        {
            WriteManifest(screen, manifestTemplate, manifestOutput!);
        }
    }

    // ── The description ───────────────────────────────────────────────

    // The item, with the sizes the images are drawn at (dp)
    private sealed record Description(
        RgbaImage Logo,
        (float Width, float Height) LogoSize,
        RgbaImage? Branding,
        (float Width, float Height) BrandingSize,
        byte Red,
        byte Green,
        byte Blue)
    {
        public string BackgroundHex => $"#{this.Red:x2}{this.Green:x2}{this.Blue:x2}";

        public static Description Read(CommandLineOptions options)
        {
            string logoFile = options.GetPath("logo");
            string? brandingFile = options.GetString("branding") is { Length: > 0 } b ? ContentPaths.Normalize(b) : null;
            string background = options.GetRequired("background");
            int logoSize = options.GetInt32("logo-size", 160, 16, 1024);
            int brandingWidth = options.GetInt32("branding-width", 0, 0, (int)BrandingAreaWidth);

            var (r, g, bl) = ParseColor(background);
            var logo = Load(logoFile);

            // The logo's longer side is LogoSize
            float logoScale = logoSize / (float)Math.Max(logo.Width, logo.Height);
            var logoDrawn = (logo.Width * logoScale, logo.Height * logoScale);
            float reach = OpaqueReach(logo) * logoScale;
            if (reach > (IconVisibleDiameter / 2) + 0.5f)
            {
                ContentBuildOutput.Warning(logoFile, "LSC0002", $"The launch screen logo, {logoDrawn.Item1:0} x {logoDrawn.Item2:0} dp, reaches {reach:0} dp from its center, outside the {IconVisibleDiameter:0} dp circle Android 12's splash screen shows of its icon, so it's cut off there. Lower LogoSize to {MathF.Floor(logoSize * IconVisibleDiameter / 2 / reach):0} or less.");
            }

            RgbaImage? branding = null;
            (float, float) brandingDrawn = (0, 0);
            if (brandingFile is not null)
            {
                branding = Load(brandingFile);

                // Fit the branding area, or BrandingWidth wide (within it)
                float fit = Math.Min(BrandingAreaWidth / branding.Width, BrandingAreaHeight / branding.Height);
                if (brandingWidth > 0)
                {
                    fit = Math.Min(fit, brandingWidth / (float)branding.Width);
                }

                brandingDrawn = (branding.Width * fit, branding.Height * fit);
            }

            return new Description(logo, logoDrawn, branding, brandingDrawn, r, g, bl);
        }

        private static RgbaImage Load(string file)
        {
            if (!File.Exists(file))
            {
                throw new ContentBuildException($"Launch screen image not found: '{file}'");
            }

            return RgbaImage.Load(file);
        }

        private static (byte R, byte G, byte B) ParseColor(string text)
        {
            string hex = text.Trim().TrimStart('#');
            if ((hex.Length == 6) && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            {
                return ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            }

            throw new ContentBuildException($"The launch screen's Background must be a color like '#5ba7dd' (was '{text}').");
        }
    }

    // An image at a scale of its drawn size, rounded to whole pixels
    private static RgbaImage Scaled(RgbaImage image, (float Width, float Height) size, float scale)
    {
        int width = Math.Max(1, (int)MathF.Round(size.Width * scale));
        int height = Math.Max(1, (int)MathF.Round(size.Height * scale));
        return ImageResampler.Resize(image, width, height);
    }

    // How far the image's farthest visible pixel is from its center, in pixels: what the
    // splash screen's circle has to hold (transparent corners don't count)
    private static float OpaqueReach(RgbaImage image)
    {
        const byte Visible = 16;
        float centerX = image.Width / 2f;
        float centerY = image.Height / 2f;
        float farthest = 0;
        byte[] pixels = image.Pixels;
        for (int y = 0; y < image.Height; y++)
        {
            int row = y * image.Stride;
            float dy = MathF.Max(MathF.Abs(y - centerY), MathF.Abs(y + 1 - centerY));
            for (int x = 0; x < image.Width; x++)
            {
                if (pixels[row + (x * 4) + 3] >= Visible)
                {
                    float dx = MathF.Max(MathF.Abs(x - centerX), MathF.Abs(x + 1 - centerX));
                    farthest = MathF.Max(farthest, (dx * dx) + (dy * dy));
                }
            }
        }

        return MathF.Sqrt(farthest);
    }

    private static string Dp(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    // An inset drawable's insets that center an image of the given size in an area, as fractions
    // of the area
    private static string Insets(
        (float Width, float Height) size,
        float areaWidth,
        float areaHeight)
    {
        string horizontal = Percent((areaWidth - size.Width) / 2 / areaWidth);
        string vertical = Percent((areaHeight - size.Height) / 2 / areaHeight);
        return $"android:insetLeft=\"{horizontal}\" android:insetRight=\"{horizontal}\" android:insetTop=\"{vertical}\" android:insetBottom=\"{vertical}\"";
    }

    private static string Percent(float fraction) => (fraction * 100).ToString("0.###", CultureInfo.InvariantCulture) + "%";

    private static void WriteText(string path, string text)
    {
        ContentPaths.CreateParentDirectory(path);
        ContentPaths.RetryWhileLocked(() => File.WriteAllText(path, text, new UTF8Encoding(false)), $"Writing '{path}'");
    }

    private static void WritePng(RgbaImage image, string path)
    {
        ContentPaths.CreateParentDirectory(path);
        ContentPaths.RetryWhileLocked(() => image.SavePng(path), $"Writing '{path}'");
    }

    // ── Android ───────────────────────────────────────────────────────

    // Resources under res/: the logo and branding per density; the icon (the logo centered in
    // Android 12's icon area) and the branding area for the splash screen; the window background
    // (color, logo, branding) for Android 11 and earlier, which the engine also shows over the
    // game's surface until its first frame; and the theme that shows it all: the splash screen's
    // attributes from API 31, the window background before.
    private static void WriteAndroid(Description screen, string resDir, string themeParent)
    {
        foreach (var (folder, scale) in AndroidDensities)
        {
            WritePng(Scaled(screen.Logo, screen.LogoSize, scale), Path.Combine(resDir, folder, "dotgame_launch_logo.png"));
            if (screen.Branding is not null)
            {
                WritePng(Scaled(screen.Branding, screen.BrandingSize, scale), Path.Combine(resDir, folder, "dotgame_launch_branding.png"));
            }
        }

        const string Header = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!-- Generated from the game's LaunchScreen item by the content pipeline. Don't edit. -->\n";
        const string Ns = "xmlns:android=\"http://schemas.android.com/apk/res/android\"";

        // The splash screen draws its icon and branding at sizes of its own (the icon in 108 dp
        // bounds, then scaled), so they place their image by fractions of their area, which scale
        // with it. Only the API 31 theme uses them.
        WriteText(Path.Combine(resDir, "drawable", "dotgame_launch_icon.xml"), Header + $"""
            <inset {Ns}
                android:drawable="@drawable/dotgame_launch_logo"
                {Insets(screen.LogoSize, IconAreaSize, IconAreaSize)} />

            """);

        string brandingItem = string.Empty;
        string brandingAttribute = string.Empty;
        if (screen.Branding is not null)
        {
            WriteText(Path.Combine(resDir, "drawable", "dotgame_launch_branding_area.xml"), Header + $"""
                <inset {Ns}
                    android:drawable="@drawable/dotgame_launch_branding"
                    {Insets(screen.BrandingSize, BrandingAreaWidth, BrandingAreaHeight)} />

                """);
            float brandingBottom = BrandingAreaBottomMargin + ((BrandingAreaHeight - screen.BrandingSize.Height) / 2);
            brandingItem = $"""
                    <item android:width="{Dp(screen.BrandingSize.Width)}dp" android:height="{Dp(screen.BrandingSize.Height)}dp" android:gravity="bottom|center_horizontal" android:bottom="{Dp(brandingBottom)}dp" android:drawable="@drawable/dotgame_launch_branding" />

                """;
            brandingAttribute = "        <item name=\"android:windowSplashScreenBrandingImage\">@drawable/dotgame_launch_branding_area</item>\n";
        }

        WriteText(Path.Combine(resDir, "drawable", "dotgame_launch_screen.xml"), Header + $"""
            <layer-list {Ns}>
                <item android:drawable="@color/dotgame_launch_background" />
                <item android:width="{Dp(screen.LogoSize.Width)}dp" android:height="{Dp(screen.LogoSize.Height)}dp" android:gravity="center" android:drawable="@drawable/dotgame_launch_logo" />
            {brandingItem}</layer-list>

            """);

        string parent = SecurityElement.Escape(themeParent);
        WriteText(Path.Combine(resDir, "values", "dotgame_launch_screen.xml"), Header + $"""
            <resources>
                <color name="dotgame_launch_background">{screen.BackgroundHex}</color>
                <!-- The game's activity theme inherits this: the launch screen as its window background. -->
                <style name="DotGame.LaunchScreen" parent="{parent}">
                    <item name="android:windowBackground">@drawable/dotgame_launch_screen</item>
                </style>
            </resources>

            """);

        // From Android 12 the system's splash screen shows it, held until the first frame
        WriteText(Path.Combine(resDir, "values-v31", "dotgame_launch_screen.xml"), Header + $"""
            <resources>
                <style name="DotGame.LaunchScreen" parent="{parent}">
                    <item name="android:windowBackground">@color/dotgame_launch_background</item>
                    <item name="android:windowSplashScreenBackground">@color/dotgame_launch_background</item>
                    <item name="android:windowSplashScreenAnimatedIcon">@drawable/dotgame_launch_icon</item>
            {brandingAttribute}    </style>
            </resources>

            """);
    }

    // ── iOS ───────────────────────────────────────────────────────────

    // LaunchScreen.storyboard, and its images at @1x, @2x and @3x.
    //
    // The images' names change as they do: iOS keeps a launch screen's images by name, so an
    // update with the same names showed its new color with the old images, even after the
    // device restarted.
    private static void WriteIos(Description screen, string outputDir)
    {
        string logoName = IosImageName("dotgame_launch_logo", screen.Logo, screen.LogoSize);
        string brandingName = (screen.Branding is null)
            ? string.Empty
            : IosImageName("dotgame_launch_branding", screen.Branding, screen.BrandingSize);

        foreach (int scale in new[] { 1, 2, 3 })
        {
            string suffix = (scale == 1) ? string.Empty : $"@{scale}x";
            WritePng(Scaled(screen.Logo, screen.LogoSize, scale), Path.Combine(outputDir, $"{logoName}{suffix}.png"));
            if (screen.Branding is not null)
            {
                WritePng(Scaled(screen.Branding, screen.BrandingSize, scale), Path.Combine(outputDir, $"{brandingName}{suffix}.png"));
            }
        }

        string Component(byte value) => (value / 255.0).ToString("0.######", CultureInfo.InvariantCulture);
        float brandingBottom = BrandingAreaBottomMargin + ((BrandingAreaHeight - screen.BrandingSize.Height) * 0.5f);

        var subviews = new StringBuilder();
        var constraints = new StringBuilder();
        var resources = new StringBuilder();

        // A view's own size constraints go in its element; its position, in its superview's
        subviews.Append($"""
                                    <imageView userInteractionEnabled="NO" contentMode="scaleAspectFit" image="{logoName}" translatesAutoresizingMaskIntoConstraints="NO" id="dgL-ogo-Img">
                                        <rect key="frame" x="0.0" y="0.0" width="{Dp(screen.LogoSize.Width)}" height="{Dp(screen.LogoSize.Height)}"/>
                                        <constraints>
                                            <constraint firstAttribute="width" constant="{Dp(screen.LogoSize.Width)}" id="dgL-ogo-Wid"/>
                                            <constraint firstAttribute="height" constant="{Dp(screen.LogoSize.Height)}" id="dgL-ogo-Hgt"/>
                                        </constraints>
                                    </imageView>

            """);
        constraints.Append("""
                                    <constraint firstItem="dgL-ogo-Img" firstAttribute="centerX" secondItem="dgV-iew-Rot" secondAttribute="centerX" id="dgL-ogo-CnX"/>
                                    <constraint firstItem="dgL-ogo-Img" firstAttribute="centerY" secondItem="dgV-iew-Rot" secondAttribute="centerY" id="dgL-ogo-CnY"/>

            """);
        resources.Append($"""
                    <image name="{logoName}" width="{Dp(screen.LogoSize.Width)}" height="{Dp(screen.LogoSize.Height)}"/>

            """);

        if (screen.Branding is not null)
        {
            subviews.Append($"""
                                        <imageView userInteractionEnabled="NO" contentMode="scaleAspectFit" image="{brandingName}" translatesAutoresizingMaskIntoConstraints="NO" id="dgB-rnd-Img">
                                            <rect key="frame" x="0.0" y="0.0" width="{Dp(screen.BrandingSize.Width)}" height="{Dp(screen.BrandingSize.Height)}"/>
                                            <constraints>
                                                <constraint firstAttribute="width" constant="{Dp(screen.BrandingSize.Width)}" id="dgB-rnd-Wid"/>
                                                <constraint firstAttribute="height" constant="{Dp(screen.BrandingSize.Height)}" id="dgB-rnd-Hgt"/>
                                            </constraints>
                                        </imageView>

                """);
            constraints.Append($"""
                                        <constraint firstItem="dgB-rnd-Img" firstAttribute="centerX" secondItem="dgV-iew-Rot" secondAttribute="centerX" id="dgB-rnd-CnX"/>
                                        <constraint firstItem="dgV-iew-Rot" firstAttribute="bottom" secondItem="dgB-rnd-Img" secondAttribute="bottom" constant="{Dp(brandingBottom)}" id="dgB-rnd-Btm"/>

                """);
            resources.Append($"""
                        <image name="{brandingName}" width="{Dp(screen.BrandingSize.Width)}" height="{Dp(screen.BrandingSize.Height)}"/>

                """);
        }

        WriteText(Path.Combine(outputDir, "LaunchScreen.storyboard"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!-- Generated from the game's LaunchScreen item by the content pipeline. Don't edit. -->
            <document type="com.apple.InterfaceBuilder3.CocoaTouch.Storyboard.XIB" version="3.0" toolsVersion="22155" targetRuntime="iOS.CocoaTouch" propertyAccessControl="none" useAutolayout="YES" launchScreen="YES" useTraitCollections="YES" useSafeAreas="YES" colorMatched="YES" initialViewController="dgV-iew-Ctl">
                <device id="retina6_12" orientation="portrait" appearance="light"/>
                <dependencies>
                    <deployment identifier="iOS"/>
                    <plugIn identifier="com.apple.InterfaceBuilder.IBCocoaTouchPlugin" version="22131"/>
                    <capability name="documents saved in the Xcode 8 format" minToolsVersion="8.0"/>
                </dependencies>
                <scenes>
                    <scene sceneID="dgS-cen-Lch">
                        <objects>
                            <viewController id="dgV-iew-Ctl" sceneMemberID="viewController">
                                <view key="view" contentMode="scaleToFill" id="dgV-iew-Rot">
                                    <rect key="frame" x="0.0" y="0.0" width="393" height="852"/>
                                    <autoresizingMask key="autoresizingMask" widthSizable="YES" heightSizable="YES"/>
                                    <subviews>
            {subviews}                        </subviews>
                                    <color key="backgroundColor" red="{Component(screen.Red)}" green="{Component(screen.Green)}" blue="{Component(screen.Blue)}" alpha="1" colorSpace="custom" customColorSpace="sRGB"/>
                                    <constraints>
            {constraints}                        </constraints>
                                </view>
                            </viewController>
                            <placeholder placeholderIdentifier="IBFirstResponder" id="dgF-rst-Rsp" userLabel="First Responder" customClass="UIResponder" sceneMemberID="firstResponder"/>
                        </objects>
                        <point key="canvasLocation" x="0" y="0"/>
                    </scene>
                </scenes>
                <resources>
            {resources}    </resources>
            </document>

            """);
    }

    // A name with a hash of what the image shows: its pixels and the size it's drawn at
    private static string IosImageName(string name, RgbaImage image, (float Width, float Height) size)
    {
        var scaled = Scaled(image, size, ImageScale);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(scaled.Width));
        hash.AppendData(BitConverter.GetBytes(scaled.Height));
        hash.AppendData(scaled.Pixels);
        return $"{name}_{Convert.ToHexString(hash.GetHashAndReset(), 0, 4).ToLowerInvariant()}";
    }

    // ── Desktop and web ───────────────────────────────────────────────

    private static void WriteKtx2(RgbaImage image, (float Width, float Height) size, string path)
    {
        var scaled = Scaled(image, size, ImageScale);
        scaled.PremultiplyAlpha();
        ContentPaths.CreateParentDirectory(path);
        ContentPaths.RetryWhileLocked(() => Ktx2Format.WriteRgba8(path, scaled, true), $"Writing '{path}'");
    }

    private static void WriteWebP(RgbaImage image, (float Width, float Height) size, string path, string compression, int lossyQuality)
    {
        // Straight alpha: the page draws it as an image, not a texture. Lossy keeps the alpha
        // lossless, so the logo's edge stays as drawn.
        var scaled = Scaled(image, size, ImageScale);
        ContentPaths.CreateParentDirectory(path);
        TextureCommand.EncodeWebP(scaled, path, compression, (compression == "Lossy") ? lossyQuality : 10);
    }

    // The page: the template's two placeholders filled in. '<!-- launch-screen:head -->' goes at
    // the start of <head>, so the color is in the page's first bytes; '<!-- launch-screen:body -->'
    // at the start of <body>. The engine fades #splash out once the first frame is on screen.
    private static void WritePage(Description screen, string template, string output, string logoUrl, string? brandingUrl)
    {
        const string HeadPlaceholder = "<!-- launch-screen:head -->";
        const string BodyPlaceholder = "<!-- launch-screen:body -->";

        string page = ContentPaths.RetryWhileLocked(() => File.ReadAllText(template), $"Reading '{template}'");
        if (!page.Contains(HeadPlaceholder, StringComparison.Ordinal) || !page.Contains(BodyPlaceholder, StringComparison.Ordinal))
        {
            throw new ContentBuildException($"The page '{template}' needs the launch screen's placeholders: '{HeadPlaceholder}' at the start of <head>, and '{BodyPlaceholder}' at the start of <body>.");
        }

        string color = screen.BackgroundHex;
        float brandingBottom = BrandingAreaBottomMargin + ((BrandingAreaHeight - screen.BrandingSize.Height) * 0.5f);
        string brandingStyle = (screen.Branding is null) ? string.Empty : $$"""
            		#splash .branding {
            			bottom: {{Dp(brandingBottom)}}px;
            			width: {{Dp(screen.BrandingSize.Width)}}px;
            			height: {{Dp(screen.BrandingSize.Height)}}px;
            		}

            """;

        string head = $$"""
            <!-- The launch screen, from the game's LaunchScreen item (generated) -->
            	<meta name="theme-color" content="{{color}}">
            	<link rel="preload" as="image" href="{{logoUrl}}" fetchpriority="high">
            	<style>
            		:root {
            			--launch-background: {{color}};
            		}
            		html, body {
            			background-color: var(--launch-background);
            		}
            		#splash {
            			position: fixed;
            			inset: 0;
            			background: var(--launch-background);
            			z-index: 1000;
            		}
            		#splash img {
            			position: absolute;
            			left: 50%;
            			transform: translateX(-50%);
            			/* Hidden while the viewport settles (an installed app's browser chrome can move
            			   the layout just after load), then shown */
            			opacity: 0;
            			transition: opacity 0.125s ease-out;
            		}
            		#splash img.visible {
            			opacity: 1;
            		}
            		#splash .logo {
            			top: 50%;
            			width: {{Dp(screen.LogoSize.Width)}}px;
            			height: {{Dp(screen.LogoSize.Height)}}px;
            			margin-top: -{{Dp(screen.LogoSize.Height * 0.5f)}}px;
            		}
            {{brandingStyle}}	</style>
            """;

        string brandingImage = (screen.Branding is null) ? string.Empty : $"\n\t\t<img class=\"branding\" src=\"{brandingUrl}\" alt=\"\" />";
        string body = $$"""
            <!-- The launch screen (generated); the engine removes it once the game's first frame is on screen -->
            	<div id="splash">
            		<img class="logo" src="{{logoUrl}}" alt="" />{{brandingImage}}
            	</div>
            	<script>
            		requestAnimationFrame(function () {
            			requestAnimationFrame(function () {
            				document.querySelectorAll('#splash img').forEach(function (image) {
            					image.classList.add('visible');
            				});
            			});
            		});
            	</script>
            """;

        page = page.Replace(HeadPlaceholder, head, StringComparison.Ordinal).Replace(BodyPlaceholder, body, StringComparison.Ordinal);
        WriteText(output, page);
    }

    // The app manifest, with the launch color as an installed app's splash and theme colors
    private static void WriteManifest(Description screen, string template, string output)
    {
        string text = ContentPaths.RetryWhileLocked(() => File.ReadAllText(template), $"Reading '{template}'");
        var manifest = JsonNode.Parse(text) as JsonObject
            ?? throw new ContentBuildException($"The app manifest '{template}' isn't a JSON object.");
        manifest["background_color"] = screen.BackgroundHex;
        manifest["theme_color"] = screen.BackgroundHex;
        WriteText(output, manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    // ── The registration ──────────────────────────────────────────────

    private static void WriteRegistration(Description screen, string output)
    {
        string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        WriteText(output, $$"""
            // <auto-generated/>
            // Generated from the game's LaunchScreen item by the content pipeline.
            #nullable enable

            using System.Runtime.CompilerServices;

            namespace DotGame.Framework.Generated;

            internal static class LaunchScreenRegistration
            {
                [ModuleInitializer]
                internal static void Register()
                {
                    var manifest = new global::DotGame.Framework.Content.ContentManifest("DotGame.LaunchScreen");
                    manifest.Add(
                        "LaunchScreen/settings",
                        "LaunchScreen/settings",
                        properties: new global::System.Collections.Generic.KeyValuePair<string, string>[]
                        {
                            new("Background", "{{screen.BackgroundHex}}"),
                            new("LogoWidth", "{{F(screen.LogoSize.Width)}}"),
                            new("LogoHeight", "{{F(screen.LogoSize.Height)}}"),
                            new("BrandingWidth", "{{F(screen.BrandingSize.Width)}}"),
                            new("BrandingHeight", "{{F(screen.BrandingSize.Height)}}"),
                        });
                    global::DotGame.Framework.Content.ContentManifest.Register(manifest);
                }
            }

            """);
    }
}
