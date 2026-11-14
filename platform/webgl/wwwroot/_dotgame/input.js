// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

import { trace, isTraceEnabled } from './diagnostics.js'

// Verbose input tracing. Forced on at startup by the C# TRACE_INPUT define
// passed through initializeInput(), and otherwise armed at runtime from the
// browser console with dgTrace(true) -- which is the only way to get it in a
// shipped build. Hot paths branch on _inputTracing() so neither the message
// string nor the interop crossing is paid for when tracing is off.
let TRACE_INPUT = false;

function _inputTracing() {
    return TRACE_INPUT || isTraceEnabled();
}

// PointerType values shared with .NET (DotGame.Framework.Input.PointerType).
const POINTER_TYPE_MOUSE = 0;
const POINTER_TYPE_TOUCH = 1;
const POINTER_TYPE_PEN   = 2;

// Pointer-id namespaces. Mouse and touch can be concurrently active
// (e.g. mouse hover then a finger touch) so each lives in its own
// numeric range to guarantee the .NET-side recognizer never confuses
// them. Touch and pen carry the browser-supplied PointerEvent.pointerId
// offset by their base to keep all three disjoint.
const MOUSE_POINTER_ID = 1;
const TOUCH_POINTER_ID_BASE = 1000;
const PEN_POINTER_ID_BASE = 100000;

let inputBindings = null;
let inputInitialized = false;

export function configureInput(bindings) {
    inputBindings = bindings;
}

export function initializeInput(traceInput) {
    if (inputInitialized) return;
    inputInitialized = true;
    TRACE_INPUT = !!traceInput;
    trace(0x8e470c2b, `initializeInput forced=${TRACE_INPUT}`);

    // Suppress right-click context menu.
    _addSuppressibleListener('contextmenu', (e) => {
        e.preventDefault();
    }, { capture: true, passive: false });

    // Suppress double-tap magnifying glass. Text-input overlays are
    // exempt: cancelling touchend also cancels the compatibility mouse
    // events and the focus that follows, which is what raises the soft
    // keyboard.
    _addSuppressibleListener('touchend', (e) => {
        if (isEditableElement(e.target)) return;
        e.preventDefault();
    }, { capture: true, passive: false });

    // Suppress system gestures.
    _addSuppressibleListener('gesturestart', (e) => {
        e.preventDefault();
    }, { capture: true, passive: false });

    // ---------------- Pointer input (mouse / touch / pen) ----------------
    //
    // All pointer types (mouse, touch, pen) flow through the Pointer Events
    // API. Listeners are registered on window in the CAPTURE phase
    // ({ capture: true }) so they run before any target/bubble handler can
    // call stopPropagation() and starve us of the event.
    //
    // The listeners below cancel the default action so the page never
    // scrolls, selects, or zooms under the game. Text-input overlays are
    // the one exemption: they sit on top of the canvas precisely so the
    // browser can focus them natively and raise the soft keyboard inside
    // the trusted gesture. Cancelling pointerdown on the overlay
    // suppresses that focus, which is why a tap on a text field used to
    // do nothing on mobile.

    _addSuppressibleListener('pointerdown', (e) => {
        if (_inputTracing()) _logInput(0x6a4f8c12, 'pointerdown', { type: e.pointerType, pointerId: e.pointerId, btn: e.button, canvas: [Math.round(e.x), Math.round(e.y)] });
        const id = _getInstanceId(e.target);
        if (id >= 0) {
            _onPointerDown(id, e);
            return;
        }
        if (isEditableElement(e.target)) {
            _markUserGesture();
            return;
        }
        e.preventDefault();
    }, { capture: true, passive: false });
    _addSuppressibleListener('pointermove', (e) => {
        const id = _getInstanceId(e.target);
        if (id >= 0) {
            _onPointerMove(id, e);
            return;
        }
        if (isEditableElement(e.target)) return;
        e.preventDefault();
    }, { capture: true, passive: false });
    _addSuppressibleListener('pointerup', (e) => {
        if (_inputTracing()) _logInput(0x37b18e94, 'pointerup', { type: e.pointerType, pointerId: e.pointerId, btn: e.button, canvas: [Math.round(e.x), Math.round(e.y)] });
        const id = _getInstanceId(e.target);
        if (id >= 0) {
            _onPointerUp(id, e);
            return;
        }
        if (isEditableElement(e.target)) return;
        e.preventDefault();
    }, { capture: true, passive: false });
    _addSuppressibleListener('pointercancel', (e) => {
        if (_inputTracing()) _logInput(0x5d2c4a06, 'pointercancel', { type: e.pointerType, pointerId: e.pointerId, btn: e.button, canvas: [Math.round(e.x), Math.round(e.y)] });
        const id = _getInstanceId(e.target);
        if (id >= 0) {
            _onPointerCancel(id, e);
            return;
        }
        if (isEditableElement(e.target)) return;
        e.preventDefault();
    }, { capture: true, passive: false });

    // Wheel events for scroll wheel input
    _addSuppressibleListener('wheel', (e) => {
        const id = _getInstanceId(e.target);
        if (id >= 0) _onWheel(id, e);
        e.preventDefault();
    }, { passive: false });

    // Keyboard events (global, not per-canvas - matches browser focus model).
    // Routing is bound to the active game instance by game.js's initialize.
    // We only yield keys when the user is actively typing in a real
    // editable element (input, textarea, contenteditable). This avoids
    // fragile activeElement checks that race against the browser
    // auto-blurring the focused element when Escape is pressed.
    _addSuppressibleListener('keydown', (e) => {
        _markUserGesture();
        const id = _getKeyInstanceId();
        if (id >= 0 && !isEditableElement(document.activeElement)) {
            if (_shouldPreventDefault(e)) {
                e.preventDefault();
            }
            inputBindings.OnKeyDown(id, e.code, _getModifierBits(e), e.repeat);
        }
    });
    _addSuppressibleListener('keyup', (e) => {
        const id = _getKeyInstanceId();
        if (id >= 0 && !isEditableElement(document.activeElement)) {
            if (_shouldPreventDefault(e)) {
                e.preventDefault();
            }
            inputBindings.OnKeyUp(id, e.code, _getModifierBits(e));
        }
    });

    if (isInputSuspended) {
        suspendInput();
    }
}

