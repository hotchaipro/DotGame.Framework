// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.
// The startup follows the .NET wasmbrowser template (MIT, .NET Foundation).

import { dotnet } from '../_framework/dotnet.js'
import { setupContent, fetchWithRetry, precompressedUrl, readPrecompressed, readWasm, waitBehindConnectionMessage, failedResponseReason } from './content.js'
import { setupAudio, setAudioFailureReporter } from './audio.js'
import { classifyPageError } from './page-errors.js'
import { setupGraphics, isWebGL2Supported } from './graphics.js'
import {
    configureInput,
    initializeInput,
    createTextInput,
    destroyTextInput,
    updateTextInput,
    focusTextInput,
    blurTextInput
} from './input.js'
import { setupGameLoop, setPreferredFrameRate, startGameLoop, requestStartupPump, initialize, firstFrameDrawn, setWindowBackground } from './game.js'
import {
    showCrashDialog,
    isCrashDialogShown,
    recordSecondaryFailure,
    showUnsupportedMessage,
    showConnectionMessage,
    isLoadFailureHandled,
    reportFailure,
    trace,
    registerDumpTrace,
    registerTraceSink,
    registerTraceToggle,
    forceTraceEnabled,
    instrumentFetch,
    instrumentModuleImports,
    markStartup
} from './diagnostics.js'

trace(0x0d47e2b5, `host modules loaded (${navigator.userAgent})`);
markStartup('host-modules');

// The launch screen is the page's #splash, painted with the page itself
if (document.getElementById('splash')) {
    markStartup('launch-shown');
}

// The managed GameBindings export, assigned once the .NET runtime is up.
// It is declared here (before the runtime is created) so the global error
// handlers below can forward to the managed crash pipeline when it is available.
let gameBindings = null;

// Report every unhandled JavaScript error and promise rejection. Unhandled
// failures are never silently swallowed. The sole exception is AbortError — the
// benign interruption the browser raises when a play() promise is superseded by a
// later pause(), load(), or play() (the JavaScript equivalent of
// OperationCanceledException) — which is always safe to ignore.
//
// These handlers are installed before the .NET runtime is created so failures
// that occur before managed init (or after the runtime exits) still surface a
// crash dialog. The dialog is shown directly from JavaScript so it works across
// the entire lifecycle; when the runtime is alive we also forward to the managed
// pipeline (best effort) so the failure is recorded. A crash dialog that is
// already visible (e.g. a richer managed report) is never replaced.
// The URL of a module of this page's own that couldn't be downloaded ('' when the browser doesn't
// name it, as Safari doesn't), or null for any other error. Chromium, Firefox and WebKit word it
// differently.
function failedModuleUrl(error) {
    if (!(error instanceof TypeError)) {
        return null;
    }
    const match = /^(?:Failed to fetch dynamically imported module|error loading dynamically imported module|Importing a module script failed)[.:]?\s*(\S*)/i
        .exec(String(error.message || ''));
    if (!match) {
        return null;
    }
    const url = match[1];
    if (url) {
        try {
            if (new URL(url, globalThis.location.href).origin !== globalThis.location.origin) {
                return null;
            }
        } catch {
            return null;
        }
    }
    return url;
}

// The WebAssembly feature the runtime asserted this browser lacks ("This browser/engine doesn't
// support WASM exception handling."), or null for any other error. The page checks for them before
// it starts the runtime (missingWasmFeature), so this is for one the runtime came to need since.
function runtimeUnsupportedFeature(error) {
    const match = /This browser\/engine doesn't support ([^.]+)\./.exec(String((error && error.message) || error || ''));
    return match ? match[1] : null;
}

// Whether a WebAssembly compile failed on what isn't a module at all (a page or an error in
// place of the file, from a host or cache the page can't control): readWasm checks the runtime's
// own files before they're compiled, so this is for any it couldn't
function isNotWasmModule(error) {
    return Boolean(error) && (error.name === 'CompileError')
        && /doesn't start with '\\0asm'|expected a module of at least|expected magic word|magic header not detected|wasm validation error: at offset 0/i.test(String(error.message || ''));
}

let reportedUnsupported = false;

