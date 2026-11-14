// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

// music-processor.js — the audio-thread half of the streaming music
// pipeline.
//
// This processor is deliberately dumb: it is a pure PCM sink. It owns no
// timing policy, no loop logic, and no notion of "where in the track" it
// is. The decoder (music-worker.js) already
// trims every decoded packet to the exact frame boundaries of the
// requested play range and re-emits the loop region as many times as
// needed, so by the time frames arrive here the only correct thing to do
// is play them back to back.
//
// Keeping the policy out of the audio thread is the whole point: process()
// runs every 128 frames (~2.7 ms at 48 kHz) on a real-time thread, and any
// branch, allocation, or garbage created here risks an audible glitch and
// shows up as a frame-rate hitch when the main thread has to help the GC.
// What remains below is a copy loop over pre-decoded Float32Arrays.
//
// Transport is the worklet MessagePort carrying transferred (zero-copy)
// Float32Array buffers rather than a SharedArrayBuffer ring: the game is
// not served with COOP/COEP headers, so `crossOriginIsolated` is false and
// SharedArrayBuffer is unavailable. Transfers are still allocation-free on
// the audio thread — ownership of the underlying buffer moves, nothing is
// copied.

// How many frames may be consumed before telling the main thread. The
// decoder uses these acknowledgements as its backpressure signal, so this
// trades message traffic against how promptly the decoder learns it may
// decode more. 4096 frames is ~85 ms at 48 kHz: far more often than the
// buffer high-water mark is reached, far less often than every quantum.
const CONSUMED_REPORT_INTERVAL_FRAMES = 4096;

// How far below the decoder's high-water mark the queue may sit and still be
// treated as proof that the decoder is parked. See _tryResumeWhenReady.
//
// Sized from CONSUMED_REPORT_INTERVAL_FRAMES, because that is what bounds the
// discrepancy: the decoder's view of the retired total is refreshed only once
// per report interval, so its idea of how much is outstanding can lead this
// processor's queue depth by at most one interval's worth of frames plus
// whatever single PCM chunk is in flight. One interval is that bound with room
// to spare -- ~85ms at 48kHz, against a 2s high-water mark.
//
// Deliberately far smaller than the pre-roll threshold (16800 frames), so a
// queue released by this slack is still comfortably above the depth needed to
// play without starving on the first quantum.
const RESUME_HIGH_WATER_SLACK_FRAMES = CONSUMED_REPORT_INTERVAL_FRAMES;