function _getInstanceId(target) {
    if (!target) return -1;
    const id = target._instanceId;
    return id !== undefined ? id : -1;
}

// The window listeners installed by initializeInput(), retained so they can be
// detached wholesale by suspendInput().
const installedListeners = [];

function _addSuppressibleListener(type, handler, options) {
    window.addEventListener(type, handler, options);
    installedListeners.push([type, handler, options]);
}

// Whether input is suspended, which can come first: a load can fail before input has started.
let isInputSuspended = false;

// The listeners suspendInput() detached, kept so resumeInput() can put them back.
const suspendedListeners = [];

// Detaches every global input listener, until resumeInput().
//
// Those listeners call preventDefault() on everything except text-input
// overlays: a game that fills the viewport must never see scrolling, text
// selection, the context menu, or the platform gestures built on them. That is
// also why arbitrary HTML overlaid on the page cannot be used while they are
// installed -- suppressing pointerdown suppresses the compatibility mouse
// events and the click that follow, so buttons never activate. (Text fields
// are the single exemption, because their native focus is the only way to
// raise a soft keyboard.)
//
// Rather than grow that exemption list -- which would put work on the pointer
// path, where latency is the whole point -- the listeners are simply removed
// while HTML is shown over the game. The crash dialog suspends input for good:
// it appears only after a fatal error, the game never resumes, and from that
// moment the page is ordinary HTML again. A report the player dismisses (a
// previous session's crash) resumes it when it closes.
export function suspendInput() {
    isInputSuspended = true;
    if (installedListeners.length === 0) return;
    trace(0x2f61d3a8, 'suspendInput: detaching global input listeners');
    for (const listener of installedListeners) {
        const [type, handler, options] = listener;
        try {
            window.removeEventListener(type, handler, options);
        } catch { /* listener already gone */ }
        suspendedListeners.push(listener);
    }
    installedListeners.length = 0;
}

// Attaches the listeners suspendInput() detached again.
export function resumeInput() {
    isInputSuspended = false;
    if (suspendedListeners.length === 0) return;
    trace(0x6c94e2b7, 'resumeInput: reattaching global input listeners');
    for (const [type, handler, options] of suspendedListeners) {
        _addSuppressibleListener(type, handler, options);
    }
    suspendedListeners.length = 0;
}

// Published globally so modules that cannot import this one can still suspend
// and resume input. diagnostics.js is the case that matters: input.js imports it for
// tracing, so it must not import back, and its crash dialog has to work whether
// or not input handling was ever initialized.
try {
    window.dgSuspendInput = suspendInput;
    window.dgResumeInput = resumeInput;
} catch { /* no window (non-browser host) */ }

