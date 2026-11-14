// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

// Browser-side diagnostics for the WebGL host: the trace facility, durable
// crash records, and the crash dialog. They live together because they are
// facets of one story -- the dialog reports the failure, the trace records what
// led up to it, and the crash store makes sure neither is lost -- and because
// all three must be usable before the .NET runtime exists and after it has
// exited.

// ============================ Tracing ============================
//
// Tracing is OFF by default in every configuration -- a shipped build must not
// spam the console -- and is armed from the browser console:
//
//     dgTrace(true)    enable live trace output
//     dgTrace(false)   disable it
//     dgDumpTrace()    dump the managed ring buffer
//
// The setting is persisted in localStorage, so enabling it and reloading also
// captures startup: the phase that is otherwise impossible to observe.
//
// It can also be armed from the URL:
//
//     ?dgTrace=1       enable for this load
//     ?dgTrace=0       disable for this load
//
// The query parameter wins over the persisted value and is never written back
// to storage, so it is a one-shot override rather than a mode change. It exists
// because localStorage is the one thing the console workflow above cannot rely
// on: in a third-party iframe (a portal embed such as Kongregate) storage is
// partitioned or blocked outright, so `dgTrace(true)` plus a reload silently
// loses the setting -- and startup is precisely what that workflow was for. The
// URL survives the frame boundary and needs no storage at all.
//
// Every JS trace is forwarded into the managed Trace ring buffer, which is the
// single source of truth for diagnostic capture; there is deliberately no
// JS-side ring buffer. Forwarding happens regardless of the toggle, mirroring
// managed Trace.Write semantics (the buffer always records; the flag only
// controls the console echo), so a crash report always carries the JS
// breadcrumbs that led up to it.
//
// The console echo for JS entries is emitted here rather than on the managed
// side. It has to be: the traces that matter most are the boot sequence, which
// runs before the runtime exists -- and, when boot is what failed, may never
// have a runtime to echo them. The managed side therefore records JS entries
// silently (see Trace.WriteFromScript) so nothing is printed twice.

const TRACE_STORAGE_KEY = 'dgTrace';

// Traces recorded before the managed sink is wired are queued so the boot
// sequence still reaches the ring buffer. Bounded: if the runtime never comes
// up, this must not grow without limit.
const PENDING_LIMIT = 256;

// Recent trace lines kept for attachment to a crash record. This is not a
// second diagnostic ring buffer -- the managed one remains the single source of
// truth -- it exists because a crash during boot happens before the managed
// buffer exists, and a crash that reloads the page destroys it. Without this,
// the most valuable crashes are the ones that arrive with no context at all.
const RECENT_TRACE_LIMIT = 64;

function readQueryOverride() {
    try {
        const value = new URLSearchParams(window.location.search).get(TRACE_STORAGE_KEY);
        return (value === null) ? null : (value === '1');
    } catch {
        // Malformed query string, or no location in this context.
        return null;
    }
}

function readInitialSetting() {
    // The URL is checked first: it is the only channel that works when storage
    // is partitioned or blocked, so it must not be overridable by a stale (or
    // unreadable) persisted value.
    const override = readQueryOverride();
    if (override !== null) {
        return override;
    }

    try {
        return window.localStorage.getItem(TRACE_STORAGE_KEY) === '1';
    } catch {
        // Storage can be unavailable (private mode, blocked third-party
        // context). Tracing simply stays off.
        return false;
    }
}

let enabled = readInitialSetting();
let managedSink = null;
let managedToggle = null;
let pending = [];
let recentTraces = [];

function formatTag(tag) {
    return '0x' + tag.toString(16).padStart(8, '0');
}

// Hot paths (per-pointer-event, per-frame) should guard their trace calls with
// this so neither the message string nor the interop crossing is paid for when
// tracing is off.
export function isTraceEnabled() {
    return enabled;
}

export function trace(tag, message) {
    const numericTag = (typeof tag === 'number') ? (tag >>> 0) : 0;
    const text = (message === undefined || message === null) ? '' : String(message);
    const line = `${new Date().toISOString()}\t${formatTag(numericTag)}\t${text}`;

    // Kept unconditionally (not just when tracing is on): a crash record is
    // worth far more with the last few lifecycle events attached, and a crash
    // is never something the operator knew to arm tracing for in advance.
    recentTraces.push(line);
    if (recentTraces.length > RECENT_TRACE_LIMIT) {
        recentTraces.shift();
    }

    if (managedSink) {
        try {
            managedSink(String(numericTag), text);
        } catch { /* runtime has exited */ }
    } else if (pending.length < PENDING_LIMIT) {
        pending.push([numericTag, text]);
    }

    if (enabled) {
        try {
            console.log(`${line.replace('\t', '\t[js]\t')}`);
        } catch { /* console unavailable */ }
    }
}

// Returns whether the choice was persisted. A failure here is not an error --
// the setting still applies to this page -- but it does mean the usual "enable
// and reload to capture startup" workflow will not survive the reload, which
// the caller reports so the operator can reach for ?dgTrace=1 instead.
function setTraceEnabled(on) {
    enabled = on;
    let persisted = true;
    try {
        window.localStorage.setItem(TRACE_STORAGE_KEY, on ? '1' : '0');
    } catch {
        persisted = false;
    }
    if (managedToggle) {
        try {
            managedToggle(on ? '1' : '0');
        } catch { /* runtime has exited */ }
    }
    return persisted;
}

// Published at module-import time -- before the .NET runtime is created -- so
// tracing can be armed and inspected even if the runtime never boots.
window.dgTrace = function (on) {
    const value = (on !== false);
    const persisted = setTraceEnabled(value);
    try {
        console.info(`[trace] tracing ${value ? 'enabled' : 'disabled'}`);
        if (!persisted) {
            console.info('[trace] this setting could not be saved (storage is'
                + ' blocked or partitioned, typical in a third-party iframe);'
                + ` it will be lost on reload -- add ?${TRACE_STORAGE_KEY}=1 to`
                + ' the game URL to capture startup');
        }
    } catch { /* console unavailable */ }
    return value;
};

// Placeholder until the managed ring buffer is reachable. Without it, calling
// dgDumpTrace() before (or after) the runtime is up is a bare TypeError, which
// reads like the hook is missing rather than the runtime being absent.
window.dgDumpTrace = function () {
    try {
        console.info('[trace] the .NET runtime is not running; no ring buffer to dump');
    } catch { /* console unavailable */ }
};

// Receives the managed ring-buffer writer and drains anything traced before the
// runtime was up.
//
// The callbacks take only strings (the tag as its unsigned decimal value, the
// toggle as '1' or '0'): WebAssembly AOT doesn't compile the marshaler's code
// for value-type callback parameters, so they ran in the interpreter.
export function registerTraceSink(fn) {
    managedSink = fn;

    const queued = pending;
    pending = [];
    for (const entry of queued) {
        try {
            fn(String(entry[0]), entry[1]);
        } catch {
            break;
        }
    }
}

// Receives the managed console-sink setter and pushes the current (possibly
// persisted) setting into it, so both halves agree from the first managed
// instruction and a persisted `dgTrace(true)` also covers managed startup.
export function registerTraceToggle(fn) {
    managedToggle = fn;
    try {
        fn(enabled ? '1' : '0');
    } catch { /* runtime has exited */ }
}

export function registerDumpTrace(fn) {
    window.dgDumpTrace = fn;
}

// Called by the managed side to force tracing on regardless of the persisted
// setting. DEBUG builds echo managed traces unconditionally, so they turn this
// on to keep the JS half consistent; release builds never call it. The
// preference is deliberately not written to storage -- forcing it on for a
// debug session must not silently leave tracing armed for a later release
// build served from the same origin.
export function forceTraceEnabled() {
    enabled = true;
}

// ==================== Interop fault attribution ====================
//
// A JavaScript error thrown inside a [JSImport] target crosses back into
// managed code as a JSException whose message is whatever the browser's
// `toString()` produced. On WebKit that is routinely useless: a failed WebIDL
// argument conversion -- passing null to AudioNode.connect, a detached view to
// bufferSubData -- is reported as the bare string 'TypeError: Type error',
// with no parameter named, no call named, and (for errors raised inside the
// binding layer) frequently no usable stack either.
//
// What reaches the crash report is then 'JSException: TypeError: Type error'
// against a game loop that calls dozens of interop functions per frame, which
// is not a diagnosable failure.
//
// Every module handed to setModuleImports is wrapped so that a throw is
// re-raised naming the module, the function, and the shape of each argument,
// with the original error preserved as `cause` and its stack carried through.
// A breadcrumb is recorded at the same time, so the ring buffer holds the
// attribution even when the managed side never gets to report it.
//
// The happy path pays one function call and nothing else: no argument is
// inspected, no string is built, and no trace is written unless the call
// actually throws.
const INTEROP_FAULT_TAG = 0x5c0ba7e1;