// Tells the player this browser can't run the game, and reports it (as a warning: the browser
// lacks a feature, nothing failed)
function stopUnsupported(feature, details) {
    if (reportedUnsupported) {
        return;
    }
    reportedUnsupported = true;
    trace(0x8a3d61f3, `unsupported browser: ${feature}`);
    markStartup('browser-unsupported');
    reportFailure('Browser not supported', `This browser doesn't support ${feature}.`, details || '',
        { level: 'warning', tags: { unsupported: feature } });
    showUnsupportedMessage(
        "This browser can't run the game",
        "It needs a newer browser than this one. Update your browser (or your device's software) to the latest version, or play on another device.");
}

function reportUnhandledError(source, error, filename) {
    if (error && error.name === 'AbortError') {
        return;
    }

    // A load failure stopped the game, and is reported: what fails after it is its aftermath, not
    // a crash. The page's reload for it cancels the downloads still in flight, which WebKit rejects
    // with "TypeError: Load failed" as the page goes
    if (isLoadFailureHandled()) {
        trace(0x6e2d48b3, `after a load failure: ${(error && error.message) || error}`);
        return;
    }

    if (reportedUnsupported) {
        trace(0x8a3d61f4, `after unsupported: ${(error && error.message) || error}`);
        return;
    }

    const unsupported = runtimeUnsupportedFeature(error);
    if ((unsupported !== null) && !isCrashDialogShown()) {
        stopUnsupported(unsupported, (error && error.stack) || '');
        return;
    }

    if (isNotWasmModule(error) && !isCrashDialogShown()) {
        trace(0x2e6d81b6, `not a wasm module: ${error.message}`);
        showConnectionMessage('', 'bad-file', `isn't a WebAssembly module: ${error.message}`);
        return;
    }

    // A module of the page's own that didn't download (the runtime's dotnet.native.js on a lost
    // connection, an earlier deploy's that's gone, or one just after a deploy, before every edge
    // has the new files) isn't a crash: the page reloads once, then asks the player to try again
    const moduleUrl = failedModuleUrl(error);
    if (moduleUrl !== null) {
        trace(0x2e6d81b5, `module not downloaded: ${(error && error.message) || error}`);
        if (!isCrashDialogShown()) {
            showConnectionMessage(moduleUrl, 'module', `${(error && error.message) || error}`);
        }
        return;
    }
    const name = (error && error.name) ? error.name : '';
    const message = (error && error.message) ? error.message : String(error);
    const stack = (error && error.stack) ? error.stack : '';

    trace(0x7d90a4e6, `unhandled ${source}: ${name}: ${message}`);

    // Another site's script failing isn't the game's fault, and the game runs on
    if (classifyPageError(stack, filename, globalThis.location.origin) === 'third-party') {
        return;
    }

    if (isCrashDialogShown()) {
        // A dialog is already up, so this one is not shown -- but a secondary
        // failure is often the one that explains the first, so it is still
        // persisted, with the crash the dialog shows (it is the same crash
        // unwinding further, not one to report on its own).
        recordSecondaryFailure('Unhandled ' + source, name + ': ' + message, stack);
        return;
    }

    // The managed pipeline first, while the runtime is alive: its report carries
    // the game's breadcrumbs, and it shows the dialog itself. The page's own
    // dialog is for when it can't (no runtime yet, or it has exited).
    if (gameBindings) {
        try {
            gameBindings.ReportUnhandledError(source, name, message, stack);
        } catch { /* runtime already exited, or it unwound as it reported */ }
    }

    if (!isCrashDialogShown()) {
        showCrashDialog('Unhandled ' + source, name + ': ' + message, stack);
    }
}

globalThis.addEventListener('unhandledrejection', (e) => {
    e.preventDefault();

    // A download that still failed after fetchWithRetry's retries (the runtime's
    // wasm and assemblies are fetched through it) is a lost connection, not a
    // crash: ask the player to try again, and report nothing
    const reason = e.reason;
    if ((reason instanceof TypeError) && reason.dgFetchUrl) {
        // NOTE: The wrapper sees every script's fetches, and one on an ad or analytics script
        // doesn't mean this game couldn't load
        if (classifyPageError(reason.stack, '', globalThis.location.origin) === 'third-party') {
            trace(0x2e6d81b4, `unhandled third-party fetch failure: ${reason.message}`);
            return;
        }

        showConnectionMessage(reason.dgFetchUrl);
        return;
    }

    reportUnhandledError('promise rejection', reason);
});