class MusicProcessor extends AudioWorkletProcessor {
    constructor(options) {
        super();

        const processorOptions = (options && options.processorOptions) || {};

        this._channelCount = processorOptions.channelCount || 2;

        // Queue of decoded chunks. Each entry is an array of one
        // Float32Array per channel, all of equal length. `_readOffset` is
        // the frame cursor into the head chunk; a chunk is shifted off
        // only once fully consumed, so a chunk larger than a render
        // quantum is drained across several process() calls without ever
        // being re-sliced or copied. `_queueHead` advances in constant time;
        // consumed entries are compacted only when a PCM message arrives.
        this._queue = [];
        this._queueHead = 0;
        this._readOffset = 0;

        // Frames sitting in `_queue` and not yet played. Tracked
        // incrementally so neither the decoder nor process() has to walk
        // the queue to answer "how much is buffered?".
        this._bufferedFrames = 0;

        this._playing = false;

        // Set once the decoder has pushed the final chunk. Draining the
        // queue after this is what constitutes the end of the track.
        this._endOfStream = false;
        this._ended = false;

        // Suppresses repeated 'starved' notifications while the decoder
        // is catching up; re-armed as soon as data arrives again.
        this._starved = false;

        // How many quanta ran short during the current dry spell, and how
        // many frames of silence they emitted in total. Reported with the
        // matching 'recovered' so a single dropout and a sustained underrun
        // burst are distinguishable in a trace; both look identical when
        // only the leading 'starved' is recorded.
        this._starvedQuanta = 0;
        this._starvedFrames = 0;

        this._consumedSinceReport = 0;

        // Cumulative frames retired by this processor since construction:
        // played, or discarded by a 'reset' that will never play them. This
        // is the authoritative figure the decoder's backpressure account is
        // derived from.
        //
        // It is reported as a running total rather than as a per-message
        // delta on purpose. A delta is lost outright if its message is
        // dropped -- which happens whenever the engine is mid-teardown, and
        // whenever a 'reset' discards queued frames that were posted but
        // never played. Every loss inflates the decoder's idea of how much
        // audio the audio thread is still holding, and because the error can
        // only accumulate, a long-looping track eventually shows a permanent
        // backlog above the high-water mark, parks the decoder forever, and
        // starves into silence. A total lets the decoder recompute the
        // account from scratch on each message, so a dropped one costs
        // nothing beyond a little latency.
        this._retiredFrames = 0;

        // Set by the 'release' message. Makes process() return false, which
        // is the only way to tell the rendering thread this processor will
        // never produce audio again and may be collected.
        this._released = false;
        this._transportPort = null;

        this._prepareThreshold = 0;
        this._prepareReported = false;

        // A lifecycle resume is different from an ordinary play: the worker
        // feeding this queue may only just be waking after the page was
        // backgrounded. Leftover PCM is not proof the pipeline is alive --
        // starting from that stale queue is what underruns after foreground.
        // Consumption stays stopped until new frames have been enqueued (or
        // EOS is reached) and the queue meets the requested threshold. The
        // processor owns `_bufferedFrames`, so it is the only component that
        // can make this decision without a stale main-thread estimate.
        //
        // `_resumeHighWater` is the decoder's own buffer bound, and waiting
        // is only safe because 'resumeWhenReady' re-posts the retired total:
        // a paused processor retires nothing, so without that message the
        // decoder can stay parked on a stale account while this side waits
        // for the PCM only the decoder can produce.
        //
        // That re-post cannot help when the decoder is not parked on the
        // account at all -- a fetch or decoder that did not survive being
        // backgrounded produces nothing no matter what it is told about
        // capacity. Leftover just below high-water then satisfies neither
        // escape, and the wait is unsatisfiable: permanent silence on a track
        // the game still reports as playing. `_resumeAllowLeftover` is how the
        // main thread ends that wait, once, from the frames already queued.
        this._resumePending = false;
        this._resumeThreshold = 0;
        this._resumeHighWater = 0;
        this._resumeAllowLeftover = false;
        this._resumeRequestId = 0;
        this._resumeBaselineFrames = 0;

        this.port.onmessage = (event) => this._onMessage(event.data);
    }

    _enqueuePcm(message) {
        if (this._queueHead >= 64 && this._queueHead * 2 >= this._queue.length) {
            this._queue.splice(0, this._queueHead);
            this._queueHead = 0;
        }
        const buffers = message.channelData;
        const channels = new Array(buffers.length);
        for (let i = 0; i < buffers.length; i++) {
            channels[i] = new Float32Array(buffers[i]);
        }
        const frames = channels.length > 0 ? channels[0].length : 0;
        if (frames === 0) {
            return;
        }
        this._queue.push(channels);
        this._bufferedFrames += frames;
        if (!this._prepareReported
            && this._prepareThreshold > 0
            && this._bufferedFrames >= this._prepareThreshold) {
            this._prepareReported = true;
            this.port.postMessage({
                type: 'prepared',
                buffered: this._bufferedFrames
            });
        }
        if (this._starved) {
            this.port.postMessage({
                type: 'recovered',
                buffered: this._bufferedFrames,
                retiredFrames: this._retiredFrames,
                starvedQuanta: this._starvedQuanta,
                starvedFrames: this._starvedFrames
            });
        }
        this._starved = false;
        this._starvedQuanta = 0;
        this._starvedFrames = 0;
        this._tryResumeWhenReady();
    }