// Mark the current task as occurring inside a trusted user gesture.
// iOS WebKit only honors element.focus()'s soft-keyboard activation
// while a gesture token is active; the text-input overlay's focus
// handler reads `_inUserGesture` to decide whether a focus event
// represents real, input-capable focus or just a programmatic focus
// that the platform may have silently muted. Cleared at the end of the
// current microtask so any synchronous focus()/blur() calls made from
// .NET (via JSExport, on the same call stack) still see the gesture.
function _markUserGesture() {
    _inUserGesture = true;
    queueMicrotask(() => { _inUserGesture = false; });
}

// ---------------- Diagnostics ----------------
//
// Forwards each tagged event to the .NET Trace ring buffer via diagnostics.js
// so it surfaces in "Copy Logs" output alongside the .NET-side recognizer
// breadcrumbs. The .NET ring buffer is the single source of truth for
// diagnostic capture; there is intentionally no JS-side ring buffer (it would
// only duplicate what the .NET side already records and what "Copy Logs"
// reads).

function _logInput(tag, name, data) {
    trace(tag, name + ' ' + JSON.stringify(data));
}

// ---------------- Coordinate conversion ----------------

function _canvasCoordsFromClient(canvas, clientX, clientY) {
    const rect = canvas.getBoundingClientRect();
    if (rect.width <= 0 || rect.height <= 0) {
        return [0, 0];
    }
    const x = (clientX - rect.left) * (canvas.width / rect.width);
    const y = (clientY - rect.top) * (canvas.height / rect.height);
    return [x, y];
}

// ---------------- Pointer handlers (PointerEvent) ----------------

function _mappedPointerId(e) {
    if (e.pointerType === 'pen') return PEN_POINTER_ID_BASE + e.pointerId;
    if (e.pointerType === 'touch') return TOUCH_POINTER_ID_BASE + e.pointerId;
    return MOUSE_POINTER_ID;
}

function _onPointerDown(instanceId, e) {
    _markUserGesture();
    e.preventDefault();
    try { e.target.setPointerCapture(e.pointerId); } catch { /* ignore */ }
    const [x, y] = _canvasCoordsFromClient(e.target, e.clientX, e.clientY);
    if (_inputTracing()) {
        _logInput(0x29f08b7d, 'down', { inst: instanceId, type: e.pointerType, btn: e.button, canvas: [Math.round(x), Math.round(y)] });
    }
    inputBindings.OnPointerDown(
        instanceId,
        _mappedPointerId(e),
        _getPointerTypeValue(e.pointerType),
        x,
        y,
        e.button,
        e.buttons
    );
}

function _onPointerMove(instanceId, e) {
    e.preventDefault();
    const [x, y] = _canvasCoordsFromClient(e.target, e.clientX, e.clientY);
    inputBindings.OnPointerMove(
        instanceId,
        _mappedPointerId(e),
        _getPointerTypeValue(e.pointerType),
        x,
        y,
        e.buttons
    );
}

function _onPointerUp(instanceId, e) {
    e.preventDefault();
    const [x, y] = _canvasCoordsFromClient(e.target, e.clientX, e.clientY);
    if (_inputTracing()) {
        _logInput(0x10c6a3e1, 'up', { inst: instanceId, type: e.pointerType, btn: e.button, canvas: [Math.round(x), Math.round(y)] });
    }
    inputBindings.OnPointerUp(
        instanceId,
        _mappedPointerId(e),
        _getPointerTypeValue(e.pointerType),
        x,
        y,
        e.button,
        e.buttons
    );
}

function _onPointerCancel(instanceId, e) {
    e.preventDefault();
    const [x, y] = _canvasCoordsFromClient(e.target, e.clientX, e.clientY);
    if (_inputTracing()) {
        _logInput(0x4b95d7f2, 'cancel', { inst: instanceId, type: e.pointerType, canvas: [Math.round(x), Math.round(y)] });
    }
    inputBindings.OnPointerCancel(
        instanceId,
        _mappedPointerId(e),
        _getPointerTypeValue(e.pointerType),
        x,
        y,
        e.buttons
    );
}

function _onWheel(instanceId, e) {
    e.preventDefault();
    const [x, y] = _canvasCoordsFromClient(e.target, e.clientX, e.clientY);
    inputBindings.OnWheel(
        instanceId,
        x,
        y,
        e.deltaX,
        e.deltaY
    );
}

function _getPointerTypeValue(pointerTypeStr) {
    switch(pointerTypeStr) {
        case 'mouse': return POINTER_TYPE_MOUSE;
        case 'touch': return POINTER_TYPE_TOUCH;
        case 'pen': return POINTER_TYPE_PEN;
        default: return POINTER_TYPE_MOUSE;
    }
}

let _keyInstanceId = -1;

export function setKeyInstanceId(instanceId) {
    _keyInstanceId = instanceId;
}