globalThis.addEventListener('error', (e) => {
    // A script error targets the window; a resource load failure targets the
    // element. The latter does not bubble, so in practice it never reaches
    // this listener -- but the discriminator is explicit rather than relying
    // on that, so a resource failure can never be escalated into a crash
    // dialog reading "undefined". It is handled by the capture-phase listener
    // below.
    if (e.target && e.target !== globalThis) {
        return;
    }
    e.preventDefault();
    reportUnhandledError('error', e.error || e.message, e.filename);
});

// Resource load failures (<img>, <script>, <link>, media) fire an 'error'
// event on the element itself and do NOT bubble, so the listener above never
// sees them -- only a capture-phase listener on window does. Script errors
// arrive here too, as ErrorEvent, and are filtered out so they are not
// reported twice.
//
// These are recorded as breadcrumbs rather than escalated to a crash dialog.
// Whatever awaited the asset (a fetch rejection, an Image.onerror) already
// reports it and is the only code that knows whether the failure is fatal;
// escalating here would turn every transient asset hiccup into a crash. The
// point is that the failure is no longer invisible to the ring buffer.
globalThis.addEventListener('error', (e) => {
    if (e instanceof ErrorEvent) {
        return;
    }
    const target = e.target;
    if (!target || target === globalThis) {
        return;
    }
    const tag = target.tagName ? target.tagName.toLowerCase() : '?';
    trace(0xb3e91d47, `resource load failed: <${tag}> ${target.src || target.href || ''}`);
}, { capture: true });

// Every failed request names its URL and its caller, so a rejection that isn't
// handled (or one that is, in a trace) says what failed and who asked for it
instrumentFetch();

// Reports a runtime-level fatal (abort/unexpected exit). These arrive from the
// runtime's own teardown path rather than as a page error, and the managed side
// is already gone by the time they fire -- so the report is built entirely from
// JavaScript and never forwarded to the managed pipeline.
function reportFatalRuntimeEvent(title, summary, stack) {
    const unsupported = runtimeUnsupportedFeature(summary);
    if (reportedUnsupported || ((unsupported !== null) && !isCrashDialogShown())) {
        stopUnsupported(unsupported, stack);
        return;
    }
    if (!isCrashDialogShown()) {
        showCrashDialog(title, summary, stack);
    } else {
        recordSecondaryFailure(title, summary, stack);
    }
}

// The game draws with WebGL 2, so a browser or device without it (older iOS, a
// GPU the browser blocks) can't run it at all. Say so plainly before creating
// the runtime, rather than starting it only to fail with a crash dialog.
if (!isWebGL2Supported()) {
    trace(0x8a3d61f2, 'webgl2 unsupported; not starting');
    markStartup('webgl2-unsupported');
    reportFailure('Browser not supported', "This browser or device doesn't support WebGL 2.", '',
        { level: 'warning', tags: { unsupported: 'WebGL 2' } });
    showUnsupportedMessage(
        "This game can't run here",
        "It needs WebGL 2, which this browser or device doesn't support. Try updating your browser, or play on another device.");

    // Nothing below can work, so stop: a module can't return, and throwing would
    // be reported as a crash, so wait for ever instead.
    await new Promise(() => { });
}

// The WebAssembly features the runtime needs: SIMD, as the runtime asserts as it starts (dotnet.js
// probes it the same way: Safari 16.4, Chrome 91, Firefox 89). Its exception handling is the form
// first shipped (DotGame.Framework.WebGL.csproj), which every browser with SIMD has; a build linked
// with the final form (try_table) on a browser without it gets the runtime's assert, which
// reportUnhandledError turns into this message too. Lockdown Mode on iOS and macOS turns
// WebAssembly off. Say so plainly, as for WebGL 2, before downloading a runtime that would only fail.
// Some browsers set WebAssembly to null instead of removing it (typeof null is 'object').
function missingWasmFeature() {
    if ((typeof WebAssembly !== 'object') || (WebAssembly === null) || (typeof WebAssembly.validate !== 'function')) {
        return 'WebAssembly';
    }
    // v128.const and i8x16.splat, as dotnet.js's probe
    if (!WebAssembly.validate(new Uint8Array([0, 97, 115, 109, 1, 0, 0, 0, 1, 5, 1, 96, 0, 1, 123, 3, 2, 1, 0, 10, 10, 1, 8, 0, 65, 0, 253, 15, 253, 98, 11]))) {
        return 'WASM SIMD';
    }
    return null;
}

