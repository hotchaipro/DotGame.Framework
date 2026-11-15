// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

// music-worker.js — fetches, demuxes, and decodes a music track off the
// main thread.
//
// Everything expensive about playing music — the network read, the Ogg
// page parsing, and the Opus decode — happens here, in a dedicated worker,
// so none of it can stall the game's requestAnimationFrame callback. The
// old HTMLMediaElement pipeline did all of this work on the main thread
// (implicitly, inside the element) which is what produced the frame-rate
// hitches this module exists to remove.
//
// The output is a stream of decoded PCM chunks posted to the main thread
// with their backing ArrayBuffers transferred (zero-copy), which relays
// them to the AudioWorklet. The worker never holds more than a bounded
// window of decoded audio: the main thread acknowledges consumption and
// this module stops decoding whenever the unplayed window is full. That
// bound is what keeps a full-length track from ever being resident as
// PCM (a 3-minute stereo track would be ~70 MB decoded).
//
// Range and loop policy lives here rather than in the worklet: the frames
// posted to the audio thread are already exactly the frames that should be
// heard, in order, loops included. See _run() below.
//
// Format assumptions: Ogg-contained Opus, which the WebGL content pipeline
// guarantees (see ContentPipeline.webgl.targets). Opus always decodes at
// 48 kHz, which is also the AudioContext rate on the browsers this engine
// targets, so no resampling is required in the common case; when the
// context rate differs the samples are resampled once here, on this
// thread, never on the audio thread.

// Opus requires a short run-up of audio before a seek target to decode
// accurately (the codec's overlap window and internal filter state).
// 80 ms is the value the Ogg Opus specification recommends as the seek
// pre-roll; frames decoded within it are discarded rather than played.
const SEEK_PREROLL_FRAMES = 3840; // 80 ms at 48 kHz

// Opus is defined to decode at 48 kHz, always, regardless of the rate of
// the material that was encoded.
const OPUS_SAMPLE_RATE = 48000;

// Nominal Opus packet duration in microseconds. Used only to synthesise
// monotonically increasing EncodedAudioChunk timestamps; the decoded frame
// positions that actually drive playback come from the Ogg granule
// positions, so this value does not have to match the encoder exactly.
const OPUS_PACKET_DURATION_US = 20000;

// WebCodecs accepts decode() calls faster than it produces AudioData. Keep
// that internal queue short so compressed packets and decoded frames cannot
// grow behind the explicit PCM high-water mark.
const MAX_DECODE_QUEUE_SIZE = 8;

// Four normal Opus packets represent roughly 80 ms. Batching them cuts
// message dispatches by 75% without adding a sample copy or enough latency to
// consume the prepare pre-roll margin.
const PCM_BATCH_CHUNKS = 4;
const MAX_OGG_INDEX_ENTRIES = 2048;

const OGG_CAPTURE_PATTERN = 0x4f676753; // 'OggS'

// ─────────────────────────── Ogg demuxing ───────────────────────────

// Incremental Ogg page reader. Bytes arrive from the fetch reader in
// arbitrarily sized chunks that have no relationship to page boundaries,
// so this buffers the tail of a partial page until the rest arrives.
const MUSIC_FETCH_RETRY_DELAYS_MS = [500, 1000, 2000, 4000, 8000];

class OggDemuxer {
    constructor(byteOffset = 0) {
        this._buffer = new Uint8Array(0);
        this._continuedPacket = null;
        this._byteOffset = byteOffset;
    }

    push(chunk) {
        if (this._buffer.length === 0) {
            this._buffer = chunk;
            return;
        }
        const merged = new Uint8Array(this._buffer.length + chunk.length);
        merged.set(this._buffer, 0);
        merged.set(chunk, this._buffer.length);
        this._buffer = merged;
    }

