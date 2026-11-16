# Launch screen

From the moment a player launches a game until its first frame is on screen, every platform shows the
same launch screen: the game's background color, its logo in the middle and, optionally, a branding
mark near the bottom. Nothing blank, black or white shows first, the image doesn't jump or change on
the way, and the launch screen gives way to the first frame with the same fade everywhere.

A game describes its launch screen once, in its project. The content pipeline makes each platform's
native form of it, and each platform's host shows it and dismisses it, so a game writes no launch code.
How to add one is in the [platforms guide](../guide/platforms.md#the-launch-screen).

## The description

One `LaunchScreen` item, in the project that builds the game's content:

```xml
<ItemGroup>
  <LaunchScreen Include="Content\LaunchScreen\logo.png"
                Background="#5ba7dd"
                LogoSize="160"
                Branding="Content\LaunchScreen\publisher.png"
                BrandingWidth="120" />
</ItemGroup>
```

| Metadata | Meaning |
|---|---|
| `Include` | The logo, an image with transparency, drawn centered |
| `Background` | The launch color, required |
| `LogoSize` | The logo's longer side in device-independent pixels; 160 by default |
| `Branding` | Optional: a mark centered in a 200 x 80 area whose bottom is 60 above the screen's |
| `BrandingWidth` | Optional: the branding's width, within that area; by default as large as fits |

Sizes are device-independent pixels everywhere: dp on Android, points on iOS, CSS pixels on the web,
logical pixels on the desktop. The layout is Android 12's splash screen, the one platform whose layout
is fixed (its icon is a fixed size in a fixed place, of which it shows only a 192 dp circle, and its
branding gets a fixed area), and every other platform places the logo and branding to match it. The
Android build warns (`LSC0002`) when the logo's visible pixels reach outside the circle, with the
largest `LogoSize` that fits; the other platforms show the whole logo, so a wide logo can be larger
there, with a smaller `LogoSize` in the Android head's item. A game has at most one item; a second is a build error, as are a missing logo or
branding file and a missing `Background`.

## The build

[`pipeline/ContentPipeline.targets`](../../pipeline/ContentPipeline.targets) validates the item
(`_PrepareLaunchScreen`) and runs the build tool's `launch-screen` command (`BuildLaunchScreen`) for the
platform each platform's targets name in `LaunchScreenPlatform`. The command,
[`pipeline/tool/Graphics/LaunchScreenCommand.cs`](../../pipeline/tool/Graphics/LaunchScreenCommand.cs),
reads the images, scales them to their drawn sizes and writes:

| Platform | What the build makes | Registered by |
|---|---|---|
| Android | The logo and branding as PNGs for each density (mdpi to xxxhdpi). A `layer-list` drawable, `dotgame_launch_screen` (the color, the centered logo, the branding). A `DotGame.LaunchScreen` theme whose `windowBackground` is that drawable, and a `values-v31` version that sets `windowSplashScreenBackground`, `windowSplashScreenAnimatedIcon` and `windowSplashScreenBrandingImage`. | `_RegisterLaunchScreenAndroidResources`, as `AndroidResource` items. The game's activity uses `@style/DotGame.LaunchScreen`; its parent is `$(LaunchScreenAndroidThemeParent)` |
| iOS | `LaunchScreen.storyboard` (the color, the logo with size constraints, centered, and the branding at the bottom) and its images at @1x, @2x and @3x | `_RegisterLaunchScreenBundleResources`, as the app's launch storyboard (`UILaunchStoryboardName`) |
| Desktop | The logo and branding as KTX2 textures, at three pixels per device-independent pixel | Content, under `LaunchScreen/` |
| Web | The logo and branding as WebP images, at three pixels per device-independent pixel | Content, under `LaunchScreen/`; then the page (below) |

On Android 12 and later the system lays the splash icon out in 108 dp bounds and scales it, so the
icon and the branding are `inset` drawables that place their image by fractions of their area, which
scale with it. The animated-icon attribute takes a still image.

On the web, a second command, `launch-page`, runs once the images' fingerprinted URLs are known
(`_GenerateLaunchScreenPage` in
[`pipeline/ContentPipeline.webgl.targets`](../../pipeline/ContentPipeline.webgl.targets)). It fills the
page template's two placeholders: `<!-- launch-screen:head -->`, at the start of `<head>`, with the
`theme-color`, a high-priority preload of the logo and the launch screen's styles, so the color is in
the page's first bytes; and `<!-- launch-screen:body -->`, at the start of `<body>`, with the `#splash`
element. The logo stays transparent for two animation frames, while an installed app's browser chrome
can still move the layout, then fades in over 125 ms. It also sets the app manifest's
`background_color` and `theme_color` to the launch color, so an installed app's browser splash is the
same color.

Every platform also gets a generated `LaunchScreen.g.cs`: a module initializer that registers a
`DotGame.LaunchScreen` content manifest with the color and the drawn sizes. The framework's internal
[`LaunchScreen`](../../framework/Platform/LaunchScreen.cs) reads it (`IsDefined`, `Background`,
`LogoSize`, `BrandingSize`) and computes the same layout as the build (`Layout`), for the hosts that
draw the launch screen themselves. A game without the item has no launch screen: `IsDefined` is false
and `Background` is black.

## Showing and dismissing it

Each platform shows something before any of the game's code runs: Android's splash screen or window
background, iOS's launch storyboard, the web page. The launch screen is drawn in that form, so it's the
first thing on screen. It stays up until the game's first frame has been **presented**, not merely
started or drawn, and then fades out over `GameHost.LaunchScreenFadeDuration` (200 ms) on the curve
`GameHost.LaunchScreenFadeEase`, the cubic Bézier `(0.3, 0, 0.8, 0.15)`, Material 3's emphasized
accelerate. Each platform uses that curve as its own timing function.

### The strategy

The run-time half is split in two, so a host knows when the game is ready and nothing about how its
launch screen was made:

- **`GameHost`** owns the sequence. A host gives it the platform's launch screen once, before startup
  (`ShowLaunchScreen`), and reports the first frame on screen, from whatever thread knows it
  (`NotifyFirstFramePresented`). Only the first report counts, and it dismisses the launch screen.
- **`ILaunchScreenStrategy`** ([framework/Platform/ILaunchScreenStrategy.cs](../../framework/Platform/ILaunchScreenStrategy.cs)),
  internal, is each platform's launch screen: `Show` puts it up, or keeps the one the system showed
  there, and `Dismiss` fades it out and removes it, moving to the thread it needs. Each platform's
  `LaunchScreenStrategy` is the only code that knows the names the build gives the native forms (the
  drawable, the storyboard key, `#splash`, the launch textures' content keys), and it records the
  startup timeline's `launch-shown` and `first-frame-presented` marks where its platform can tell.

| Platform | Shown | First frame presented (reported by the host) | Dismissed (by the strategy) |
|---|---|---|---|
| Android 12 and later | The system's splash screen, from the theme | After the first drawn frame's `eglSwapBuffers` (`RenderThread`) | A pre-draw listener on the content view (`SplashScreenHold`) holds the splash screen on screen; releasing it on the UI thread lets the window draw, and the splash screen's exit animation (`SplashScreenFade`) fades it with a `PathInterpolator` |
| Android 11 and earlier | The theme's window background, then the same drawable as the surface view's background | As above | The surface view's background fades out |
| iOS | The launch storyboard, then the same storyboard as an overlay on the game view, added in the run-loop turn the window appears | The first drawn frame's drawable `AddPresentedHandler` (on the simulator, whose drawables don't support it, the command buffer completing) | A `UIViewPropertyAnimator` with `UICubicTimingParameters` fades the overlay out |
| Desktop | A launch frame drawn before the window is shown and again once it is | After the first drawn frame's `SubmitFrame` | The launch frame is drawn over the game's frames, with decreasing opacity from then |
| Web | The page's `#splash` | The first drawn frame: the page waits for the browser to composite it | `game.js` waits an animation frame, since the browser composites a WebGL frame once its callback returns, then raises `dotgame:presented` and fades `#splash` out with a CSS transition |

The details per platform:

- **Android** ([`LaunchScreenStrategy`](../../platform/android/LaunchScreenStrategy.cs)). The game's
  surface is a separate surface, which draws while the content view is held back. An undrawn window has
  no input focus, and a key pressed then (Back during a slow start) becomes an ANR after five seconds, so
  the hold ends after three seconds at most: the launch screen drawable then becomes the surface view's
  background, as on Android 11 and earlier, and stays until the first frame.
- **iOS** ([`LaunchScreenStrategy`](../../platform/ios/LaunchScreenStrategy.cs)). Until the game has
  drawn, each frame [`iOSGameEngine`](../../platform/ios/iOSGameEngine.cs) runs clears the view to
  `LaunchScreen.Background` under the overlay, so nothing else can show through.
- **Desktop** ([`LaunchScreenStrategy`](../../platform/desktop/LaunchScreenStrategy.cs)). Desktop has no
  launch screen of the system's, so the strategy draws one. The window is created hidden; the host
  creates the GPU device, loads the launch textures, presents a launch frame (`PresentFrame`), shows
  the window, and shows the launch screen, which presents another. A backend that presents to a hidden
  window shows the launch screen from the first visible frame; one that doesn't leaves at most one
  frame unpainted. Each frame the host draws it: alone (`Draw`) until the game draws, then over the
  game's frames (`DrawOverGame`), opaque until it's dismissed and fading from then. It's drawn once into
  a texture the window's size (again when the size changes), with the sprite batch, in logical pixels,
  and each frame draws that texture, so the launch screen fades as one image: drawing its layers each
  at the fade's opacity would show the game through the logo less than through the color around it. A
  window-exposed event runs a frame too, so the launch screen stays correct while the window is resized.
- **Web** ([`LaunchScreenStrategy`](../../platform/webgl/LaunchScreenStrategy.cs),
  [`game.js`](../../platform/webgl/wwwroot/_dotgame/game.js)). The page shows `#splash` itself, so
  `Show` does nothing, and `main.js` records `launch-shown`. Only the page knows when a frame is
  composited, so the host reports the first drawn frame, and `Dismiss` calls `firstFrameDrawn` with the
  duration and `GameHost.LaunchScreenFadeCssEase`; the page records `first-frame-presented`. The
  splash is removed when its transition ends, or by a timer if the transition never runs (a hidden tab).
  `dotgame:started`, raised by `main.js` when the game has started, stays for pages that want that
  moment instead.

The two startup timeline marks, `launch-shown` and `first-frame-presented`, let the time to the first
frame, and any gap, be compared across platforms and builds.

## After the launch screen

Once the launch screen has gone, whatever the game's drawing doesn't cover shows the color the game
clears its window to, never the launch color. `GraphicsService` records the color of each frame's first
clear of the window (`WindowBackground`, internal, exposed to hosts as `GameHost.WindowBackground`).

On Android, iOS and the desktop the game's surface fills the window, safe areas included, so those areas
show the clear color by themselves. A browser can keep the page out of parts of the screen (Chrome on iOS
leaves the side safe areas out in landscape, and overscroll shows past the page), and those show the
page's background. So the web host passes `WindowBackground` to the page whenever it changes
(`setWindowBackground`), and when the launch screen goes the page's `html` and `body` background changes
from the launch color to it, with the launch screen's fade. Until then the page keeps the launch color
there, so the strips match the launch screen while it shows. The browser's own chrome (`theme-color`)
keeps the launch color.

A game that wants no visible change between the launch screen and its first scene clears to the launch
color.

## Why

- **One description.** When every platform has its own copies of the art, its own colors and its own
  show-and-hide code, they drift apart: a slightly different blue, a different logo size, a different
  moment of dismissal. A single item makes that impossible, and the hosts' code serves every game.
- **The platform's launch surface first.** Whatever a platform shows before the game's code runs is
  what the player sees first. If the launch screen weren't drawable in that form, something different
  (the launcher icon on Android 12, a plain color, an unpainted window) would show and then give way to
  it, which reads as two splashes or a pop-in. That's why the layout is Android 12's fixed one, and why
  Android 11 and earlier use the same drawable as both the window background and the surface's
  background: there's no visible change between them.
- **Presented, not started.** A game has started before it has drawn, and has drawn before the frame is
  on screen. Dismissing on either shows at least one frame of whatever is underneath, usually a bare
  view or canvas, and a slow first frame (a first shader use) shows it for longer. Only the platform
  knows when a frame is on screen, so the host dismisses the launch screen, not the game.
- **Desktop draws before it shows.** A window shown before its GPU device exists is unpainted, black,
  for as long as the device and the game take to start. Drawing a launch frame first makes the launch
  screen the window's first content.
- **The same fade everywhere.** One duration and one curve, the one Material gives elements leaving the
  screen, so the hand-off looks the same on a phone, a desktop and a browser.

## Limits

- One color per game, with no dark variant: the launch screen always uses the game's color, which is
  also what its first frame can clear to.
- A static logo on every platform. Android 12's animated splash icon isn't supported.
- A platform whose targets don't set `LaunchScreenPlatform` skips the item with a warning.
- On the web, `GenerateWebPage=false` turns off the generated page, launch screen included.
