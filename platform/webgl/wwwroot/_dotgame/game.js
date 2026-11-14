// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

import { reportStartupContent, takeStartupFetches } from './content.js'
import { setKeyInstanceId, getKeyInstanceId, isEditableElement } from './input.js'
import { trace, recordFrame, markStartup, showCrashDialog, onCrashDialog } from './diagnostics.js'

let gameBindings = null;

// The crash dialog is up: no more frames
let isCrashed = false;

onCrashDialog(() => {
    isCrashed = true;
    for (const instance of gameInstances.values()) {
        cancelAnimationFrame(instance.handle);
    }
});
let windowEventsInitialized = false;
const gameInstances = new Map(); // canvasId → { handle }
let safeAreaProbe = null;

// How long a lost WebGL context may stay lost while the page is on screen. The
// browser normally restores one within a second or two; one it never restores
// leaves the game unable to draw, its last frame frozen on screen, so after
// this it's reported as a crash.
const CONTEXT_RESTORE_TIMEOUT_MS = 10000;

function getSafeBounds(canvas, dpr) {
    if (!safeAreaProbe) {
        safeAreaProbe = document.createElement('div');
        safeAreaProbe.style.cssText =
            'position:fixed;' +
            'padding-top:env(safe-area-inset-top,0px);' +
            'padding-right:env(safe-area-inset-right,0px);' +
            'padding-bottom:env(safe-area-inset-bottom,0px);' +
            'padding-left:env(safe-area-inset-left,0px);' +
            'pointer-events:none;visibility:hidden;';
        document.body.appendChild(safeAreaProbe);
    }
    const style = getComputedStyle(safeAreaProbe);
    let top    = (parseFloat(style.paddingTop)    || 0) * dpr;
    let right  = (parseFloat(style.paddingRight)  || 0) * dpr;
    let bottom = (parseFloat(style.paddingBottom) || 0) * dpr;
    let left   = (parseFloat(style.paddingLeft)   || 0) * dpr;

    // The visual viewport shrinks when something overlays the page without being reported
    // through env(safe-area-inset-*), such as the soft keyboard or an in-app browser's
    // (e.g. Reddit's) chrome bar, while the canvas keeps its full 100dvh layout box. The
    // difference between the two is the obscured strip on each edge, which is added to the
    // safe area so the game lays out clear of it while still drawing behind it. Reference
    // canvas.clientWidth/clientHeight -- the exact CSS box the backing store
    // (canvas.width/height) is scaled from -- so the gap maps 1:1 onto the pixels subtracted
    // below. An installed PWA has no browser chrome, and its visual viewport can differ from
    // the layout viewport at rest, so there only the bottom strip is used, and only while a
    // text field is focused and the keyboard can therefore be showing.
    const standalone = window.matchMedia?.('(display-mode: standalone)').matches
        || window.navigator.standalone === true;
    const vv = window.visualViewport;
    if (vv) {
        const layoutW = canvas.clientWidth;
        const layoutH = canvas.clientHeight;
        const bottomGap = Math.max(0, layoutH - vv.height - vv.offsetTop) * dpr;
        if (!standalone) {
            left   = Math.max(left,   Math.max(0, vv.offsetLeft) * dpr);
            top    = Math.max(top,    Math.max(0, vv.offsetTop)  * dpr);
            right  = Math.max(right,  Math.max(0, layoutW - vv.width  - vv.offsetLeft) * dpr);
            bottom = Math.max(bottom, bottomGap);
        } else if (isEditableElement(document.activeElement)) {
            bottom = Math.max(bottom, bottomGap);
        }
    }

    top    = Math.round(top);
    right  = Math.round(right);
    bottom = Math.round(bottom);
    left   = Math.round(left);
    return {
        x: left,
        y: top,
        width:  canvas.width  - left - right,
        height: canvas.height - top  - bottom
    };
}

export function setupGameLoop(bindings) {
    gameBindings = bindings;
}

// Browsers report gamepads only by polling, and only once one has been used, so nothing is
// polled until then. What's kept is each pad's last reading, to send only what changed.
let gamepadsSeen = false;
const gamepadReadings = new Map();