export function getKeyInstanceId() {
    return _keyInstanceId;
}

function _getKeyInstanceId() {
    return _keyInstanceId;
}

// Returns true when keys should be left to the browser because the user
// is editing text. Text-input overlay <input>/<textarea> elements count
// as editable so the browser's default key handling (character insertion,
// caret movement, IME) runs.
export function isEditableElement(el) {
    if (!el) {
        return false;
    }
    const tag = el.tagName;
    if (tag === 'INPUT') {
        const t = (el.type || 'text').toLowerCase();
        return !(t === 'checkbox' || t === 'radio' || t === 'button' ||
                 t === 'submit' || t === 'reset' || t === 'range' ||
                 t === 'color' || t === 'file' || t === 'image');
    }
    if (tag === 'TEXTAREA') return true;
    if (el.isContentEditable) return true;
    return false;
}

function _getModifierBits(e) {
    let bits = 0;
    if (e.shiftKey) bits |= 1;
    if (e.ctrlKey)  bits |= 2;
    if (e.altKey)   bits |= 4;
    if (e.metaKey)  bits |= 8;
    return bits;
}

function _shouldPreventDefault(e) {
    if (e.ctrlKey || e.metaKey) return false;
    const code = e.code;
    if (code && code.length >= 2 && code[0] === 'F') {
        const n = parseInt(code.substring(1), 10);
        if (n >= 1 && n <= 24) return false;
    }
    return true;
}

// ---------------- Text input overlays ----------------
//
// Each TextInput on the .NET side owns a transparent <input> (or
// <textarea>) overlay positioned over the on-canvas text field. The
// overlay receives taps natively, so iOS Safari can bring up the soft
// keyboard synchronously inside the trusted gesture handler. The
// overlay is rendered transparent (color, caret, background) so the
// engine retains full visual control.

const _textInputs = new Map(); // id -> { el, isTextArea, multiline, confirmed, composing, preCompositionValue }
let _inUserGesture = false;
let _selectionStyleInjected = false;
let _selectionChangeListenerInstalled = false;

// Install once: whenever the document selection changes, if our active
// overlay's selection has drifted off end-of-value, snap it back. This
// is pure UX (instant visual recovery from iOS loupe drag, double-tap,
// or 'Select All' from the iOS edit callout). Data integrity is
// already guaranteed by the beforeinput handler below; this just keeps
// the visible native caret position consistent with the engine caret.
function _ensureSelectionChangeListener() {
    if (_selectionChangeListenerInstalled) {
        return;
    }
    _selectionChangeListenerInstalled = true;
    document.addEventListener('selectionchange', () => {
        const active = document.activeElement;
        if (!active) {
            return;
        }
        for (const entry of _textInputs.values()) {
            if (entry.el !== active) {
                continue;
            }
            if (entry.composing) {
                return;
            }
            const el = entry.el;
            const len = el.value.length;
            if (el.selectionStart !== len || el.selectionEnd !== len) {
                try { el.setSelectionRange(len, len); } catch { /* ignore */ }
            }
            return;
        }
    });
}

// Cached result of a CSS media query that detects precision input
// hardware (mouse / trackpad). On iPad with Magic Keyboard or any
// desktop / laptop the query is true and we treat programmatic
// focus() outside a user gesture as 'input is real' (the user has a
// hardware keyboard and can type immediately). On iPhone or iPad
// running in pure-touch mode the query is false: programmatic focus
// without a gesture won't open the soft keyboard, so we leave the
// engine caret hidden until a confirming signal (gesture-context
// focus, keydown, input, or pointerdown) actually arrives. This is
// strictly better than blinking a misleading engine caret.
//
// Edge case: iPad/iPhone with a Bluetooth keyboard but no pointing
// device reports false; in that case the engine caret simply does
// not appear until the first keydown, at which point confirmFocus
// fires and the caret takes over. Acceptable and self-resolving.
let _hasPrecisionInputCached = null;
let _precisionInputMql = null;
function _hasPrecisionInput() {
    if (_hasPrecisionInputCached === null) {
        try {
            _precisionInputMql = window.matchMedia('(hover: hover) and (pointer: fine)');
            _hasPrecisionInputCached = _precisionInputMql.matches;
            _precisionInputMql.addEventListener('change', (e) => {
                _hasPrecisionInputCached = e.matches;
            });
        } catch {
            _hasPrecisionInputCached = false;
        }
    }
    return _hasPrecisionInputCached;
}

// How long a pointerdown on an overlay counts as "this focus was asked
// for by the user". Touch focus does not land on pointerdown: the
// browser focuses the field when it synthesizes the click after
// touchend, which can be hundreds of milliseconds later.
const GESTURE_FOCUS_WINDOW_MS = 1500;