    // Returns the next complete page, or null when more bytes are needed.
    // A page is { granulePosition, packets, endOfStream }.
    readPage() {
        const buffer = this._buffer;

        // 27 bytes is the fixed part of the page header, before the
        // variable-length segment table.
        if (buffer.length < 27) {
            return null;
        }

        const view = new DataView(buffer.buffer, buffer.byteOffset, buffer.byteLength);
        if (view.getUint32(0, false) !== OGG_CAPTURE_PATTERN) {
            throw new Error('Not an Ogg stream (missing OggS capture pattern)');
        }

        const headerType = buffer[5];
        const byteOffset = this._byteOffset;

        // The granule position is a 64-bit value; JavaScript numbers carry
        // it exactly up to 2^53 frames, which at 48 kHz is roughly six
        // thousand years of audio, so the split read below is safe.
        const granuleLow = view.getUint32(6, true);
        const granuleHigh = view.getUint32(10, true);
        const granulePosition = granuleHigh * 4294967296 + granuleLow;

        const segmentCount = buffer[26];
        const headerLength = 27 + segmentCount;
        if (buffer.length < headerLength) {
            return null;
        }

        let bodyLength = 0;
        for (let i = 0; i < segmentCount; i++) {
            bodyLength += buffer[27 + i];
        }

        const pageLength = headerLength + bodyLength;
        if (buffer.length < pageLength) {
            return null;
        }

        // Split the page body into packets. A packet is the concatenation
        // of segments up to and including the first segment shorter than
        // 255 bytes; a packet whose final segment is exactly 255 bytes
        // continues onto the next page.
        const packets = [];
        let offset = headerLength;
        let packetStart = offset;
        let packetComplete = false;

        for (let i = 0; i < segmentCount; i++) {
            const segmentLength = buffer[27 + i];
            offset += segmentLength;
            if (segmentLength < 255) {
                packets.push(this._takePacket(buffer.subarray(packetStart, offset), i === 0));
                packetStart = offset;
                packetComplete = true;
            } else {
                packetComplete = false;
            }
        }

        if (!packetComplete && packetStart < offset) {
            // Trailing bytes belong to a packet that finishes on a later
            // page; stash them so the continuation can be joined.
            this._stashContinuation(buffer.subarray(packetStart, offset));
        }

        this._buffer = buffer.subarray(pageLength);
        this._byteOffset += pageLength;

        return {
            byteOffset,
            granulePosition,
            packets: packets.filter((packet) => packet !== null),
            endOfStream: (headerType & 0x04) !== 0,
            continued: (headerType & 0x01) !== 0
        };
    }

    _takePacket(bytes, isFirstSegmentOfPage) {
        const pending = this._continuedPacket;
        if (pending && isFirstSegmentOfPage) {
            const joined = new Uint8Array(pending.length + bytes.length);
            joined.set(pending, 0);
            joined.set(bytes, pending.length);
            this._continuedPacket = null;
            return joined;
        }
        // A stashed continuation that is not claimed by the first packet
        // of this page means the stream is malformed or was seeked; drop
        // it rather than corrupting the packet that follows.
        this._continuedPacket = null;
        return bytes.length > 0 ? bytes.slice() : null;
    }

    _stashContinuation(bytes) {
        const pending = this._continuedPacket;
        if (!pending) {
            this._continuedPacket = bytes.slice();
            return;
        }
        const joined = new Uint8Array(pending.length + bytes.length);
        joined.set(pending, 0);
        joined.set(bytes, pending.length);
        this._continuedPacket = joined;
    }
}

// Parses the OpusHead identification header (the first packet of an Ogg
// Opus stream). Its pre-skip field is essential: an Opus decoder always
// emits a short run of priming samples before the real audio, and playing
// them would shift the whole track — and any loop point — out of place.
function parseOpusHead(packet) {
    if (packet.length < 19) {
        throw new Error('Malformed OpusHead packet');
    }
    const view = new DataView(packet.buffer, packet.byteOffset, packet.byteLength);
    return {
        channelCount: packet[9],
        preSkip: view.getUint16(10, true)
    };
}

function isOpusHead(packet) {
    return packet.length >= 8
        && packet[0] === 0x4f && packet[1] === 0x70 && packet[2] === 0x75 && packet[3] === 0x73
        && packet[4] === 0x48 && packet[5] === 0x65 && packet[6] === 0x61 && packet[7] === 0x64;
}

function isOpusTags(packet) {
    return packet.length >= 8
        && packet[0] === 0x4f && packet[1] === 0x70 && packet[2] === 0x75 && packet[3] === 0x73
        && packet[4] === 0x54 && packet[5] === 0x61 && packet[6] === 0x67 && packet[7] === 0x73;
}