if (typeof window !== 'undefined') {
    window.addEventListener('gamepadconnected', () => {
        gamepadsSeen = true;
    });
}

// The standard mapping's buttons, as GamepadButton's values; triggers (6 and 7) are axes
const GAMEPAD_BUTTONS = [0, 1, 2, 3, 4, 5, -1, -1, 6, 7, 9, 10, 11, 12, 13, 14, 8];
const GAMEPAD_KIND_CONNECTED = 0;
const GAMEPAD_KIND_DISCONNECTED = 1;
const GAMEPAD_KIND_PRESSED = 2;
const GAMEPAD_KIND_RELEASED = 3;
const GAMEPAD_KIND_AXIS = 4;
const GAMEPAD_AXIS_LEFT_TRIGGER = 4;
const GAMEPAD_AXIS_RIGHT_TRIGGER = 5;

function pollGamepads(instanceId) {
    if (!gamepadsSeen) {
        return;
    }

    const pads = navigator.getGamepads();
    for (let index = 0; index < 8; index++) {
        const pad = (index < pads.length) ? pads[index] : null;
        let reading = gamepadReadings.get(index);

        if (!pad || !pad.connected || (pad.mapping !== 'standard')) {
            if (reading) {
                gamepadReadings.delete(index);
                gameBindings.OnGamepadEvent(instanceId, GAMEPAD_KIND_DISCONNECTED, index, 0, 0);
            }
            continue;
        }

        if (!reading) {
            reading = { buttons: new Array(17).fill(false), axes: new Array(6).fill(0) };
            gamepadReadings.set(index, reading);
            gameBindings.OnGamepadEvent(instanceId, GAMEPAD_KIND_CONNECTED, index, 0, 0);
        }

        for (let button = 0; (button < GAMEPAD_BUTTONS.length) && (button < pad.buttons.length); button++) {
            const mapped = GAMEPAD_BUTTONS[button];
            const pressed = pad.buttons[button].pressed;
            if ((mapped >= 0) && (pressed !== reading.buttons[button])) {
                reading.buttons[button] = pressed;
                gameBindings.OnGamepadEvent(instanceId, pressed ? GAMEPAD_KIND_PRESSED : GAMEPAD_KIND_RELEASED, index, mapped, 0);
            }
        }

        const axes = [
            pad.axes[0] ?? 0,
            pad.axes[1] ?? 0,
            pad.axes[2] ?? 0,
            pad.axes[3] ?? 0,
            pad.buttons[6]?.value ?? 0,
            pad.buttons[7]?.value ?? 0,
        ];
        for (let axis = 0; axis < axes.length; axis++) {
            if (axes[axis] !== reading.axes[axis]) {
                reading.axes[axis] = axes[axis];
                gameBindings.OnGamepadEvent(instanceId, GAMEPAD_KIND_AXIS, index, axis, axes[axis]);
            }
        }
    }
}

export function startGameLoop(canvasId, instanceId) {
    trace(0x5a2e91c7, `startGameLoop canvas='${canvasId}' inst=${instanceId}`);

    // Store instanceId on the canvas element for direct access from event handlers
    const canvas = document.getElementById(canvasId);
    canvas._instanceId = instanceId;

    // Bind keyboard routing to this game instance. instanceId is guaranteed
    // here as a parameter, so the claim is reliable (relying on initialize()
    // proved fragile). Only claim if no canvas currently owns keyboard --
    // otherwise a newly started canvas would silently steal keys from an
    // already-focused canvas. Pointerdown handles user-initiated focus.
    if (!canvas.hasAttribute('tabindex')) {
        canvas.tabIndex = 0;
    }
    canvas.style.outline = canvas.style.outline || 'none';
    if (getKeyInstanceId() < 0) {
        setKeyInstanceId(instanceId);
    }
    canvas.addEventListener('pointerdown', () => {
        if (getKeyInstanceId() !== instanceId) {
            setKeyInstanceId(instanceId);
        }
    });

    // Capture state in closure to avoid Map lookup per tick
    const state = { handle: 0 };
    gameInstances.set(canvasId, state);

    let lastFrameTime = -Infinity;

    function gameLoopTick(currentTime) {
        // NOTE: A frame is due within half a display frame of its time, so 60 on a 60 Hz display
        // is every frame, and a little timing noise doesn't drop one
        const preferredFrameRate = preferredFrameRates.get(canvasId);
        const interval = preferredFrameRate > 0 ? 1000 / preferredFrameRate : 0;
        if (currentTime - lastFrameTime < interval - 4) {
            state.handle = requestAnimationFrame(gameLoopTick);
            return;
        }

        lastFrameTime = currentTime;

        // Each display frame's time and the game's work in it, for dgFrameStats
        const start = performance.now();
        document.getElementById(canvasId)?.dgApplySize?.();
        pollGamepads(instanceId);
        gameBindings.RunFrame(instanceId, currentTime);
        recordFrame(currentTime, performance.now() - start);

        // NOTE: A fault in this frame that didn't unwind (one reported from a continuation that
        // the browser handled) has shown the crash dialog by now
        if (!isCrashed) {
            state.handle = requestAnimationFrame(gameLoopTick);
        }
    }

    state.handle = requestAnimationFrame(gameLoopTick);
}