// Tells .NET the field really has keyboard focus. `entry.confirmed`
// mirrors what .NET believes, so it must only ever be set while the
// element is genuinely focused -- otherwise a .NET-side blur becomes a
// no-op el.blur() that raises no 'blur' event, the flag stays stuck
// true, and no further GotFocus is ever reported (which silently kills
// the engine's caret).
function _confirmFocus(id, entry) {
    entry.gestureFocusAt = 0;
    if (entry.confirmed) {
        return;
    }
    entry.confirmed = true;
    if (inputBindings) {
        inputBindings.OnTextInputGotFocus(id);
    }
}

// Retires a confirmed focus. Idempotent, so it is safe to call both from
// the 'blur' event and from the programmatic blur path (where the
// element may not have been focused yet and therefore raises no event).
function _retireFocus(id, entry) {
    entry.gestureFocusAt = 0;
    if (!entry.confirmed) {
        return;
    }
    entry.confirmed = false;
    if (inputBindings) {
        inputBindings.OnTextInputLostFocus(id);
    }
}

function _hasRecentFocusGesture(entry) {
    return (entry.gestureFocusAt > 0) &&
           ((Date.now() - entry.gestureFocusAt) < GESTURE_FOCUS_WINDOW_MS);
}

function _ensureSelectionStyle() {
    // Inject a single global stylesheet that hides the native
    // selection background/foreground on our overlays. Even with the
    // input's color set to transparent, the browser draws a selection
    // rectangle when the user drags or invokes 'Select All' from the
    // iOS callout menu; this rule keeps the field visually empty.
    if (_selectionStyleInjected) {
        return;
    }
    _selectionStyleInjected = true;
    const style = document.createElement('style');
    style.textContent =
        '.hcg-text-input::selection{background:transparent;color:transparent;}' +
        '.hcg-text-input::-moz-selection{background:transparent;color:transparent;}';
    document.head.appendChild(style);
}

function _createInputElement(multiline) {
    _ensureSelectionStyle();
    const el = document.createElement(multiline ? 'textarea' : 'input');
    if (!multiline) {
        el.type = 'text';
    }
    el.className = 'hcg-text-input';
    el.autocapitalize = 'none';
    el.autocomplete = 'off';
    el.spellcheck = false;
    el.setAttribute('aria-hidden', 'true');
    // Transparent overlay positioned absolutely over the canvas. The
    // overlay is fully invisible (text, caret, background, selection)
    // so the engine retains complete visual control. font-size:16px
    // prevents iOS Safari from auto-zooming on focus.
    //
    // The native caret stays permanently hidden (caret-color:
    // transparent). Engine-caret visibility is gated by a separate
    // 'confirmed' flag that flips when input is genuinely possible:
    //   * a focus event inside a trusted user gesture, OR
    //   * the hardware-keyboard probe (matchMedia hover+fine), OR
    //   * any keydown / input / pointerdown on the overlay.
    // This avoids the misleading-caret bug on iPhone Safari where
    // programmatic focus() outside a gesture moves activeElement but
    // suppresses the soft keyboard.
    //
    // -webkit-touch-callout:none suppresses the iOS long-press
    // Copy/Define/Look-Up menu over the apparently-empty overlay.
    el.style.cssText =
        'position:absolute;' +
        'left:-9999px;' +
        'top:0;' +
        'width:1px;' +
        'height:1px;' +
        'margin:0;' +
        'padding:0;' +
        'border:0;' +
        'outline:none;' +
        'background:transparent;' +
        'color:transparent;' +
        'caret-color:transparent;' +
        'font-size:16px;' +
        'z-index:1000;' +
        'resize:none;' +
        'overflow:hidden;' +
        'touch-action:manipulation;' +
        '-webkit-touch-callout:none;';
    return el;
}