// Describes an argument by shape rather than value. Enough to identify a
// conversion failure (a null where an object is required, a typed array of the
// wrong length or type) without copying game data into a log.
function describeArgument(value) {
    if (value === null) {
        return 'null';
    }
    if (value === undefined) {
        return 'undefined';
    }

    const type = typeof value;
    if (type === 'number' || type === 'boolean' || type === 'bigint') {
        return String(value);
    }
    if (type === 'string') {
        return value.length > 32 ? `string(${value.length})` : JSON.stringify(value);
    }
    if (type === 'function') {
        return 'function';
    }

    try {
        if (ArrayBuffer.isView(value)) {
            return `${value.constructor.name}(${value.length})`;
        }
        if (Array.isArray(value)) {
            return `Array(${value.length})`;
        }
        const name = value.constructor ? value.constructor.name : 'Object';
        return name || 'Object';
    } catch {
        // A proxy or cross-realm object that refuses introspection.
        return type;
    }
}

function describeArguments(args) {
    const parts = [];
    for (let i = 0; i < args.length; i++) {
        parts.push(describeArgument(args[i]));
    }
    return parts.join(', ');
}

function attributeInteropFault(moduleName, functionName, args, error) {
    const site = `${moduleName}.${functionName}(${describeArguments(args)})`;
    const name = (error && error.name) ? error.name : 'Error';
    const message = (error && error.message) ? error.message : String(error);

    trace(INTEROP_FAULT_TAG, `${site} threw ${name}: ${message}`);

    // A second attribution would bury the first: an inner import that already
    // named itself is the precise site, and the outer frames only repeat it.
    if (error && error.dgInteropSite) {
        return error;
    }

    const attributed = new Error(`${site}: ${name}: ${message}`);
    attributed.dgInteropSite = site;
    attributed.cause = error;

    // The managed JSException reads its stack from this object, so the
    // original frames are carried over rather than replaced by the frames of
    // this wrapper -- which are always the same three and identify nothing.
    const originalStack = (error && error.stack) ? String(error.stack) : '';
    if (originalStack) {
        attributed.stack = `${attributed.message}\n${originalStack}`;
    }

    return attributed;
}

function instrumentFunction(moduleName, functionName, fn) {
    return function instrumentedImport(...args) {
        try {
            const result = fn.apply(this, args);

            // An async import is awaited by the managed side as a Task, so a
            // rejection surfaces there exactly as a synchronous throw does and
            // needs the same attribution.
            if (result && typeof result.then === 'function') {
                return result.then(
                    undefined,
                    (e) => { throw attributeInteropFault(moduleName, functionName, args, e); });
            }

            return result;
        } catch (e) {
            throw attributeInteropFault(moduleName, functionName, args, e);
        }
    };
}

const FETCH_FAULT_TAG = 0x4f2a9c61;

// A failed request names neither what it was fetching nor who asked: a network
// failure is a bare TypeError ("Load failed" in Safari, "Failed to fetch" in
// Chrome), and Safari gives it no stack at all. So a rejection nothing handled
// -- one of the runtime loader's parallel downloads, say -- reaches the crash
// report as "TypeError: Load failed" and nothing else. This adds the URL to the
// error's message, and the stack of the code that called fetch to its stack,
// and traces the failure. The error itself is kept (still a TypeError, with its
// own name), so code that tells network failures from others still can.
export function attributeFetchFault(url, callerStack, error) {
    if (!error || (typeof error !== 'object') || error.dgFetchUrl || (error.name === 'AbortError')) {
        return error;
    }

    const name = error.name || 'Error';
    const message = error.message || String(error);
    const where = relativeUrl(url);
    trace(FETCH_FAULT_TAG, `fetch ${where} failed: ${name}: ${message}`);

    try {
        error.dgFetchUrl = url;
        error.message = `${message} (fetching ${where})`;
        const stack = String(callerStack ?? '');
        const caller = stack ? `\nFetched from:\n${stack}` : '';
        error.stack = (error.stack ? String(error.stack) : `${name}: ${error.message}`) + caller;
    } catch {
        // A frozen or host-owned error keeps its own message; the trace has it.
    }

    return error;
}

// The stack of the code that called the caller of this function, without this
// function's frame, the "Error" line Chrome starts a stack with, or `skipFrames`
// more frames (wrappers between the caller and the code worth naming), as an
// object whose string is that stack. Only the Error is created here: V8 formats a
// stack when it's first read, which is most of the cost, so a request that
// succeeds (nearly all of them) never pays it.
export function captureCallerStack(skipFrames = 0) {
    const error = new Error();
    let text = null;
    return {
        toString() {
            if (text === null) {
                const stack = error.stack;
                if (!stack) {
                    text = '';
                } else {
                    const lines = String(stack).split('\n');
                    const start = (lines[0] && !/^\s*at\s|@/.test(lines[0])) ? 1 : 0;
                    text = lines.slice(start + 2 + skipFrames).join('\n');
                }
            }
            return text;
        },
    };
}

function describeRequest(input) {
    if (typeof input === 'string') {
        return input;
    }
    if (input && typeof input.url === 'string') {
        return input.url;
    }
    return String(input);
}

// Wraps the page's fetch so every failed request is attributed (see
// attributeFetchFault). Installed before the runtime is created, so its
// downloads are covered as well as the game's own.
export function instrumentFetch() {
    const nativeFetch = globalThis.fetch;
    if ((typeof nativeFetch !== 'function') || nativeFetch.dgInstrumented) {
        return;
    }

    const instrumentedFetch = function instrumentedFetch(input, init) {
        const url = describeRequest(input);
        const callerStack = captureCallerStack();
        let request;
        try {
            request = nativeFetch.call(this, input, init);
        } catch (e) {
            throw attributeFetchFault(url, callerStack, e);
        }
        return request.then(undefined, (e) => { throw attributeFetchFault(url, callerStack, e); });
    };
    instrumentedFetch.dgInstrumented = true;
    globalThis.fetch = instrumentedFetch;
}