// ─────────────────────────── Resampling ───────────────────────────

// Linear-interpolation resampler used only when the AudioContext is not
// running at 48 kHz. It is stateful across chunks — it remembers the last
// sample of each channel and the fractional read cursor — because
// resampling each chunk independently would reset the interpolation at
// every boundary and produce a periodic click.
//
// Linear interpolation is chosen deliberately over a windowed-sinc filter:
// this path only runs on hardware whose output rate differs from Opus's
// native 48 kHz, the ratio is close to 1 (48/44.1), and the artefacts sit
// far above the music's energy. A higher-order filter would cost more CPU
// than the mismatch itself.
class Resampler {
    constructor(inputRate, outputRate, channelCount) {
        this.ratio = inputRate / outputRate;
        this._channelCount = channelCount;
        this._previous = new Float32Array(channelCount);
        this._hasPrevious = false;
        this._cursor = 0;
    }

    process(input) {
        const channelCount = this._channelCount;
        const inputFrames = input[0].length;
        if (inputFrames === 0) {
            return input;
        }

        // Position 0 refers to the last sample of the previous chunk, so
        // the readable span is one frame longer than the chunk itself.
        const available = inputFrames + (this._hasPrevious ? 1 : 0);
        if (!this._hasPrevious) {
            this._cursor = 0;
        }

        const outputFrames = Math.max(0, Math.floor((available - 1 - this._cursor) / this.ratio) + 1);
        const output = new Array(channelCount);
        for (let channel = 0; channel < channelCount; channel++) {
            output[channel] = new Float32Array(outputFrames);
        }

        const sampleAt = (channel, index) => {
            if (this._hasPrevious) {
                return index === 0 ? this._previous[channel] : input[channel][index - 1];
            }
            return input[channel][index];
        };

        let cursor = this._cursor;
        for (let frame = 0; frame < outputFrames; frame++) {
            const base = Math.floor(cursor);
            const fraction = cursor - base;
            for (let channel = 0; channel < channelCount; channel++) {
                const a = sampleAt(channel, base);
                const b = sampleAt(channel, Math.min(base + 1, available - 1));
                output[channel][frame] = a + (b - a) * fraction;
            }
            cursor += this.ratio;
        }

        this._cursor = cursor - (available - 1);
        for (let channel = 0; channel < channelCount; channel++) {
            this._previous[channel] = input[channel][inputFrames - 1];
        }
        this._hasPrevious = true;

        return output;
    }
}

// ─────────────────────────── Track streaming ───────────────────────────

class TrackStreamer {
    constructor(request) {
        this._url = request.url;
        this._outputSampleRate = request.outputSampleRate;
        this._highWaterFrames = request.highWaterFrames;
        this._preRollFrames = request.preRollFrames;
        this._transportPort = request.transportPort || null;

        // Play range, in source (48 kHz) frames. `endFrame` of null means
        // "to the end of the track".
        this._startFrame = request.startFrame;
        this._endFrame = request.endFrame;
        this._loopFrame = request.loopFrame;
        this._loop = request.loop;

        this._channelCount = 0;
        this._preSkip = 0;
        this._resampler = null;

        // Backpressure account, kept as two cumulative totals rather than a
        // single running balance: frames posted to the main thread, and
        // frames the audio thread reports it has retired. Outstanding work
        // is simply the difference.
        //
        // A running balance decremented by per-message deltas drifts upward
        // whenever a report is lost (during teardown, or when a 'reset'
        // discards queued frames), and the error is never recoverable. Over a
        // long looping track the phantom backlog crosses the high-water mark,
        // parks the decoder in _awaitCapacity permanently, and starves the
        // track into silence. Recomputing from totals is self-correcting.
        this._postedFrames = 0;
        this._retiredFrames = 0;
        this._capacityWaiter = null;
        this._decoderCapacityWaiter = null;

        this._closed = false;
        this._abortController = null;
        this._announcedReady = false;

        this._pass = 0;
        this._capacityWaitCount = 0;
        this._maxDecoderQueueSize = 0;
        this._supportsRangedCopy = true;
        this._pendingPcmChunks = [];
        this._pendingPcmTransfers = [];
        this._pendingPcmFrames = 0;
        this._oggIndex = [];
        this._oggIndexStride = 1;
        this._oggPageNumber = 0;

        // The decoder for the range currently being streamed, held on the
        // instance purely so close() can retire it immediately rather than
        // waiting for the fetch abort to unwind _playRange. See close().
        this._decoder = null;

        // Settled when run() has finished unwinding, which is what lets the
        // owner wait for a clean teardown before terminating this worker.
        this.finished = null;
    }

