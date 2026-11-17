// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

import { trace as writeTrace, isTraceEnabled, onCrashDialog } from './diagnostics.js'
import { resolveContentUrl, fetchWithRetry, waitBehindConnectionMessage, failedResponseReason } from './content.js'

// Verbose audio tracing. Forced on at startup by the C# TRACE_AUDIO define
// passed through initializeAudio(), and otherwise armed at runtime from the
// browser console with dgTrace(true) -- which is the only way to get it in a
// shipped build. When neither is on, nothing is recorded and nothing is
// echoed: the trace call is skipped entirely.
let TRACE_AUDIO = false;

function _audioTracing() {
    return TRACE_AUDIO || isTraceEnabled();
}

// The managed lifecycle callback, invoked from AudioServiceStrategy.OnInitialize
// once the runtime is up. Deliberately distinct from setupAudio(): that is a
// factory that must run before the runtime exists (it produces the bindings
// table the runtime imports), whereas the TRACE_AUDIO value only exists once
// managed code runs. It is surfaced on the bindings table setupAudio returns,
// so the split is an ordering detail rather than a second entry point.
function initializeAudio(traceAudio) {
    TRACE_AUDIO = !!traceAudio;
    trace(0x2f8b91c4, `initializeAudio forced=${TRACE_AUDIO}`);
}

// Gates every audio trace in this module. The console echo itself is left to
// diagnostics.trace, which owns the global toggle -- forwarding here would
// print twice whenever dgTrace(true) is also on.
function trace(tag, message) {
    if (_audioTracing()) {
        writeTrace(tag, message);
    }
}

// How a fault in this module reaches the managed side, once the runtime is up
let audioFailureReporter = null;

export function setAudioFailureReporter(reporter) {
    audioFailureReporter = reporter;
}