// Classes reach the import table as ordinary function-valued properties (the
// graphics module exposes Texture2D, RenderTexture and SpriteBatch alongside
// its imports, and constructs them through `this`). Wrapping one in a plain
// function would break `new`, so they are left alone -- they are never
// [JSImport] targets.
function isClassConstructor(fn) {
    try {
        return /^\s*class[\s{]/.test(Function.prototype.toString.call(fn));
    } catch {
        return false;
    }
}

// Wraps the functions of a module registered with setModuleImports. `exports`
// may be the import table itself (input.js, game.js style) or a table of named
// sub-objects (graphics.js, audio.js style); both shapes are walked, one level
// deep, which is the depth the [JSImport] lookup path itself supports.
export function instrumentModuleImports(moduleName, exports) {
    if (!exports || typeof exports !== 'object') {
        return exports;
    }

    for (const key of Object.keys(exports)) {
        const value = exports[key];

        if (typeof value === 'function') {
            if (!isClassConstructor(value)) {
                exports[key] = instrumentFunction(moduleName, key, value);
            }
            continue;
        }

        // A nested import table: `graphics.createTexture2D` is reached as
        // exports.graphics.createTexture2D, so the leaf names must carry the
        // qualifier the managed [JSImport] attribute uses.
        if (value && typeof value === 'object' && !Array.isArray(value)) {
            for (const nested of Object.keys(value)) {
                const member = value[nested];
                if (typeof member === 'function'
                    && !isClassConstructor(member)
                    && !nested.startsWith('_')) {
                    value[nested] = instrumentFunction(`${key}`, nested, member);
                }
            }
        }
    }

    return exports;
}

function collectDiagnosticContext() {
    const parts = [];

    try {
        parts.push(`uptime=${Math.round(performance.now() / 1000)}s`);
    } catch { /* timing unavailable */ }

    // Chromium-only, and absent on the Safari builds where memory kills are
    // most common -- reported when present because it is decisive when it is.
    try {
        const memory = performance.memory;
        if (memory && memory.usedJSHeapSize) {
            parts.push(`jsHeap=${Math.round(memory.usedJSHeapSize / 1048576)}MB`
                + `/${Math.round(memory.jsHeapSizeLimit / 1048576)}MB`);
        }
    } catch { /* not exposed */ }

    try {
        if (navigator.deviceMemory) {
            parts.push(`deviceMemory=${navigator.deviceMemory}GB`);
        }
        if (navigator.hardwareConcurrency) {
            parts.push(`cores=${navigator.hardwareConcurrency}`);
        }
    } catch { /* not exposed */ }


    return parts.join(' ');
}

// ======================= Crash persistence ========================
//
// A crash report is only useful if it survives long enough to be read. Console
// output does not qualify: a reload, a navigation, or a runtime abort wipes it,
// and on mobile the console is often not open at the moment of failure. Every
// crash is therefore written synchronously to localStorage before the dialog is
// rendered, and replayed to the console on the next page load.
//
// Console hooks:
//
//     dgCrashes()       re-print everything currently stored
//     dgClearCrashes()  discard it
//
// Records are capped in count and size so this can never fill the origin's
// storage quota and break the game's own saved state, which shares it.

const CRASH_STORAGE_KEY = 'dgCrashReports';

// A single fault typically produces two records -- the managed report and the
// JS-boundary view of the same unwind -- so this holds roughly five crashes.
// That is deliberately enough to see a crash *loop* (crash, reload, crash),
// which is the scenario this store exists for. Worst case is a few hundred KB
// against a multi-megabyte origin quota, and the writer prunes automatically if
// the quota is ever hit.
const CRASH_MAX_RECORDS = 10;
const CRASH_MAX_DETAILS = 48 * 1024;

// The failures after a crash kept with its record (recordSecondaryFailure)
const CRASH_MAX_SECONDARY = 4;
const CRASH_MAX_SECONDARY_DETAILS = 8 * 1024;

function readCrashRecords() {
    try {
        const raw = window.localStorage.getItem(CRASH_STORAGE_KEY);
        if (!raw) {
            return [];
        }
        const parsed = JSON.parse(raw);
        return Array.isArray(parsed) ? parsed : [];
    } catch {
        // Unreadable or corrupt (a partial write from a page killed mid-write).
        // Treated as empty rather than throwing out of a crash path.
        return [];
    }
}

function writeCrashRecords(records) {
    // Storage is shared with the game's saved state, and a crash record is the
    // least important thing in it. On a quota failure, drop the oldest records
    // and retry rather than let the write fail outright -- and give up quietly
    // rather than throw from inside a crash handler.
    let candidate = records.slice(-CRASH_MAX_RECORDS);
    while (candidate.length > 0) {
        try {
            window.localStorage.setItem(CRASH_STORAGE_KEY, JSON.stringify(candidate));
            return true;
        } catch {
            candidate = candidate.slice(1);
        }
    }
    try {
        window.localStorage.removeItem(CRASH_STORAGE_KEY);
    } catch { /* storage unavailable entirely */ }
    return false;
}

function truncate(text, limit) {
    const value = (text === undefined || text === null) ? '' : String(text);
    return (value.length > limit)
        ? `${value.slice(0, limit)}\n... [truncated ${value.length - limit} chars]`
        : value;
}

/// Persists one crash. Called before the dialog is rendered so the record
/// exists even if rendering itself fails or the page dies immediately after.
/// `classification` is optional: { level, tags }, which a crash reporter
/// (the game's own, such as one for Sentry) sends as the event's level and tags. A dead
/// session's record is tagged session_end, and only 'on_screen' is reported.
export function recordCrash(title, summary, details, classification) {
    try {
        const records = readCrashRecords();
        const record = {
            time: new Date().toISOString(),
            url: (typeof location !== 'undefined') ? location.href : '',
            userAgent: (typeof navigator !== 'undefined') ? navigator.userAgent : '',
            context: collectDiagnosticContext(),
            title: truncate(title || 'Unhandled Exception', 512),
            summary: truncate(summary, 4096),
            details: truncate(details, CRASH_MAX_DETAILS),
            traces: recentTraces.slice(),
            reported: false
        };
        if (classification) {
            if (classification.level) {
                record.level = classification.level;
            }
            if (classification.tags) {
                record.tags = classification.tags;
            }
        }
        records.push(record);
        writeCrashRecords(records);
        return record.time;
    } catch {
        // Recording must never mask the crash it is describing.
        return null;
    }
}

// The record of the crash this page's dialog shows
let dialogCrashTime = null;

/// Persists a failure that arrives while the crash dialog is up. It is the same
/// crash unwinding further (the startup task rejecting with the exception the
/// dialog shows, the runtime aborting after it), so it is added to the dialog's
/// record, where it may explain the crash, rather than reported as a crash of
/// its own. It is kept apart from the record's details, whose stack is the
/// crash's (a crash reporter parses it for the crash's frames).
export function recordSecondaryFailure(title, summary, details) {
    try {
        const records = readCrashRecords();
        const record = (dialogCrashTime !== null)
            ? records.find(r => r.time === dialogCrashTime)
            : undefined;
        if (!record) {
            recordCrash(title, summary, details);
            return;
        }
        const secondary = Array.isArray(record.secondary) ? record.secondary : [];
        if (secondary.length < CRASH_MAX_SECONDARY) {
            secondary.push({
                time: new Date().toISOString(),
                title: truncate(title || 'Unhandled Exception', 512),
                summary: truncate(summary, 4096),
                details: truncate(details, CRASH_MAX_SECONDARY_DETAILS)
            });
        }
        record.secondary = secondary;
        writeCrashRecords(records);
    } catch {
        // Recording must never mask the crash it is describing.
    }
}

function formatCrashRecord(record, index, total) {
    const parts = [
        `--- crash ${index + 1}/${total} --------------------------------`,
        `when:  ${record.time}`,
        `url:   ${record.url}`,
        `agent: ${record.userAgent}`
    ];
    if (record.context) {
        parts.push(`state: ${record.context}`);
    }
    parts.push(`${record.title}: ${record.summary}`);
    if (record.details) {
        parts.push('', record.details);
    }
    for (const failure of (record.secondary || [])) {
        parts.push('', `--- then, ${failure.time}: ${failure.title}: ${failure.summary} ---`, failure.details);
    }
    if (record.traces && record.traces.length) {
        parts.push('', `--- last ${record.traces.length} trace entries before the crash ---`, record.traces.join('\n'));
    }
    return parts.join('\n');
}

function printCrashRecords(records) {
    for (let i = 0; i < records.length; i++) {
        try {
            console.error(formatCrashRecord(records[i], i, records.length));
        } catch { /* console unavailable */ }
    }
}

// Replays crashes from previous page loads. Runs at module import time -- ahead
// of the .NET runtime -- so a crash that killed the last load is on screen
// before anything else can fail. Already-printed records are kept (they are
// still retrievable via dgCrashes) but are not re-printed on every reload,
// which would bury the current session's output.
function replayStoredCrashes() {
    const records = readCrashRecords();
    if (records.length === 0) {
        return;
    }

    const unreported = records.filter(r => !r.reported);
    if (unreported.length > 0) {
        try {
            console.error(`[crash] ${unreported.length} crash report(s) recorded before this page load:`);
        } catch { /* console unavailable */ }
        printCrashRecords(unreported);
        try {
            console.info('[crash] dgCrashes() re-prints these; dgClearCrashes() discards them.');
        } catch { /* console unavailable */ }

        for (const record of records) {
            record.reported = true;
        }
        writeCrashRecords(records);
    } else {
        try {
            console.info(`[crash] ${records.length} earlier crash report(s) stored; call dgCrashes() to view.`);
        } catch { /* console unavailable */ }
    }
}

window.dgCrashes = function () {
    const records = readCrashRecords();
    if (records.length === 0) {
        try {
            console.info('[crash] no crash reports stored.');
        } catch { /* console unavailable */ }
    } else {
        printCrashRecords(records);
    }
    return records;
};

window.dgClearCrashes = function () {
    try {
        window.localStorage.removeItem(CRASH_STORAGE_KEY);
        console.info('[crash] stored crash reports cleared.');
    } catch { /* storage unavailable */ }
};

// ---------------- Abnormal termination detection ----------------
//
// The failures above all run *some* code at the moment of failure. A tab that
// is killed outright does not: an out-of-memory kill, a browser/GPU process
// crash, a wasm trap that takes the tab down, or the user force-reloading a
// hung page all end the session with no handler ever firing. Nothing can be
// recorded at crash time, so instead a live "session" marker is kept in
// storage and cleared on orderly shutdown. A marker still present on the next
// load means the previous session died without one.
//
// The marker carries a rolling tail of recent traces, so an abnormal
// termination is still reported with the context that led up to it. It is
// rewritten on a throttle (never per trace) to keep this off the hot path.
//
// It also records where the page was, so the report can tell a session that
// died in front of the player from one the player had already left:
//
//   on screen        visible and focused. A hang or a crash the player saw;
//                    shown to them at the next launch and reported as fatal.
//                    A hung main thread runs no handlers, so its last marker
//                    is the focused heartbeat before the hang: still on screen.
//   not focused      visible, but another window or app had the focus: the
//                    Android tab switcher or notification shade, a desktop
//                    window on a second monitor, or a tab closed or killed
//                    straight after a blur. Normal (the player left), so like
//                    the background: recorded with all its details (dgCrashes),
//                    but not shown to the player and not reported.
//   in the background hidden: most likely reclaimed by the system. Recorded,
//                    not shown, and not reported.
//   on a phone       any of these, on a phone or tablet: its last word can't be
//                    trusted, as the system can kill the tab as the player leaves,
//                    before the browser has saved what was written. Analytics
//                    showed a session sending its backgrounded event after the
//                    last marker that reached storage, which still said on screen.
//                    Recorded, not shown, and not reported. A
//                    phone's real hang looks the same, so it goes unreported too.
//
// So the marker is written synchronously whenever any of these change (blur,
// focus, visibilitychange, freeze, resume), not only on the heartbeat: the
// last word must be the state the page died in. Markers from builds that
// didn't record the focus keep the old rule (visible means on screen).

const SESSION_KEY_PREFIX = 'dgSession:';
const SESSION_TRACE_TAIL = 32;
const SESSION_HEARTBEAT_MS = 2000;
// A dead marker is proven dead by watching it for longer than one heartbeat:
// a live tab advances its own marker within this window, a dead one cannot.
const SESSION_LIVENESS_WINDOW_MS = 5000;

// Declared before any function that reads it, so a trace arriving during
// module evaluation can never hit the temporal dead zone.
const sessionStarted = new Date().toISOString();

// Storage is shared by every tab on this origin, so each session owns a
// uniquely keyed marker. Without this, a second tab would mistake a first
// tab's live marker for the remains of a crashed session.
const sessionKey = SESSION_KEY_PREFIX + sessionStarted + ':' +
    Math.random().toString(36).slice(2, 10);

let sessionClosed = false;

function documentHasFocus() {
    try {
        return (typeof document === 'undefined') || (typeof document.hasFocus !== 'function')
            || document.hasFocus();
    } catch {
        return true;
    }
}

// `cause` names what wrote the marker (the heartbeat, or the event), so a
// report shows whether the session's last word was a blur, say.
function writeSessionMarker(cause) {
    if (sessionClosed) {
        return;
    }
    try {
        window.localStorage.setItem(sessionKey, JSON.stringify({
            started: sessionStarted,
            url: (typeof location !== 'undefined') ? location.href : '',
            userAgent: (typeof navigator !== 'undefined') ? navigator.userAgent : '',
            updated: Date.now(),
            cause: (typeof cause === 'string') ? cause : 'heartbeat',
            // Where the page was (see above): visible and focused is on
            // screen, visible alone is not focused, and hidden is in the
            // background.
            visible: (typeof document === 'undefined') || (document.visibilityState !== 'hidden'),
            focused: documentHasFocus(),
            touchPoints: navigator.maxTouchPoints || 0,
            // Refreshed on every heartbeat, so the last value written is the
            // state moments before the tab died -- which for a memory kill is
            // the single most useful number in the whole report.
            context: collectDiagnosticContext(),
            traces: recentTraces.slice(-SESSION_TRACE_TAIL)
        }));
    } catch { /* storage unavailable */ }
}

// Clears this session's marker: it ended in an orderly way. pagehide is the
// reliable end-of-life signal across browsers (including iOS Safari, where
// unload is not).
function closeSessionMarker() {
    sessionClosed = true;
    try {
        window.localStorage.removeItem(sessionKey);
    } catch { /* storage unavailable */ }
}

function readSessionMarker(key) {
    try {
        const raw = window.localStorage.getItem(key);
        return raw ? JSON.parse(raw) : null;
    } catch {
        return null;
    }
}

function listForeignSessionKeys() {
    const keys = [];
    try {
        for (let i = 0; i < window.localStorage.length; i++) {
            const key = window.localStorage.key(i);
            if (key && key.startsWith(SESSION_KEY_PREFIX) && key !== sessionKey) {
                keys.push(key);
            }
        }
    } catch { /* storage unavailable */ }
    return keys;
}

// Whether a marker came from a phone or tablet. An iPad's agent says Macintosh, so
// its touch points tell them apart.
function isMobileMarker(marker) {
    const agent = marker.userAgent || '';
    return /Android|iPhone|iPad|iPod|Mobile/i.test(agent)
        || (/Macintosh/.test(agent) && (marker.touchPoints > 1));
}

// Where a dead session was when its marker was last written (see the
// marker's comment): 'on_screen', 'not_focused' or 'background'. A marker
// without `focused` (an older build) keeps the old rule: visible is on screen.
function deadSessionState(marker) {
    if (isMobileMarker(marker)) {
        return 'on_a_phone';
    }
    if (marker.visible !== true) {
        return 'background';
    }
    return (marker.focused === false) ? 'not_focused' : 'on_screen';
}

// A coarse bucket of the marker's age, for a Sentry tag (the exact age is in
// the details). A small one means the next launch came straight after: a page
// reloaded or reopened because it hung, say.
function markerAgeBucket(ms) {
    const seconds = ms / 1000;
    if (!(seconds >= 0)) return 'unknown';
    if (seconds < 60) return 'under_1m';
    if (seconds < 600) return 'under_10m';
    if (seconds < 3600) return 'under_1h';
    if (seconds < 86400) return 'under_1d';
    return 'over_1d';
}

const DEAD_SESSION_REPORTS = {
    on_screen: {
        title: 'Session ended abnormally',
        summary: 'The previous session stopped while it was on screen and focused, '
            + 'without shutting down: it hung, or was killed (out of memory, a '
            + 'browser/renderer crash, a wasm trap, or closed or reloaded while frozen).',
        level: 'fatal'
    },
    not_focused: {
        title: 'Session ended while not focused',
        summary: 'The previous session stopped while it was visible but not focused '
            + '(another window or app had the focus: a tab switcher, the notification '
            + 'shade, a window on another monitor), without shutting down. Most likely '
            + 'the player left and the tab was closed or killed.',
        level: 'info'
    },
    on_a_phone: {
        title: 'Session ended on a phone',
        summary: 'The previous session stopped without shutting down on a phone or tablet, '
            + 'where the system can kill a tab as the player leaves it, before what it last '
            + 'saved has been written. Most likely the player left.',
        level: 'info'
    },
    background: {
        title: 'Session ended in the background',
        summary: 'The previous session ended while it was in the background, most '
            + 'likely reclaimed by the system.',
        level: 'info'
    }
};

// Records a session that ended without shutting down, and returns the report
// to show the player when it ended on screen (null otherwise: in the
// background, the system reclaiming a suspended page is normal, and when it
// wasn't focused, the player had most likely left).
function reportDeadSession(marker) {
    // Promoted to a full crash record so it is replayed, retrievable via
    // dgCrashes(), and retained exactly like any other crash.
    const state = deadSessionState(marker);
    const report = DEAD_SESSION_REPORTS[state];
    const title = report.title;
    const summary = report.summary;
    const sinceWrite = Date.now() - marker.updated;
    const focus = (marker.focused === undefined) ? 'not recorded' : String(marker.focused);
    const details = [
        `state:           ${state.replace('_', ' ')} (visible ${marker.visible === true}, focused ${focus})`,
        `session started: ${marker.started}`,
        `last activity:   ${new Date(marker.updated).toISOString()} (the last marker write, by ${marker.cause || 'not recorded'})`,
        `since then:      ${Math.round(sinceWrite / 1000)}s before this report (heartbeat every ${SESSION_HEARTBEAT_MS / 1000}s)`,
        `lifetime:        ${Math.round((marker.updated - Date.parse(marker.started)) / 1000)}s`,
        `url:             ${marker.url}`,
        `agent:           ${marker.userAgent}`,
        `state at death:  ${marker.context || '(not captured)'}`,
        '',
        (marker.traces && marker.traces.length)
            ? `--- last ${marker.traces.length} trace entries of the dead session ---\n${marker.traces.join('\n')}`
            : '(no trace context was captured)'
    ].join('\n');
    recordCrash(title, summary, details, {
        level: report.level,
        tags: {
            session_end: state,
            session_focus_recorded: String(marker.focused !== undefined),
            session_last_write: marker.cause || 'unknown',
            session_marker_age: markerAgeBucket(sinceWrite)
        }
    });
    return (state === 'on_screen') ? { title, summary, details } : null;
}

// Markers left by other sessions are only proof of a crash if nobody is still
// updating them. Rather than guess from age alone -- which would misreport a
// long-idle but perfectly healthy tab -- each candidate is watched for longer
// than one heartbeat. A live tab advances its marker within that window; a tab
// that was killed cannot.
function detectAbnormalTermination() {
    const candidates = listForeignSessionKeys();
    if (candidates.length === 0) {
        return;
    }

    const snapshot = new Map();
    for (const key of candidates) {
        const marker = readSessionMarker(key);
        if (marker) {
            snapshot.set(key, marker.updated);
        }
    }
    if (snapshot.size === 0) {
        return;
    }

    setTimeout(() => {
        let found = 0;
        let shown = null;
        for (const [key, seenAt] of snapshot) {
            const marker = readSessionMarker(key);
            if (!marker) {
                // The owning tab shut down cleanly while we were watching.
                continue;
            }
            if (marker.updated !== seenAt) {
                // Still beating: another tab is alive and well.
                continue;
            }
            shown = reportDeadSession(marker) || shown;
            found++;
            try {
                window.localStorage.removeItem(key);
            } catch { /* storage unavailable */ }
        }
        if (found > 0) {
            replayStoredCrashes();
        }
        if (shown) {
            showPreviousSessionReport(shown.title, shown.summary, shown.details);
        }
    }, SESSION_LIVENESS_WINDOW_MS);
}

if (typeof window.addEventListener === 'function') {
    // pagehide ends the session: the marker is removed rather than
    // rewritten. A page kept in the back/forward cache comes back with
    // pageshow (persisted), which opens its marker again.
    window.addEventListener('pagehide', closeSessionMarker);
    window.addEventListener('pageshow', (event) => {
        if (event && event.persisted) {
            sessionClosed = false;
            writeSessionMarker('pageshow');
        }
    });
    // Each written synchronously, in the handler: the next heartbeat may never
    // come. Hidden and frozen pages flush before they can be discarded (the
    // marker stays, because being hidden is not a shutdown); blur records
    // that the player has left, even when no hidden state follows (the
    // Android tab switcher, the notification shade, a kill straight after);
    // focus and the return to visible say it is on screen again right away.
    window.addEventListener('blur', () => writeSessionMarker('blur'));
    window.addEventListener('focus', () => writeSessionMarker('focus'));
    // visibilitychange, freeze and resume are fired at the document
    if (typeof document !== 'undefined' && typeof document.addEventListener === 'function') {
        document.addEventListener('visibilitychange', () => writeSessionMarker('visibilitychange'));
        document.addEventListener('freeze', () => writeSessionMarker('freeze'));
        document.addEventListener('resume', () => writeSessionMarker('resume'));
    }
}

writeSessionMarker('start');
// An unconditional heartbeat: liveness must not depend on the game happening
// to emit traces, or an idle-but-healthy tab would look dead.
try {
    const heartbeat = setInterval(() => writeSessionMarker('heartbeat'), SESSION_HEARTBEAT_MS);
    if (typeof heartbeat === 'object' && typeof heartbeat.unref === 'function') {
        heartbeat.unref();
    }
} catch { /* timers unavailable */ }

detectAbnormalTermination();

replayStoredCrashes();

// ========================== Crash dialog ==========================

function ensureStyles() {
    if (document.getElementById('dg-crash-style')) return;
    const style = document.createElement('style');
    style.id = 'dg-crash-style';
    style.textContent =
        // touch-action and user-select are reasserted here: the host page sets
        // them to none so the game surface never scrolls or shows a selection
        // magnifier, and the details pane must scroll and be selectable.
        '.dg-crash-backdrop{position:fixed;inset:0;background:rgba(0,0,0,0.65);z-index:2147483646;display:flex;align-items:center;justify-content:center;font-family:system-ui,sans-serif;touch-action:auto;-webkit-user-select:text;user-select:text;}' +
        '.dg-crash-dialog{background:#1e1e1e;color:#eee;max-width:min(90vw,720px);max-height:80vh;display:flex;flex-direction:column;border:1px solid #555;border-radius:6px;padding:16px;box-shadow:0 8px 32px rgba(0,0,0,0.5);}' +
        '.dg-crash-title{font-size:1.1rem;font-weight:600;margin:0 0 8px 0;color:#ff6b6b;}' +
        '.dg-crash-summary{margin:0 0 12px 0;font-family:ui-monospace,Consolas,monospace;font-size:0.9rem;white-space:pre-wrap;word-break:break-word;}' +
        '.dg-crash-details{flex:1 1 auto;overflow:auto;background:#0f0f0f;color:#ddd;border:1px solid #333;border-radius:4px;padding:8px;font-family:ui-monospace,Consolas,monospace;font-size:0.8rem;white-space:pre;}' +
        '.dg-crash-buttons{display:flex;gap:8px;justify-content:flex-end;margin-top:12px;}' +
        '.dg-crash-btn{padding:6px 14px;border:1px solid #555;background:#333;color:#eee;border-radius:4px;cursor:pointer;font:inherit;}' +
        '.dg-crash-btn:hover{background:#444;}' +
        '.dg-crash-btn-primary{border-color:#888;}' +
        // The unsupported-browser message: for players, so plain text and no
        // error colors
        '.dg-message-title{font-size:1.1rem;font-weight:600;margin:0 0 8px 0;}' +
        '.dg-message-text{margin:0;font-size:1rem;line-height:1.4;}';
    document.head.appendChild(style);
}

// Copies text, and says whether it did. Call it first thing in a tap's handler,
// with no await before it: Safari allows a clipboard write only within the
// tap's user gesture, which writeText() is still in, being called at once. The
// execCommand fallback runs only once writeText() has failed, possibly outside
// the gesture, so its result is checked rather than assumed.
async function copyToClipboardImpl(text) {
    if (!text) return false;
    if (navigator.clipboard && window.isSecureContext) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch { /* fall back */ }
    }
    let ta = null;
    try {
        ta = document.createElement('textarea');
        ta.value = text;
        // Read-only, so iOS doesn't raise the keyboard for it
        ta.readOnly = true;
        ta.style.position = 'fixed';
        ta.style.opacity = '0';
        document.body.appendChild(ta);
        ta.select();
        // iOS selects nothing with select() alone
        ta.setSelectionRange(0, text.length);
        return document.execCommand('copy');
    } catch {
        return false;
    } finally {
        if (ta && ta.parentNode) ta.parentNode.removeChild(ta);
    }
}