    _findRangeEntry(from) {
        if (from <= 0 || this._oggIndex.length === 0) {
            return null;
        }
        const target = Math.max(0, from - SEEK_PREROLL_FRAMES);
        let selected = null;
        for (let i = 0; i < this._oggIndex.length; i++) {
            const entry = this._oggIndex[i];
            if (entry.frame > target) {
                break;
            }
            selected = entry;
        }
        return selected;
    }

    _recordOggPage(page, previousGranule) {
        const pageNumber = this._oggPageNumber++;
        if (page.continued || pageNumber % this._oggIndexStride !== 0) {
            return;
        }
        this._oggIndex.push({
            byteOffset: page.byteOffset,
            previousGranule,
            frame: Math.max(0, previousGranule - this._preSkip)
        });
        if (this._oggIndex.length > MAX_OGG_INDEX_ENTRIES) {
            this._oggIndex = this._oggIndex.filter((_, index) => (index & 1) === 0);
            this._oggIndexStride *= 2;
        }
    }

    close() {
        this._closed = true;
        if (this._abortController) {
            this._abortController.abort();
            this._abortController = null;
        }

        // Retire the decoder here, synchronously, instead of leaving it to
        // the finally block in _playRange. That block does not run until the
        // pending reader.read() rejects from the abort above, so the decoder
        // would otherwise still be alive -- and still holding queued packets
        // and in-flight AudioData -- when the owner terminates this worker.
        // Killing a worker thread out from under a live WebCodecs decoder
        // crashes the content process on WebKit (the page reloads), which is
        // reached on every stop of a looping track, since a loop never
        // reaches the end-of-stream path that closes the decoder normally.
        const decoder = this._decoder;
        this._decoder = null;
        if (decoder && decoder.state !== 'closed') {
            try {
                decoder.close();
            } catch (error) {
                // A decoder that has already failed closes itself; there is
                // nothing left to release and nothing to report.
            }
        }

        this._releaseCapacityWaiter();
        this._releaseDecoderCapacityWaiter();
        this._resampler = null;
        this._pendingPcmChunks.length = 0;
        this._pendingPcmTransfers.length = 0;
        this._pendingPcmFrames = 0;
        if (this._transportPort) {
            this._transportPort.onmessage = null;
            this._transportPort.close();
            this._transportPort = null;
        }
    }

    _releaseDecoderCapacityWaiter() {
        const waiter = this._decoderCapacityWaiter;
        if (!waiter) {
            return;
        }
        this._decoderCapacityWaiter = null;
        waiter();
    }

    _awaitDecoderCapacity(decoder) {
        if (this._closed
            || decoder.state === 'closed'
            || decoder.decodeQueueSize < MAX_DECODE_QUEUE_SIZE) {
            return null;
        }
        return new Promise((resolve) => {
            // Resolve from the decoder output callback rather than relying on
            // WebKit's inconsistent AudioDecoder 'dequeue' event delivery.
            this._decoderCapacityWaiter = resolve;
        });
    }

    reportConsumed(retiredFrames) {
        // Monotonic: a stale or out-of-order message must never walk the
        // total backwards and manufacture outstanding work.
        if (retiredFrames > this._retiredFrames) {
            this._retiredFrames = retiredFrames;
        }
        if (this._closed || this._outstandingFrames() < this._highWaterFrames) {
            this._releaseCapacityWaiter();
        }
    }

    _outstandingFrames() {
        return Math.max(0, this._postedFrames - this._retiredFrames);
    }

    _releaseCapacityWaiter() {
        const waiter = this._capacityWaiter;
        if (waiter) {
            this._capacityWaiter = null;
            waiter();
        }
    }