    _tryResumeWhenReady() {
        if (!this._resumePending) {
            return;
        }

        const newFrames = this._bufferedFrames - this._resumeBaselineFrames;
        // Deliberately '>=' against the decoder's *outstanding* bound rather
        // than the queue depth alone.
        //
        // The two sides account differently and the gap between them is where
        // this used to deadlock. The decoder parks when
        // posted - retired >= highWater, a figure that includes frames still in
        // flight to this processor; this side sees only what has landed in the
        // queue. So the decoder can be parked at exactly high-water while
        // _bufferedFrames reads a few hundred frames short of it, and a strict
        // depth test then concludes the decoder is still working when it is in
        // fact waiting on the retired total that only consumption produces.
        //
        // Captured repro: buffered=95688 against highWater=96000 -- 312 frames,
        // 6.5ms of audio, below the mark. Not EOS, not at high-water, so no
        // escape applied and both sides waited on each other for the full 10s
        // watchdog before the leftover override broke the tie.
        //
        // RESUME_HIGH_WATER_SLACK_FRAMES absorbs that gap. It is a lower bound
        // on 'the decoder considers itself full', not a measurement of the
        // queue, so it only ever releases a wait that could not have been
        // satisfied by waiting longer.
        const leftoverAtHighWater = this._resumeHighWater > 0
            && this._bufferedFrames >= this._resumeHighWater - RESUME_HIGH_WATER_SLACK_FRAMES;
        const leftoverPermitted = this._resumeAllowLeftover
            && this._bufferedFrames >= this._resumeThreshold;
        // The EOS escape exists because no further frames will arrive, so the
        // pre-roll threshold can never be met and waiting for it would be
        // waiting forever. It still requires frames to actually be present:
        // released with an empty queue it would start consuming and fall
        // straight into the end-of-stream path in the same quantum, producing
        // a start-then-immediately-stop rather than a resumed track.
        const endedWithFrames = this._endOfStream && this._bufferedFrames > 0;
        // New PCM is required after foreground. Three exceptions do not wait:
        // EOS, because no further frames will arrive; leftover already at the
        // worker high-water mark, because the decoder is parked until this
        // processor starts retiring frames; and leftover the main thread has
        // explicitly permitted after the refill stalled, because playing a
        // queue that already covers the pre-roll is strictly better than
        // staying silent for a refill that is not coming.
        //
        // Every one of these paths guarantees a buffer that is full enough to
        // play from before consumption is enabled: the threshold directly, the
        // high-water mark (which is above it) directly, and EOS because the
        // queue is by definition the entire remainder of the track. Output is
        // therefore never enabled against a queue that would starve on the
        // first quantum -- which is the buzz on the way back in.
        if (!endedWithFrames && !leftoverAtHighWater && !leftoverPermitted
            && (newFrames <= 0 || this._bufferedFrames < this._resumeThreshold)) {
            return;
        }

        this._resumePending = false;
        this._resumeAllowLeftover = false;

        // A track that ended while it was suspended has nothing left to play.
        // Retire it here rather than enabling consumption for a queue that is
        // already empty: process() would report 'ended' on its next quantum
        // anyway, and this reaches the same outcome without a spurious start.
        if (this._endOfStream && this._bufferedFrames === 0) {
            this._playing = false;
            this._ended = true;
            this.port.postMessage({ type: 'ended' });
            return;
        }

        this._playing = true;
        this.port.postMessage({
            type: 'resumeReady',
            requestId: this._resumeRequestId,
            buffered: this._bufferedFrames,
            newFrames,
            fromLeftover: leftoverPermitted && newFrames <= 0,
            retiredFrames: this._retiredFrames,
            endOfStream: this._endOfStream
        });
    }