function _attachHandlers(id, el, multiline, entry) {
    // Confirmed-focus model:
    //  The overlay's native caret is permanently hidden. The engine
    //  draws its own caret only after we 'confirm' that input is
    //  really possible. Confirmation triggers:
    //    * focus inside a trusted user gesture
    //    * focus while a hardware keyboard is detected
    //    * any keydown / input / pointerdown on the overlay
    //  These avoid the iPhone-Safari case where programmatic focus()
    //  outside a gesture silently fails to open the soft keyboard:
    //  the engine caret stays hidden until the user actually does
    //  something that proves input is reaching the field.
    //
    // Append-only edit model (bulletproof):
    //  The engine's TextBox always renders its caret at end-of-text.
    //  The native field's selection (caret position) is invisible but
    //  still moveable via arrow keys, Home/End, double-tap, or the
    //  iOS magnifying loupe -- a moved selection would silently
    //  splice mid-text on the next keystroke, contradicting the
    //  visible engine caret. We enforce append-only at the earliest
    //  possible chokepoint, the 'beforeinput' event:
    //
    //    * For every editing intent (insertText, insertFromPaste,
    //      insertLineBreak, deleteContentBackward, deleteByCut,
    //      historyUndo, historyRedo, ...) we preventDefault() and
    //      manually re-apply the edit at end-of-value, ignoring the
    //      user's selection range entirely. There is no code path
    //      through the overlay that can mutate el.value except the
    //      writes performed here.
    //
    //    * IME composition cannot use preventDefault() without
    //      breaking the composing state machine. Instead we snapshot
    //      el.value at compositionstart and, on compositionend,
    //      replace el.value with snapshot + composedData so the
    //      composed run lands at end-of-text regardless of where the
    //      IME inserted it.
    //
    //    * A document-level selectionchange listener snaps any drift
    //      back to end-of-value as a UX polish; data integrity is
    //      guaranteed by the two mechanisms above even without it.
    const collapseToEnd = () => {
        if (entry.composing) {
            return;
        }
        const len = el.value.length;
        try {
            el.setSelectionRange(len, len);
        } catch {
            // type=email/number reject setSelectionRange; ignore.
        }
    };
    const confirmFocus = () => _confirmFocus(id, entry);
    // Apply a candidate next value at end-of-text and notify .NET.
    // Used by beforeinput's manual re-apply path. We always set
    // selection to end-of-value after the write so the next keystroke
    // also appends. Setting el.value programmatically does not fire
    // 'input', so we raise OnTextInputChanged ourselves.
    const applyAppend = (nextValue) => {
        if (el.maxLength > 0 && nextValue.length > el.maxLength) {
            nextValue = nextValue.substring(0, el.maxLength);
        }
        if (el.value !== nextValue) {
            el.value = nextValue;
        }
        const len = el.value.length;
        try { el.setSelectionRange(len, len); } catch { /* ignore */ }
        if (inputBindings) {
            inputBindings.OnTextInputChanged(id, el.value);
        }
    };
    el.addEventListener('focus', () => {
        if (_hasRecentFocusGesture(entry) || _inUserGesture || _hasPrecisionInput()) {
            confirmFocus();
        }
        collapseToEnd();
    });
    el.addEventListener('blur', () => {
        _retireFocus(id, entry);
    });
    el.addEventListener('pointerdown', () => {
        if (document.activeElement === el) {
            // Already focused -- typically by a programmatic focus the
            // platform muted -- so no 'focus' event will follow. A tap on
            // a focused field does raise the soft keyboard, so this is
            // real input from here on.
            confirmFocus();
            return;
        }
        // Otherwise do NOT confirm here: on touch the element is not
        // focused until the browser synthesizes the click after touchend.
        // Confirming now would report focus that does not exist yet, and
        // a .NET-side blur arriving in between would leave the flag
        // stuck. Just arm the focus event that is about to follow.
        entry.gestureFocusAt = Date.now();
    });
    el.addEventListener('compositionstart', () => {
        entry.composing = true;
        // Snapshot the value before the composing run begins. Whatever
        // the IME inserts into el.value during composition is treated
        // as the composed data; on compositionend we discard the
        // browser's placement and append the composed run to the
        // snapshot, guaranteeing the IME result lands at end-of-text.
        entry.preCompositionValue = el.value;
        confirmFocus();
    });
    el.addEventListener('compositionupdate', (e) => {
        inputBindings.OnTextInputCompositionChanged(id, typeof e.data === 'string' ? e.data : '');
    });
    el.addEventListener('compositionend', (e) => {
        entry.composing = false;
        inputBindings.OnTextInputCompositionChanged(id, '');
        // e.data holds the final composed string. If the browser
        // didn't supply it (rare), fall back to the diff between
        // current and pre-composition values.
        let composed = (e && typeof e.data === 'string') ? e.data : null;
        if (composed === null) {
            const before = entry.preCompositionValue || '';
            const now = el.value;
            composed = now.length > before.length ? now.substring(before.length) : '';
        }
        const base = entry.preCompositionValue || '';
        entry.preCompositionValue = null;
        applyAppend(base + composed);
    });
    // beforeinput is the authoritative chokepoint. It fires before
    // any built-in editing action mutates el.value. We cancel the
    // default and apply an append-only equivalent ourselves. This
    // makes splice/mid-text insertion structurally impossible: the
    // user's selection range is never honored.
    el.addEventListener('beforeinput', (e) => {
        confirmFocus();
        // Composition events have their own dedicated handlers above.
        if (entry.composing ||
            e.inputType === 'insertCompositionText' ||
            e.inputType === 'insertFromComposition' ||
            e.inputType === 'deleteByComposition') {
            return;
        }
        const t = e.inputType || '';
        // Always cancel the default; we'll re-apply.
        e.preventDefault();
        const current = el.value;
        if (t.startsWith('insert')) {
            let data = '';
            if (typeof e.data === 'string' && e.data.length > 0) {
                data = e.data;
            } else if (e.dataTransfer) {
                // Paste / drag-and-drop path.
                data = e.dataTransfer.getData('text/plain') || '';
            } else if (t === 'insertLineBreak' || t === 'insertParagraph') {
                data = multiline ? '\n' : '';
            }
            if (data.length === 0) {
                return;
            }
            applyAppend(current + data);
        } else if (t.startsWith('delete')) {
            // Honor delete intents but only at end-of-text. iOS
            // backspace, ctrl-backspace, delete-word-backward, etc.
            // Anything that would delete forward (impossible at EOT)
            // or in the middle is ignored. Cut is treated as
            // backspace-of-end (we don't expose the suppressed
            // selection to the clipboard; the visible engine caret
            // shows nothing is selected anyway).
            if (current.length === 0) {
                return;
            }
            let next;
            if (t === 'deleteWordBackward') {
                // Strip trailing whitespace then trailing word chars.
                let i = current.length;
                while (i > 0 && /\s/.test(current[i - 1])) i--;
                while (i > 0 && !/\s/.test(current[i - 1])) i--;
                next = current.substring(0, i);
            } else if (t === 'deleteSoftLineBackward' ||
                       t === 'deleteHardLineBackward' ||
                       t === 'deleteEntireSoftLine') {
                const nl = current.lastIndexOf('\n');
                next = nl < 0 ? '' : current.substring(0, nl + 1);
            } else if (t === 'deleteContentForward' ||
                       t === 'deleteWordForward' ||
                       t === 'deleteSoftLineForward' ||
                       t === 'deleteHardLineForward') {
                // Forward delete at end-of-text is a no-op.
                return;
            } else {
                // deleteContentBackward, deleteContent, deleteByCut,
                // deleteByDrag, etc. -> single-char backspace.
                next = current.substring(0, current.length - 1);
            }
            applyAppend(next);
        } else if (t === 'historyUndo' || t === 'historyRedo') {
            // Suppress undo/redo entirely; the engine owns text state.
            return;
        }
        // Any other inputType is left preventDefault'd with no
        // re-apply, which is the safe default.
    });
    // The 'input' event fires for any mutation that beforeinput let
    // through, plus for IME composition steps. Our beforeinput
    // handler cancels everything except composition, so by the time
    // 'input' fires here the value is already authoritative; we just
    // keep selection pinned to end-of-text. (We deliberately do NOT
    // re-notify .NET here -- applyAppend / compositionend already
    // did.)
    el.addEventListener('input', () => {
        confirmFocus();
        if (!entry.composing) {
            collapseToEnd();
        }
    });
    el.addEventListener('select', () => {
        collapseToEnd();
    });
    el.addEventListener('keydown', (e) => {
        confirmFocus();
        if (!inputBindings) {
            return;
        }
        if (e.key === 'Enter' && !multiline) {
            e.preventDefault();
            inputBindings.OnTextInputSubmit(id);
        } else if (e.key === 'Escape') {
            e.preventDefault();
            inputBindings.OnTextInputCancel(id);
        }
    });
}