    // Suspends decoding while the audio thread still holds a full window
    // of unplayed frames. Without this the decoder would race ahead and
    // buffer the entire track.
    _awaitCapacity() {
        if (this._closed || this._outstandingFrames() < this._highWaterFrames) {
            return Promise.resolve();
        }
        this._capacityWaitCount++;
        return new Promise((resolve) => {
            this._capacityWaiter = resolve;
        });
    }

    async run() {
        try {
            // The first pass plays [startFrame, endFrame). Every later
            // pass plays [loopFrame, endFrame) — the loop region — and is
            // reached only when looping is enabled, so a non-looping track
            // runs this loop exactly once.
            let from = this._startFrame;
            while (!this._closed) {
                const pass = ++this._pass;
                const framesBeforePass = this._postedFrames;
                await this._playRange(from);
                this._flushPcmBatch();
                if (this._closed || !this._loop) {
                    break;
                }

                // A pass that emitted nothing has established that the range
                // it was given is empty, and looping back to the same range
                // can only establish it again. Without this the loop spins as
                // fast as the HTTP cache can serve the file -- the captured
                // repro turned over 128,000 passes, each one fetching, demuxing
                // and discarding the whole track while posting zero frames.
                //
                // The owner sees only that no audio ever arrived, so its
                // prepare watchdog eventually fires with 'no worker activity',
                // which is precisely backwards: the worker is saturating a core
                // and holding the fetch pipeline open. Reported as a fault
                // instead, so the cause is named at the point it is known.
                //
                // Reached when the play range starts at or past the end of the
                // track: no page satisfies the seek predicate in _playRange, so
                // decoding never begins and the decoder is never constructed.
                if (this._postedFrames === framesBeforePass) {
                    throw new Error(`music loop produced no audio for '${this._url}' (pass ${pass} from frame ${from} emitted nothing; the play range is empty)`);
                }

                from = this._loopFrame;
            }

            if (!this._closed) {
                this._postPlaybackMessage({ type: 'endOfStream' });
            }
        } catch (error) {
            if (!this._closed) {
                self.postMessage({
                    type: 'error',
                    message: (error && error.message) ? error.message : String(error)
                });
            }
        }
    }

    // Fetches the track, waiting out the network going away as the rest of the game's content does:
    // a looping track fetches again at each loop, so it's exposed for as long as it plays, and a
    // phone drops the network while the page is hidden. Only a failure to fetch is retried, and a
    // track that still fails ends, reported, rather than taking the game with it.
    async _fetchWithRetry(init) {
        for (let attempt = 0; ; attempt += 1) {
            try {
                return await fetch(this._url, init);
            } catch (error) {
                if (this._closed || (error && (error.name === 'AbortError')) || (attempt >= MUSIC_FETCH_RETRY_DELAYS_MS.length)) {
                    throw error;
                }

                await new Promise((resolve) => setTimeout(resolve, MUSIC_FETCH_RETRY_DELAYS_MS[attempt]));
                if (!self.navigator.onLine) {
                    await new Promise((resolve) => self.addEventListener('online', resolve, { once: true }));
                }
            }
        }
    }