// How long Copy details shows how the copy went, before it reads Copy details again
const COPY_FEEDBACK_MS = 2000;

// The host installs global input listeners that call preventDefault() on every
// pointer, touch and context-menu event, so the game -- which fills the
// viewport -- never sees scrolling, selection, or platform gestures. That also
// suppresses the synthesized click, which would leave the buttons below dead.
// They are torn down rather than made conditional, because testing each event
// for an exemption would put work on the input path the game cannot afford.
// Doing so is safe for the crash dialog, after which the game does not resume;
// a previous session's report resumes input when the player closes it.
//
// The hook is published by input.js on window rather than imported, because
// input.js imports this module for tracing (so this module must not import
// back) and the dialog has to work even when input was never initialized --
// exactly the case for a crash during boot, where there is nothing to suspend.
function suspendHostInput() {
    try {
        const suspend = window.dgSuspendInput;
        if (typeof suspend === 'function') {
            suspend();
        }
    } catch { /* input module unavailable or already gone */ }
}

function resumeHostInput() {
    try {
        const resume = window.dgResumeInput;
        if (typeof resume === 'function') {
            resume();
        }
    } catch { /* input module unavailable or already gone */ }
}

const PREVIOUS_SESSION_REPORT_ID = 'dg-previous-session-backdrop';
// How long the report waits for this session's splash to go, before it is shown
// over a game that hasn't started
const PREVIOUS_SESSION_REPORT_WAIT_MS = 60000;