export function createTextInput(instanceId) {
    if (_textInputs.has(instanceId)) {
        return;
    }
    _ensureSelectionChangeListener();
    const el = _createInputElement(false);
    const entry = { el, isTextArea: false, multiline: false, confirmed: false, gestureFocusAt: 0, composing: false, preCompositionValue: null };
    _attachHandlers(instanceId, el, false, entry);
    document.body.appendChild(el);
    _textInputs.set(instanceId, entry);
}

export function destroyTextInput(instanceId) {
    const entry = _textInputs.get(instanceId);
    if (!entry) {
        return;
    }
    entry.el.remove();
    _textInputs.delete(instanceId);
}

function _ensureKind(instanceId, multiline) {
    let entry = _textInputs.get(instanceId);
    if (!entry) {
        return null;
    }
    const wantTextArea = !!multiline;
    if (entry.isTextArea === wantTextArea) {
        return entry;
    }
    // Re-create with the correct element type, preserving focus/value.
    const wasFocused = (document.activeElement === entry.el);
    const oldValue = entry.el.value;
    entry.el.remove();
    const el = _createInputElement(wantTextArea);
    el.value = oldValue;
    entry = { el, isTextArea: wantTextArea, multiline: wantTextArea, confirmed: false, gestureFocusAt: 0, composing: false, preCompositionValue: null };
    _attachHandlers(instanceId, el, wantTextArea, entry);
    document.body.appendChild(el);
    _textInputs.set(instanceId, entry);
    if (wasFocused) {
        el.focus();
    }
    return entry;
}