// Startup pump. Until the game has started, it advances only when its queued
// work is drained, and animation frames are what drain it. A hidden page gets
// no animation frames, so a game opened in a background tab would load nothing
// until it was shown. While starting, the engine asks for a drain whenever work
// is posted, and the drain runs on a MessageChannel message: unlike timers,
// those aren't throttled in background tabs, and nothing runs while startup is
// only waiting on the network. Requests are coalesced, so one message serves
// any number of posts before it runs.
const startupPumpChannel = new MessageChannel();
const startupPumpInstances = new Set();

startupPumpChannel.port1.onmessage = () => {
    const instanceIds = [...startupPumpInstances];
    startupPumpInstances.clear();
    for (const instanceId of instanceIds) {
        // RunFrame reports its own faults; one that escapes ends the game, as
        // it does from the animation frame loop.
        gameBindings.RunFrame(instanceId, performance.now());
    }
};

export function requestStartupPump(instanceId) {
    if (startupPumpInstances.size === 0) {
        startupPumpChannel.port2.postMessage(null);
    }
    startupPumpInstances.add(instanceId);
}

// The launch screen. The engine calls this once the game has drawn its first frame. The browser
// composites a WebGL frame after its animation frame callback returns, so the next animation
// frame is the first with the game on screen: then the page's #splash fades out, and
// 'dotgame:presented' is raised for pages that want the moment.
export function firstFrameDrawn(fadeMilliseconds, fadeEase) {
    // NOTE: Now, not in the next frame, by which the game may have started fetching what comes after it
    const startupFetches = takeStartupFetches();
    requestAnimationFrame(() => {
        markStartup('first-frame-presented');
        reportStartupContent(startupFetches);
        window.dispatchEvent(new CustomEvent('dotgame:presented'));
        dismissLaunchScreen(fadeMilliseconds, fadeEase);
    });
}

// The color the game clears its window to (the engine's letterbox color), and whether the launch
// screen has gone. The page shows the color wherever the canvas doesn't reach, such as the side
// safe areas Chrome on iOS keeps the page out of in landscape, and overscroll, as the other
// platforms' windows show it in their safe areas. Until the launch screen goes, the page keeps
// the launch screen's color there.
let windowBackground = null;
let isLaunchScreenDismissed = false;

export function setWindowBackground(rgb) {
    windowBackground = '#' + rgb.toString(16).padStart(6, '0');
    if (isLaunchScreenDismissed) {
        showWindowBackground();
    }
}

function showWindowBackground() {
    if (windowBackground === null) {
        return;
    }

    document.documentElement.style.backgroundColor = windowBackground;
    document.body.style.backgroundColor = windowBackground;
}