    _onMessage(message) {
        switch (message.type) {
            case 'pcm': {
                this._enqueuePcm(message);
                break;
            }

            case 'pcmBatch':
                for (let i = 0; i < message.chunks.length; i++) {
                    this._enqueuePcm(message.chunks[i]);
                }
                break;

            case 'connect':
                this._transportPort = message.port;
                this._prepareThreshold = message.prepareThreshold || 0;
                this._transportPort.onmessage = (event) => this._onMessage(event.data);
                this._transportPort.start();
                this.port.postMessage({ type: 'transportReady' });
                break;

            case 'endOfStream':
                this._endOfStream = true;
                if (!this._prepareReported) {
                    this._prepareReported = true;
                    this.port.postMessage({
                        type: 'prepared',
                        buffered: this._bufferedFrames
                    });
                }
                this._tryResumeWhenReady();
                break;

            case 'play':
                this._resumePending = false;
                this._resumeHighWater = 0;
                this._playing = true;
                break;

            case 'resumeWhenReady':
                this._playing = false;
                this._resumePending = true;
                this._resumeThreshold = Math.max(0, message.thresholdFrames || 0);
                this._resumeHighWater = Math.max(0, message.highWaterFrames || 0);
                this._resumeAllowLeftover = false;
                this._resumeRequestId = message.requestId || 0;
                this._resumeBaselineFrames = this._bufferedFrames;
                this.port.postMessage({
                    type: 'resumeWaiting',
                    requestId: this._resumeRequestId,
                    buffered: this._bufferedFrames,
                    baseline: this._resumeBaselineFrames,
                    retiredFrames: this._retiredFrames,
                    threshold: this._resumeThreshold,
                    highWater: this._resumeHighWater,
                    endOfStream: this._endOfStream
                });
                // Leftover queue depth below high-water must not satisfy
                // resume. EOS and a full leftover queue can complete without
                // a later PCM message.
                //
                // Re-post the retired total first. This processor is paused
                // and therefore retiring nothing, while the decoder's account
                // is only refreshed every consumption interval -- so at the
                // moment of suspend it can still read as full even though the
                // queue has drained below high-water. Waiting for new PCM
                // without this message would park the decoder against a stale
                // account and leave both sides waiting on each other.
                this._postRetired();
                if (this._endOfStream
                    || (this._resumeHighWater > 0
                        && this._bufferedFrames >= this._resumeHighWater - RESUME_HIGH_WATER_SLACK_FRAMES)) {
                    this._tryResumeWhenReady();
                }
                break;

            // Sent when the main thread's refill watchdog has waited long
            // enough to conclude no new PCM is coming. Request-id matched so a
            // late override cannot release a newer, still-legitimate wait.
            case 'resumeAllowLeftover':
                if (this._resumePending
                    && (message.requestId || 0) === this._resumeRequestId) {
                    this._resumeAllowLeftover = true;
                    this._tryResumeWhenReady();
                }
                break;

            case 'pause':
                // Playback position is the queue itself, so pausing needs
                // to do nothing beyond stopping consumption: the unplayed
                // frames stay exactly where they are.
                //
                // The retired total is re-posted because consumption is about
                // to stop for an unbounded period: this is the last chance to
                // give the decoder an accurate account before it has to make
                // backpressure decisions without further updates.
                this._resumePending = false;
                this._resumeHighWater = 0;
                this._resumeAllowLeftover = false;
                this._postRetired();
                this._playing = false;
                break;

            case 'reset':
                // Stop / re-prepare. Drop everything so the next play
                // starts from freshly decoded frames rather than stale
                // ones left over from the previous range.
                //
                // The discarded frames are counted as retired: the decoder is
                // still holding them against its buffer budget and nothing
                // else will ever account for them.
                this._retiredFrames += this._bufferedFrames;
                this._queue.length = 0;
                this._queueHead = 0;
                this._readOffset = 0;
                this._bufferedFrames = 0;
                this._playing = false;
                this._resumePending = false;
                this._resumeHighWater = 0;
                this._resumeAllowLeftover = false;
                this._endOfStream = false;
                this._ended = false;
                this._starved = false;
                this._starvedQuanta = 0;
                this._starvedFrames = 0;
                this._consumedSinceReport = 0;
                this._postRetired();
                break;

            case 'release':
                // Permanent teardown, sent when the owning engine is
                // stopped. Distinct from 'reset': that one recycles this
                // processor for another range, whereas this one retires it.
                //
                // process() returning true keeps a processor alive even
                // after its node is disconnected and dropped, because the
                // rendering thread holds its own reference. Without this
                // message every stopped track would leave a processor
                // running on the audio thread for the lifetime of the page,
                // each still holding its queue, so repeatedly starting and
                // stopping music grows memory until the tab is killed.
                this._queue.length = 0;
                this._queueHead = 0;
                this._readOffset = 0;
                this._bufferedFrames = 0;
                this._playing = false;
                this._resumePending = false;
                this._resumeHighWater = 0;
                this._resumeAllowLeftover = false;
                this._released = true;
                if (this._transportPort) {
                    this._transportPort.onmessage = null;
                    this._transportPort.close();
                    this._transportPort = null;
                }
                break;

            default:
                break;
        }
    }