// Builds the dialog both crash reports use: a title, a summary, the details,
// and a row of buttons that starts with Copy details. The caller adds its
// other buttons and puts it on the page.
function createReportDialog(id, title, summary, details) {
    ensureStyles();

    // Remove any previous instance — re-shown crashes replace.
    const existing = document.getElementById(id);
    if (existing) existing.remove();

    const backdrop = document.createElement('div');
    backdrop.id = id;
    backdrop.className = 'dg-crash-backdrop';

    const dialog = document.createElement('div');
    dialog.className = 'dg-crash-dialog';
    dialog.setAttribute('role', 'alertdialog');
    dialog.setAttribute('aria-modal', 'true');

    const h = document.createElement('h2');
    h.className = 'dg-crash-title';
    h.textContent = title || 'Unhandled Exception';

    const s = document.createElement('div');
    s.className = 'dg-crash-summary';
    s.textContent = summary || '';

    const d = document.createElement('pre');
    d.className = 'dg-crash-details';
    d.textContent = details || '';

    const buttons = document.createElement('div');
    buttons.className = 'dg-crash-buttons';

    const copyBtn = document.createElement('button');
    copyBtn.className = 'dg-crash-btn dg-crash-btn-primary';
    copyBtn.textContent = 'Copy details';
    const clipboardText = [title || 'Unhandled Exception', summary || '', details || '']
        .filter(part => part !== '')
        .join('\n\n');
    let copyFeedbackTimer = 0;
    copyBtn.onclick = async () => {
        // Each tap copies again and shows its own outcome
        const copied = await copyToClipboardImpl(clipboardText);
        copyBtn.textContent = copied ? 'Copied!' : "Couldn't copy";
        clearTimeout(copyFeedbackTimer);
        copyFeedbackTimer = setTimeout(() => {
            copyBtn.textContent = 'Copy details';
        }, COPY_FEEDBACK_MS);
    };
    buttons.appendChild(copyBtn);

    dialog.appendChild(h);
    dialog.appendChild(s);
    dialog.appendChild(d);
    dialog.appendChild(buttons);
    backdrop.appendChild(dialog);

    return { backdrop, buttons };
}