const missingFeature = missingWasmFeature();
if (missingFeature !== null) {
    stopUnsupported(missingFeature, '');
    await new Promise(() => { });
}

// Creating the AudioContext blocks the main thread for hundreds of milliseconds
// while the browser opens the output device. Do it while the main thread is
// idle waiting for the runtime, instead of at first use while content loads,
// on the critical path to the first frame. Not before the runtime's wasm has
// finished downloading: the browser feeds the streaming compile from the main
// thread, so blocking it then delays the runtime by as long (measured). Its
// resource timing entry arrives when the download ends; the compile then
// continues off the main thread.
const audio = setupAudio();
if (typeof PerformanceObserver === 'function') {
    const wasmObserver = new PerformanceObserver((list) => {
        if (list.getEntries().some((entry) => /\/dotnet\.native\.[^/]*wasm$/.test(entry.name))) {
            wasmObserver.disconnect();
            setTimeout(audio.prepareAudioContext, 0);
        }
    });
    wasmObserver.observe({ type: 'resource' });
}

// The runtime's downloads (its wasm, assemblies and data) retry a network failure
// once, 100 ms later. A phone suspends a backgrounded page's network, so a player
// who switches apps while the game loads would see both attempts fail -- and the
// downloads still in flight reject unobserved ("Unhandled promise rejection:
// TypeError: Load failed"). They're fetched here instead, retried once the page is
// visible and online again. The options are the loader's own: no-cache (so a
// fingerprinted asset revalidates rather than going stale) and the asset's
// integrity hash. Its JS modules must load from a URL, so they keep the default.
// A published build fetches the precompressed copy (see content.js's readPrecompressed),
// without the hash: a copy the host didn't send as Brotli fails it, which the browser
// reports as a network failure, so the error saying the host isn't set up would never
// show. The copy's name has the fingerprint of the file's contents, as the file's has.
function loadBootResource(type, name, defaultUri, integrity) {
    if (type === 'dotnetjs') {
        return undefined;
    }
    // A runtime file the host doesn't have (a 404) is from a page an earlier deploy left in the
    // browser's cache, whose fingerprinted files are gone: as with content, the player is asked
    // to try again, and the reload gets the current page. The runtime would fail on it otherwise.
    // A wasm file (the runtime's, and each assembly's) is checked for WebAssembly's magic number
    // before it's compiled: what a host or a cache sent in its place (a page, an error) is a file
    // the page didn't get, as a 404 is, and the browser's compile error wouldn't say so
    const url = precompressedUrl(defaultUri);
    if (url === defaultUri) {
        return fetchWithRetry(defaultUri, {
            cache: 'no-cache',
            integrity: integrity || undefined,
        }, (response) => response.ok ? checkWasm(response, defaultUri) : notOnHost(response, defaultUri));
    }
    return fetchWithRetry(url, { cache: 'no-cache' },
        (response) => response.ok ? checkWasm(readPrecompressed(response, defaultUri), defaultUri, response) : notOnHost(response, defaultUri));
}