    // Streams the file from the beginning, discarding everything before
    // `from`, and emits frames until `endFrame` (or the end of the track).
    //
    // Restarting from byte zero on each loop looks wasteful but is not:
    // the response is served from the HTTP cache after the first pass, the
    // pages before the loop point are discarded without ever being decoded
    // (granule positions make that possible without a codec), and the
    // alternative — retaining the loop region as PCM — is precisely the
    // memory cost this design exists to avoid.
    async _playRange(from) {
        let rangeEntry = this._findRangeEntry(from);
        let demuxer = new OggDemuxer(rangeEntry ? rangeEntry.byteOffset : 0);
        let decoder = null;
        let headerSeen = rangeEntry !== null;
        let tagsSeen = rangeEntry !== null;

        // Absolute position, in source frames, of the next frame the
        // decoder will output. Before the pre-skip is consumed this is
        // negative, which is exactly what makes the priming samples fall
        // outside the emit range and be discarded.
        let decodeCursor = 0;
        let decoding = false;
        let previousGranule = rangeEntry ? rangeEntry.previousGranule : 0;

        // Counts packets handed to the decoder in this range, purely to
        // generate strictly increasing chunk timestamps.
        let submittedPackets = 0;

        let pendingError = null;

        this._abortController = new AbortController();
        let response = await this._fetchWithRetry({
            signal: this._abortController.signal,
            headers: rangeEntry ? { Range: `bytes=${rangeEntry.byteOffset}-` } : undefined
        });
        const contentRange = rangeEntry ? response.headers.get('Content-Range') : null;
        const validRange = rangeEntry
            && response.status === 206
            && contentRange
            && contentRange.startsWith(`bytes ${rangeEntry.byteOffset}-`);
        if (rangeEntry && !validRange) {
            // A 200 response means the server ignored Range and supplied the
            // complete representation, which remains a valid sequential
            // fallback. Other statuses get one clean full-fetch retry.
            if (response.status !== 200) {
                try {
                    await response.body.cancel();
                } catch (error) {
                    // The replacement fetch below owns the useful response.
                }
                response = await this._fetchWithRetry({ signal: this._abortController.signal });
            }
            rangeEntry = null;
            demuxer = new OggDemuxer();
            headerSeen = false;
            tagsSeen = false;
            previousGranule = 0;
        }
        if (!response.ok) {
            throw new Error(`Failed to fetch music '${this._url}' (${response.status} ${response.statusText})`);
        }

        const reader = response.body.getReader();

        // getReader() creates `reader.closed` eagerly, and close() errors the
        // body stream by aborting the fetch, which rejects that promise with
        // an AbortError. Nothing awaits it -- the read loop below awaits
        // read(), whose rejection is handled -- so without a handler here the
        // abort surfaces as an unhandled rejection ('BodyStreamBuffer was
        // aborted'), which the listener at the bottom of this file forwards
        // to the owner as a fault. That fires on every stop of a looping
        // track, because a loop is always mid-fetch when it is stopped.
        reader.closed.catch(() => { });


        const emitDecoded = (audioData) => {
            try {
                const frameCount = audioData.numberOfFrames;
                const channelCount = audioData.numberOfChannels;

                const chunkStart = decodeCursor;
                decodeCursor += frameCount;

                const emitFrom = Math.max(chunkStart, from);
                const emitTo = this._endFrame != null
                    ? Math.min(chunkStart + frameCount, this._endFrame)
                    : chunkStart + frameCount;

                if (emitTo <= emitFrom) {
                    return;
                }

                const frameOffset = emitFrom - chunkStart;
                const emitFrames = emitTo - emitFrom;
                const copiesWholeChunk = frameOffset === 0 && emitFrames === frameCount;
                const planes = new Array(channelCount);
                for (let channel = 0; channel < channelCount; channel++) {
                    const plane = new Float32Array(emitFrames);
                    if (copiesWholeChunk || !this._supportsRangedCopy) {
                        if (copiesWholeChunk) {
                            audioData.copyTo(plane, { planeIndex: channel, format: 'f32-planar' });
                        } else {
                            const fullPlane = new Float32Array(frameCount);
                            audioData.copyTo(fullPlane, { planeIndex: channel, format: 'f32-planar' });
                            plane.set(fullPlane.subarray(frameOffset, frameOffset + emitFrames));
                        }
                    } else {
                        try {
                            audioData.copyTo(plane, {
                                planeIndex: channel,
                                frameOffset,
                                frameCount: emitFrames,
                                format: 'f32-planar'
                            });
                        } catch (error) {
                            if (!(error instanceof TypeError)) {
                                throw error;
                            }
                            this._supportsRangedCopy = false;
                            const fullPlane = new Float32Array(frameCount);
                            audioData.copyTo(fullPlane, { planeIndex: channel, format: 'f32-planar' });
                            plane.set(fullPlane.subarray(frameOffset, frameOffset + emitFrames));
                        }
                    }
                    planes[channel] = plane;
                }

                this._emit(planes);
            } finally {
                audioData.close();
            }
        };

        const ensureDecoder = () => {
            if (decoder) {
                return;
            }
            decoder = new AudioDecoder({
                output: (audioData) => {
                    try {
                        emitDecoded(audioData);
                    } catch (error) {
                        pendingError = error;
                    } finally {
                        this._releaseDecoderCapacityWaiter();
                    }
                },
                error: (error) => {
                    pendingError = error;
                    this._releaseDecoderCapacityWaiter();
                }
            });
            this._decoder = decoder;
            decoder.configure({
                codec: 'opus',
                sampleRate: OPUS_SAMPLE_RATE,
                numberOfChannels: this._channelCount
            });
        };

        try {
            while (!this._closed) {
                const { value, done } = await reader.read();
                if (done) {
                    break;
                }

                demuxer.push(value);

                let page;
                let finished = false;
                while (!finished && !this._closed && (page = demuxer.readPage()) !== null) {
                    if (this._pass === 1) {
                        this._recordOggPage(page, previousGranule);
                    }
                    for (const packet of page.packets) {
                        if (!headerSeen && isOpusHead(packet)) {
                            const head = parseOpusHead(packet);
                            this._channelCount = head.channelCount;
                            this._preSkip = head.preSkip;
                            headerSeen = true;
                            continue;
                        }
                        if (headerSeen && !tagsSeen && isOpusTags(packet)) {
                            tagsSeen = true;
                            continue;
                        }
                        if (!headerSeen) {
                            continue;
                        }

                        if (!decoding) {
                            // Page granule positions let whole pages be
                            // skipped without running the codec at all,
                            // so seeking to a start offset or a loop point
                            // costs page parsing, not decoding.
                            const pageEndFrame = page.granulePosition - this._preSkip;
                            if (pageEndFrame < from - SEEK_PREROLL_FRAMES) {
                                previousGranule = page.granulePosition;
                                break;
                            }
                            decoding = true;
                            decodeCursor = previousGranule === 0
                                ? -this._preSkip
                                : previousGranule - this._preSkip;
                            this._prepareOutput();
                            ensureDecoder();
                        }

                        // Timestamps must advance across submitted chunks;
                        // a decoder is entitled to reject a stream whose
                        // timestamps stand still. Opus packets in a normal
                        // Ogg stream are 20 ms, and the exact value only
                        // has to be monotonic for the decoder's benefit —
                        // output framing comes from decodeCursor, not from
                        // this — so a 20 ms step per packet is sufficient.
                        decoder.decode(new EncodedAudioChunk({
                            type: 'key',
                            timestamp: submittedPackets * OPUS_PACKET_DURATION_US,
                            data: packet
                        }));
                        this._maxDecoderQueueSize = Math.max(this._maxDecoderQueueSize, decoder.decodeQueueSize);
                        submittedPackets++;

                        const decoderCapacity = this._awaitDecoderCapacity(decoder);
                        if (decoderCapacity) {
                            await decoderCapacity;
                        }
                        if (this._closed) {
                            break;
                        }

                        if (this._outstandingFrames() >= this._highWaterFrames) {
                            await this._awaitCapacity();

                            // close() releases this wait after closing the
                            // decoder: the next packet's decode() would throw
                            // on it, on every stop while the audio thread is
                            // full, so the rest of the page is left
                            if (this._closed) {
                                break;
                            }
                        }
                    }

                    previousGranule = page.granulePosition;

                    if (pendingError) {
                        throw pendingError;
                    }

                    if (this._endFrame != null && decoding && decodeCursor >= this._endFrame) {
                        finished = true;
                    }
                }

                if (finished) {
                    break;
                }

                await this._awaitCapacity();
            }

            if (decoder && !this._closed) {
                await decoder.flush();
            }
            if (pendingError) {
                throw pendingError;
            }
        } finally {
            try {
                await reader.cancel();
            } catch (error) {
                // Not swallowed: a cancel that fails outside a deliberate
                // close means the response body was left in an unexpected
                // state, which is reported like any other streaming fault.
                // During a close the reader is already released, so a
                // rejection there is the expected outcome and is ignored.
                if (!this._closed) {
                    self.postMessage({
                        type: 'error',
                        message: `failed to cancel response reader: ${(error && error.message) ? error.message : String(error)}`
                    });
                }
            }
            this._releaseDecoderCapacityWaiter();
            if (decoder && decoder.state !== 'closed') {
                decoder.close();
            }
            if (this._decoder === decoder) {
                this._decoder = null;
            }
            this._abortController = null;
        }
    }