// What the crash dialog stops: the game's loops and its audio
const crashStopHandlers = [];

export function onCrashDialog(stop) {
    crashStopHandlers.push(stop);
}

function stopGame() {
    for (const stop of crashStopHandlers) {
        try {
            stop();
        } catch { /* stopping must never block the dialog */ }
    }
}

// The app's splash screen is an opaque, fixed-position overlay that paints
// before the WASM runtime takes over. A crash can occur while it is still
// visible (a fatal during runtime download/boot, or early engine startup before
// the splash is removed after runMain). On some browsers -- notably iOS Safari
// -- a fixed-position splash can paint over a dialog even though the dialog
// uses a higher z-index, so the dialog renders but stays hidden behind the
// splash. Removing the splash keeps the dialog on top. '#splash' is the same
// host<->app convention main.js uses to hide the splash after startup.
function removeSplash() {
    try {
        const splash = document.getElementById('splash');
        if (splash) {
            splash.remove();
        }
    } catch { /* splash removal must never block a dialog */ }
}

// ======================= Crash reporter ========================
//
// A crash reporter (the game's own, such as one for Sentry) sends each crash the
// dialog shows before the dialog appears: the player can't reload the page, or
// close it thinking the game is done, while the report is still on its way. The
// reporter gets the crash's record (recordCrash's fields) and returns a promise
// that settles once the report is sent, or has failed; the dialog waits for it
// up to CRASH_REPORT_WAIT_MS, then shows anyway. Its record stays in storage
// either way, for the reporter to send at the next launch if it couldn't now.

const CRASH_REPORT_WAIT_MS = 5000;

let crashReporter = null;

/// Sets the crash reporter: report(record) returns a promise that settles once
/// the crash is sent (or sending it failed). One reporter; a later one replaces it.
export function registerCrashReporter(report) {
    crashReporter = (typeof report === 'function') ? report : null;
}

// Crawlers and link previewers that run the page: what fails for them (often no WebGL 2) says
// nothing about players, so it's recorded and traced but not reported. Crawlers name themselves in
// a "compatible;" part; a bare "bot" would also match phones (Cubot)
const CRAWLER_AGENT = /compatible;[^;)]*(?:bot|crawler|spider)\b|googlebot|bingbot|applebot|crawler|spider|slurp|facebookexternalhit|meta-externalagent|meta-webindexer|bingpreview|google-inspectiontool|chrome-lighthouse/i;

function isCrawler() {
    try {
        return CRAWLER_AGENT.test(navigator.userAgent || '');
    } catch {
        return false;
    }
}

function reportCrash(time) {
    const report = crashReporter;
    if (!report || (time === null)) {
        return Promise.resolve();
    }
    if (isCrawler()) {
        trace(0x6d2a90f6, 'not reported: a crawler');
        return Promise.resolve();
    }
    let record;
    try {
        record = readCrashRecords().find(r => r.time === time);
    } catch {
        record = undefined;
    }
    if (!record) {
        return Promise.resolve();
    }
    let timer = 0;
    const timeout = new Promise((resolve) => {
        timer = setTimeout(() => {
            try { trace(0x6d2a90f3, `crash report not sent within ${CRASH_REPORT_WAIT_MS} ms; the dialog shows now`); } catch { }
            resolve();
        }, CRASH_REPORT_WAIT_MS);
    });
    let sending;
    try {
        sending = Promise.resolve(report(record)).catch((error) => {
            try { trace(0x6d2a90f4, `crash reporter failed: ${(error && error.message) || error}`); } catch { }
        });
    } catch (error) {
        try { trace(0x6d2a90f4, `crash reporter failed: ${(error && error.message) || error}`); } catch { }
        sending = Promise.resolve();
    }
    return Promise.race([sending, timeout]).finally(() => clearTimeout(timer));
}

/// Records and reports a failure the player is told about with a message rather than the crash
/// dialog: a browser that can't run the game, or a page that got something other than the file it
/// asked for. `classification` is recordCrash's ({ level, tags }). The crash reporter sends it as
/// it would a crash (or at the next launch, if there's none yet or it can't now); the promise
/// settles once it's sent, or after the dialog's wait at most. Never throws.
export function reportFailure(title, summary, details, classification) {
    try {
        trace(0x6d2a90f5, `reported failure: ${title}: ${summary}`);
        return reportCrash(recordCrash(title, summary, details, classification));
    } catch {
        return Promise.resolve();
    }
}

// The crash dialog's state: from the crash until the page goes, whether it's
// rendered yet or waits for its report
let crashDialogShown = false;
let crashDialogContent = null;

// The crash reports the dialog still waits for
let crashReportsSending = 0;

/// Whether this page has had a crash: its dialog is up, or about to be once its
/// report is sent. A later failure is the same crash unwinding further.
export function isCrashDialogShown() {
    return crashDialogShown;
}

export function showCrashDialog(title, summary, details) {
    // Persist first. Rendering can fail, the page can be reloaded by the very
    // fault being reported, or the runtime can abort a moment later -- in every
    // one of those cases the record is already durable.
    const time = recordCrash(title, summary, details);

    // The game doesn't resume from this dialog, so it stops here rather than
    // running on behind it in an unknown state, possibly saving that state
    stopGame();

    // A second report of the same crash (the managed report after the page's,
    // say) replaces what the dialog shows, as it's the richer one, and is sent
    // too: the dialog shows once every report made so far is sent
    if (!crashDialogShown) {
        dialogCrashTime = time;
    }
    crashDialogShown = true;
    crashDialogContent = { title, summary, details };

    try {
        console.error(`[crashDialog] ${title}: ${summary}\n${details}`);
    } catch { /* console unavailable */ }

    crashReportsSending++;
    reportCrash(time).finally(() => {
        crashReportsSending--;
        if (crashReportsSending === 0) {
            renderCrashDialog();
        }
    });
}

function renderCrashDialog() {
    const { title, summary, details } = crashDialogContent;
    try {
        suspendHostInput();

        // A fatal error replaces a previous session's report: it is the one
        // the player can still act on, and the game behind both is gone.
        const previous = document.getElementById(PREVIOUS_SESSION_REPORT_ID);
        if (previous) previous.remove();

        const { backdrop, buttons } = createReportDialog('dg-crash-backdrop', title, summary, details);

        const restartBtn = document.createElement('button');
        restartBtn.className = 'dg-crash-btn';
        restartBtn.textContent = 'Restart';
        restartBtn.onclick = () => {
            // A reload is the only reliable recovery: the runtime is already
            // dead, so restarting the app means restarting the page.
            try {
                window.location.reload();
            } catch { /* navigation blocked -- leave the dialog in place */ }
        };

        // There is deliberately no Close button: the dialog is only shown for a
        // fatal error, the game does not resume behind it, and dismissing it
        // would leave the user staring at a dead canvas with the report gone.
        // Restart reloads instead, which is the only way back to a live game.
        buttons.appendChild(restartBtn);

        document.body.appendChild(backdrop);
        removeSplash();
    } catch (e) {
        // Last-resort fallback — never throw from the dialog path.
        try { console.error('[crashDialog] failed to render', e); } catch { }
        try { window.alert(`${title || 'Unhandled Exception'}\n\n${summary || ''}\n\n${details || ''}`); } catch { }
    }
};