async function checkWasm(response, url, sent) {
    if (!/\.wasm$/i.test(url.split(/[?#]/)[0])) {
        return response;
    }
    const checked = await readWasm(response);
    if (checked.response) {
        return checked.response;
    }
    const headers = sent || response;
    const summary = `${url} isn't a WebAssembly module: ${headers.status} ${headers.headers.get('Content-Type') || '(no type)'}`
        + ` ${headers.headers.get('Content-Encoding') || '(no encoding)'}, ${checked.length} bytes or more, starting ${checked.start}`;
    trace(0x1c5e93d8, summary);
    return waitBehindConnectionMessage(url, 'bad-file', summary);
}

function notOnHost(response, url) {
    trace(0x1c5e93d7, `runtime file not on the host: ${response.status} ${url}`);
    return waitBehindConnectionMessage(url, failedResponseReason(response), `${response.status} ${url}`);
}

const { setModuleImports, getAssemblyExports, runMain } = await dotnet
    .withApplicationArguments("start")
    .withResourceLoader(loadBootResource)
    // A runtime-level abort -- a wasm trap, an out-of-memory growth failure, a
    // failed assertion in the native runtime, or Environment.Exit -- does not
    // raise a page error. The runtime prints and tears itself down, so without
    // these hooks the most catastrophic class of failure is the one that leaves
    // no crash record at all. onAbort is unrecoverable and reported as a crash;
    // onExit is only a crash when the exit code is non-zero (a clean exit is a
    // normal shutdown, not a fault).
    .withModuleConfig({
        onAbort: (reason) => {
            const text = (reason && reason.message) ? reason.message : String(reason);
            trace(0x51c4a70e, `runtime abort: ${text}`);
            reportFatalRuntimeEvent('Runtime abort', text,
                (reason && reason.stack) ? reason.stack : '');
        },
        onExit: (code) => {
            trace(0x2b7f61d9, `runtime exit: ${code}`);
            if (code !== 0) {
                reportFatalRuntimeEvent(
                    'Runtime exited unexpectedly',
                    `The .NET runtime exited with code ${code}.`,
                    '');
            }
        }
    })
    .create();

trace(0x4e1b7c03, 'runtime created');
markStartup('runtime-created');

// Register the diagnostics module for managed [JSImport] before any managed
// code runs. The managed crash pipeline (FatalError -> CrashReportProvider ->
// JSDiagnostics.ShowCrashDialog) and the trace bridge (Trace.Initialize,
// called on the first line of Main) both bind to this module, so it must exist
// from the very first managed instruction: a fatal during early engine startup
// (Phase 1 service creation / Game.Initialize, e.g. a failed WebGL context on
// Safari) would otherwise call an unbound import, the throw would be swallowed,
// and the process would FailFast with no dialog. Registering it here (rather
// than later via JSHost.ImportAsync in Phase 2) closes that startup gap for
// every app.
setModuleImports('diagnostics.js', {
    showCrashDialog: showCrashDialog,
    registerDumpTrace: registerDumpTrace,
    registerTraceSink: registerTraceSink,
    registerTraceToggle: registerTraceToggle,
    forceTraceEnabled: forceTraceEnabled,
    markStartup: markStartup
});

// Every table below is passed through instrumentModuleImports so that a throw
// inside an import is re-raised naming the call and the shape of its arguments.
// Without it a WebKit binding failure arrives in managed code as nothing but
// 'JSException: TypeError: Type error', which does not identify the call that
// failed -- and the game loop makes dozens of them per frame. The wrapper costs
// one function call on the happy path and builds nothing unless a call throws.
//
// diagnostics.js is deliberately left uninstrumented: it is the pipeline that
// reports the fault, so wrapping it would route a failure back through itself.
setModuleImports('content.js', instrumentModuleImports('content.js', {
    content: setupContent()
}));

setModuleImports('audio.js', instrumentModuleImports('audio.js', {
    audio: audio
}));

setModuleImports('graphics.js', instrumentModuleImports('graphics.js', {
    graphics: setupGraphics()
}));

setModuleImports('input.js', instrumentModuleImports('input.js', {
    inputService: {
        initializeInput: initializeInput,
        createTextInput: createTextInput,
        destroyTextInput: destroyTextInput,
        updateTextInput: updateTextInput,
        focusTextInput: focusTextInput,
        blurTextInput: blurTextInput
    }
}));

setModuleImports('game.js', instrumentModuleImports('game.js', {
    gamePlatform: {
        setPreferredFrameRate: setPreferredFrameRate,
        startGameLoop: startGameLoop,
        requestStartupPump: requestStartupPump,
        initialize: initialize,
        firstFrameDrawn: firstFrameDrawn,
        setWindowBackground: setWindowBackground
    }
}));

// run the C# Main() method which initializes the game
const exports = await getAssemblyExports('DotGame.Framework.WebGL');
trace(0x1c8f52ad, 'assembly exports resolved');

// Unwrap exports so game.js/input.js don't depend on assembly or namespace paths.
// gameBindings was declared earlier so the global error handlers can forward to
// the managed crash pipeline once the runtime is available.
gameBindings = exports.DotGame.Framework.WebGL.GameBindings;
const inputBindings = exports.DotGame.Framework.WebGL.Input.InputServiceStrategy;

// Initialize game loop with exports reference
setupGameLoop(gameBindings);
setAudioFailureReporter((message, stack) => gameBindings.ReportAudioFailure(message, stack));

// Initialize input (pointer, keyboard, and text-input) with exports reference
configureInput(inputBindings);

// Initialize the application
trace(0x6b03d719, 'runMain starting');
markStartup('run-main');
await runMain();
trace(0x2fa61b84, 'runMain returned');

trace(0x4c6ba1d8, 'game started');
window.dispatchEvent(new CustomEvent('dotgame:started'));