function dismissLaunchScreen(fadeMilliseconds, fadeEase) {
    isLaunchScreenDismissed = true;

    const splash = document.getElementById('splash');
    if (!splash) {
        showWindowBackground();
        return;
    }

    // The page around the canvas changes to the game's color with the launch screen's fade
    const pageTransition = `background-color ${fadeMilliseconds}ms ${fadeEase}`;
    document.documentElement.style.transition = pageTransition;
    document.body.style.transition = pageTransition;
    showWindowBackground();

    let removed = false;
    const remove = () => {
        if (!removed) {
            removed = true;
            splash.remove();
            document.documentElement.style.transition = '';
            document.body.style.transition = '';
        }
    };

    splash.style.pointerEvents = 'none';
    // The curve every platform's fade follows (the framework's GameHost.LaunchScreenFadeEase)
    splash.style.transition = `opacity ${fadeMilliseconds}ms ${fadeEase}`;
    splash.addEventListener('transitionend', (event) => {
        if (event.target === splash) {
            remove();
        }
    });

    // Apply the transition before changing the opacity, so the change animates
    void splash.offsetWidth;
    splash.style.opacity = '0';

    // transitionend doesn't fire if the transition never runs (the tab was hidden), so a
    // timer makes sure the launch screen never stays over the game
    setTimeout(remove, fadeMilliseconds + 100);
}

// Each canvas's frame rate limit, which the game can change at any time
const preferredFrameRates = new Map();

export function setPreferredFrameRate(canvasId, preferredFrameRate) {
    preferredFrameRates.set(canvasId, preferredFrameRate);
}