    // Reports the running retired-frame total, plus the current queue depth
    // for diagnostics. Sent on every consumption interval and whenever a
    // reset retires queued frames, so the decoder's account tracks the audio
    // thread even if individual messages are lost.
    _postRetired() {
        const destination = this._transportPort || this.port;
        destination.postMessage({
            type: 'consumed',
            retiredFrames: this._retiredFrames,
            buffered: this._bufferedFrames
        });

        // The main thread needs this same total as a play cursor, so a graph
        // rebuild can resume the track where it left off instead of
        // restarting it.
        //
        // Sent separately, and only when a transport port is in use, because
        // that port goes straight to the worker: in that configuration the
        // message above bypasses the main thread entirely, which is the whole
        // point of the direct transport. Without this the cursor is only ever
        // observable on the relay fallback. In relay mode the message above
        // already arrives on this.port, so re-sending would just double the
        // traffic.
        if (this._transportPort) {
            this.port.postMessage({
                type: 'position',
                retiredFrames: this._retiredFrames
            });
        }
    }

    // Signature is fixed by the spec: process(inputs, outputs, parameters).
    // The first argument is inputs; this node is constructed with
    // numberOfInputs: 0, so a single-parameter signature binds an always
    // empty array and renders permanent silence.
    process(inputs, outputs) {
        // Returning false retires this processor permanently. Only ever
        // reached after an explicit 'release', so a merely idle or paused
        // track is never torn down.
        if (this._released) {
            return false;
        }

        const output = outputs[0];
        if (!output || output.length === 0) {
            return true;
        }

        const quantumFrames = output[0].length;
        const channelCount = output.length;

        if (!this._playing || this._ended) {
            // Written explicitly rather than relying on the buffer arriving
            // zero-filled.
            //
            // The spec does guarantee a zeroed output buffer on entry, and
            // this code previously depended on that and simply declined to
            // write. The guarantee is per-call though, and the failure mode if
            // an implementation ever reuses a buffer without re-zeroing is
            // precisely the symptom being chased here: the last non-silent
            // quantum repeating out of a stale hardware buffer. A memset of
            // 128 frames is negligible next to the copy loop below, and it
            // removes the assumption entirely.
            for (let channel = 0; channel < channelCount; channel++) {
                output[channel].fill(0);
            }

            return true;
        }

        let written = 0;

        while (written < quantumFrames && this._queueHead < this._queue.length) {
            const chunk = this._queue[this._queueHead];
            const chunkFrames = chunk[0].length;
            const availableInChunk = chunkFrames - this._readOffset;
            const framesToCopy = Math.min(availableInChunk, quantumFrames - written);

            for (let channel = 0; channel < channelCount; channel++) {
                // Mono content feeds every output channel from the single
                // decoded channel, which keeps the up-mix here (a pointer
                // choice) instead of duplicating the samples in memory.
                const source = chunk[channel < chunk.length ? channel : 0];
                const destination = output[channel];
                const readBase = this._readOffset;
                for (let frame = 0; frame < framesToCopy; frame++) {
                    destination[written + frame] = source[readBase + frame];
                }
            }

            this._readOffset += framesToCopy;
            written += framesToCopy;
            this._bufferedFrames -= framesToCopy;
            this._consumedSinceReport += framesToCopy;
            this._retiredFrames += framesToCopy;

            if (this._readOffset >= chunkFrames) {
                this._queueHead++;
                this._readOffset = 0;
            }
        }

        if (written < quantumFrames) {
            // The queue ran dry mid-quantum. Zero the tail explicitly, for the
            // same reason the idle branch does: this is the partial-quantum
            // case, so the frames left unwritten sit directly after real audio
            // and are the most likely of all to be heard if the buffer is not
            // clean.
            for (let channel = 0; channel < channelCount; channel++) {
                output[channel].fill(0, written);
            }

            // Either the track is over or the decoder fell behind; the
            // remainder of the output is silent in both cases.
            if (this._endOfStream) {
                this._ended = true;
                this._playing = false;
                this.port.postMessage({ type: 'ended' });
            } else {
                this._starvedQuanta++;
                this._starvedFrames += quantumFrames - written;
                if (!this._starved) {
                    this._starved = true;
                    this.port.postMessage({
                        type: 'starved',
                        buffered: this._bufferedFrames,
                        retiredFrames: this._retiredFrames
                    });
                }
            }
        }

        if (this._consumedSinceReport >= CONSUMED_REPORT_INTERVAL_FRAMES) {
            this._consumedSinceReport = 0;
            this._postRetired();
        }

        return true;
    }
}

registerProcessor('music-processor', MusicProcessor);