    _prepareOutput() {
        if (this._announcedReady) {
            return;
        }
        this._announcedReady = true;

        if (this._outputSampleRate !== OPUS_SAMPLE_RATE) {
            this._resampler = new Resampler(OPUS_SAMPLE_RATE, this._outputSampleRate, this._channelCount);
        }

        self.postMessage({
            type: 'format',
            channelCount: this._channelCount,
            sampleRate: this._outputSampleRate,
            resampling: this._resampler !== null
        });
    }

    _postPlaybackMessage(message, transfers) {
        const destination = this._transportPort || self;
        destination.postMessage(message, transfers || []);
    }

    _flushPcmBatch() {
        const chunks = this._pendingPcmChunks;
        if (chunks.length === 0) {
            return;
        }
        const transfers = this._pendingPcmTransfers;
        const frames = this._pendingPcmFrames;
        this._pendingPcmChunks = [];
        this._pendingPcmTransfers = [];
        this._pendingPcmFrames = 0;
        this._postPlaybackMessage({ type: 'pcmBatch', chunks, transfers, frames }, transfers);
    }

    _emit(planes) {
        const output = this._resampler ? this._resampler.process(planes) : planes;
        const frames = output[0].length;
        if (frames === 0) {
            return;
        }

        // Each plane is exact-sized by emitDecoded (or by the resampler), so
        // its buffer can be transferred directly without another sample copy.
        const buffers = new Array(output.length);
        for (let channel = 0; channel < output.length; channel++) {
            buffers[channel] = output[channel].buffer;
        }

        this._postedFrames += frames;
        this._pendingPcmFrames += frames;
        this._pendingPcmChunks.push({ channelData: buffers, frames });
        for (let channel = 0; channel < buffers.length; channel++) {
            this._pendingPcmTransfers.push(buffers[channel]);
        }
        if (this._pendingPcmChunks.length >= PCM_BATCH_CHUNKS) {
            this._flushPcmBatch();
        }
    }
}