// Shows the player the report of a previous session that stopped on screen
// (it hung, or was killed), so a freeze or a crash that took the page down
// with it is surfaced like any other crash. It is already recorded. Unlike a
// fatal error's dialog the game here is alive, so it waits until the game is
// showing (over the splash it would hide the start), and Continue closes it
// and gives the game its input back.
function showPreviousSessionReport(title, summary, details) {
    const deadline = Date.now() + PREVIOUS_SESSION_REPORT_WAIT_MS;
    const show = () => {
        if (crashDialogShown) {
            // This session's own fatal error is showing (or about to); it takes precedence.
            return;
        }
        if (document.getElementById('splash') && (Date.now() < deadline)) {
            setTimeout(show, 250);
            return;
        }

        try {
            console.error(`[crashDialog] ${title}: ${summary}\n${details}`);

            suspendHostInput();

            const { backdrop, buttons } = createReportDialog(PREVIOUS_SESSION_REPORT_ID, title, summary, details);

            const continueBtn = document.createElement('button');
            continueBtn.className = 'dg-crash-btn';
            continueBtn.textContent = 'Continue';
            continueBtn.onclick = () => {
                backdrop.remove();
                resumeHostInput();
            };
            buttons.appendChild(continueBtn);

            document.body.appendChild(backdrop);
            removeSplash();
        } catch (e) {
            try { console.error('[crashDialog] failed to render', e); } catch { }
            resumeHostInput();
        }
    };
    show();
}

// Tells the player the game can't run in this browser or on this device. Unlike
// the crash dialog it's written for players: no details to copy, and no Restart,
// since restarting can't help. It isn't a crash either: nothing failed, the
// device just lacks what the game needs (main.js reports it, as a warning).
export function showUnsupportedMessage(title, message) {
    try {
        console.warn(`[unsupported] ${title}: ${message}`);

        ensureStyles();

        const backdrop = document.createElement('div');
        backdrop.id = 'dg-unsupported-backdrop';
        backdrop.className = 'dg-crash-backdrop';

        const dialog = document.createElement('div');
        dialog.className = 'dg-crash-dialog';
        dialog.setAttribute('role', 'alertdialog');
        dialog.setAttribute('aria-modal', 'true');

        const h = document.createElement('h2');
        h.className = 'dg-message-title';
        h.textContent = title;

        const p = document.createElement('p');
        p.className = 'dg-message-text';
        p.textContent = message;

        dialog.appendChild(h);
        dialog.appendChild(p);
        backdrop.appendChild(dialog);
        document.body.appendChild(backdrop);

        // Remove the splash, which can paint over a fixed-position dialog on iOS
        // Safari (see showCrashDialog)
        try {
            const splash = document.getElementById('splash');
            if (splash) {
                splash.remove();
            }
        } catch { /* splash removal must never block the message */ }
    } catch {
        try { window.alert(`${title}\n\n${message}`); } catch { }
    }
}

// Why the game couldn't get a file it needs, which says what the player is told:
//   'offline'   a download that still failed after its retries: the connection
//   'stale'     the host doesn't have it (a 404): a page left from an earlier deploy, often one a
//               phone restored from its cache, asking for files the current deploy replaced
//   'module'    one of the page's scripts didn't download: after a deploy, as 'stale', or offline
//   'bad-file'  what came isn't the file (a page or an error in its place)
const LoadFailureMessages = {
    offline: 'Check your internet connection, then try again.',
    stale: 'The game has been updated. Reload to play the latest version.',
    module: 'Something went wrong while loading the game. Try again.',
    'bad-file': 'Something went wrong while loading the game. Try again.',
};

// The page reloads itself once for a failure a reload fixes (all but 'offline'), and not again
// within this long: a reload that didn't help shows the message instead of reloading for ever
const UPDATE_RELOAD_KEY = 'dgReloadedForUpdate';
const UPDATE_RELOAD_WINDOW_MS = 60000;

function reloadedForUpdateRecently() {
    try {
        const at = Number(window.sessionStorage.getItem(UPDATE_RELOAD_KEY) || 0);
        return (Date.now() - at) < UPDATE_RELOAD_WINDOW_MS;
    } catch {
        // Without storage it can't tell, so it doesn't reload, rather than risk doing so for ever
        return true;
    }
}

let loadFailureShown = false;

/// Whether a load failure has stopped the game (see showConnectionMessage): the page is reloading
/// or showing the message, and what fails after it isn't a crash of its own.
export function isLoadFailureHandled() {
    return loadFailureShown;
}

/// Shown when the game couldn't get a file it needs (see LoadFailureMessages for the reasons):
/// not a fault of the game's code, so no crash dialog, but reported (as a warning, tagged
/// page_failure), so each is counted. A failure a reload can fix reloads the page once, after the
/// report is sent; otherwise, or if that didn't help, the player is told and Try Again reloads,
/// the only way to restart the runtime's download.
export function showConnectionMessage(url, reason = 'offline', detail = '') {
    const kind = Object.prototype.hasOwnProperty.call(LoadFailureMessages, reason) ? reason : 'offline';
    trace(0x6e2d48b1, `couldn't load ${url || 'the game'} (${kind})`);
    if (loadFailureShown || document.getElementById('dg-connection-backdrop')) {
        return;
    }
    loadFailureShown = true;

    // What the game needed hasn't come, so it stops rather than run on, and its input goes, as a
    // button can't be tapped under the message
    stopGame();

    const reloadRecently = reloadedForUpdateRecently();
    const reload = (kind !== 'offline') && !reloadRecently;

    // NOTE: A file that came back as something else is the host's fault (an error); the rest are
    // a player's connection or an old page (warnings). The summary is only the reason, so each
    // reason is one issue, whatever the file's (fingerprinted, per-deploy) name; the file is in
    // the details. An old page's first failure ('stale', 'module') isn't reported: every deploy
    // leaves some, and the reload fixes them. One that comes back after the reload is reported,
    // as a page that keeps asking for old files is a fault of the host's caching
    const reporting = (reload && (kind !== 'bad-file'))
        ? Promise.resolve()
        : reportFailure("Couldn't load the game", kind, detail || url || '(no file named)',
            { level: (kind === 'bad-file') ? 'error' : 'warning', tags: { page_failure: kind, after_reload: reloadRecently ? 'yes' : 'no' } });

    if (reload) {
        try {
            window.sessionStorage.setItem(UPDATE_RELOAD_KEY, String(Date.now()));
        } catch { /* no storage: reloadedForUpdateRecently says so next time */ }
        trace(0x6e2d48b2, 'reloading for the current version');
        reporting.finally(() => {
            try {
                window.location.reload();
            } catch {
                renderLoadFailureMessage(kind);
            }
        });
        return;
    }

    renderLoadFailureMessage(kind);
}

function renderLoadFailureMessage(kind) {
    try {
        suspendHostInput();
        ensureStyles();

        const backdrop = document.createElement('div');
        backdrop.id = 'dg-connection-backdrop';
        backdrop.className = 'dg-crash-backdrop';

        const dialog = document.createElement('div');
        dialog.className = 'dg-crash-dialog';
        dialog.setAttribute('role', 'alertdialog');
        dialog.setAttribute('aria-modal', 'true');

        const h = document.createElement('h2');
        h.className = 'dg-message-title';
        h.textContent = "Couldn't load the game";

        const p = document.createElement('p');
        p.className = 'dg-message-text';
        p.textContent = LoadFailureMessages[kind];

        const buttons = document.createElement('div');
        buttons.className = 'dg-crash-buttons';
        const retryBtn = document.createElement('button');
        retryBtn.className = 'dg-crash-btn';
        retryBtn.textContent = (kind === 'stale') ? 'Reload' : 'Try Again';
        retryBtn.onclick = () => {
            try {
                window.location.reload();
            } catch { /* navigation blocked -- leave the message in place */ }
        };
        buttons.appendChild(retryBtn);

        dialog.appendChild(h);
        dialog.appendChild(p);
        dialog.appendChild(buttons);
        backdrop.appendChild(dialog);
        document.body.appendChild(backdrop);

        // Remove the splash, which can paint over a fixed-position dialog on iOS
        // Safari (see showCrashDialog)
        try {
            const splash = document.getElementById('splash');
            if (splash) {
                splash.remove();
            }
        } catch { /* splash removal must never block the message */ }
    } catch {
        try { window.alert(`Couldn't load the game. ${LoadFailureMessages[kind]}`); } catch { }
    }
}