export function initialize(elementId) {
    const canvas = document.getElementById(elementId);
    const instanceId = canvas._instanceId;

    // Keyboard routing is bound in startGameLoop (which runs before this
    // function and receives instanceId as a parameter). Don't rebind here.

    // Sizes the backing store from the current CSS box and device pixel ratio and
    // reports the result to managed code. The device pixel ratio is read on every
    // call (never captured), because it can change while the page is alive -- for
    // example on page zoom, or when a display change occurs while the tab is
    // backgrounded. A captured value would leave the backing store and the managed
    // ClientBounds disagreeing, which shows up as a wrongly scaled scene.
    //
    // Assigning canvas.width clears the canvas even when the value doesn't change, so it's only
    // assigned when it does, and only at the start of a frame, just before the game draws: the
    // observer runs after the frame's drawing, and clearing there would show the page through.
    let reported = '';
    let observedWidth = 0;
    let observedHeight = 0;
    let isSizeChanged = false;

    function syncCanvasSize() {
        const dpr = window.devicePixelRatio || 1;
        const width = observedWidth || Math.round(canvas.clientWidth * dpr);
        const height = observedHeight || Math.round(canvas.clientHeight * dpr);
        if (canvas.width !== width) {
            canvas.width = width;
        }
        if (canvas.height !== height) {
            canvas.height = height;
        }

        const sb = getSafeBounds(canvas, dpr);
        const report = `${width},${height},${sb.x},${sb.y},${sb.width},${sb.height},${dpr}`;
        if (report !== reported) {
            reported = report;
            gameBindings.OnCanvasResized(instanceId, width, height, sb.x, sb.y, sb.width, sb.height, dpr);
        }

        return sb;
    }

    // Called at the start of each frame
    canvas.dgApplySize = () => {
        if (isSizeChanged) {
            isSizeChanged = false;
            syncCanvasSize();
        }
    };

    // The backing size in device pixels, exact at fractional scales, where the browser reports it
    const observer = new ResizeObserver((entries) => {
        const box = entries[entries.length - 1].devicePixelContentBoxSize;
        observedWidth = box ? Math.round(box[0].inlineSize) : 0;
        observedHeight = box ? Math.round(box[0].blockSize) : 0;
        isSizeChanged = true;
    });
    try {
        observer.observe(canvas, { box: 'device-pixel-content-box' });
    } catch {
        observer.observe(canvas);
    }

    // The scale can change with no change to the CSS box, on a move to another monitor
    const watchPixelRatio = () => {
        const query = window.matchMedia(`(resolution: ${window.devicePixelRatio || 1}dppx)`);
        query.addEventListener('change', () => {
            isSizeChanged = true;
            watchPixelRatio();
        }, { once: true });
    };
    watchPixelRatio();

    // Initialize ClientSize synchronously before returning to C# so it is set before OnLoad is called
    const safeBounds = syncCanvasSize();
    trace(0x71bd0a36, `initialize inst=${instanceId} canvas=${canvas.width}x${canvas.height} dpr=${window.devicePixelRatio || 1} safe=(${safeBounds.x},${safeBounds.y},${safeBounds.width},${safeBounds.height})`);

    if (!windowEventsInitialized) {
        windowEventsInitialized = true;

        // The game pauses when the page loses focus or is hidden, and resumes
        // when it gets either back. Focus alone can't be relied on to return:
        // iOS can blur the page for a sheet over it (a share target's compose
        // view, say) and never send the focus that ends it, which left the game
        // paused with its last frame frozen on screen. So it also resumes when
        // the page is shown again, and when the player touches it or presses a
        // key, since input reaching the page proves it's in front.
        let windowActive = true;
        const activate = (reason) => {
            if (windowActive || (document.visibilityState === 'hidden')) {
                return;
            }
            windowActive = true;
            trace(0x4f6a2d18, `window activated (${reason})`);
            gameBindings.OnWindowActivated();
        };
        const deactivate = (reason) => {
            if (!windowActive) {
                return;
            }
            windowActive = false;
            trace(0x93b1c5e0, `window deactivated (${reason})`);
            gameBindings.OnWindowDeactivated();
        };
        window.addEventListener('focus', () => activate('focus'));
        window.addEventListener('blur', () => deactivate('blur'));
        document.addEventListener('visibilitychange', () => {
            const hidden = document.visibilityState === 'hidden';
            gameBindings.OnPageVisibilityChanged(hidden);
            if (hidden) {
                deactivate('hidden');
            } else {
                activate('visible');
            }
        });
        window.addEventListener('pageshow', () => activate('pageshow'));
        window.addEventListener('pointerdown', () => activate('pointerdown'), { capture: true, passive: true });
        window.addEventListener('keydown', () => activate('keydown'), { capture: true, passive: true });

        if (window.visualViewport) {
            // NOTE: These fire repeatedly while a keyboard animates or a page is pinched, and the
            // size is only reported if this changed it
            const onViewportChanged = () => {
                isSizeChanged = true;
            };
            window.visualViewport.addEventListener('resize', onViewportChanged);
            window.visualViewport.addEventListener('scroll', onViewportChanged);
        }
    }

    // Counts only time on screen: a hidden page's context can be lost for as
    // long as it stays hidden, and is restored when it's shown again.
    let contextRestoreTimer = 0;
    const checkContextRestored = () => {
        contextRestoreTimer = 0;
        if (document.visibilityState === 'hidden') {
            contextRestoreTimer = setTimeout(checkContextRestored, CONTEXT_RESTORE_TIMEOUT_MS);
            return;
        }
        trace(0x5b07e3c9, `webglcontext not restored inst=${instanceId}`);
        showCrashDialog(
            'Graphics context lost',
            `The browser took the game's WebGL context and didn't restore it within ${CONTEXT_RESTORE_TIMEOUT_MS / 1000} seconds, so the game can't draw.`,
            '');
    };

    canvas.addEventListener('webglcontextlost', (e) => {
        trace(0xa4d8317f, `webglcontextlost inst=${instanceId}`);
        e.preventDefault();
        gameBindings.OnContextLost(instanceId);
        if (!contextRestoreTimer) {
            contextRestoreTimer = setTimeout(checkContextRestored, CONTEXT_RESTORE_TIMEOUT_MS);
        }
    });
    canvas.addEventListener('webglcontextrestored', () => {
        trace(0xc61f9042, `webglcontextrestored inst=${instanceId}`);
        clearTimeout(contextRestoreTimer);
        contextRestoreTimer = 0;
        gameBindings.OnContextRestored(instanceId);

        // The device pixel ratio or CSS box may have changed while the context was
        // lost, so the size is synced and reported again. This is the only place a
        // restore sizes the canvas (graphics.js doesn't), so the backing store and
        // managed code's ClientBounds can't disagree, which draws at the wrong scale.
        const sb = syncCanvasSize();
        trace(0x1c8f34a7, `webglcontextrestored resync inst=${instanceId} canvas=${canvas.width}x${canvas.height} dpr=${window.devicePixelRatio || 1} safe=(${sb.x},${sb.y},${sb.width},${sb.height})`);
    });
}