// ─────────────────────────── Worker entry ───────────────────────────

let streamer = null;

// A promise rejection that escapes inside this worker is otherwise completely
// invisible: it is delivered only to the worker's own unhandledrejection, and
// worker.onerror on the main thread is never called for it (that fires for
// uncaught exceptions and load failures, not rejections). Route it through the
// same 'error' message the streamer already uses, so the owner reports it and
// settles any pending prepare instead of the fault presenting as silence.
self.addEventListener('unhandledrejection', (event) => {
    event.preventDefault();
    const reason = event.reason;
    if (reason && reason.name === 'AbortError') {
        // The only AbortController in this worker is the one close() aborts,
        // so an AbortError is always the expected consequence of a deliberate
        // teardown, never a streaming fault.
        return;
    }
    self.postMessage({
        type: 'error',
        message: `unhandled rejection in music worker: ${(reason && reason.message) ? reason.message : String(reason)}`
    });
});

self.onmessage = (event) => {
    const message = event.data;
    switch (message.type) {
        case 'start':
            if (streamer) {
                streamer.close();
            }
            streamer = new TrackStreamer(message);
            if (streamer._transportPort) {
                streamer._transportPort.onmessage = (transportEvent) => {
                    const transportMessage = transportEvent.data;
                    if (transportMessage.type === 'consumed') {
                        streamer.reportConsumed(transportMessage.retiredFrames);
                    }
                };
                streamer._transportPort.start();
            }
            // run() handles its own faults and never rejects, so the promise
            // is kept only as the "fully unwound" signal for 'close'.
            streamer.finished = streamer.run();
            break;

        case 'consumed':
            if (streamer) {
                streamer.reportConsumed(message.retiredFrames);
            }
            break;

        case 'close': {
            // Acknowledged rather than silent: the owner terminates this
            // worker only once the reply arrives, so that terminate() never
            // lands on a live decoder or an in-flight fetch.
            const closing = streamer;
            streamer = null;
            if (!closing) {
                self.postMessage({ type: 'closed' });
                break;
            }
            closing.close();
            Promise.resolve(closing.finished)
                .catch(() => { })
                .then(() => self.postMessage({ type: 'closed' }));
            break;
        }

        default:
            break;
    }
};