function _applyInputType(el, inputType, multiline) {
    // Mirrors DotGame.Framework.Input.TextInputType:
    // 0=Text, 1=Password, 2=Number, 3=Decimal, 4=Phone, 5=Email, 6=Url, 7=Search
    if (multiline) {
        el.inputMode = 'text';
        return;
    }
    el.removeAttribute('pattern');
    switch (inputType) {
        case 1:
            el.type = 'password';
            el.inputMode = 'text';
            break;
        case 2:
            el.type = 'text';
            el.inputMode = 'numeric';
            el.pattern = '[0-9]*';
            break;
        case 3:
            el.type = 'text';
            el.inputMode = 'decimal';
            break;
        case 4:
            el.type = 'tel';
            el.inputMode = 'tel';
            break;
        case 5:
            el.type = 'email';
            el.inputMode = 'email';
            break;
        case 6:
            el.type = 'url';
            el.inputMode = 'url';
            break;
        case 7:
            el.type = 'search';
            el.inputMode = 'search';
            break;
        default:
            el.type = 'text';
            el.inputMode = 'text';
            break;
    }
}

export function updateTextInput(instanceId, x, y, width, height, text, inputType, maxLength, multiline) {
    const entry = _ensureKind(instanceId, multiline);
    if (!entry) {
        return;
    }
    const el = entry.el;
    _applyInputType(el, inputType, multiline);
    if (maxLength > 0) {
        el.maxLength = maxLength;
    } else {
        el.removeAttribute('maxlength');
    }
    if (el.value !== (text || '')) {
        el.value = text || '';
        // Keep the native selection at end-of-value so subsequent
        // typing appends rather than splicing mid-text.
        const len = el.value.length;
        try { el.setSelectionRange(len, len); } catch { /* ignore */ }
    }
    // Position over the on-canvas text field. An empty rect (all zero)
    // hides the overlay off-screen.
    if (width > 0 && height > 0) {
        // The .NET side reports bounds in canvas (viewport) pixel space.
        // Convert back to CSS pixels using the active canvas's
        // bounding-rect / backing-size ratio so the overlay aligns
        // with the visible text field on high-DPI displays.
        const canvas = _findActiveCanvas();
        let cssX = x, cssY = y, cssW = width, cssH = height;
        if (canvas) {
            const rect = canvas.getBoundingClientRect();
            const sx = rect.width / canvas.width;
            const sy = rect.height / canvas.height;
            cssX = rect.left + x * sx;
            cssY = rect.top + y * sy;
            cssW = width * sx;
            cssH = height * sy;
        }
        el.style.left = cssX + 'px';
        el.style.top = cssY + 'px';
        el.style.width = cssW + 'px';
        el.style.height = cssH + 'px';
    } else {
        el.style.left = '-9999px';
        el.style.top = '0';
        el.style.width = '1px';
        el.style.height = '1px';
    }
}

function _findActiveCanvas() {
    // The keys are routed to a single active game instance ID; find the
    // canvas the engine tagged with that ID.
    if (_keyInstanceId < 0) {
        return null;
    }
    const canvases = document.getElementsByTagName('canvas');
    for (let i = 0; i < canvases.length; i++) {
        if (canvases[i]._instanceId === _keyInstanceId) {
            return canvases[i];
        }
    }
    return null;
}

export function focusTextInput(instanceId) {
    const entry = _textInputs.get(instanceId);
    if (!entry) {
        return;
    }
    entry.el.focus();
    try {
        entry.el.setSelectionRange(entry.el.value.length, entry.el.value.length);
    } catch {
        // type=email/number reject setSelectionRange; ignore.
    }
}

export function blurTextInput(instanceId) {
    const entry = _textInputs.get(instanceId);
    if (!entry) {
        return;
    }
    entry.el.blur();
    // If the element was not actually focused (a touch focus that has
    // not landed yet) no 'blur' event fires, so retire the confirmation
    // explicitly. Harmless when the event already did it.
    _retireFocus(instanceId, entry);
}