// ========================= Startup report =========================
//
// Startup is measured in every build, published ones included, so it can be
// checked against the live site rather than only on a developer's machine.
// The engine marks each milestone once (StartupTimeline.Mark in managed code,
// markStartup here), each becomes a performance mark ('hc:<name>'), and when
// startup is over -- postload finished, or 10 seconds after the first frame if
// the game has no postload -- the page assembles a report:
//
//     window.dotgameStartup     marks so far, then the full report
//     dgStartupReport()         the report as of now, from the console
//
// The report holds the marks (milliseconds since navigation start), the
// navigation timing, every resource (start, end, bytes, protocol, whether it
// came from cache), and the connection and device it ran on. It's built from
// data the page already has; nothing is sent anywhere. A one-line summary is
// logged with console.info.

// Resource timing keeps 250 entries by default, fewer than a Debug runtime
// loads, and a full buffer drops everything after it.
try {
    performance.setResourceTimingBufferSize(1000);
} catch { /* not supported */ }

const startupState = {
    marks: {},
    details: {},
    startedHidden: (typeof document !== 'undefined') ? document.hidden : false,
    complete: false,
};
window.dotgameStartup = startupState;

let startupReportTimer = 0;
const STARTUP_REPORT_FALLBACK_MS = 10000;

// Records a startup milestone once: the first time it's reached.
export function markStartup(name, detail) {
    if (Object.prototype.hasOwnProperty.call(startupState.marks, name)) {
        return;
    }

    try {
        performance.mark('hc:' + name, (detail == null) ? undefined : { detail: detail });
    } catch { /* not supported */ }

    startupState.marks[name] = Math.round(performance.now());
    if (detail != null) {
        startupState.details[name] = detail;
    }

    if (name === 'postload-done') {
        finishStartupReport('postload-done');
    } else if ((name === 'first-frame') && !startupReportTimer) {
        // A game with nothing to postload never marks postload-done
        startupReportTimer = setTimeout(() => finishStartupReport('timeout'), STARTUP_REPORT_FALLBACK_MS);
    }
}

function relativeUrl(url) {
    const base = location.href.replace(/[?#].*$/, '').replace(/[^/]*$/, '');
    return url.startsWith(base) ? url.substring(base.length) : url;
}

function buildStartupReport(reason) {
    const round = (value) => (typeof value === 'number') ? Math.round(value) : null;
    const navigation = performance.getEntriesByType('navigation')[0];
    const resources = performance.getEntriesByType('resource').map((entry) => ({
        name: relativeUrl(entry.name),
        initiator: entry.initiatorType,
        start: round(entry.startTime),
        end: round(entry.responseEnd),
        transferSize: entry.transferSize,
        encodedBodySize: entry.encodedBodySize,
        decodedBodySize: entry.decodedBodySize,
        protocol: entry.nextHopProtocol || null,
        // A response from the HTTP cache transfers nothing over the network
        cached: (entry.transferSize === 0) && (entry.decodedBodySize > 0),
    }));
    resources.sort((a, b) => a.start - b.start);

    const connection = navigator.connection;
    return {
        reportVersion: 1,
        reason: reason,
        url: location.href,
        time: new Date().toISOString(),
        startedHidden: startupState.startedHidden,
        marks: Object.assign({}, startupState.marks),
        details: Object.assign({}, startupState.details),
        navigation: navigation ? {
            protocol: navigation.nextHopProtocol || null,
            responseStart: round(navigation.responseStart),
            responseEnd: round(navigation.responseEnd),
            domInteractive: round(navigation.domInteractive),
            domContentLoaded: round(navigation.domContentLoadedEventEnd),
            load: round(navigation.loadEventEnd),
            transferSize: navigation.transferSize,
        } : null,
        connection: connection ? {
            effectiveType: connection.effectiveType,
            downlinkMbps: connection.downlink,
            rttMs: connection.rtt,
            saveData: connection.saveData,
        } : null,
        device: {
            userAgent: navigator.userAgent,
            deviceMemoryGB: navigator.deviceMemory || null,
            cores: navigator.hardwareConcurrency || null,
            devicePixelRatio: window.devicePixelRatio,
            viewport: `${window.innerWidth}x${window.innerHeight}`,
        },
        totals: {
            resources: resources.length,
            transferredBytes: resources.reduce((sum, r) => sum + (r.transferSize || 0), 0),
            cachedResources: resources.filter((r) => r.cached).length,
        },
        resources: resources,
    };
}

function finishStartupReport(reason) {
    if (startupState.complete) {
        return;
    }

    clearTimeout(startupReportTimer);
    const report = buildStartupReport(reason);
    Object.assign(startupState, report, { complete: true });

    const m = report.marks;
    const kb = Math.round(report.totals.transferredBytes / 1024);
    console.info(`[startup] first frame ${m['first-frame'] ?? '?'} ms, started ${m['game-started'] ?? '?'} ms, ` +
        `preload done ${m['preload-done'] ?? '?'} ms, postload done ${m['postload-done'] ?? '?'} ms; ` +
        `${report.totals.resources} resources, ${kb} KB transferred; window.dotgameStartup has the full report`);
}

// The report as of now, for the console: dgStartupReport()
window.dgStartupReport = () => buildStartupReport('requested');

// ========================= Frame timing =========================
//
// Every animation frame records when it ran and how long the game's frame took
// (game.js calls recordFrame), into a fixed ring of the last FRAME_CAPACITY
// frames: two numbers per frame, nothing allocated. From the console, or a
// measuring tool:
//
//     dgFrameStats()               the frames since the first one drawn
//     dgFrameStats(fromMs, toMs)   the frames in a window (ms since navigation)
//
// It reports frame intervals (smoothness as the player sees it: 16.7 ms at 60
// Hz) and frame work (the game's own time per frame, which shows a slower build
// long before frames are dropped), with long frames (over 50 ms) and an
// estimate of dropped frames.

const FRAME_CAPACITY = 4096;
const frameTimes = new Float64Array(FRAME_CAPACITY);
const frameWork = new Float32Array(FRAME_CAPACITY);
let frameCount = 0;

export function recordFrame(time, workMs) {
    const index = frameCount % FRAME_CAPACITY;
    frameTimes[index] = time;
    frameWork[index] = workMs;
    frameCount++;
}

function percentile(sorted, fraction) {
    if (!sorted.length) {
        return null;
    }
    return sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * fraction))];
}

function frameStats(fromMs, toMs) {
    const from = fromMs ?? startupState.marks['first-frame'] ?? 0;
    const to = toMs ?? Infinity;
    const first = Math.max(0, frameCount - FRAME_CAPACITY);
    const intervals = [];
    const work = [];
    let previous = null;
    for (let n = first; n < frameCount; n++) {
        const index = n % FRAME_CAPACITY;
        const time = frameTimes[index];
        if ((time >= from) && (time <= to)) {
            if (previous !== null) {
                intervals.push(time - previous);
            }
            work.push(frameWork[index]);
        }
        previous = time;
    }

    const round = (value) => (value === null) ? null : Math.round(value * 10) / 10;
    const summarize = (values) => {
        const sorted = [...values].sort((a, b) => a - b);
        return {
            p50: round(percentile(sorted, 0.5)),
            p95: round(percentile(sorted, 0.95)),
            max: round(sorted.length ? sorted[sorted.length - 1] : null),
        };
    };

    // Frames a 60 Hz display would have shown in the gaps between frames
    const frameMs = 1000 / 60;
    const dropped = intervals.reduce((sum, interval) => sum + Math.max(0, Math.round(interval / frameMs) - 1), 0);
    return {
        from: Math.round(from),
        to: Number.isFinite(to) ? Math.round(to) : null,
        frames: work.length,
        intervalMs: summarize(intervals),
        workMs: summarize(work),
        longFrames: intervals.filter((interval) => interval > 50).length,
        droppedFrames: dropped,
    };
}

window.dgFrameStats = frameStats;