export function setupAudio() {
    let audioContext = null;

    onCrashDialog(() => audioContext?.suspend().catch(() => { }));

    let unlocked = false;
    // Music plays that arrived while audio was locked, keyed by the stream
    // they belong to, replayed in arrival order once a gesture unlocks.
    //
    // Keyed per stream rather than held in a single slot, because unrelated
    // tracks are routinely started within the same locked window and they are
    // not alternatives to one another: entering the first level starts the
    // looping background song and then a one-shot level ident, on two separate
    // streams, a few milliseconds apart. A single slot makes the second one
    // silently evict the first, so the background music is never replayed and
    // the track stays stopped for the rest of the session -- with the game
    // still reporting it as playing, so nothing ever retries it.
    //
    // Sound (AudioBufferSource) deferred plays are still dropped rather than
    // queued -- they would be stale by the time the user gesture arrives.
    const pendingMusicOps = new Map();
    let unlockListenersAdded = false;

    let visibilityListenerAdded = false;

    // Every streaming music engine that is currently alive.
    //
    // Music is the one thing in this module that cannot be driven purely by
    // suspending the AudioContext: its worklet processor is fed by a worker
    // whose fetch and decode are frozen the moment the page is hidden, so the
    // processor has to be told to stop consuming its queue before the suspend
    // and told to resume afterwards. That requires reaching every engine, and
    // this set is how.
    //
    // Keyed on the engine rather than the stream it belongs to, because the
    // engine is the thing that owns the worker and the processor. A stream is
    // a long-lived shell that builds and discards many engines over its life
    // -- every re-prepare replaces one -- so membership tied to the stream
    // would track an object that is not the one being suspended, and would
    // outlive the engine it was standing in for.
    //
    // Membership is added in the constructor and removed in stop(), which is
    // idempotent. Since every engine's teardown runs through stop(), the set
    // cannot retain an engine that has been torn down.
    const liveStreamingEngines = new Set();

    // A one-frame silent AudioBuffer, reused for every priming play.
    //
    // Belongs to the context and is discarded with it: an AudioBuffer is bound
    // to the rate it was created at, so a buffer carried across a rebuilt
    // context would be resampled on the audio thread on every prime.
    let scratchBuffer = null;

    // Every live object that owns context-bound state, kept so the graph can
    // be rebuilt onto a fresh context.
    //
    // This bookkeeping is the price of rebuildAudioGraph and exists for no
    // other reason. An AudioNode belongs permanently to the context that
    // created it and cannot be moved, so a rebuild has to visit every node the
    // module has handed out and construct a replacement. The managed side
    // holds these objects as opaque handles and keeps using them across a
    // rebuild, which is what makes the operation invisible to the game -- but
    // only if nothing is missed here.
    //
    // Membership is added in each create* function and removed in the matching
    // destroy*, so these mirror exactly what the game still holds. They must
    // not be allowed to retain anything past its destroy: a Set is a strong
    // reference, so a missed removal is a leak of a decoded buffer or a whole
    // music pipeline.
    const liveVoices = new Set();
    const liveVolumeControls = new Set();
    const liveAudioBuffers = new Set();
    const liveAudioStreams = new Set();

    // Coalescing state for overlapping suspend/resume transitions.
    //
    // A suspend or a resume is asynchronous and can take hundreds of
    // milliseconds to settle, while the events that drive them -- a
    // visibilitychange as the player flicks between apps, a tap landing as the
    // page is going away -- arrive far faster than that. Letting a resume run
    // while a suspend is still in flight races two opposite requests against
    // the same context, and the one that settles last wins, which is not
    // necessarily the one the player asked for last.
    //
    // Instead, whichever transition is already running owns the context, and
    // the later request records only its intent and returns. The running
    // transition reads that intent at its tail and performs the opposite
    // operation itself, so the context always ends in the state that was asked
    // for most recently, and never has two transitions in flight at once.
    let suspendInFlight = false;
    let resumeInFlight = false;
    let resumeStartedAtMs = 0;
    const RESUME_STUCK_MS = 2000;
    let resumeRequestedDuringSuspend = false;
    let suspendRequestedDuringResume = false;

    // Set for the whole of rebuildAudioGraph, awaits included.
    //
    // A rebuild tears the graph down, awaits oldContext.close() and an
    // OfflineAudioContext render per off-rate buffer, and only then constructs
    // the replacement nodes. The game loop is not paused for any of that: it is
    // the same main thread, and it keeps issuing cues across every one of those
    // awaits. In that window every live voice and stream has a null gainNode
    // and `audioContext` is briefly null, so a cue issued into it either
    // connects a source to null -- which WebKit reports as the bare
    // 'TypeError: Type error', with no indication of which call failed -- or
    // constructs a stray AudioContext that the rebuild immediately discards.
    //
    // Cues are dropped for the duration instead. That is the same judgement the
    // rebuild already makes for the voices it rewires but deliberately does not
    // restart: a sound effect is tied to a moment, and by the time the graph is
    // back the moment has passed. Handles created in the window are registered
    // with a null gainNode and adopted by the rewire loop at the rebuild's tail,
    // so the game keeps using them afterwards exactly as if nothing happened.
    let graphRebuildInFlight = false;

    // Resolvers for callers parked on whenGraphRebuildSettles(), released by
    // the rebuild's finally on every exit path (success and failure alike).
    let graphRebuildWaiters = [];

    // Resolves once no rebuild is in flight.
    //
    // For the asynchronous work that cannot simply be dropped the way a cue can.
    // A content load is the case that matters: it is a request for an asset the
    // game is about to need, so discarding it would leave the sound permanently
    // missing, and it must not be served either -- decoding against a context
    // built mid-rebuild normalises the samples to a rate the replacement does
    // not run at, which costs a resample on the audio thread on every play for
    // the rest of the session. The rebuild reconciles rates only for buffers
    // already decoded when it took its snapshot, so one decoded after that
    // point would simply be missed. Waiting gets it the real context.
    function whenGraphRebuildSettles() {
        if (!graphRebuildInFlight) {
            return Promise.resolve();
        }
        return new Promise((resolve) => graphRebuildWaiters.push(resolve));
    }

    function releaseGraphRebuildWaiters() {
        if (graphRebuildWaiters.length === 0) {
            return;
        }
        const waiters = graphRebuildWaiters;
        graphRebuildWaiters = [];
        for (const resolve of waiters) {
            resolve();
        }
    }

    // When the page went hidden, and how long it may stay hidden before the
    // context is assumed unreliable on return.
    //
    // The sample-rate probe is necessary but demonstrably not sufficient: the
    // captured repro shows a sound effect running 626 ms against an expected
    // 261 ms while sampleRate, outputLatency, baseLatency and the graph-vs-
    // hardware lag all read exactly nominal. Nothing observable distinguished
    // the broken device from a healthy one, so no measurement taken after the
    // fact could have gated the repair.
    //
    // Elapsed hidden time is used instead because it is the one input that is
    // known *before* the first cue is scheduled, which is the only point where
    // a rebuild can still prevent the delay rather than explain it. A short
    // app-switch keeps its device; a long one has had the session torn down
    // and reassigned underneath us.
    //
    // 30s matches Howler's autoSuspend dwell -- the same "nothing has been
    // driving this device for long enough to stop trusting it" judgement, from
    // the one reference implementation that had to solve it for the whole web.
    let pageHiddenAtMs = 0;
    const HIDDEN_REBUILD_THRESHOLD_MS = 30000;

    // Set when the live context has been observed in WebKit's 'interrupted'
    // state, and cleared only by the rebuild that answers it.
    //
    // An interruption means the OS took the audio session away and gave it
    // back -- a call, Siri, an alarm, a route change. resume() alone does
    // recover the context object, which is why the visibility listener treats
    // 'interrupted' as resumable, but it does not establish that the session
    // came back on the same device or at the same rate. That is the identical
    // hazard the hidden-dwell threshold exists for, arrived at by a different
    // route, and it deserves the same answer.
    //
    // Recorded as a latch rather than tested at resume time because the state
    // is transient: by the time the page is foregrounded and safeResumeContext
    // reads audioContext.state, the context may have already moved on to
    // 'suspended' and the interruption left no trace. Only the statechange
    // handler is guaranteed to see it, so that is where it has to be captured.
    let contextWasInterrupted = false;

    // Set when resume() found the context closed although its state didn't say so yet: the
    // browser closes a context (its output device went away) and updates `state` a task later,
    // so the check before resume() saw 'suspended'. The next resume rebuilds it, as it does any
    // closed context.
    let contextClosedDuringResume = false;

    // `sampleRate` is not specified. Passing one forces the context to
    // a rate the hardware may not be running at, which makes the platform
    // resample the entire mixed output on the audio thread for the lifetime of
    // the page. Omitting it lets the context adopt the device's native rate,
    // and decoded sounds are normalised to whatever that turns out to be at
    // load time (see resampleToContextRate).
    const AUDIO_CONTEXT_OPTIONS = null;

    function getAudioContext() {
        if (!audioContext) {
            // Set before constructing the context: the session type steers
            // which AVAudioSession category iOS puts the page in, and that
            // choice is made when the context is created.
            applyAudioSessionType();

            const Ctor = window.AudioContext || window.webkitAudioContext;
            audioContext = AUDIO_CONTEXT_OPTIONS
                ? new Ctor(AUDIO_CONTEXT_OPTIONS)
                : new Ctor();
            traceContextLifecycle(0x6aa59260, 'created');

            // Every state transition is traced, because the context object
            // survives transitions that can change its properties underneath
            // it -- most importantly outputLatency, which the browser can
            // re-acquire larger after a long backgrounding and then keep for
            // the lifetime of the page. Without a before/after record the
            // change leaves no evidence at all: every node, buffer, and
            // start() call looks identical.
            //
            // The hardware rate is probed alongside it. A state transition is
            // exactly when the device can have been handed back at a different
            // rate, and the live context will not revise the value it reports,
            // so the probe is the only way the divergence becomes visible.
            const createdContext = audioContext;
            audioContext.onstatechange = () => {
                traceContextLifecycle(0x9febbcb9, 'statechange');

                // Latched here rather than read at resume time because this is
                // the only place the transient 'interrupted' state is certain
                // to be observed.
                //
                // Compared against the context this handler was installed on,
                // not merely against null. rebuildAudioGraph detaches the old
                // handler before closing, but a statechange already queued when
                // it does so still runs, and by then `audioContext` names the
                // replacement -- so testing the module variable would let a
                // discarded context arm a rebuild against a healthy new one.
                if (audioContext === createdContext
                    && createdContext.state === 'interrupted'
                    && !contextWasInterrupted) {
                    contextWasInterrupted = true;
                    trace(0x47e9c3c2, 'AudioContext interrupted; rebuild armed for next resume');
                }
            };

            // Strictly 'running'. Anything else -- 'suspended' at
            // construction, or 'interrupted' if a context is somehow created
            // while the audio session is already taken -- must arm the unlock
            // listeners instead, because in neither case will a source
            // started now be heard.
            if (audioContext.state === 'running') {
                unlocked = true;
                primeOutputPipeline('created');
            } else {
                addUnlockListeners();
            }

            // Installed with the context, not with the music graph. This used
            // to be armed only from ensureStreamOutput, so a session that had
            // played sounds but never started a music track ran with no
            // visibility handling at all: nothing suspended the context on
            // backgrounding, and iOS was left to tear the device down under a
            // running context -- precisely the unclean teardown this is meant
            // to prevent. Sounds alone are enough to need it.
            addVisibilityListener();

            // A context created while the page is hidden -- a game that
            // starts in a background tab -- sees no visibilitychange until the
            // page is shown, so it would otherwise play to a tab nobody is
            // looking at. Suspend it now; the listener resumes it when the
            // page becomes visible.
            if (document.hidden && audioContext.state === 'running') {
                traceContextLifecycle(0x5e93c2a7, 'created hidden, suspending');
                safeSuspendContext();
            }
        }
        return audioContext;
    }

    // Creates the AudioContext ahead of its first use. Construction blocks the
    // main thread while the browser opens the output device (600-900 ms
    // measured in Edge), and the first use is in the middle of loading content,
    // on the critical path to the first frame. main.js calls this while the
    // main thread would otherwise sit idle waiting for the runtime's wasm to
    // download and compile. The context starts suspended until a gesture, as it
    // would have anyway: nothing has been played yet.
    function prepareAudioContext() {
        try {
            getAudioContext();
        } catch (e) {
            // Left for the first real use to report; it fails the same way
            trace(0x7c2e5a18, `prepareAudioContext failed: ${e && e.name ? e.name : e}`);
        }
    }

    // Declares the intended use of audio to iOS, via the Audio Session API
    // (Safari 16.4+). Left unset, the session type is 'auto' and the browser
    // infers a category, which can land on one meant for something else.
    //
    // 'ambient' is the deliberate choice, and the description matters because
    // it is the opposite of "owns its audio": an ambient session mixes with
    // whatever else the device is playing rather than interrupting it, so a
    // player's podcast or music keeps going, and it respects the hardware
    // mute switch, which is what a player silencing their phone expects a
    // game to obey. The alternative, 'playback', asserts that this page's
    // audio is the point of the session -- it ignores the mute switch and
    // stops other apps' audio, which is correct for a media player and wrong
    // here.
    //
    // Guarded and non-fatal: the API does not exist outside Safari.
    function applyAudioSessionType() {
        const session = navigator.audioSession;
        if (!session) {
            return;
        }

        try {
            session.type = 'ambient';
            trace(0x1c6ea940, `audioSession.type set to '${session.type}'`);
        } catch (e) {
            trace(0x1c6ea941, `audioSession.type not settable: ${e}`);
        }
    }

    // Reports whether the hardware rate has moved away from what the live
    // context was built for.
    //
    // Returns true when a genuine divergence is confirmed. This is the one
    // fault that resuming, priming, and re-scheduling categorically cannot
    // repair: sampleRate is immutable, so a context built for 48 kHz on
    // hardware now running at 44.1 kHz will resample every buffer on the audio
    // thread, forever, no matter what else is done to it.
    //
    // Compared against the live context's own rate rather than against a value
    // captured at construction. The context's rate is the rate every decoded
    // buffer is actually normalised to, so it is the only figure a divergence
    // has any consequence relative to -- and unlike a stored baseline it
    // cannot go stale or be lost across a rebuild.
    //
    // A null probe is not a mismatch. Failing to construct a probe context
    // tells us nothing about the hardware, and must not be allowed to trigger
    // the most expensive repair in the module.
    function probeHardwareSampleRateDrift(reason) {
        if (!audioContext) {
            return false;
        }

        const probed = probeAudioContext();
        if (probed === null) {
            return false;
        }
        const probedRate = probed.sampleRate;

        const contextRate = audioContext.sampleRate;
        if (probedRate === contextRate) {
            return false;
        }

        trace(0x3f7b2e0b, `hardware sample rate changed reason=${reason} context=${contextRate} probed=${probedRate}`);

        return true;
    }

    // Reads the hardware's current output sample rate from a throwaway context.
    //
    // An AudioContext's sampleRate is fixed for its lifetime, and WebKit does
    // not revise the value a long-lived context reports when the underlying
    // device changes rate -- switching to or from Bluetooth, or the OS handing
    // the session back with a different device after an interruption. The live
    // context therefore keeps reporting the rate it was born with, while the
    // hardware is running at another. Every buffer decoded from then on is
    // normalised to a stale rate, which the AudioBufferSourceNode then has to
    // resample on the audio thread on every single play.
    //
    // A fresh context is the only way to ask what the hardware is actually
    // doing now, because the rate is chosen at construction. It is constructed
    // purely to be interrogated and closed immediately; nothing is ever routed
    // through it.
    //
    // No options are passed. A latencyHint would be meaningless for a context
    // that never renders, and passing sampleRate would defeat the entire
    // purpose by forcing the answer this is trying to discover.
    //
    // Returns null rather than throwing if the probe cannot be built. Some
    // engines cap concurrent AudioContexts, and a construction refused because
    // too many are live must not become a crash.
    function probeAudioContext() {
        const Ctor = window.AudioContext || window.webkitAudioContext;
        if (!Ctor) {
            return null;
        }

        let probe = null;
        try {
            probe = new Ctor();
            const snapshot = readContextProperties(probe);
            return snapshot;
        } catch (e) {
            trace(0x3f7b2e09, `AudioContext probe failed: ${e && e.name ? e.name : e}`);
            return null;
        } finally {
            // close() is asynchronous and its promise is deliberately dropped.
            // The probe is already unreferenced by the time this runs and the
            // rate has been read; nothing is waiting on the teardown. A
            // rejection here would otherwise surface as an unhandled rejection
            // for a context nobody is using.
            if (probe) {
                try {
                    const closed = probe.close();
                    if (closed && typeof closed.catch === 'function') {
                        closed.catch(() => { });
                    }
                } catch (e) {
                    // A context that refuses to close will be collected with
                    // the page. Not worth reporting.
                }
            }
        }
    }

    // Reads every device-derived value a throwaway context will report, so the
    // live context can be compared against a freshly-acquired one.
    //
    // sampleRate alone was not enough to catch the observed delay: it was
    // identical on both sides while a cue still ran 2.4x long. Every readable
    // property is captured rather than a hand-picked few, because the whole
    // reason this exists is that the discriminating value is not yet known --
    // pre-selecting fields would risk excluding the one that finally differs.
    //
    // Values are read individually and defensively. A property access on an
    // AudioContext can throw (WebKit has historically thrown on latency
    // getters for a context with no device), and one bad getter must not lose
    // the whole snapshot.
    //
    // A probe context is never resumed, so its outputLatency is usually 0 and
    // its state is 'suspended'. Those are reported rather than suppressed:
    // hiding them would misrepresent what was measured.
    const PROBE_CONTEXT_PROPERTIES = [
        'sampleRate',
        'state'
    ];

    function readContextProperties(ctx) {
        const snapshot = {};
        for (const name of PROBE_CONTEXT_PROPERTIES) {
            try {
                const value = ctx[name];
                snapshot[name] = (typeof value === 'number' || typeof value === 'string')
                    ? value
                    : null;
            } catch (e) {
                snapshot[name] = null;
            }
        }
        return snapshot;
    }

    // Traces the live context's properties at a lifecycle event.
    //
    // The context object survives transitions that can change what it reports
    // -- most notably outputLatency, which the browser can re-acquire larger
    // after a long backgrounding and then keep for the lifetime of the page.
    // A snapshot at each lifecycle point is what makes such a change visible
    // at all; nothing else about a node, buffer, or start() call differs.
    //
    // Does nothing unless audio tracing is on. The probe constructs a second
    // AudioContext, and construction blocks the main thread while the browser
    // opens the output device: 250-450 ms measured in Edge at startup, on the
    // critical path to the first frame, and again on every state change (the
    // first tap's resume included). Only the trace uses it.
    function traceContextLifecycle(tag, reason) {
        if (!_audioTracing()) {
            return;
        }

        const probed = probeAudioContext();
        const probedFields = Object.keys(probed)
            .map((name) => `${name}=${probed[name] === null ? 'n/a' : probed[name]}`)
            .join(' ');

        if (!audioContext) {
            trace(tag, `AudioContext ${reason} probed=[${probedFields}]`);
            return;
        }

        const context = readContextProperties(audioContext);
        const contextFields = Object.keys(context)
            .map((name) => `${name}=${context[name] === null ? 'n/a' : context[name]}`)
            .join(' ');
        trace(tag, `AudioContext ${reason} probed=[${probedFields}] context=[${contextFields}]`);
    }

    // Rebuilds the entire audio graph onto a brand-new AudioContext.
    //
    // This is the last resort for the one fault nothing else can repair: the
    // browser re-acquiring the output device with a permanently larger IO
    // buffer after a suspend/resume. Every cue is then uniformly late for the
    // life of the page, and because the enlarged latency is a property of the
    // context object, no amount of resuming, priming, or re-scheduling fixes
    // it. Only a different context does. This is why the operation is worth
    // its cost despite being the most disruptive thing in this module.
    //
    // It is deliberately hard to trigger -- a resume that was both slow to
    // complete and followed by a hardware clock that repeatedly failed to
    // advance, and only within the window after a lifecycle suspend -- because
    // the cure is genuinely expensive: music is interrupted and restarted from
    // its prepared start point, and every decoded sound buffer is resampled if
    // the new context lands on a different rate.
    //
    // The managed side keeps holding the same Voice, VolumeControl,
    // AudioBuffer, and AudioStream handles throughout. Their context-bound
    // fields are replaced in place, which is what lets a rebuild happen without
    // the game knowing or any interop handle going stale.
    // Replaces the context and rewires every live object onto it. Nothing
    // else: it does not resume the replacement and it does not restart music.
    //
    // That restraint is what keeps the lifecycle acyclic. Restarting music
    // from here would call playAudioStream, which asks for a resume, which is
    // where rebuilds are decided -- a cycle back into this function. Instead
    // the captured music is handed back to safeResumeContext, the sole caller,
    // which replays it through restoreMusicAfterRebuild once the replacement is
    // running. Calls therefore only ever run one way: safeResumeContext ->
    // rebuildAudioGraph -> return.
    //
    // Returns the captured music entries to restore (possibly empty) on
    // success, or null if there was no context to replace or the rebuild
    // failed part-way.
    async function rebuildAudioGraph(reason) {
        if (!audioContext) {
            return null;
        }

        const oldContext = audioContext;
        traceContextLifecycle(0x47e9c3ba, `AudioContext rebuild starting reason=${reason}`);

        // Raised before the first teardown and lowered only once the rewire
        // below has finished, so it covers every await in between -- the window
        // in which the graph does not exist and the game loop must not be
        // allowed to touch it. See the declaration for why.
        graphRebuildInFlight = true;

        try {
            // Capture what music was doing before anything is torn down. The
            // engines are one-shot pipelines bound to the old context's
            // worklet, so they cannot be carried over -- only the intent to be
            // playing a given range, and the position it had reached, and both
            // are replayed at the end.
            //
            // Only looping tracks are captured. A looping track is the
            // background bed the player expects to still be there when they
            // return, and its absence is conspicuous for the rest of the
            // level. Everything else -- one-shot music (idents, stingers) and
            // sound effects alike -- is a cue tied to a moment that has now
            // passed, so replaying it here would fire it out of context and
            // preserving its position would restore a fragment nothing is
            // waiting for. Those are dropped: the stream is still torn down
            // below and left Loaded with its prepareArgs intact, so the game
            // can start it again whenever it next wants it.
            const musicToRestore = [];
            for (const stream of liveAudioStreams) {
                if ((stream.state === MusicState.Playing
                    || stream.state === MusicState.Prepared
                    || stream.state === MusicState.Preparing)
                    && stream.prepareArgs
                    && stream.prepareArgs.loop) {
                    // Read the cursor while the engine is still alive; after
                    // releaseMusicEngine below there is nothing left to ask.
                    // A track that is Prepared or still Preparing has produced
                    // no audio, so it correctly reports null and restarts from
                    // its prepared start, which is where it already was.
                    const engine = stream.engine;
                    const position = (engine && typeof engine.positionSeconds === 'function')
                        ? engine.positionSeconds()
                        : null;
                    trace(
                        0x47e9c3be,
                        `music rebuild captured '${stream.url}' state=${stream.state} position=${position == null ? 'none' : position.toFixed(3)}`);
                    musicToRestore.push({
                        stream,
                        args: stream.prepareArgs,
                        position,
                        wasPlaying: stream.request === PlaybackRequest.Playing
                    });
                } else if (!pendingMusicOps.has(stream)) {
                    // Nothing brings this one back, so it mustn't say it's coming.
                    stream.request = PlaybackRequest.None;
                }
            }

            // Tear down music first. These own workers, decoders, fetches, and
            // worklet processors; leaving any of them attached to the discarded
            // context leaks every one of those for the life of the page.
            for (const stream of liveAudioStreams) {
                releaseMusicEngine(stream);
                if (stream.gainNode) {
                    safeAudioCleanup('rebuild music gain disconnect', () => stream.gainNode.disconnect());
                    stream.gainNode = null;
                    stream.destination = null;
                }
            }

            for (const voice of liveVoices) {
                voice.restartAfterRebuild = voice.isLooping && !!voice.activeSource;
                stopActiveSource(voice);
                if (voice.gainNode) {
                    safeAudioCleanup('rebuild voice gain disconnect', () => voice.gainNode.disconnect());
                    voice.gainNode = null;
                }
                voice.destination = null;
                voice.pausedOffset = null;
                voice.ended = true;
            }

            for (const vc of liveVolumeControls) {
                if (vc.gainNode) {
                    safeAudioCleanup('rebuild volume gain disconnect', () => vc.gainNode.disconnect());
                }
            }

            // Retain the decoded PCM before the old context goes away.
            // decodedBuffer is plain sample data and is not bound to a context;
            // only its sample rate ties it to one, which the resample below
            // reconciles.
            const decodedByBuffer = new Map();
            for (const buf of liveAudioBuffers) {
                if (buf.decodedBuffer) {
                    decodedByBuffer.set(buf, buf.decodedBuffer);
                }
            }

            // Detach handlers before closing so the discarded context cannot
            // deliver a statechange that the code below would misread as
            // belonging to the new one.
            oldContext.onstatechange = null;
            audioContext = null;
            scratchBuffer = null;

            try {
                await oldContext.close();
            } catch (e) {
                // A context that refuses to close is still being replaced. The
                // new one is what the graph is rebuilt onto either way, so this
                // must not abort the repair.
                trace(0x47e9c3bb, `AudioContext rebuild close failed (non-fatal): ${e && e.name ? e.name : e}`);
            }

            // Construct and bring up the replacement exactly as the initial
            // context is brought up, before any of the retained state is
            // restored onto it.
            //
            // getAudioContext is the single constructor path, so the rebuilt
            // context is created with the same audio session type, the same
            // AUDIO_CONTEXT_OPTIONS, the same statechange handler, the same
            // unlock arming, and the same visibility listener -- in that order
            // -- as the one built at
            // startup. Including primeOutputPipeline: a context that comes up
            // 'running' is primed inside getAudioContext itself, which is why
            // there is no prime call here for that case.
            //
            // That ordering matters beyond tidiness. A fresh context is primed
            // and brought up before it owns a single buffer, node, or stream,
            // and the rebuild has to reproduce that: the device pipeline is
            // asynchronous work the browser does off this thread, so starting
            // it while the graph is still empty is what overlaps it with the
            // restoration below rather than queueing it behind. The resample
            // loop is the specific hazard -- it awaits an OfflineAudioContext
            // render per off-rate buffer, and anything sequenced after it
            // hands the pipeline construction cost straight back to the first
            // audible cue.
            const ctx = getAudioContext();

            // No resume here. The replacement is left in whatever state it was
            // constructed in and handed back to safeResumeContext, which called
            // this and goes on to resume it as the ordinary resume path -- so
            // there is exactly one place that resumes a context, and the
            // restored music below is waiting on that one call rather than on a
            // second one issued from inside the repair.

            // The new context may have landed on a different sample rate, in
            // which case every decoded buffer must be renormalised -- otherwise
            // each one would be resampled on the audio thread on every single
            // play, which is the very latency this repair exists to remove.
            for (const [buf, decoded] of decodedByBuffer) {
                buf.decodedBuffer = decoded.sampleRate === ctx.sampleRate
                    ? decoded
                    : await resampleToContextRate(ctx, decoded, 'rebuild');
            }

            for (const vc of liveVolumeControls) {
                const previousVolume = vc.gainNode ? vc.gainNode.gain.value : 1.0;
                vc.gainNode = ctx.createGain();
                // Assign .value rather than scheduling: the level must read
                // back correctly within this same turn, for the same reason
                // setVolume documents.
                vc.gainNode.gain.value = previousVolume;
            }

            // Once they all have nodes, as each connects to its parent's
            for (const vc of liveVolumeControls) {
                vc.gainNode.connect(volumeControlOutput(vc, ctx));
            }

            // Voices are rewired but deliberately not restarted. A sound effect
            // is a short cue tied to a moment that has passed; replaying it
            // here would fire it out of context. The node is rebuilt anyway
            // because the game still holds the Voice handle and must be able
            // to play it again -- what is discarded is the playback, not the
            // object. Looping music, which is long-lived and positional, is
            // the one thing restored below.
            for (const voice of liveVoices) {
                voice.gainNode = ctx.createGain();
                voice.destination = outputOf(voice.volumeControl, ctx);
                voice.gainNode.connect(voice.destination);
                // A loop has no end to have been cut short of, so it carries on
                if (voice.restartAfterRebuild) {
                    voice.restartAfterRebuild = false;
                    startSource(voice, voice.sliceOffset, voice.sliceDuration);
                }
            }

            trace(0x47e9c3bc, `AudioContext rebuild complete reason=${reason} rate=${ctx.sampleRate} state=${ctx.state} voices=${liveVoices.size} controls=${liveVolumeControls.size} buffers=${decodedByBuffer.size} music=${musicToRestore.length}`);
            return musicToRestore;
        } catch (e) {
            // A rebuild that fails part-way leaves the graph in pieces, and
            // there is no second recovery behind this one. Surfaced so the
            // session ends with an explanation rather than silently.
            reportAudioError(`AudioContext rebuild failed (${reason})`, e);
            return null;
        } finally {
            graphRebuildInFlight = false;
            releaseGraphRebuildWaiters();
        }
    }

    // Replays the music a rebuild captured, onto the context that replaced the
    // one it was captured from.
    //
    // Deliberately separate from rebuildAudioGraph and called only once the
    // replacement is running, because this reaches playAudioStream, which asks
    // for a resume. Running it here means that request finds the context
    // already running and returns immediately, instead of re-entering the
    // repair decision from inside the repair.
    function restoreMusicAfterRebuild(entries) {
        for (const entry of entries) {
            const { stream, args, position, wasPlaying } = entry;
            ensureStreamOutput(stream);

            // Resume from where the track had got to rather than from its
            // prepared start, so a rebuild mid-song is a continuation and not a
            // restart. Only the start moves: end and loop are absolute
            // positions in the source and survive unchanged.
            let restoreArgs = args;
            if (position != null && position > 0) {
                const range = musicRangeSeconds(buildMusicParams(
                    args.start, args.end, args.loopPoint, args.loop));
                // Never past the end: a track that ran out during the rebuild
                // is restored at its start, since resuming at the end would
                // produce nothing.
                if (range.end == null || position < range.end) {
                    restoreArgs = {
                        start: position,
                        end: args.end,
                        loopPoint: args.loopPoint,
                        loop: args.loop
                    };
                    trace(
                        0x47e9c3bd,
                        `music rebuild resuming '${stream.url}' at ${position.toFixed(3)}s (prepared start ${range.start.toFixed(3)}s)`);
                }
            }

            if (wasPlaying) {
                playAudioStream(stream, restoreArgs.start, restoreArgs.end, restoreArgs.loopPoint, restoreArgs.loop);
            } else {
                prepareAudioStream(stream, restoreArgs.start, restoreArgs.end, restoreArgs.loopPoint, restoreArgs.loop);
            }
        }

        // Requests the game issued *during* the rebuild were queued rather than
        // served, since the graph did not exist to serve them. They are replayed
        // last so a track that was both captured above and re-requested mid-
        // rebuild ends up doing what the newer request asked for.
        flushPendingMusicOps();
    }

    function addUnlockListeners() {
        if (unlockListenersAdded) return;
        unlockListenersAdded = true;

        // 'pointerdown' is deliberately absent. WebKit only treats a specific
        // set of events as activation-triggering for AudioContext.resume(),
        // and pointerdown is not in it: a resume issued from a pointerdown
        // handler rejects with NotAllowedError, so on iOS every tap armed
        // this way was silently discarded and a deferred music play only
        // started when some *later* call happened to fall inside the sticky
        // activation window left by an earlier touchend -- the "music needs
        // a second tap" symptom. 'touchend' and 'click' are the events that
        // do confer activation there, and 'pointerup' covers mouse/pen on
        // engines where the compatibility click is suppressed (input.js
        // preventDefaults the pointer stream, which does not affect user
        // activation but does remove the synthesized click).
        //
        // Touch pointerup must NOT be filtered out here in the hope that
        // touchend will arrive instead. input.js calls preventDefault() on
        // pointerdown and takes pointer capture, and suppressing the pointer
        // stream that way also suppresses the compatibility touch events built
        // on it -- so on a canvas-filling game no touchend is delivered at all.
        // Skipping touch pointerup therefore left nothing to unlock on: the
        // first two taps produced no resume attempt of any kind, and audio only
        // started on a later stray `click`, which is the three-tap symptom.
        // pointerup is a genuine activation-triggering event, so it is used for
        // touch as well.
        const events = ['pointerup', 'touchend', 'click', 'keydown'];
        async function onGesture(event) {
            // Nothing to unlock yet. Reaching requestAudioContextResume with
            // no context would throw and be reported as a fatal audio fault.
            if (!audioContext) return;

            // The test is "is this context actually producing audio", not "has
            // some context been unlocked at some point". `unlocked` is sticky
            // and survives both a suspend and a rebuild, so testing it alone
            // made this handler bail on every gesture that followed a resume --
            // the listeners were armed, they fired, and they returned here
            // without ever issuing resume(). A tap then did nothing, while a
            // drag happened to work only because releasing one takes the game
            // through playAudioStream's paused branch, which issues its own
            // resume() from inside the gesture. Same tap, two different
            // outcomes, decided by which game code the gesture happened to run.
            //
            // Deriving the decision from the context state instead makes the
            // unlock gesture identical on a cold start and after a resume, and
            // keeps working for the rebuilt context, which is a different
            // object that has never been unlocked regardless of what this flag
            // says.
            if (unlocked && audioContext.state === 'running') return;
            const eventName = event && event.type ? event.type : 'unknown';
            const wasUnlocked = unlocked;

            // safeResumeContext owns the resume itself, including tracing an
            // autoplay refusal and re-arming these listeners for the next
            // attempt. A false return means this gesture did not confer
            // transient activation, so there is nothing to unlock yet.
            if (!await safeResumeContext()) {
                trace(0x60f3b28e, `AudioContext unlock gesture did not resume event=${eventName}`);
                return;
            }

            trace(
                wasUnlocked ? 0x0a7de517 : 0x60f3b28d,
                wasUnlocked
                    ? `AudioContext recovered from interruption by gesture event=${eventName}`
                    : `AudioContext unlocked by gesture listener event=${eventName}`);
            events.forEach(e => document.removeEventListener(e, onGesture, true));
            // Clear the guard along with the listeners so a later re-lock
            // (safeResumeContext's failure path) can arm a fresh set. Leaving
            // it set here would make that call a no-op: later sounds would
            // remain scheduled against a suspended context and deferred music
            // would never flush.
            unlockListenersAdded = false;
            if (!wasUnlocked) {
                flushPendingMusicOps();
            }
        }
        events.forEach(e => document.addEventListener(e, onGesture, { capture: true }));
    }

    // Makes sure the centralized gesture unlock is armed before a sound source
    // is scheduled.
    //
    // The gesture listeners installed by addUnlockListeners cannot be relied
    // on for this. They are registered on `document` in the capture phase,
    // while input.js registers its pointer listeners on `window` in the
    // capture phase -- and `window` is an ancestor of `document`, so input.js
    // always runs first. The game therefore receives the pointerdown, runs its
    // handler, and calls playVoice for the tap sound *before* the audio
    // module's own gesture listener has run at all.
    //
    // On top of that, resume() is asynchronous: even once onGesture does run,
    // `unlocked` is not set until its promise resolves, which is a later task.
    // Both effects point the same way -- the first tap's sound is issued while
    // `unlocked` is still false.
    //
    // A source started while the context is suspended is not lost: the context
    // clock is frozen, so start(0) is held until a later qualifying pointerup,
    // touchend, click, or keydown listener completes the shared resume. Do not
    // issue resume() from this function: the game commonly calls it from
    // pointerdown, which WebKit does not accept for audio activation, and that
    // rejected in-flight request would occupy the shared slot and suppress the
    // pointerup that follows.
    function unlockFromGesture() {
        const ctx = getAudioContext();

        if (ctx.state === 'running') {
            unlocked = true;
            return ctx;
        }

        addUnlockListeners();

        return ctx;
    }

    // Reports an audio fault to the managed side
    // (GameBindings.ReportAudioFailure -> GameHost.ReportAudioFailure):
    // the game runs on silently and the fault is reported, except that a
    // Debug build ends with the crash dialog.
    //
    // Every fault in this module goes through here, rather than only logging:
    // a console message is invisible in a shipped build, so a degraded audio
    // engine would reach no one but the players, as unexplained silence.
    //
    // The one exception is a condition the browser is entitled to cause, a
    // music stream whose fetch or decoder did not survive being backgrounded.
    // That is only traced, and the affected track ended, as it isn't a fault.
    //
    // Reporting from a fresh macrotask keeps a Debug build's throw out of
    // whichever event handler or promise found the fault, where it would be
    // attributed to the handler, and makes it genuinely unhandled, which is
    // the channel main.js listens on.
    function reportAudioError(message, detail) {
        trace(0xb52704c9, `audio fault: ${message}${detail ? `: ${detail}` : ''}`);
        let error;
        if (detail instanceof Error) {
            // Do NOT assign to detail.message. DOMException (and Error in some
            // engines) exposes `message` as a readonly accessor on the
            // prototype, so the assignment throws
            // "TypeError: Attempted to assign to readonly property" in WebKit --
            // from inside a .catch handler, turning a reported audio fault into
            // an unhandled promise rejection and a crash. Wrap instead, keeping
            // the original as `cause` so its stack is still available.
            error = new Error(`[audio] ${message}: ${detail.message}`, { cause: detail });
            error.name = 'AudioEngineError';
        } else {
            error = new Error(`[audio] ${message}${detail ? `: ${detail}` : ''}`);
            error.name = 'AudioEngineError';
        }
        setTimeout(() => {
            if (audioFailureReporter) {
                audioFailureReporter(error.message, error.stack ?? '');
            }
        }, 0);
    }

    // AudioBuffer — decoded PCM data. Pure data: every cue that plays
    // a slice of it is independent and lives on the framework side.

    class AudioBuffer {
        constructor() {
            this.decodedBuffer = null;
        }
    }

    function createAudioBuffer() {
        const buf = new AudioBuffer();
        liveAudioBuffers.add(buf);
        return buf;
    }

    function destroyAudioBuffer(buf) {
        liveAudioBuffers.delete(buf);
        buf.decodedBuffer = null;
    }

    function getAudioBufferDuration(buf) {
        return buf.decodedBuffer ? buf.decodedBuffer.duration : 0;
    }

    // ---------------------------------------------------------------------
    // Context lifecycle: suspend on hide, resume on show.
    //
    // Everything that drives the AudioContext between running and stopped
    // lives in this section, in the order it executes: the shared primitives
    // first, then the suspend path, then the resume path, then the visibility
    // listener that calls them.
    // ---------------------------------------------------------------------

    // True when the context is in WebKit's non-standard 'interrupted' state.
    //
    // iOS puts a context here when something else takes the audio session --
    // an incoming call, Siri, an alarm, or a route change such as AirPods
    // disconnecting. It is not in the Web Audio spec, so a state check written
    // against 'running' and 'suspended' alone silently mis-handles it: the
    // context is neither, so code that resumes "if suspended" never fires and
    // code that treats "not running" as already-handled does nothing either.
    // The result is a game that goes permanently silent after a phone call.
    //
    // It is recoverable, and that distinguishes it from the device loss that
    // safeResumeContext reports as fatal: the context object is intact and
    // resume() is the correct response; the interruption merely has to have
    // ended first, which is what returning to the foreground signifies.
    function isContextInterrupted() {
        return !!audioContext && audioContext.state === 'interrupted';
    }

    // Drives the suspend and resume functions from page visibility.
    function addVisibilityListener() {
        if (visibilityListenerAdded) return;
        visibilityListenerAdded = true;

        document.addEventListener('visibilitychange', () => {
            traceContextLifecycle(0x2d8b6f14, `visibilitychange hidden=${document.hidden}`);

            if (document.hidden) {
                // Stamped unconditionally, not only when the context is
                // running. The hidden interval is a property of the page, and
                // a context that reaches 'running' only after the return still
                // spent that whole interval detached from a live device.
                pageHiddenAtMs = Date.now();
                if (audioContext && audioContext.state === 'running') {
                    safeSuspendContext();
                }
                return;
            }

            // 'interrupted' is included deliberately. A context that was taken
            // by a call or Siri comes back in that state rather than
            // 'suspended', and returning to the foreground is precisely the
            // moment the interruption has ended and resume() can succeed.
            // Testing only for 'suspended' left it stranded.
            //
            // NOTE: Only once unlocked. Before the first gesture Chromium's resume() can stay pending
            // rather than be refused, and the gesture that should unlock audio would then find a
            // resume already in flight.
            if (unlocked && audioContext && (audioContext.state === 'suspended' || isContextInterrupted())) {
                safeResumeContext();
            }
        });
    }

    // The single path that stops the context.
    async function safeSuspendContext() {
        if (!audioContext) return;

        // A resume already in flight owns the context. Record the intent and
        // leave: that resume performs the suspend itself once it has settled.
        if (resumeInFlight) {
            trace(0x8c1a5d71, 'AudioContext suspend deferred behind in-flight resume');
            suspendRequestedDuringResume = true;
            resumeRequestedDuringSuspend = false;
            return;
        }

        // Already suspending. The request is already satisfied by the
        // transition in flight, and any resume that raced ahead of it is
        // superseded by this newer suspend.
        if (suspendInFlight) {
            resumeRequestedDuringSuspend = false;
            return;
        }

        suspendInFlight = true;
        resumeRequestedDuringSuspend = false;

        trace(0x8c1a5d70, 'AudioContext suspend requested');

        try {
            // Awaited rather than inspected, because suspend() reports its
            // outcome in three different ways: it normally returns a promise,
            // legacy WebKit returns `undefined` after suspending
            // synchronously, and it can throw synchronously. Awaiting inside
            // the try folds all three into this one try/catch -- `await
            // undefined` is a success with nothing to wait for, and a
            // synchronous throw lands in the catch alongside a rejection.
            await audioContext.suspend();
        } catch (error) {
            // Traced, never reported. On iOS an interruption taking the audio
            // session out from under the context rejects the promise as a
            // matter of course, and the context ends up stopped either way --
            // which is all this call wanted. Escalating that to
            // reportAudioError would report a fault for an incoming phone
            // call.
            trace(0x8c1a5d78, `AudioContext suspend failed (non-fatal): ${error && error.name ? error.name : error}`);
        } finally {
            suspendInFlight = false;
        }

        // A resume arrived while this suspend was settling, so the page is
        // back and the most recent request was to be running. Honour it now
        // that the context is no longer mid-transition.
        if (resumeRequestedDuringSuspend) {
            resumeRequestedDuringSuspend = false;
            trace(0x8c1a5d72, 'AudioContext resuming after suspend (resume requested mid-suspend)');
            await safeResumeContext();
        }
    }

    // The single path that starts the context. Every resume in this module
    // goes through here.
    //
    // Also does everything a successful resume owes the graph: repair the
    // context if the device changed underneath it, otherwise prime the output
    // pipeline and restart the music decoders.
    //
    // Returns true if the context is running afterwards. Callers that have
    // follow-up work to do only on success (the unlock gesture listener) test
    // the result; callers that simply want the device live (music playback,
    // visibility recovery) can ignore it.
    //
    // Deliberately tolerant of being called when no resume is needed or when
    // the browser refuses: a refusal is a normal outcome before the first
    // qualifying gesture, not a fault. That is what lets every caller funnel
    // through here without each one re-deciding which rejections matter.
    async function safeResumeContext() {
        if (!audioContext) return false;

        if (audioContext.state === 'running') {
            unlocked = true;
            return true;
        }

        // A suspend already in flight owns the context. Record the intent and
        // leave: that suspend performs the resume itself once it has settled.
        if (suspendInFlight) {
            trace(0x47e9c3b2, 'AudioContext resume deferred behind in-flight suspend');
            resumeRequestedDuringSuspend = true;
            suspendRequestedDuringResume = false;
            return false;
        }

        // Already resuming. The request is already satisfied by the transition
        // in flight, and any suspend that raced ahead of it is superseded by
        // this newer resume.
        //
        // NOTE: Unless it has been pending far too long, which is a resume the browser will never
        // answer: a new one, with a gesture behind it, is the way out.
        if (resumeInFlight && ((Date.now() - resumeStartedAtMs) < RESUME_STUCK_MS)) {
            suspendRequestedDuringResume = false;
            return false;
        }

        resumeInFlight = true;
        resumeStartedAtMs = Date.now();
        suspendRequestedDuringResume = false;

        try {
            trace(0x47e9c3b1, 'AudioContext resume requested');

            // The repair decision is taken first, before anything is done to
            // the context, because a rebuild replaces the context outright.
            // Deciding here means the rest of this function runs once, against
            // whichever context is the live one by the time it is reached: a
            // rebuild simply substitutes a fresh context underneath and
            // execution falls straight through into the ordinary resume below,
            // which is then the only place that resumes anything.
            //
            // Both inputs are answerable while the context is still suspended.
            // The drift probe builds its own throwaway context, and the hidden
            // interval is a property of the page.

            // A closed context can never run again. 'closed' is terminal by
            // definition -- resume() on it rejects with InvalidStateError
            // rather than recovering -- so a replacement is the only way back,
            // and the rebuild path already exists to construct one.
            //
            // Deliberately not a device-loss report, unlike the resume
            // rejection it pre-empts. 'closed' is a property of this context
            // object, not of the hardware: a browser closes a context out from
            // under the page when the output device goes away, but the device
            // is routinely back by the time the player returns, and a fresh
            // context then comes up perfectly healthy. Reporting it as fatal
            // would end the session for a condition that repairs itself.
            //
            // Checked before the probe below because neither of the other two
            // inputs can change the answer: no measurement of a context that
            // is already finished argues for keeping it, so asking would only
            // cost a throwaway context.
            const contextClosed = (audioContext.state === 'closed') || contextClosedDuringResume;
            contextClosedDuringResume = false;

            // Rebuild only when the hardware sample rate has actually changed.
            //
            // An AudioContext's sampleRate is fixed at construction, but the
            // device underneath it is not: switching to or from Bluetooth, or
            // the OS handing the session back on a different device after an
            // interruption, leaves the live context reporting a rate the
            // hardware is no longer running at. Every buffer is then normalised
            // to a stale rate and the AudioBufferSourceNode resamples it on the
            // audio thread on every single play -- permanent, and unfixable
            // short of a new context.
            //
            // probeHardwareSampleRateDrift answers this by constructing a
            // throwaway context and reading its rate, which is the only way to
            // ask what the hardware is doing now.
            //
            // This replaces an unconditional post-resume rebuild that existed
            // to establish whether rebuilding fixed the delayed-sound-effect
            // symptom at all. It did, and this is the condition that was being
            // looked for: rebuilding on every resume also discarded and
            // reconstructed a perfectly healthy graph, which is expensive and
            // re-costs the music refill each time.
            // NOTE: Not before the first unlock, nothing having played yet to go wrong, so the first
            // tap doesn't pay for a throwaway context
            const rateChanged = !contextClosed && unlocked && probeHardwareSampleRateDrift('resume');

            // How long the page was hidden, resolved before the marker is
            // cleared.
            //
            // A long absence is treated as a rebuild condition in its own
            // right, even though every reading looks nominal.
            //
            // This is deliberately not a measurement of the fault. The fault is
            // not measurable from script: in the captured repro the delayed cue
            // ran 2.4x its expected duration while sampleRate, outputLatency
            // and the graph-vs-hardware lag were all unchanged. What the log
            // does show is that the affected resume followed a 9m15s background
            // interval and took 1006 ms to settle -- by far the longest in the
            // session, against ~540 ms for the short switches that behaved
            // correctly.
            //
            // Rebuilding here trades a graph reconstruction the player cannot
            // hear (the page is only just returning, nothing is scheduled yet)
            // against a delay they certainly can. That trade is only sound
            // because the threshold keeps it off the common path: rapid
            // app-switching, which is frequent, never reaches it.
            const hiddenMs = pageHiddenAtMs ? (Date.now() - pageHiddenAtMs) : 0;
            pageHiddenAtMs = 0;

            // Consumed and cleared together, in the same way the hidden marker
            // above is, so the latch drives exactly one rebuild.
            //
            // Cleared unconditionally rather than only on a successful rebuild.
            // If the rebuild is skipped because a more specific reason already
            // won, the replacement context it builds is new either way and the
            // interruption no longer describes anything live. If the rebuild is
            // attempted and fails, safeResumeContext gives up entirely and
            // there is no later resume for a retained flag to serve.
            const wasInterrupted = contextWasInterrupted;
            contextWasInterrupted = false;

            // Ordered with the closed check first because it is the most
            // specific diagnosis available: a closed context may well also have
            // been hidden past the threshold, and naming that instead would
            // attribute the rebuild to the dwell time rather than to the
            // context being finished.
            // The interruption is ordered above the dwell threshold because it
            // is an observed event rather than an inference: a context that was
            // interrupted and also hidden a long while should be reported as
            // interrupted, since that is what actually took the session away.
            // It sits below the rate probe because a confirmed rate change is
            // the more precise account of the same underlying device swap.
            const rebuildReason = contextClosed
                ? 'context closed'
                : (rateChanged
                    ? 'hardware sample rate changed'
                    : (wasInterrupted
                        ? 'context was interrupted'
                        : (hiddenMs >= HIDDEN_REBUILD_THRESHOLD_MS
                            ? 'hidden beyond reliability threshold'
                            : null)));

            // A rebuild that fails has left the graph in pieces and there is no
            // recovery behind it, so that is the one outcome that gives up
            // here. A rebuild that succeeds falls through to the resume below,
            // which now applies to the replacement context.
            //
            // The music it captured is replayed only after that resume, so the
            // play calls it makes find the context already running and this
            // function is never re-entered from inside the repair.
            let musicToRestore = null;
            if (rebuildReason) {
                trace(0x47e9c3c1, `AudioContext rebuilding before resume (${rebuildReason})`);
                musicToRestore = await rebuildAudioGraph(rebuildReason);
                if (!musicToRestore) {
                    return false;
                }

                // Nothing to resume if the replacement came up running.
                if (audioContext.state === 'running') {
                    unlocked = true;
                    primeOutputPipeline('rebuilt');
                    restoreMusicAfterRebuild(musicToRestore);
                    return true;
                }
            }

            // Start the music refill before resume() is issued, not after it
            // settles.
            //
            // The processor will not consume until its pre-roll threshold is
            // met, and the frames that satisfy it have to be fetched, decoded,
            // and posted -- work that takes far longer than resume() does.
            // Requesting it only once the device is already live means the
            // graph starts rendering against a queue that is still empty, so
            // the first thing the freshly woken device receives is the starved
            // path: partial quanta at render-quantum period, which is the buzz
            // on the way back in.
            //
            // Issued here instead, the refill overlaps the resume rather than
            // queueing behind it. Output is silent throughout -- the processor
            // is still gated on its threshold -- so nothing can be heard early;
            // by the time the device is running the queue is either full or
            // provably unfillable, and the existing watchdog handles the
            // latter.
            resumeMusicAfterSuspend();

            const resumedContext = audioContext;
            try {
                // See safeSuspendContext: awaiting inside the try folds all
                // three of resume()'s outcome shapes -- promise, legacy
                // synchronous `undefined`, synchronous throw -- into this one
                // try/catch.
                await resumedContext.resume();
            } catch (error) {
                // The context is not running, so it is re-locked and the
                // gesture listeners re-armed either way: the next qualifying
                // interaction retries, rather than leaving sounds scheduled
                // against a stopped context and deferred music never flushing.
                unlocked = false;
                addUnlockListeners();

                // A NotAllowedError is the browser's autoplay policy refusing a
                // resume that did not carry transient activation. It is the
                // expected outcome before the first qualifying gesture and on
                // any event type the engine does not treat as activating, so it
                // is traced rather than reported -- escalating it would show the
                // crash dialog on an ordinary cold start. The trace is what
                // makes the refusal visible in a shipped build, where silence
                // with no record is indistinguishable from a resume never
                // attempted.
                if (error && error.name === 'NotAllowedError') {
                    trace(0x60f3b28e, `AudioContext resume refused pending user gesture: ${error.name}`);
                    return false;
                }

                // A context closed under the resume (InvalidStateError, its state 'closed' by
                // now, or replaced meanwhile) is a finished context object, not a lost device:
                // like one found closed above, it's rebuilt, at the next gesture
                if ((error && error.name === 'InvalidStateError')
                    || (resumedContext.state === 'closed')
                    || (resumedContext !== audioContext)) {
                    contextClosedDuringResume = true;
                    trace(0x3c9e51a7, `AudioContext closed during resume; rebuilt on the next gesture: ${error && error.name ? error.name : ''} ${error && error.message ? error.message : error}`);
                    return false;
                }

                // Anything else means the OS tore down the output device and it
                // cannot be re-acquired: there is no API to repair a context in
                // that state, so it is reported as a fault.
                //
                // rebuildAudioGraph is deliberately NOT attempted as a
                // fallback here. It exists for a context that is working but
                // permanently late, and the context it constructs would have to
                // be resumed through this same call -- which is precisely what
                // has just failed.
                reportAudioError('AudioContext resume failed; audio device is unrecoverable', error);
                return false;
            }

            unlocked = true;

            // Snapshot of what the context looks like coming back to the
            // foreground, recorded for every resume whether or not it was
            // preceded by a rebuild.
            traceContextLifecycle(0x1d5c0b43, 'resume succeeded');

            primeOutputPipeline('resumed');

            // Music is deliberately NOT refilled again here. The call above,
            // issued before resume(), has already done it, and repeating it
            // now is actively harmful rather than merely redundant.
            //
            // A second 'resumeWhenReady' allocates a new request id, which
            // makes the processor set _playing = false and re-gate consumption
            // that the first request had already released -- and it re-baselines
            // _resumeBaselineFrames to whatever is queued at that instant, so
            // the new wait demands frames that arrive *after* a queue which is
            // already full. The captured stall is exactly this: request 3
            // cleared at 96712 frames, consumption drained one 1024-frame
            // quantum, and request 4 re-gated at 95688 -- 312 frames under the
            // 96000 high-water escape and not at EOS, so neither of the
            // processor's own escapes applied. The decoder had nothing to
            // produce because the queue was already full, so no new frames
            // could ever satisfy the baseline, and the track sat silent for the
            // full 10s watchdog before the leftover override released it.
            //
            // The rebuild branch is different in kind: its engines are newly
            // constructed against the replacement context and have never been
            // asked to resume, so they still need starting.
            if (musicToRestore) {
                restoreMusicAfterRebuild(musicToRestore);
            }

            return true;
        } finally {
            resumeInFlight = false;

            // A suspend arrived while this resume was settling, so the page has
            // gone away again and the most recent request was to be stopped.
            // Honour it now that the context is no longer mid-transition.
            //
            // In the finally rather than after it, so every exit above -- the
            // autoplay refusal, the unrecoverable device, a failed rebuild --
            // still ends in the state that was asked for last.
            if (suspendRequestedDuringResume) {
                suspendRequestedDuringResume = false;
                trace(0x47e9c3b3, 'AudioContext suspending after resume (suspend requested mid-resume)');
                await safeSuspendContext();
            }
        }
    }

    // Starts one silent single-frame source through the real destination.
    //
    // This is the Howler unlock trick, and its value here is not unlocking --
    // that is already handled -- but forcing the browser to actually build and
    // start its output pipeline. A context can report 'running' with its
    // device pipeline not yet spun up; on WebKit the first *real* start() then
    // pays that construction cost, which is heard as the first sound effect
    // after a resume arriving late. Paying it here, on a source nobody can
    // hear, moves the cost off the first audible cue.
    //
    // Routed to ctx.destination rather than through a volume control: this must
    // exercise the same path a real voice takes all the way to the device, and
    // a muted or ducked control could let the browser shortcut it.
    //
    // Deliberately fire-and-forget and never escalated. A failure here means
    // the prime did not happen, which costs the latency this is trying to
    // avoid and nothing else -- it must not turn into a crash dialog.
    function primeOutputPipeline(reason) {
        if (!audioContext) {
            return;
        }

        try {
            const ctx = audioContext;
            if (!scratchBuffer || scratchBuffer.sampleRate !== ctx.sampleRate) {
                scratchBuffer = ctx.createBuffer(1, 1, ctx.sampleRate);
            }
            const source = ctx.createBufferSource();
            source.buffer = scratchBuffer;
            source.connect(ctx.destination);
            source.onended = () => {
                safeAudioCleanup('scratch source disconnect', () => source.disconnect());
            };
            source.start(0);
        } catch (e) {
            trace(0x47e9c3b8, `AudioContext output prime failed reason=${reason} error=${e && e.name ? e.name : e}`);
        }
    }

    // Re-arms streaming engines after a lifecycle suspend.
    //
    // There is no matching pause function. The processors are never told to
    // stop: they render for as long as the platform pulls from them and stop
    // when it stops asking. This exists only to restart the decoder side,
    // whose worker was frozen while the page was hidden.
    function resumeMusicAfterSuspend() {
        for (const engine of liveStreamingEngines) {
            try {
                engine.resumeAfterSuspend();
            } catch (e) {
                trace(0x8c1a5d76, `music resume-after-suspend failed: ${e && e.name ? e.name : e}`);
            }
        }
    }

    // Guarantees a decoded buffer is at the AudioContext's own sample rate.
    //
    // decodeAudioData is specified to resample to the context rate, so this
    // is normally a no-op assertion. It is kept as defence in depth because
    // the alternative failure is silent and expensive: an AudioBuffer whose
    // rate differs from the context is resampled by the AudioBufferSourceNode
    // on *every* play, on the audio thread, adding latency to a sound effect
    // that is supposed to be instant. Doing the conversion once here, at load
    // time, means the play path can never pay it.
    //
    // The conversion runs through an OfflineAudioContext at the target rate,
    // which is the only resampler the platform exposes to script.
    async function resampleToContextRate(ctx, decoded, filePath) {
        if (!decoded || decoded.sampleRate === ctx.sampleRate) {
            return decoded;
        }

        trace(0x3f7b2e08, `resampling '${filePath}' ${decoded.sampleRate}Hz -> ${ctx.sampleRate}Hz at load`);

        const frames = Math.max(
            1,
            Math.ceil(decoded.duration * ctx.sampleRate));

        const offline = new OfflineAudioContext(
            decoded.numberOfChannels,
            frames,
            ctx.sampleRate);

        const source = offline.createBufferSource();
        source.buffer = decoded;
        source.connect(offline.destination);
        source.start(0);

        return await offline.startRendering();
    }

    async function loadAudioBufferFromContent(buf, filePath) {
        // Never decode against a context a rebuild is in the middle of
        // replacing. See whenGraphRebuildSettles.
        await whenGraphRebuildSettles();

        const ctx = getAudioContext();
        let arrayBuffer;
        try {
            arrayBuffer = await fetchWithRetry(resolveContentUrl(filePath), undefined, async (response) => {
                if (!response.ok) {
                    // NOTE: Not thrown, which the catch below would take for a lost connection
                    writeTrace(0x5d7a20c6, `audio request failed: ${response.status} '${filePath}'`);
                    return waitBehindConnectionMessage(filePath, failedResponseReason(response), `${response.status} ${filePath}`);
                }
                return response.arrayBuffer();
            });
        } catch (error) {
            // NOTE: What arrived was cut short, or the server refused it, which trying again can fix
            writeTrace(0x5d7a20c4, `couldn't load audio '${filePath}': ${error && error.message}`);
            return waitBehindConnectionMessage(filePath);
        }

        let decoded;
        try {
            decoded = await ctx.decodeAudioData(arrayBuffer);
        } catch (error) {
            // A format this browser can't decode -- Ogg Opus before Safari 18.4 -- which no retry
            // fixes: the sound stays silent and the game carries on, as music does where it can't
            // play. Old WebKit rejected with no error at all.
            if (!error || error.name === 'EncodingError') {
                writeTrace(0x5d7a20c5, `can't decode audio '${filePath}', so it won't play: ${error && error.message}`);
                buf.decodedBuffer = ctx.createBuffer(1, 1, ctx.sampleRate);
                return;
            }
            writeTrace(0x5d7a20c4, `couldn't load audio '${filePath}': ${error.name}: ${error.message}`);
            return waitBehindConnectionMessage(filePath);
        }

        buf.decodedBuffer = await resampleToContextRate(ctx, decoded, filePath);
    }

    // Voice — a pre-allocated player for one decoded AudioBuffer.
    // Owns a cached GainNode that is routed once to either the
    // destination or a VolumeControl's gainNode. Each play creates
    // a fresh AudioBufferSourceNode (Web Audio source nodes are
    // inherently one-shot) and routes it through the cached gain;
    // the managed side never allocates per play.

    class Voice {
        constructor(buffer, offsetSeconds, durationSeconds) {
            this.buffer = buffer;
            this.gainNode = null;
            this.destination = null;     // current downstream node
            // The volume control this voice is routed to, or null for the
            // destination. Retained rather than derived from `destination`,
            // which holds an old-context node a rebuild cannot reuse: the
            // routing has to be re-established from the control itself.
            this.volumeControl = null;
            this.activeSource = null;    // currently-playing source, if any
            this.pausedOffset = null;    // seconds within slice if paused
            // Slice baked at creation time. Subsequent Play calls
            // take no parameters and replay this same sub-range.
            this.sliceOffset = offsetSeconds || 0;
            this.sliceDuration = durationSeconds || 0;
            this.startedAt = 0;          // ctx.currentTime the slice's start would have played, at the rate it plays at now
            this.isLooping = false;
            this.playbackRate = 1;       // a multiple of the recorded speed; every source is made with it
            this.restartAfterRebuild = false;
            this.ended = true;
        }
    }

    function createVoice(buffer, offsetSeconds, durationSeconds) {
        if (!buffer || !buffer.decodedBuffer) {
            throw new Error('AudioBuffer has no audio data loaded');
        }

        // Mid-rebuild there is no context to create the gain on -- asking for
        // one would construct a stray context the rebuild then discards. The
        // voice is registered without its node and the rewire loop at the
        // rebuild's tail builds it, which is the same thing that happens to
        // every voice that already existed.
        if (graphRebuildInFlight) {
            const deferred = new Voice(buffer, offsetSeconds, durationSeconds);
            liveVoices.add(deferred);
            trace(0x1d84f6c2, 'createVoice deferred: graph rebuild in flight');
            return deferred;
        }

        const ctx = getAudioContext();

        // Last line of defence for the no-resampling-on-play guarantee.
        //
        // An AudioBufferSourceNode whose buffer rate differs from the
        // context rate is resampled on the audio thread on every single
        // start(), which is precisely the latency this engine removes by
        // normalising at load. loadAudioBufferFromContent already
        // guarantees the match, so reaching here is impossible by
        // construction and means that normalisation was bypassed -- every
        // play of this cue would silently pay the cost. Surfaced rather
        // than patched over, in keeping with the rest of this module:
        // quietly-degraded audio reaches players as unexplained lag with
        // nothing in the logs to explain it.
        if (buffer.decodedBuffer.sampleRate !== ctx.sampleRate) {
            reportAudioError(
                'sound buffer reached createVoice at the wrong sample rate',
                `${buffer.decodedBuffer.sampleRate}Hz vs context ${ctx.sampleRate}Hz`);
        }

        const voice = new Voice(buffer, offsetSeconds, durationSeconds);
        voice.gainNode = ctx.createGain();
        voice.destination = outputOf(null, ctx);
        voice.gainNode.connect(voice.destination);
        liveVoices.add(voice);
        return voice;
    }

    // Teardown helper. Disconnect/stop calls are wrapped only so that a
    // failure part-way through does not abandon the remaining cleanup and
    // leak nodes -- the fault is still reported, never swallowed.
    //
    // The single exemption is InvalidStateError from stop(): calling stop()
    // on a source that was never started is defined to throw, and the stop
    // paths below deliberately call it without tracking that state. Nothing
    // is wrong when it happens, so it is not escalated.
    function safeAudioCleanup(what, action) {
        try {
            action();
        } catch (e) {
            if (e && e.name === 'InvalidStateError') {
                return;
            }
            reportAudioError(`audio node cleanup failed (${what})`, e);
        }
    }

    function destroyVoice(voice) {
        liveVoices.delete(voice);
        stopVoice(voice);
        if (voice.gainNode) {
            safeAudioCleanup('voice gain disconnect', () => voice.gainNode.disconnect());
            voice.gainNode = null;
        }
        voice.destination = null;
        voice.buffer = null;
    }

    function stopActiveSource(voice) {
        const src = voice.activeSource;
        if (!src) {
            return;
        }
        voice.activeSource = null;
        safeAudioCleanup('voice source onended', () => { src.onended = null; });
        safeAudioCleanup('voice source stop', () => src.stop());
        safeAudioCleanup('voice source disconnect', () => src.disconnect());
    }

    function startSource(voice, offsetSeconds, durationSeconds) {
        // Without its cached gain there is nothing to route through, and
        // connect(null) is a WebIDL conversion failure -- which WebKit raises
        // as the bare 'TypeError: Type error', escaping through the [JSImport]
        // boundary as a fatal JSException with no indication of its origin.
        // Reachable whenever a cue lands between a rebuild's teardown and its
        // rewire, or after the voice has been destroyed.
        if (!voice.gainNode) {
            trace(0x1d84f6c3, 'startSource skipped: voice has no gain node (graph rebuilding or voice destroyed)');
            return;
        }

        const ctx = getAudioContext();
        const src = ctx.createBufferSource();
        src.buffer = voice.buffer.decodedBuffer;
        src.connect(voice.gainNode);
        voice.activeSource = src;
        // The offset, the duration and the loop points below are in seconds of the buffer, which
        // the rate doesn't change: only how fast they pass
        if (voice.playbackRate !== 1) {
            src.playbackRate.value = voice.playbackRate;
        }
        // NOTE: From where a resume carries on, so a second pause finds the right place
        voice.startedAt = ctx.currentTime - ((offsetSeconds - voice.sliceOffset) / voice.playbackRate);
        voice.pausedOffset = null;
        voice.ended = false;
        // Reuse a single shared 'ended' handler. The source node
        // carries a back-reference to its owning voice so the handler
        // needs no per-Play closure allocation.
        src._voice = voice;
        src.onended = sharedOnEnded;
        if (voice.isLooping) {
            src.loop = true;
            src.loopStart = voice.sliceOffset;
            if (voice.sliceDuration > 0) {
                src.loopEnd = voice.sliceOffset + voice.sliceDuration;
            }
            src.start(0, offsetSeconds);
        } else if (durationSeconds > 0) {
            src.start(0, offsetSeconds, durationSeconds);
        } else {
            src.start(0, offsetSeconds);
        }
    }

    function sharedOnEnded(event) {
        const src = event.target;
        const voice = src && src._voice;
        if (voice && voice.activeSource === src) {
            voice.activeSource = null;
            voice.ended = true;
        }
    }

    function playVoice(voice) {
        if (!voice || !voice.buffer || !voice.buffer.decodedBuffer) {
            return;
        }

        // The graph is mid-rebuild: this voice has no output node and the
        // context it would be built on does not exist yet. Dropping the cue is
        // the same call the rebuild itself makes about the playback it does not
        // restore -- and the alternative is a fatal TypeError out of
        // startSource. The voice is rewired by the rebuild and plays normally
        // the next time the game asks for it.
        if (graphRebuildInFlight || !voice.gainNode) {
            trace(0x1d84f6c4, 'playVoice dropped: graph rebuild in flight');
            return;
        }

        // A sound effect is nearly always triggered by the very gesture that
        // is meant to unlock audio (a tap on a button). Dropping it here made
        // the first tap silent and required a second one. Arm the unlock
        // listeners instead of issuing resume(): this call commonly arrives
        // from pointerdown, which WebKit will not activate, and a started
        // source is held until a later qualifying gesture completes resume.
        unlockFromGesture();

        if (voice.pausedOffset != null) {
            // Resume from the paused position within the slice.
            const remaining = voice.sliceDuration > 0
                ? Math.max(0, voice.sliceDuration - voice.pausedOffset)
                : 0;
            startSource(voice, voice.sliceOffset + voice.pausedOffset, remaining);
            return;
        }

        stopActiveSource(voice);
        startSource(voice, voice.sliceOffset, voice.sliceDuration);
    }

    function stopVoice(voice) {
        if (!voice) {
            return;
        }
        stopActiveSource(voice);
        voice.pausedOffset = null;
        voice.ended = true;
    }

    function pauseVoice(voice) {
        if (!voice || !voice.activeSource) {
            return;
        }
        voice.pausedOffset = slicePosition(voice);
        stopActiveSource(voice);
    }

    // Seconds into the slice that a playing voice has reached
    // Where the voice is in its slice, in seconds of the buffer
    function slicePosition(voice) {
        const elapsed = Math.max(0, getAudioContext().currentTime - voice.startedAt) * voice.playbackRate;
        const length = voice.sliceDuration > 0
            ? voice.sliceDuration
            : voice.buffer.decodedBuffer.duration - voice.sliceOffset;
        return voice.isLooping && length > 0 ? elapsed % length : Math.min(elapsed, length);
    }

    // A source's length and looping are set as it starts, so one that's
    // playing is replaced by another from the same place
    function setVoiceLooping(voice, isLooping) {
        if (!voice || voice.isLooping === isLooping) {
            return;
        }
        if (!voice.activeSource) {
            voice.isLooping = isLooping;
            return;
        }
        const position = slicePosition(voice);
        stopActiveSource(voice);
        voice.isLooping = isLooping;
        const remaining = voice.sliceDuration > 0 ? Math.max(0, voice.sliceDuration - position) : 0;
        startSource(voice, voice.sliceOffset + position, remaining);
    }

    function isVoicePlaying(voice) {
        return !!(voice && voice.activeSource && !voice.ended);
    }

    // How fast the voice plays, as a multiple of the recorded speed. A source that's playing
    // takes the new rate at once (its 'ended' follows by itself), and the time its slice began
    // is moved so that the place it has reached is the same at the new rate.
    function setVoicePlaybackRate(voice, rate) {
        if (!voice || voice.playbackRate === rate) {
            return;
        }
        const src = voice.activeSource;
        if (!src) {
            voice.playbackRate = rate;
            return;
        }
        const position = slicePosition(voice);
        voice.playbackRate = rate;
        voice.startedAt = getAudioContext().currentTime - (position / rate);
        src.playbackRate.value = rate;
    }

    function setVoiceVolumeControl(voice, vc) {
        if (!voice) {
            return;
        }

        // Record the routing and stop. Mid-rebuild the voice has no gain node
        // to rewire, and the control's node -- if it still has one -- belongs
        // to the context that is being discarded, so connecting to it would
        // raise InvalidAccessError across contexts. The rewire loop reads
        // voice.volumeControl, so the intent recorded here is what it applies.
        if (graphRebuildInFlight || !voice.gainNode) {
            voice.volumeControl = vc || null;
            return;
        }

        safeAudioCleanup('voice gain rewire disconnect', () => voice.gainNode.disconnect());
        voice.volumeControl = vc || null;
        voice.destination = outputOf(vc, getAudioContext());
        voice.gainNode.connect(voice.destination);
    }

    // ──────────────────────────── Music ────────────────────────────
    //
    // Music is streamed through a pure Web Audio graph: a worker fetches,
    // demuxes, and decodes the track in packets (music-worker.js) and an
    // AudioWorkletProcessor plays the decoded frames (music-processor.js).
    //
    // The previous implementation used an HTMLMediaElement behind a
    // MediaElementAudioSourceNode. That put the fetch, the container
    // parsing, the decode, and the loop bookkeeping ('timeupdate' fired on
    // the main thread several times a second) on the same thread as the
    // game loop, so starting or looping a track stole frame time and
    // produced visible hitches. None of that work touches the main thread
    // any more: the main thread only relays already-decoded buffers, and
    // those transfers are zero-copy.
    //
    // Nothing is fetched or decoded until prepare or play. Loading a music
    // track costs one small object, which is what keeps game startup free
    // of music I/O.

    // How much audio must be decoded before prepare resolves. Enough to
    // absorb a decode stall at the moment playback starts, small enough
    // that preparing a track is not itself a long operation.
    const MUSIC_PREROLL_SECONDS = 0.35;

    // Upper bound on decoded-but-unplayed audio. This is the knob that
    // keeps memory flat regardless of track length: the decoder is
    // suspended whenever this much audio is already queued, so a
    // three-minute track costs the same as a ten-second one.
    const MUSIC_HIGH_WATER_SECONDS = 2.0;

    // Opus always decodes at 48 kHz, so every position the game expresses
    // in seconds converts to source frames at this rate — never at the
    // AudioContext rate, which may differ.
    const OPUS_SAMPLE_RATE = 48000;

    // How long a stopped worker is given to close its decoder and abandon
    // its fetch before it is terminated anyway. Teardown is a handful of
    // synchronous calls plus one task, so this only ever matters for a
    // worker that is already wedged.
    const MUSIC_WORKER_CLOSE_TIMEOUT_MS = 2000;

    // How long prepare may go without ANY sign of life from the worker
    // before it is declared wedged. This is deliberately a no-progress
    // window rather than a deadline on the whole prepare: fetching and
    // decoding the pre-roll is network-bound, so on a cold CDN edge or a
    // slow mobile link a perfectly healthy prepare can legitimately take
    // longer than any fixed budget. What is never healthy is a worker that
    // has been silent -- no PCM, no diagnostic, no error -- for this long,
    // and the timer restarts on every message so only that case trips it.
    const MUSIC_PREPARE_STALL_TIMEOUT_MS = 15000;

    // How long a lifecycle refill may go without the processor reporting
    // readiness before it is traced, and how many of those windows pass
    // before it is escalated as a fault.
    //
    // The refill deliberately has no bypass -- it will not start music from
    // leftover PCM -- so a pipeline that never refills would otherwise stay
    // silent with a single trace line and nothing further. Re-arming turns
    // that into a repeating record, and the escalation makes a genuinely
    // wedged worker visible rather than presenting as unexplained silence.
    const MUSIC_RESUME_STALL_TIMEOUT_MS = 2000;
    const MUSIC_RESUME_STALL_LIMIT = 5;

    // Monotonic clock for the stall window. Date.now() would be wrong here:
    // it can jump backwards or forwards with a system clock adjustment.
    const now = () => (typeof performance !== 'undefined' && performance.now)
        ? performance.now()
        : Date.now();

    // Static web asset fingerprinting rewrites every *.js filename
    // (audio.js is served as audio.<hash>.js), so a plain relative URL
    // would 404 in a published build. The document's import map carries
    // the logical -> fingerprinted mapping, and import.meta.resolve is the
    // only API that consults it; new URL() does not. Neither
    // audioWorklet.addModule() nor the Worker constructor resolves module
    // specifiers either, so the specifier is resolved here, once, and the
    // resulting absolute URL handed to both.
    function resolveSiblingModule(specifier) {
        if (typeof import.meta.resolve === 'function') {
            const resolved = import.meta.resolve(specifier);
            if (resolved) {
                return resolved;
            }
        }
        // Unfingerprinted (development) builds serve the literal name.
        return new URL(specifier, import.meta.url).href;
    }

    const musicProcessorUrl = resolveSiblingModule('./music-processor.js');
    const musicWorkerUrl = resolveSiblingModule('./music-worker.js');

    // The addModule promise, together with the context it was loaded into.
    //
    // An AudioWorkletGlobalScope belongs to one AudioContext and is destroyed
    // with it, so a module loaded into a context that has since been replaced
    // has registered nothing in the new one. Caching the promise alone would
    // resolve immediately after a graph rebuild and every AudioWorkletNode
    // construction would then throw 'AudioWorklet does not have a valid
    // AudioWorkletGlobalScope'. Holding the context alongside the promise is
    // what makes the cache invalidate itself on a rebuild.
    let musicWorkletModulePromise = null;
    let musicWorkletModuleContext = null;

    function ensureMusicWorkletModule(ctx) {
        if (!musicWorkletModulePromise || musicWorkletModuleContext !== ctx) {
            musicWorkletModuleContext = ctx;
            // addModule resolves only once the processor script has been
            // evaluated and registerProcessor has run. A rejection here
            // (bad URL, syntax error, failed registration) would otherwise
            // propagate as an opaque prepare failure.
            musicWorkletModulePromise = ctx.audioWorklet.addModule(musicProcessorUrl)
                .then(() => {
                    trace(0x3f68e9a2, `music worklet loaded '${musicProcessorUrl}'`);
                })
                .catch((error) => {
                    // Drop the cache so a later attempt can retry rather than
                    // replaying this rejection for the life of the context.
                    if (musicWorkletModuleContext === ctx) {
                        musicWorkletModulePromise = null;
                        musicWorkletModuleContext = null;
                    }
                    reportAudioError(`failed to load music worklet '${musicProcessorUrl}'`, error);
                    throw error;
                });
        }
        return musicWorkletModulePromise;
    }

    // The URL each music worker is constructed from: a blob URL of the
    // worker script, fetched once.
    //
    // Every track owns its own worker, and constructing a Worker from the
    // script's URL requests it again each time. Tracks that start together
    // (a scene's background music and an ident) race past the HTTP cache and
    // download it twice, and each later track at least revalidates it. The
    // worker imports nothing, so running it from a blob loses nothing.
    // A failed fetch falls back to the script URL, so the Worker constructor
    // reports the failure through its onerror as before.
    let musicWorkerUrlPromise = null;

    function ensureMusicWorkerUrl() {
        if (!musicWorkerUrlPromise) {
            musicWorkerUrlPromise = fetch(musicWorkerUrl)
                .then((response) => {
                    if (!response.ok) {
                        throw new Error(`HTTP ${response.status}`);
                    }
                    return response.blob();
                })
                .then((blob) => URL.createObjectURL(
                    new Blob([blob], { type: 'text/javascript' })))
                .catch((error) => {
                    // Drop the cache so a later track can retry.
                    musicWorkerUrlPromise = null;
                    trace(0x6e2b9d47, `music worker fetch failed (${musicWorkerUrl}): ${error}`);
                    return musicWorkerUrl;
                });
        }
        return musicWorkerUrlPromise;
    }

    let streamingMusicSupportPromise = null;

    // The streaming path needs WebCodecs with an Opus decoder, plus
    // AudioWorklet and Worker. Where any of those is missing the buffered
    // fallback below takes over, so support is probed once and the answer
    // reused rather than branching per track.
    function supportsStreamingMusic() {
        if (!streamingMusicSupportPromise) {
            streamingMusicSupportPromise = (async () => {
                if (typeof AudioDecoder === 'undefined'
                    || typeof AudioWorkletNode === 'undefined'
                    || typeof Worker === 'undefined') {
                    trace(0xd21f4b85, 'streaming music unsupported (missing WebCodecs, AudioWorklet, or Worker)');
                    return false;
                }
                try {
                    const support = await AudioDecoder.isConfigSupported({
                        codec: 'opus',
                        sampleRate: OPUS_SAMPLE_RATE,
                        numberOfChannels: 2
                    });
                    trace(0x5b30ec47, `streaming music opus support=${!!support.supported}`);
                    return !!support.supported;
                } catch (e) {
                    trace(0xe8a6207c, `streaming music probe failed: ${e}`);
                    return false;
                }
            })();
        }
        return streamingMusicSupportPromise;
    }

    // Mirrors DotGame.Framework.Platform.Audio.MusicState so the browser
    // backend reports exactly the same lifecycle as every other
    // platform. Kept in sync with that enum's numeric values.
    const MusicState = {
        Unloaded: 0,
        Loaded: 1,
        Preparing: 2,
        Prepared: 3,
        Playing: 4,
        Paused: 5
    };

    // What the game last asked of a track, as against how far its state has got.
    const PlaybackRequest = {
        None: 0,
        Playing: 1,
        Paused: 2
    };

    class AudioStream {
        constructor() {
            this.url = null;
            // Format of the encoded artifact, supplied by the content
            // manifest so nothing has to be probed at runtime.
            this.channelCount = 2;
            this.gainNode = null;
            this.destination = null;
            this.volumeControl = null;

            this.engine = null;
            this.preparePromise = null;
            this.paramsKey = null;

            // The seconds-domain arguments the current engine was built
            // from, kept so a consumed engine can be rebuilt for exactly the
            // same range without the game having to prepare again.
            this.prepareArgs = null;

            // Set the moment the engine is told to play. An engine is a
            // one-shot pipeline over a specific frame range -- the worker
            // fetches and decodes forward only and cannot rewind -- so once
            // it has played it can neither be replayed nor restarted at the
            // prepared start point, and must be rebuilt.
            this.engineStarted = false;

            // Lifecycle state, tracked here rather than read back from the
            // graph: Music.State and Music.IsPlaying are polled every frame
            // by the game, so they must be field reads and never a round
            // trip.
            this.state = MusicState.Loaded;

            // The two independent reasons to keep the engine (worker,
            // worklet, and decoded audio) alive. Buffers are released only
            // when both are clear, which is what makes prepare/play/
            // unprepare/stop order-independent.
            this.prepareRetained = false;
            this.playbackRetained = false;

            // What Music.IsPlaying and Music.IsPaused report, from the call
            // that asked, however long the track takes to get there. The
            // prepare continuation starts playback if it's still Playing.
            // A teardown leaves it, as a rebuild's replay still has to honor it.
            this.request = PlaybackRequest.None;
        }
    }

    function createAudioStream() {
        // Deliberately allocation-only. No AudioContext is created here:
        // constructing one before a user gesture leaves it suspended, and
        // music loading happens long before the player has interacted.
        const stream = new AudioStream();
        liveAudioStreams.add(stream);
        return stream;
    }

    function loadAudioStreamFromContent(stream, filePath, channelCount) {
        stream.url = resolveContentUrl(filePath);
        if (channelCount > 0) {
            stream.channelCount = channelCount;
        }
    }

    // Fallback for content whose manifest carries no precomputed duration.
    // Metadata loading remains streaming and never materializes the encoded
    // file or decoded PCM in JavaScript memory.
    async function getAudioStreamDuration(stream) {
        if (!stream || !stream.url) {
            return 0;
        }
        const media = new Audio();
        media.preload = 'metadata';
        media.crossOrigin = 'anonymous';
        try {
            return await new Promise((resolve) => {
                media.onloadedmetadata = () => resolve(Number.isFinite(media.duration) ? media.duration : 0);
                media.onerror = () => resolve(0);
                media.src = stream.url;
                media.load();
            });
        } finally {
            media.onloadedmetadata = null;
            media.onerror = null;
            media.removeAttribute('src');
            media.load();
        }
    }

    // Returns the context this stream's output is attached to, or null when a
    // graph rebuild owns the context.
    //
    // The null answer is the point of the function. Across a rebuild
    // `audioContext` is deliberately null, and getAudioContext() responds to a
    // null context by *constructing* one -- which here would be a different
    // context from the replacement the rebuild is about to install. The
    // stream's gain node, and the AudioWorkletNode the caller is about to hang
    // off it, would then live in a context wired to nothing: the track is
    // silent, and the stray context is never closed. WebKit allows only a
    // handful of AudioContexts per page, so a few rebuilds taken this way
    // exhaust the budget and audio stops working for the rest of the session.
    // Callers defer instead, and the rebuild's rewire and replay bring the
    // stream back on the real context.
    function ensureStreamOutput(stream) {
        if (graphRebuildInFlight) {
            trace(0x1d84f6c6, `stream output deferred during rebuild '${stream && stream.url ? stream.url : 'none'}'`);
            return null;
        }

        const ctx = getAudioContext();
        if (!stream.gainNode) {
            // One cached gain node per stream, exactly as Voice does, so
            // routing to a VolumeControl is a single reconnect rather than
            // a rebuild of the playback graph.
            stream.gainNode = ctx.createGain();
            // A control that was created (or carried) across a rebuild can be
            // waiting for its own node, and connect(null) is a WebIDL
            // conversion failure that WebKit reports only as
            // 'TypeError: Type error'. Fall back to the destination, which is
            // what the rebuild's rewire loop does for the same case.
            stream.destination = outputOf(stream.volumeControl, ctx);
            stream.gainNode.connect(stream.destination);
        }
        addVisibilityListener();
        return ctx;
    }

    function toFrames(seconds) {
        return Math.max(0, Math.round(seconds * OPUS_SAMPLE_RATE));
    }

    // Converts the game's (start, duration, loopPoint) seconds into the
    // absolute source-frame range the decoder works in. loopPoint is a
    // position in the music, as start and end are, on every platform.
    function buildMusicParams(startSeconds, endSeconds, loopPointSeconds, loop) {
        const startFrame = toFrames(startSeconds != null ? startSeconds : 0);
        const endFrame = endSeconds != null ? toFrames(endSeconds) : null;
        const loopFrame = loopPointSeconds != null
            ? toFrames(loopPointSeconds)
            : startFrame;
        return { startFrame, endFrame, loopFrame, loop: !!loop };
    }

    // The source-time range a set of music params describes, in seconds.
    // Shared by both engines and by the rebuild's position arithmetic so
    // there is one conversion from frames back to the seconds the game uses.
    function musicRangeSeconds(params) {
        const rate = OPUS_SAMPLE_RATE;
        return {
            start: params.startFrame / rate,
            end: params.endFrame != null ? params.endFrame / rate : null,
            loopStart: params.loopFrame / rate
        };
    }

    // Where playback has actually reached, in absolute source seconds, given
    // how much audio the processor has retired since it was constructed.
    //
    // The processor's retired total is the only play cursor the streaming
    // pipeline has: it counts frames that have genuinely left for the device,
    // so it advances in real time and stalls when the track stalls, which a
    // wall clock started at play() would not. It counts *output* frames, hence
    // the divide by the context rate rather than the Opus rate.
    //
    // The elapsed figure is a distance travelled, not a position, so a looping
    // track has to be folded back into its range: the first pass runs from
    // start, and every pass after that from the loop point, which is not
    // necessarily the same place.
    function musicPositionFromElapsed(params, elapsedSeconds) {
        const range = musicRangeSeconds(params);
        if (!(elapsedSeconds > 0)) {
            return range.start;
        }

        // Without an end there is no range to wrap within, so the position is
        // simply how far it has run.
        //
        // For a looping track that is a distance, not a position, and the two
        // part company the moment it wraps: the range ends at the end of the
        // file, whose length is known only to the worker that demuxes it, so
        // there is nothing here to take the modulus against and the estimate
        // grows without bound. Handing that back as a resume start puts the
        // next prepare beyond the last page, where no page satisfies the seek
        // and the range decodes to nothing -- and, being a loop, retries that
        // empty range forever. Report the prepared start instead: a rebuilt
        // loop restarts its cycle rather than continuing mid-phrase, which is
        // audible but correct, where the alternative is permanent silence.
        if (range.end == null) {
            return params.loop ? range.start : range.start + elapsedSeconds;
        }

        const firstPass = range.end - range.start;
        if (elapsedSeconds < firstPass || firstPass <= 0) {
            return range.start + elapsedSeconds;
        }

        // A non-looping track that has run past its end is finished; report
        // the end rather than a position outside the range.
        if (!params.loop) {
            return range.end;
        }

        const loopLength = range.end - range.loopStart;
        if (loopLength <= 0) {
            return range.loopStart;
        }
        return range.loopStart + ((elapsedSeconds - firstPass) % loopLength);
    }

    function musicParamsKey(params) {
        return `${params.startFrame}/${params.endFrame}/${params.loopFrame}/${params.loop}`;
    }

    function cancelPendingMusic(stream) {
        pendingMusicOps.delete(stream);
    }

    // Queues a music request that arrived while a graph rebuild owned the
    // context, to be replayed once the replacement is running.
    //
    // Shares pendingMusicOps with the unlock deferral because it is the same
    // situation: the track wants to be playing, the graph cannot serve it yet,
    // and the newest request per stream should win. restoreMusicAfterRebuild
    // flushes the map for exactly the reason an unlock gesture does.
    //
    // releaseMusicEngine is what makes the replay work rather than silently
    // doing nothing. Its reset clears preparePromise and paramsKey, and
    // beginMusicPrepare short-circuits on a live preparePromise with a matching
    // key -- so without the reset the replay would be handed back the very
    // prepare that was just abandoned, and the track would never start.
    function deferMusicDuringRebuild(stream, startSeconds, endSeconds, loopPointSeconds, loop, wantsPlay) {
        const play = !!wantsPlay;

        releaseMusicEngine(stream);

        trace(0x1d84f6c7, `music request deferred during rebuild '${stream.url}' play=${play}`);

        pendingMusicOps.set(stream, () => {
            if (play) {
                replayDeferredPlay(stream, startSeconds, endSeconds, loopPointSeconds, loop);
            } else {
                prepareAudioStream(stream, startSeconds, endSeconds, loopPointSeconds, loop);
            }
        });
    }

    // A play the game paused while it waited loads for the resume that may come,
    // and doesn't start.
    function replayDeferredPlay(stream, startSeconds, endSeconds, loopPointSeconds, loop) {
        if (stream.request === PlaybackRequest.Paused) {
            stream.playbackRetained = true;
            beginMusicPrepare(stream, startSeconds, endSeconds, loopPointSeconds, loop);
            return;
        }

        playAudioStream(stream, startSeconds, endSeconds, loopPointSeconds, loop);
    }

    // Replays every music request that was deferred while the graph could not
    // serve it -- because audio was locked, or because a rebuild owned the
    // context.
    //
    // The map is emptied before any of them run: each invoke re-enters
    // playAudioStream, which will itself defer again if the unlock did not
    // actually take, and mutating the map while iterating it would otherwise
    // either drop that new entry or loop on it.
    function flushPendingMusicOps() {
        if (pendingMusicOps.size === 0) {
            return;
        }
        const ops = Array.from(pendingMusicOps.values());
        pendingMusicOps.clear();
        trace(0x0a7de518, `replaying ${ops.length} deferred music request(s)`);
        for (const invoke of ops) {
            invoke();
        }
    }

    // Shuts a music worker down without ever terminating it mid-decode.
    //
    // The worker replies 'closed' once it has closed its AudioDecoder and
    // aborted its fetch; only then is the thread terminated, which is still
    // what guarantees the worker, its buffers, and its script are gone. The
    // timeout is the backstop for a worker that never answers (one wedged
    // before its message loop, say) — terminating such a worker is no worse
    // than leaking it.
    function closeMusicWorker(worker) {
        let terminated = false;
        let watchdog = 0;

        const terminate = () => {
            if (terminated) {
                return;
            }
            terminated = true;
            clearTimeout(watchdog);
            worker.onmessage = null;
            worker.onerror = null;
            safeAudioCleanup('music worker terminate', () => worker.terminate());
        };

        worker.onmessage = (event) => {
            if (event.data && event.data.type === 'closed') {
                terminate();
            }
        };
        // A worker that faults while closing is never going to acknowledge.
        worker.onerror = terminate;

        watchdog = setTimeout(() => {
            // The worker never acknowledged. Terminating now is a last resort:
            // it can land on a live decoder, which is the content-process crash
            // this handshake exists to avoid -- so it is recorded rather than
            // done silently, because it is a prime suspect for any tab kill
            // that follows.
            trace(0xc47d1e93, 'music worker close not acknowledged; forcing terminate');
            terminate();
        }, MUSIC_WORKER_CLOSE_TIMEOUT_MS);

        try {
            worker.postMessage({ type: 'close' });
        } catch (error) {
            terminate();
        }
    }

    // Streaming engine — the intended path. Owns one worker and one
    // worklet node; both are created on prepare and destroyed on stop, so
    // a stopped track holds no decoder, no buffers, and no thread.
    class StreamingMusicEngine {
        constructor(stream, params) {
            this._stream = stream;
            this._params = params;
            this._node = null;
            this._worker = null;
            this._ended = false;

            // Resolver for the in-flight prepare, held here so stop() can
            // settle it. A stop that lands while prepare is still awaiting
            // its pre-roll would otherwise leave that promise pending
            // forever, pinning the engine, the stream, and every closure
            // above it -- a leak that grows with each start/stop cycle.
            this._settle = null;
            this._stopped = false;

            this._resumeRequestId = 0;
            this._resumeTimeout = 0;
            this._resumePending = false;
            this._resumeStallCount = 0;
            this._resumeLeftoverReleased = false;
            this._lastResumeWaiting = null;

            this._pcmRelayCount = 0;
            this._foregroundedAt = 0;

            // Frames the processor has retired, and the context rate they were
            // rendered at. Together these are the play cursor: the only
            // measure of how far this track has actually got that advances
            // with real output and stalls when output stalls. Retained here so
            // a graph rebuild can resume from the position instead of
            // restarting the track from its prepared start.
            //
            // Updated from the 'consumed' messages the processor already
            // sends; nothing extra is posted for this.
            this._retiredFrames = 0;
            this._retiredRate = 0;

            liveStreamingEngines.add(this);

            // Paired with the stop trace below. Every engine constructed must
            // reach exactly one stop, so a session log should show these two
            // counts interleaving around a stable figure. A live count that
            // only climbs is an engine that was discarded without being
            // released, which pins a worker, a WebCodecs decoder, a streaming
            // fetch, and a worklet processor for the lifetime of the page.
            trace(0x9f2b640b, `music engine start url='${stream.url}' live=${liveStreamingEngines.size}`);
        }

        _settlePrepare() {
            const settle = this._settle;
            if (settle) {
                this._settle = null;
                settle();
            }
        }

        // Records the play cursor. A running total, so the newest value is
        // always the whole truth and a dropped message costs nothing.
        _notePosition(retiredFrames, sampleRate) {
            if (typeof retiredFrames === 'number' && retiredFrames > this._retiredFrames) {
                this._retiredFrames = retiredFrames;
                this._retiredRate = sampleRate;
            }
        }

        // Absolute source position this track has reached, or null if it has
        // not produced audio yet (in which case the caller should keep the
        // prepared start).
        //
        // The retired total counts frames at the *output* rate, which is why
        // the rate they were rendered at is captured alongside them: a rebuild
        // onto a context with a different rate must not divide by the new one.
        positionSeconds() {
            if (this._retiredFrames <= 0 || this._retiredRate <= 0) {
                return null;
            }
            return musicPositionFromElapsed(
                this._params,
                this._retiredFrames / this._retiredRate);
        }

        async prepare() {
            const stream = this._stream;
            const ctx = ensureStreamOutput(stream);
            if (!ctx) {
                // A rebuild started while this prepare was in flight. Returning
                // leaves the engine half-built, which is deliberate and safe:
                // beginMusicPrepare sees the same rebuild on the way out,
                // retires this engine, and re-queues the request against the
                // replacement context.
                return;
            }

            const [, workerUrl] = await Promise.all([
                ensureMusicWorkletModule(ctx),
                ensureMusicWorkerUrl()
            ]);

            // These are awaited, so a stop can already
            // have run by the time they resolve. Building the node and worker
            // now would leak both, since stop() has nothing left to tear down.
            if (this._stopped) {
                return;
            }

            const node = new AudioWorkletNode(ctx, 'music-processor', {
                numberOfInputs: 0,
                numberOfOutputs: 1,
                outputChannelCount: [stream.channelCount],
                processorOptions: { channelCount: stream.channelCount }
            });
            node.connect(stream.gainNode);
            this._node = node;
            node.onprocessorerror = (event) => {
                // The processor threw and has been permanently removed from
                // the rendering graph by the browser; this track can never
                // produce audio again, so it is not recoverable.
                if (this._stopped) {
                    return;
                }
                reportAudioError('music worklet processor error', event);
            };

            // type: 'module' loads the worker as an ES module: it is parsed
            // in strict mode, as music-processor.js is: addModule() always
            // parses it as a module. The URL is the shared blob (see
            // ensureMusicWorkerUrl), so starting a track fetches nothing.
            const worker = new Worker(workerUrl, { type: 'module' });
            this._worker = worker;

            // A worker that fails to load or throws at top level reports
            // through onerror, never through onmessage, so without this a
            // broken worker script is completely silent — no audio and no
            // diagnostic.
            worker.onerror = (event) => {
                if (this._stopped) {
                    return;
                }
                // A worker that fails to load or throws at top level is a
                // broken deployment, not a transient fault: no music will
                // ever play. Settle the prepare first so the caller is not
                // left awaiting a worker that is never going to respond.
                this._settlePrepare();
                reportAudioError(
                    `music worker script error (${musicWorkerUrl})`,
                    (event && event.message) ? event.message : event);
            };

            const preRollFrames = Math.round(MUSIC_PREROLL_SECONDS * ctx.sampleRate);
            const highWaterFrames = Math.round(MUSIC_HIGH_WATER_SECONDS * ctx.sampleRate);

            let bufferedFrames = 0;
            // Last time the worker or the processor showed any sign of life.
            // Drives the stall watchdog below.
            let lastProgressAt = now();
            let settleTransport = null;
            const ready = new Promise((resolve) => { this._settle = resolve; });

            node.port.onmessage = (event) => {
                if (this._stopped) {
                    return;
                }
                lastProgressAt = now();
                const message = event.data;
                if (message.type === 'consumed') {
                    // Relay mode only: with a direct transport this message
                    // goes processor -> worker and never arrives here, which
                    // is why the cursor is carried by 'position' as well.
                    this._notePosition(message.retiredFrames, ctx.sampleRate);
                    // Relay the play cursor to the decoder; this is what
                    // releases it to decode the next window. The payload is a
                    // running total, so dropping one of these (during
                    // teardown, say) costs only latency -- the next message
                    // re-establishes the decoder's account in full.
                    worker.postMessage({
                        type: 'consumed',
                        retiredFrames: message.retiredFrames
                    });
                } else if (message.type === 'position') {
                    // The play cursor, sent to the main thread regardless of
                    // which transport carries the decoder's copy.
                    this._notePosition(message.retiredFrames, ctx.sampleRate);
                } else if (message.type === 'ended') {
                    this._ended = true;
                    onAudioStreamEnded(stream);
                } else if (message.type === 'prepared') {
                    bufferedFrames = message.buffered || bufferedFrames;
                    this._settlePrepare();
                } else if (message.type === 'transportReady') {
                    if (settleTransport) {
                        settleTransport(true);
                    }
                } else if (message.type === 'resumeWaiting') {
                    this._lastResumeWaiting = message;
                    trace(
                        0x8c1a5d7b,
                        `music resume waiting request=${message.requestId} buffered=${message.buffered} baseline=${message.baseline} threshold=${message.threshold} highWater=${message.highWater} retired=${message.retiredFrames} eos=${message.endOfStream}`);
                } else if (message.type === 'resumeReady') {
                    if (message.requestId === this._resumeRequestId) {
                        this._resumePending = false;
                        clearTimeout(this._resumeTimeout);
                        this._resumeTimeout = 0;
                    }
                    trace(
                        0x8c1a5d7c,
                        `music resume ready request=${message.requestId} buffered=${message.buffered} newFrames=${message.newFrames} fromLeftover=${message.fromLeftover === true} retired=${message.retiredFrames} eos=${message.endOfStream} waitMs=${this._foregroundedAt > 0 ? (now() - this._foregroundedAt).toFixed(1) : 'n/a'}`);
                } else if (message.type === 'starved' || message.type === 'recovered') {
                    const sinceForegroundMs = this._foregroundedAt > 0
                        ? now() - this._foregroundedAt
                        : null;
                    const extent = message.type === 'recovered'
                        ? ` starvedQuanta=${message.starvedQuanta} starvedFrames=${message.starvedFrames}`
                        : '';
                    trace(
                        message.type === 'starved' ? 0x12fa6d37 : 0x8ad517c4,
                        `music processor ${message.type} buffered=${message.buffered} retired=${message.retiredFrames}${extent} resumeRequest=${this._resumeRequestId} sinceForegroundMs=${sinceForegroundMs !== null ? sinceForegroundMs.toFixed(1) : 'n/a'}`);
                }
            };

            worker.onmessage = (event) => {
                if (this._stopped) {
                    return;
                }
                lastProgressAt = now();
                const message = event.data;
                switch (message.type) {
                    case 'pcm':
                        // Straight relay, transferring ownership onward so
                        // the main thread never copies a sample.
                        node.port.postMessage(message, message.channelData);
                        this._pcmRelayCount++;
                        bufferedFrames += message.frames;
                        if (bufferedFrames >= preRollFrames) {
                            this._settlePrepare();
                        }
                        break;

                    case 'pcmBatch':
                        // The buffers remain individually transferable; the
                        // batch reduces dispatches without concatenating PCM.
                        node.port.postMessage(message, message.transfers);
                        this._pcmRelayCount++;
                        bufferedFrames += message.frames;
                        if (bufferedFrames >= preRollFrames) {
                            this._settlePrepare();
                        }
                        break;

                    case 'endOfStream':
                        node.port.postMessage({ type: 'endOfStream' });
                        this._settlePrepare();
                        break;

                    case 'error':
                        // A failed prepare must never hang the caller, so the
                        // stream is ended and the prepare settled -- but the
                        // fetch/demux/decode fault is reported rather than
                        // left to present as unexplained silence.
                        this._ended = true;
                        onAudioStreamEnded(stream);

                        // Not playing yet, so that didn't end it: without this the
                        // prepare's continuation would start the dead engine, and
                        // the track would say it's playing, silently, from then on.
                        if (stream.engine === this) {
                            stream.request = PlaybackRequest.None;
                            stream.prepareRetained = false;
                            stream.playbackRetained = false;
                            releaseMusicEngine(stream);
                        }

                        this._settlePrepare();
                        reportAudioError(`music worker failed for '${stream.url}'`, message.message);
                        break;

                    case 'diagnostic':
                        if (message.event === 'rangeFetch') {
                            trace(0x45db907f, `music worker rangeFetch pass=${message.pass} ranged=${message.ranged} offset=${message.byteOffset}`);
                        } else {
                            trace(
                                0x45db907e,
                                `music worker ${message.event} pass=${message.pass} posted=${message.postedFrames} retired=${message.retiredFrames} outstanding=${message.outstandingFrames}`
                                    + (message.event === 'rangeEnd'
                                        ? ` waits=${message.capacityWaitCount} maxDecodeQueue=${message.maxDecoderQueueSize}`
                                        : ''));
                        }
                        break;

                    default:
                        break;
                }
            };

            const startMessage = {
                type: 'start',
                url: stream.url,
                outputSampleRate: ctx.sampleRate,
                startFrame: this._params.startFrame,
                endFrame: this._params.endFrame,
                loopFrame: this._params.loopFrame,
                loop: this._params.loop,
                preRollFrames,
                highWaterFrames
            };

            if (typeof MessageChannel !== 'undefined') {
                const channel = new MessageChannel();
                let transportTimer = 0;
                const transportReady = new Promise((resolve) => {
                    settleTransport = (connected) => {
                        settleTransport = null;
                        clearTimeout(transportTimer);
                        resolve(connected);
                    };
                    transportTimer = setTimeout(() => {
                        if (settleTransport) {
                            settleTransport(false);
                        }
                    }, 250);
                });

                try {
                    node.port.postMessage({
                        type: 'connect',
                        port: channel.port2,
                        prepareThreshold: preRollFrames
                    }, [channel.port2]);
                } catch (error) {
                    if (settleTransport) {
                        settleTransport(false);
                    }
                }

                const connected = await transportReady;
                if (this._stopped) {
                    channel.port1.close();
                    return;
                }
                if (connected) {
                    startMessage.transportPort = channel.port1;
                    worker.postMessage(startMessage, [channel.port1]);
                    trace(0x52b17d4a, 'music PCM transport=direct');
                } else {
                    channel.port1.close();
                    worker.postMessage(startMessage);
                    trace(0x52b17d4b, 'music PCM transport=relay');
                }
            } else {
                worker.postMessage(startMessage);
                trace(0x52b17d4b, 'music PCM transport=relay');
            }

            // Without this the caller simply never returns and the failure is
            // indistinguishable from silence.
            //
            // The timer re-arms rather than firing once: it only reports when
            // the worker has been completely silent for the whole window, and
            // it never counts time spent hidden, because a backgrounded page
            // has its timers clamped and its worker's fetch deprioritised --
            // wall-clock time there says nothing about whether the pipeline is
            // wedged. When it does fire the prepare is settled as well as
            // reported, so the caller is not left awaiting a worker that has
            // already been declared dead.
            let watchdog = 0;
            const armWatchdog = (delay) => {
                watchdog = setTimeout(() => {
                    if (this._stopped || !this._settle) {
                        return;
                    }
                    if (typeof document !== 'undefined' && document.hidden) {
                        lastProgressAt = now();
                        armWatchdog(MUSIC_PREPARE_STALL_TIMEOUT_MS);
                        return;
                    }
                    const idle = now() - lastProgressAt;
                    if (idle < MUSIC_PREPARE_STALL_TIMEOUT_MS) {
                        armWatchdog(MUSIC_PREPARE_STALL_TIMEOUT_MS - idle);
                        return;
                    }
                    // NOTE: A game awaiting this mustn't hang on its launch screen, and the track
                    // is the only thing lost
                    this._settlePrepare();
                    trace(0x5e20c7a1, `music prepare stalled for '${stream.url}': no worker activity for ${Math.round(idle)}ms (bufferedFrames=${bufferedFrames}, needed=${preRollFrames})`);
                }, delay);
            };
            armWatchdog(MUSIC_PREPARE_STALL_TIMEOUT_MS);

            try {
                await ready;
            } finally {
                clearTimeout(watchdog);
            }
        }

        play() {
            if (this._node) {
                // No resume here. The engine is a consumer of a context that
                // someone else has already brought up: every caller of this
                // method requests the resume immediately beforehand, which is
                // the layer that owns lifecycle decisions.
                //
                // Asking for it from here would invert that -- the music
                // pipeline calling back up into the lifecycle orchestrator that
                // is, on some paths, what started this playback -- and a
                // suspended context is not silently broken anyway: the clock is
                // frozen, so the processor simply waits and starts when the
                // pending resume lands.
                //
                // A lifecycle refill owns the processor until it has new PCM
                // or EOS. A raw play would clear that wait and start from
                // leftover frames.
                //
                // Traced because it is a silent path: the caller believes the
                // track is playing and the resume that will actually start it
                // happens later, so without a record this is indistinguishable
                // from a play that was simply lost.
                if (this._resumePending) {
                    trace(
                        0x8c1a5d7d,
                        `music play deferred to lifecycle resume request=${this._resumeRequestId} resumePending=${this._resumePending}`);
                    return;
                }
                this._node.port.postMessage({ type: 'play' });
            } else {
                reportAudioError('music play called with no worklet node', 'prepare did not complete');
            }
        }

        pause() {
            this._resumeRequestId++;
            this._resumePending = false;
            this._resumeStallCount = 0;
            clearTimeout(this._resumeTimeout);
            this._resumeTimeout = 0;
            if (this._node) {
                this._node.port.postMessage({ type: 'pause' });
            }
        }

        // Pause driven by the page being backgrounded, as opposed to the game
        // calling Music.Pause. Kept separate so the stream's own Playing/
        // Paused state -- which the game polls -- is not disturbed by
        // something the player did not do.
        //
        // The message is what actually matters: it reaches the processor on
        // the rendering thread and stops it consuming its queue, so no starved
        // partial quanta can be emitted while the worker is frozen and the
        // device is still live.
        //
        // Returns a promise that settles once the processor has stopped and
        // rendered enough silence to replace audio already queued between the
        // graph and the hardware. Merely acknowledging the pause message is
        // insufficient: it proves the flag changed, not that the last audible
        // buffers have left the device queue.
        resumeAfterSuspend() {
            // Deliberately not gated on a matching pause. Nothing pauses this
            // engine for a suspend any more -- the processor is left running
            // and the platform stops pulling from it on its own -- so there is
            // no flag to test. What still has to happen is restarting the
            // decoder side: the worker's fetch and AudioDecoder were frozen
            // with the page, so the queue the processor has been draining is
            // stale and needs refilling before consumption resumes.
            //
            // Only resume consumption if the track was genuinely playing; a
            // track the game had paused must stay paused.
            if (this._node && this._stream && this._stream.state === MusicState.Playing) {
                const requestId = ++this._resumeRequestId;
                const ctx = getAudioContext();
                const thresholdFrames = Math.round(MUSIC_PREROLL_SECONDS * ctx.sampleRate);
                const highWaterFrames = Math.round(MUSIC_HIGH_WATER_SECONDS * ctx.sampleRate);
                this._foregroundedAt = now();
                this._resumePending = true;
                this._resumeStallCount = 0;
                this._resumeLeftoverReleased = false;
                this._lastResumeWaiting = null;
                const pcmRelaysAtRequest = this._pcmRelayCount;
                this._node.port.postMessage({
                    type: 'resumeWhenReady',
                    requestId,
                    thresholdFrames,
                    highWaterFrames
                });

                // A short track reaches readiness through endOfStream even if
                // it cannot fill the threshold. This watchdog never sends
                // 'play' -- that would clear the processor's wait behind its
                // back -- so while it still has reason to expect a refill it
                // re-arms and reports instead.
                //
                // Once the windows are exhausted the refill is not merely slow.
                // The queue can sit just below the worker high-water mark, so
                // neither of the processor's own escapes applies: not EOS, and
                // not full enough to prove the decoder is parked on capacity.
                // If the fetch or decoder did not survive being backgrounded,
                // no message can produce the frames that gate is waiting for
                // and the track stays silent for the session while the game
                // still reports it as playing. So the last window permits the
                // processor to start from the leftover queue it already has,
                // which is bounded by the same pre-roll threshold a fresh
                // refill would have had to meet. Only if that is refused too --
                // meaning there is genuinely not even a pre-roll to play -- is
                // this reported as a fault.
                clearTimeout(this._resumeTimeout);
                const armResumeWatchdog = () => {
                    this._resumeTimeout = setTimeout(() => {
                        if (this._stopped
                            || requestId !== this._resumeRequestId
                            || !this._resumePending
                            || !this._node
                            || !this._stream
                            || this._stream.state !== MusicState.Playing) {
                            return;
                        }

                        this._resumeStallCount++;
                        const waiting = this._lastResumeWaiting;
                        const pcmRelays = this._pcmRelayCount - pcmRelaysAtRequest;
                        const detail = `request=${requestId} window=${this._resumeStallCount}/${MUSIC_RESUME_STALL_LIMIT} waitMs=${(now() - this._foregroundedAt).toFixed(1)} pcmRelays=${pcmRelays} buffered=${waiting ? waiting.buffered : 'n/a'} baseline=${waiting ? waiting.baseline : 'n/a'} threshold=${waiting ? waiting.threshold : 'n/a'} highWater=${waiting ? waiting.highWater : 'n/a'}`;

                        if (this._resumeStallCount >= MUSIC_RESUME_STALL_LIMIT) {
                            this._resumeTimeout = 0;
                            trace(0x8c1a5d7e, `music resume refill releasing from leftover PCM ${detail}`);
                            this._resumeLeftoverReleased = true;
                            this._node.port.postMessage({
                                type: 'resumeAllowLeftover',
                                requestId
                            });

                            // The processor answers with 'resumeReady' if the
                            // leftover covers the pre-roll. Nothing else will
                            // arrive if it does not, so one final window ends
                            // the track instead of leaving it wedged.
                            //
                            // Deliberately not reportAudioError: a background
                            // track that cannot refill after the page was
                            // backgrounded isn't a fault, as the browser is free
                            // to tear down a fetch or a decoder while hidden.
                            // The condition is traced, and the track is ended
                            // through the same path a naturally finished stream
                            // uses, so the game observes it as stopped and can
                            // start it again rather than believing it plays.
                            this._resumeTimeout = setTimeout(() => {
                                if (this._stopped
                                    || requestId !== this._resumeRequestId
                                    || !this._resumePending
                                    || !this._stream
                                    || this._stream.state !== MusicState.Playing) {
                                    return;
                                }
                                this._resumeTimeout = 0;
                                this._resumePending = false;
                                trace(
                                    0x8c1a5d7f,
                                    `music resume abandoned for '${this._stream.url}' ${detail} leftoverReleaseRefused=true`);
                                this._ended = true;
                                onAudioStreamEnded(this._stream);
                            }, MUSIC_RESUME_STALL_TIMEOUT_MS);
                            return;
                        }

                        trace(0x8c1a5d7a, `music resume refill stalled ${detail}`);
                        armResumeWatchdog();
                    }, MUSIC_RESUME_STALL_TIMEOUT_MS);
                };
                armResumeWatchdog();
            }
        }

        stop() {
            if (this._stopped) {
                return;
            }
            this._stopped = true;
            this._resumeRequestId++;
            this._resumePending = false;
            this._resumeStallCount = 0;
            clearTimeout(this._resumeTimeout);
            this._resumeTimeout = 0;
            liveStreamingEngines.delete(this);
            trace(0x9f2b640c, `music engine stop url='${this._stream ? this._stream.url : 'none'}' live=${liveStreamingEngines.size}`);

            if (this._worker) {
                // The worker owns a WebCodecs AudioDecoder and a streaming
                // fetch, and terminate() stops its thread wherever it happens
                // to be -- including inside the decoder. On WebKit that kills
                // the content process outright and the page reloads. It is
                // reached on every stop of a *looping* track: a loop never
                // ends on its own, so the decoder is always live at that
                // point, whereas a one-shot track has normally already run to
                // end-of-stream and closed its decoder before the stop, which
                // is why only looping music showed the fault.
                //
                // So the worker is asked to close first and terminated only
                // once it acknowledges, by which point it holds no decoder
                // and no request. Drop the existing handlers immediately
                // regardless: a message already in flight would otherwise
                // still be dispatched against an engine being torn down.
                const worker = this._worker;
                this._worker = null;
                worker.onerror = null;
                closeMusicWorker(worker);
            }
            if (this._node) {
                const node = this._node;
                this._node = null;
                node.port.onmessage = null;
                node.onprocessorerror = null;
                // 'release' retires the processor on the audio thread. A
                // disconnected AudioWorkletNode does NOT stop its processor:
                // the rendering thread keeps its own reference and keeps
                // calling process() for as long as it returns true, so
                // without this every stopped track leaks a live processor
                // and its queue for the lifetime of the page.
                node.port.postMessage({ type: 'release' });
                safeAudioCleanup('music worklet node disconnect', () => node.disconnect());
            }

            // Release anyone still awaiting prepare. The worker that would
            // have resolved it is closing and its messages are no longer
            // dispatched here, so without this the promise never settles and
            // everything it closes over is pinned.
            this._settlePrepare();
        }
    }


    function releaseMusicEngine(stream) {
        if (stream.engine) {
            stream.engine.stop();
            stream.engine = null;
        }
        stream.preparePromise = null;
        stream.paramsKey = null;
        stream.engineStarted = false;

        if (stream.state !== MusicState.Unloaded) {
            stream.state = MusicState.Loaded;
        }
    }

    // Applies the retention rules after a retain flag is cleared: the
    // engine is torn down only when neither a prepare nor an active
    // playback still holds it.
    function settleMusicRetention(stream) {
        if (!stream.prepareRetained && !stream.playbackRetained) {
            releaseMusicEngine(stream);
            return;
        }

        if (!stream.playbackRetained
            && (stream.state === MusicState.Playing
                || stream.state === MusicState.Paused)) {
            stream.state = MusicState.Prepared;
        }

        if (!stream.engineStarted || stream.playbackRetained) {
            // Never played, or still playing: the engine is either parked at
            // the prepared start point or actively in use, and must be left
            // alone. (Dropping a prepare retain mid-playback lands here.)
            return;
        }

        // The engine has produced audio, so its worker has already streamed
        // past the prepared start point (and, for a track that reached its
        // natural end, has closed its decoder altogether). Leaving it in
        // place would report Prepared while holding a pipeline that either
        // plays nothing at all -- the "audible only every other play" fault --
        // or resumes from wherever the stop landed instead of the prepared
        // start. Rebuild it so Prepared means what it says.
        const args = stream.prepareArgs;
        const prepareRetained = stream.prepareRetained;

        releaseMusicEngine(stream);

        if (prepareRetained && args) {
            beginMusicPrepare(stream, args.start, args.end, args.loopPoint, args.loop);
        }
    }

    // Builds the engine and fetches/decodes up to the pre-roll threshold
    // without producing any audio, then either settles at Prepared or
    // flushes a play request that arrived while it was preparing.
    function beginMusicPrepare(stream, startSeconds, endSeconds, loopPointSeconds, loop) {
        // A rebuild owns the graph: there is no context to bind an engine to
        // yet, and asking for one would construct a stray context rather than
        // the replacement. See ensureStreamOutput for what that costs.
        if (graphRebuildInFlight) {
            deferMusicDuringRebuild(stream, startSeconds, endSeconds, loopPointSeconds, loop, stream.request === PlaybackRequest.Playing);
            return Promise.resolve();
        }

        const params = buildMusicParams(startSeconds, endSeconds, loopPointSeconds, loop);
        const key = musicParamsKey(params);

        if (stream.preparePromise && stream.paramsKey === key) {
            return stream.preparePromise;
        }

        // A different range needs a different engine: the decoder streams
        // a specific range rather than the whole file.
        //
        // The request survives the release, so the continuation below starts
        // the rebuilt engine. Were it lost, the track would settle at Prepared
        // and stay silent until the next Play found it Prepared and started
        // it -- the "every other play is audible" symptom.
        releaseMusicEngine(stream);

        stream.prepareArgs = {
            start: startSeconds,
            end: endSeconds,
            loopPoint: loopPointSeconds,
            loop: loop
        };
        stream.paramsKey = key;
        stream.state = MusicState.Preparing;

        stream.preparePromise = (async () => {
            // Streaming is the only music path. A browser without WebCodecs,
            // AudioWorklet, Worker, or Opus gets no music rather than a
            // fallback: the HTMLMediaElement engine that used to cover those
            // cases only mattered for Safari before 26, and it carried its own
            // lifecycle model -- a second set of suspend/resume rules for a
            // configuration that is no longer shipped to.
            //
            // Reported and returned rather than thrown. Music is not worth
            // failing a scene load over, and the stream is left in Preparing
            // with no engine, which the state accessors already handle.
            if (!(await supportsStreamingMusic())) {
                trace(0xd21f4b86, `music unsupported on this browser; '${stream.url}' will not play`);
                return;
            }

            // A stop, unprepare, or differently-parameterised request can have
            // superseded this prepare while the capability probe was in
            // flight. Building the engine anyway orphans it: the stop ran
            // releaseMusicEngine while stream.engine was still null (it is
            // only assigned below, after this await), so there was nothing for
            // it to tear down -- and the continuation bails out on this same
            // check, so nothing ever stops what is built here. An orphaned
            // engine keeps a worker, a live WebCodecs decoder, a streaming
            // fetch, and an AudioWorkletNode whose processor is never sent
            // 'release', all for the lifetime of the page. Repeat that on a
            // few scene changes and the tab is killed for memory.
            if (stream.paramsKey !== key) {
                return;
            }

            const engine = new StreamingMusicEngine(stream, params);
            stream.engine = engine;

            try {
                await engine.prepare();
            } catch (error) {
                // A prepare that throws leaves this engine referenced by
                // nobody the moment a stop has already cleared stream.engine:
                // the catch below can only tear down what the stream still
                // points at. Stopping it here is what guarantees its worker,
                // decoder, and processor are retired on the failure path too.
                engine.stop();
                if (stream.engine === engine) {
                    stream.engine = null;
                }
                throw error;
            }

            // A rebuild started while this prepare was in flight, so the engine
            // is either half-built (ensureStreamOutput refused it a context) or
            // bound to a context that is being discarded. Retire it and re-queue
            // the request; the replay builds a fresh engine on the replacement.
            //
            // Checked before the supersede test below because this is the more
            // specific condition: the stream still points at this engine, so
            // that test would pass and leave the dead engine installed.
            if (graphRebuildInFlight && stream.engine === engine) {
                deferMusicDuringRebuild(stream, startSeconds, endSeconds, loopPointSeconds, loop, stream.request === PlaybackRequest.Playing);
                return;
            }

            // prepare() awaits the worklet module, the fetch, and the pre-roll,
            // so the same supersede can land here. stop() is idempotent and is
            // what actually retires the worker, the decoder, and the processor.
            if (stream.paramsKey !== key || stream.engine !== engine) {
                trace(0x1d5a8e37, 'music prepare superseded; releasing orphaned engine');
                engine.stop();
                if (stream.engine === engine) {
                    stream.engine = null;
                }
            }
        })().then(() => {
            // A stop, unprepare, or differently-parameterised request may
            // have superseded this one while it was preparing.
            if (stream.paramsKey !== key || !stream.engine) {
                return;
            }

            if (stream.request === PlaybackRequest.Playing) {
                stream.state = MusicState.Playing;
                stream.engineStarted = true;
                safeResumeContext();
                stream.engine.play();
            } else {
                stream.state = MusicState.Prepared;
            }
        }).catch((error) => {
            if (stream.paramsKey !== key) {
                return;
            }
            // A prepare failure must not reject into the game's await, so the
            // engine is torn down and the track degrades to silence. The fault
            // itself is escalated to the crash dialog: silently swallowing it
            // makes a broken music pipeline indistinguishable from a track
            // that is merely quiet.
            stream.request = PlaybackRequest.None;
            stream.playbackRetained = false;
            releaseMusicEngine(stream);
            reportAudioError(`music prepare failed for '${stream.url}'`, error);
        });

        return stream.preparePromise;
    }

    // Primes a track: fetches and decodes up to the pre-roll threshold and
    // resolves, without producing any audio. Retains the engine until
    // unprepareAudioStream is called, so a later stop leaves the track
    // Prepared rather than releasing it.
    function prepareAudioStream(stream, startSeconds, endSeconds, loopPointSeconds, loop) {
        if (!stream || !stream.url || stream.state === MusicState.Unloaded) {
            return Promise.resolve();
        }

        stream.prepareRetained = true;

        const key = musicParamsKey(buildMusicParams(startSeconds, endSeconds, loopPointSeconds, loop));

        // Playing or paused, the retain is all this does: rebuilding the engine
        // for another range would stop what the game is listening to.
        if (stream.state === MusicState.Playing
            || stream.state === MusicState.Paused
            || (stream.state === MusicState.Prepared && stream.paramsKey === key)) {
            return Promise.resolve();
        }

        // Nor does it change the range of a play that's on its way.
        if (stream.state === MusicState.Preparing
            && stream.request !== PlaybackRequest.None
            && stream.preparePromise) {
            return stream.preparePromise;
        }

        return beginMusicPrepare(stream, startSeconds, endSeconds, loopPointSeconds, loop);
    }

    // Releases the prepare retain. The engine survives if playback still
    // holds it; the eventual stop then performs the release.
    function unprepareAudioStream(stream) {
        if (!stream || stream.state === MusicState.Unloaded) {
            return;
        }

        stream.prepareRetained = false;

        settleMusicRetention(stream);
    }

    function playAudioStream(stream, startSeconds, endSeconds, loopPointSeconds, loop) {
        if (!stream || stream.state === MusicState.Unloaded) {
            return;
        }

        if (!stream.url) {
            throw new Error('AudioStream has no audio source loaded');
        }

        // Paused before it started, so what it was asked to play is still on its
        // way: this lets it start, ignoring the range arguments as a resume does.
        // Starts it here only if it's ready. The prepare continuation or the
        // deferred request does otherwise, as each reads the request.
        if (stream.request === PlaybackRequest.Paused
            && stream.state !== MusicState.Paused) {
            stream.request = PlaybackRequest.Playing;
            stream.playbackRetained = true;
            if (stream.state === MusicState.Prepared
                && stream.engine
                && !graphRebuildInFlight) {
                if (!unlocked || isContextInterrupted()) {
                    unlockFromGesture();
                }
                stream.state = MusicState.Playing;
                stream.engineStarted = true;
                safeResumeContext();
                stream.engine.play();
            }
            return;
        }

        stream.request = PlaybackRequest.Playing;

        // A rebuild owns the graph. Every branch below either asks
        // getAudioContext() for a context outright or reaches beginMusicPrepare,
        // and both would build against a context that is about to be replaced.
        // Checked ahead of the paused branch on purpose: the rebuild tears the
        // engine down regardless, so there is no pause position left to resume
        // from and this has to become a fresh play once the graph is back.
        if (graphRebuildInFlight) {
            deferMusicDuringRebuild(stream, startSeconds, endSeconds, loopPointSeconds, loop, true);
            return;
        }

        // Resuming from pause ignores the range arguments, per
        // AudioService.Play's documented contract.
        if (stream.state === MusicState.Paused) {
            stream.playbackRetained = true;
            stream.state = MusicState.Playing;
            stream.engineStarted = true;
            getAudioContext();
            safeResumeContext();
            if (stream.engine) {
                stream.engine.play();
            }
            return;
        }

        if (stream.state === MusicState.Playing) {
            return;
        }

        if (!unlocked || isContextInterrupted()) {
            // Arm the centralized unlock listeners. This call often arrives
            // from pointerdown, which WebKit will not activate, so resume()
            // is not issued here. A later qualifying gesture completes it.
            //
            // An interrupted context takes the same route. `unlocked` stays
            // true across an iOS interruption, so without this check a play
            // issued after a phone call would go straight to starting sources
            // against a context that renders nothing.
            unlockFromGesture();
        }

        if (!unlocked) {
            // Genuinely locked -- this play did not originate from a gesture.
            // Defer the whole request, including the fetch and decode, rather
            // than spending them on audio that cannot be heard. Keyed on the
            // stream, so a later play on a *different* track queues alongside
            // this one instead of replacing it, while a repeat play of this
            // same track supersedes its own earlier request as it should.
            pendingMusicOps.set(stream, () =>
                replayDeferredPlay(stream, startSeconds, endSeconds, loopPointSeconds, loop));
            return;
        }

        stream.playbackRetained = true;

        const params = buildMusicParams(startSeconds, endSeconds, loopPointSeconds, loop);
        const key = musicParamsKey(params);

        if (stream.state === MusicState.Prepared
            && stream.paramsKey === key
            && stream.engine) {
            // Primed for exactly this range: start with minimal latency.
            stream.state = MusicState.Playing;
            stream.engineStarted = true;
            safeResumeContext();
            stream.engine.play();
            return;
        }

        // Play is fire-and-forget: if the track was never prepared, or was
        // prepared for a different range, preparation starts now and
        // playback begins the moment it is ready.
        beginMusicPrepare(stream, startSeconds, endSeconds, loopPointSeconds, loop);
    }

    // Stops playback and releases the playback retain. The engine is torn
    // down only when no prepare retain is outstanding; otherwise the track
    // settles back to Prepared so the next play is still latency-free.
    function stopAudioStream(stream) {
        if (!stream || stream.state === MusicState.Unloaded) {
            return;
        }

        stream.request = PlaybackRequest.None;
        stream.playbackRetained = false;
        cancelPendingMusic(stream);

        trace(0x4e82c7d1, `stopAudioStream state=${stream.state}`);

        if (stream.engine
            && (stream.state === MusicState.Playing
                || stream.state === MusicState.Paused)) {
            stream.engine.pause();
        }

        settleMusicRetention(stream);
    }

    function pauseAudioStream(stream) {
        if (!stream || stream.request !== PlaybackRequest.Playing) {
            return;
        }

        stream.request = PlaybackRequest.Paused;

        // A track still preparing, or waiting to, doesn't start when it's ready.
        if (stream.state !== MusicState.Playing) {
            return;
        }

        stream.state = MusicState.Paused;

        trace(0x5b17ea93, 'pauseAudioStream');

        if (stream.engine) {
            stream.engine.pause();
        }
    }

    // Playback reached its natural end. Uses the same retention
    // accounting as an explicit stop.
    function onAudioStreamEnded(stream) {
        if (!stream || stream.state !== MusicState.Playing) {
            return;
        }

        stream.request = PlaybackRequest.None;
        stream.playbackRetained = false;

        settleMusicRetention(stream);
    }

    function getAudioStreamState(stream) {
        return stream ? stream.state : MusicState.Unloaded;
    }

    function isAudioStreamPlaying(stream) {
        return !!stream && stream.request === PlaybackRequest.Playing;
    }

    function isAudioStreamPaused(stream) {
        return !!stream && stream.request === PlaybackRequest.Paused;
    }

    function setAudioStreamVolumeControl(stream, vc) {
        if (!stream) {
            return;
        }
        stream.volumeControl = vc;
        trace(0x7a3e1b56, `setAudioStreamVolumeControl vc=${vc ? 'yes' : 'null'} gain=${vc && vc.gainNode ? vc.gainNode.gain.value : 'n/a'} wired=${stream.gainNode ? 'yes' : 'deferred'}`);
        if (stream.gainNode) {
            safeAudioCleanup('music gain rewire disconnect', () => stream.gainNode.disconnect());
            stream.destination = outputOf(vc, getAudioContext());
            stream.gainNode.connect(stream.destination);
        }
    }

    function destroyAudioStream(stream) {
        liveAudioStreams.delete(stream);
        cancelPendingMusic(stream);
        if (stream) {
            // Drop both retains so the teardown below always releases,
            // regardless of any outstanding prepare.
            stream.prepareRetained = false;
            stream.playbackRetained = false;
        }
        stopAudioStream(stream);
        if (stream && stream.gainNode) {
            safeAudioCleanup('music gain disconnect', () => stream.gainNode.disconnect());
            stream.gainNode = null;
            stream.destination = null;
        }
        if (stream) {
            stream.url = null;
            stream.volumeControl = null;
            stream.prepareArgs = null;
            stream.state = MusicState.Unloaded;
        }
    }

    // VolumeControl — wraps a GainNode for volume control.

    class VolumeControl {
        constructor() {
            this.gainNode = null;
            this.parent = null;
        }
    }

    // Over every control with no parent, and everything playing with no control
    let masterVolumeControl = null;

    function getMasterVolumeControl() {
        if (!masterVolumeControl) {
            masterVolumeControl = new VolumeControl();
            liveVolumeControls.add(masterVolumeControl);

            // As createVolumeControl: mid-rebuild, the rewire loop gives it a node
            if (!graphRebuildInFlight) {
                const ctx = getAudioContext();
                masterVolumeControl.gainNode = ctx.createGain();
                masterVolumeControl.gainNode.connect(ctx.destination);
            }
        }

        return masterVolumeControl;
    }

    // The node that what plays through a control (or none) connects to
    function outputOf(vc, ctx) {
        const control = vc || getMasterVolumeControl();
        return control.gainNode || ctx.destination;
    }

    // The node a control's own gain connects to
    function volumeControlOutput(vc, ctx) {
        return (vc === masterVolumeControl) ? ctx.destination : outputOf(vc.parent, ctx);
    }

    function setVolumeControlParent(vc, parent) {
        vc.parent = parent || null;
        if (vc.gainNode) {
            vc.gainNode.disconnect();
            vc.gainNode.connect(volumeControlOutput(vc, getAudioContext()));
        }
    }

    function createVolumeControl() {
        const vc = new VolumeControl();

        // As createVoice: mid-rebuild there is no context to build the gain on,
        // and the rewire loop adopts any control that reaches it without one.
        if (graphRebuildInFlight) {
            liveVolumeControls.add(vc);
            trace(0x1d84f6c5, 'createVolumeControl deferred: graph rebuild in flight');
            return vc;
        }

        const ctx = getAudioContext();
        vc.gainNode = ctx.createGain();
        vc.gainNode.connect(outputOf(null, ctx));
        liveVolumeControls.add(vc);
        return vc;
    }

    function destroyVolumeControl(vc) {
        liveVolumeControls.delete(vc);
        for (const child of liveVolumeControls) {
            if (child.parent === vc) {
                setVolumeControlParent(child, null);
            }
        }
        // Drop the back-references before the node goes. A voice that kept
        // pointing at a destroyed control would be rewired to it by a later
        // rebuild, reconnecting the graph to a control the game has released.
        for (const voice of liveVoices) {
            if (voice.volumeControl === vc) {
                voice.volumeControl = null;
            }
        }
        for (const stream of liveAudioStreams) {
            if (stream.volumeControl === vc) {
                stream.volumeControl = null;
            }
        }
        if (vc.gainNode) {
            vc.gainNode.disconnect();
            vc.gainNode = null;
        }
    }

    function getVolume(vc) {
        return vc.gainNode ? vc.gainNode.gain.value : 1.0;
    }

    function clearVolumeAutomation(gain) {
        // Volume controls own their AudioParam exclusively, so clear its
        // entire automation timeline rather than only events beginning at
        // `now`. cancelScheduledValues(now) does not remove a curve whose
        // start time is already in the past, which makes a reused control
        // retain stale automation on browsers without cancel-and-hold.
        gain.cancelScheduledValues(0);
    }

    function setVolume(vc, volume) {
        if (vc.gainNode) {
            // Cancel any in-flight fade ramp first. Without this, a pending
            // ramp scheduled by fadeVolume stays on the automation timeline
            // and continues ramping toward its target after the set, e.g. a
            // Stop(fade) immediately followed by Play of the same song would
            // fade the new playback out.
            //
            const gain = vc.gainNode.gain;
            clearVolumeAutomation(gain);

            // Assign the intrinsic value rather than scheduling it with
            // setValueAtTime(volume, now). An automation event only takes
            // effect once the audio thread reaches it, so `gain.value` keeps
            // reporting the *previous* volume for the rest of this JS turn --
            // and a fade started in that same turn seeds itself by reading
            // `gain.value`, which would make it start from the level the
            // control held before this set. Assigning `.value` applies
            // immediately and reads back consistently.
            gain.value = volume;
            trace(0x3f9c05e2, `setVolume volume=${volume}`);
        }
    }

    // The fade *shape* is not decided here. `samples` arrives already
    // evaluated by the managed side (framework/Audio/VolumeFadeCurve.cs) as
    // evenly spaced gain values, the first being the volume the fade starts
    // from and the last the volume it ends on. This function only schedules
    // them.
    //
    // That split is deliberate. The curve is an extensible class a game can
    // subclass, so there is no shape identifier that could be mapped to a
    // native ramp here -- and evaluating it in one place means the browser and
    // the platforms that poll their volume produce the same fade by
    // construction rather than by two implementations kept in step. It also
    // removes the exponential ramp's awkwardness around zero: the curve's last
    // sample is the exact requested target, so a fade to silence lands on true
    // zero with no epsilon floor and no corrective step.
    function fadeVolume(vc, durationSeconds, samples) {
        if (!vc.gainNode) {
            return;
        }

        const ctx = getAudioContext();
        const now = ctx.currentTime;
        const gain = vc.gainNode.gain;

        // `samples` is a memory view over the WASM heap, valid only for the
        // duration of this call, so it must be copied before it is handed to
        // Web Audio -- which retains the curve for the whole fade. slice()
        // detaches a Float64Array copy from the heap, and Float32Array.from
        // narrows it to the only type setValueCurveAtTime accepts. (A memory
        // view is not itself a typed array, so it cannot be passed to either
        // one directly.)
        const curve = samples ? Float32Array.from(samples.slice()) : null;
        const sampleCount = curve ? curve.length : 0;

        clearVolumeAutomation(gain);

        trace(0x6b1f27a4, `fadeVolume duration=${durationSeconds} samples=${sampleCount} from=${sampleCount ? curve[0] : 'n/a'} to=${sampleCount ? curve[sampleCount - 1] : 'n/a'}`);

        if (durationSeconds > 0 && sampleCount >= 2) {
            try {
                // The gain holds the curve's final value once the curve ends, so
                // no terminal setValueAtTime is needed to pin the target.
                gain.setValueCurveAtTime(curve, now, durationSeconds);
                return;
            } catch (e) {
                // Falling through to the immediate set would be silent data
                // loss, so the fault is surfaced -- but the target is still
                // applied below so the mix does not get stuck at the wrong
                // level.
                reportAudioError('failed to schedule volume fade curve', e);
            }
        }

        // No fade to run (or scheduling it failed) — apply the target, which is
        // the last sample. With no samples at all there is nothing to apply.
        if (sampleCount > 0) {
            gain.setValueAtTime(curve[sampleCount - 1], now);
        }
    }

    return {
        initializeAudio,
        prepareAudioContext,
        createAudioBuffer,
        destroyAudioBuffer,
        getAudioBufferDuration,
        loadAudioBufferFromContent,
        createVoice,
        destroyVoice,
        playVoice,
        stopVoice,
        pauseVoice,
        isVoicePlaying,
        setVoiceLooping,
        setVoicePlaybackRate,
        setVoiceVolumeControl,
        createAudioStream,
        destroyAudioStream,
        loadAudioStreamFromContent,
        getAudioStreamDuration,
        prepareAudioStream,
        unprepareAudioStream,
        playAudioStream,
        stopAudioStream,
        pauseAudioStream,
        getAudioStreamState,
        isAudioStreamPlaying,
        isAudioStreamPaused,
        setAudioStreamVolumeControl,
        createVolumeControl,
        getMasterVolumeControl,
        setVolumeControlParent,
        destroyVolumeControl,
        getVolume,
        setVolume,
        fadeVolume
    };
}
