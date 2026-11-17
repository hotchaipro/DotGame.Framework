# Audio

The framework plays two kinds of audio. A `Sound` is decoded into memory once and played through
voices, for effects that must start at once. `Music` streams as it plays, for long tracks. The same
game code behaves the same way on every platform: a voice's `Play` allocates nothing and never blocks,
no music call blocks the calling thread, volume changes and fades reach audio that's already playing,
and when the platform's audio system isn't available the game keeps running, silently, instead of
stalling or ending.

How to use it is in the [audio guide](../guide/audio.md). This document is how it works.

## The public API

The types are in [`framework/Audio`](../../framework/Audio):

| Type | Role |
|---|---|
| [`AudioService`](../../framework/Audio/AudioService.cs) | `Game.Audio`. Makes voices (`CreateVoice`) and volume controls (`CreateVolumeControl`), holds `MasterVolumeControl`, and answers `ShouldPlayMusic`. |
| [`Sound`](../../framework/Audio/Sound.cs) | Content: one decoded buffer. It has no notion of sub-ranges; a voice plays an `(offset, duration)` segment of it, chosen when the voice is made. |
| [`SoundVoice`](../../framework/Audio/SoundVoice.cs) | Plays one copy of its segment at a time: `Play(loop)`, `Pause`, `Resume`, `Stop`, `IsPlaying`, `IsPaused`, and `PlaybackRate`, how fast it plays (below). Overlapping copies take several voices. |
| [`Music`](../../framework/Audio/Music.cs) | Content: a streamed track. `Play(start, end, loopPoint, loop)`, `PrepareAsync`, `Unprepare`, `Pause`, `Resume`, `Stop`, `IsPlaying`, `IsPaused`, `Duration`. |
| [`VolumeControl`](../../framework/Audio/VolumeControl.cs) | A volume, or a fade to one, in a tree under `MasterVolumeControl`. A voice or a track plays at the product of its control's volume and every parent's. |
| [`FadeCurve`](../../framework/Audio/FadeCurve.cs) | The shape of a fade (`Linear`, `Exponential`, `Sine`, `SCurve`, or a game's own subclass), evaluated by managed code on every platform. |

The public types are thin. They validate arguments (a negative offset, an end before the start, a NaN
volume), keep the bookkeeping that's the same everywhere, and forward to the platform:

- **Voices and their sound.** A `Sound` keeps a list of its live voices. Unloading the sound stops them
  first, so no voice plays on through freed samples, and after that `Stop` and `Pause` on a voice don't
  reach the platform at all.
- **Pause and resume.** `SoundVoice` and `Music` track `IsPaused` themselves, so `Resume` is a no-op unless
  the game paused, and `Play` on a paused voice or track starts over rather than resuming.
- **Playback rate.** `SoundVoice.PlaybackRate` is a multiple of the recorded speed, kept on the voice
  across plays, clamped to 0.25 to 4 (what the narrowest platform, iOS's varispeed unit, offers), with
  NaN refused. The voice tells the platform only when the value changes, since a pool sets it on every
  play. The sound is resampled, so pitch and length change together.
- **Volume control lifetimes.** A `VolumeControl` counts what's under it (child controls, voices, tracks).
  Disposing it sets its platform volume to 0 and keeps the platform control until the count reaches 0,
  so nothing under it changes behavior or dangles. The master control can't be disposed.

## The platform contract

Each platform implements the internal interfaces in
[`framework/Platform/Audio`](../../framework/Platform/Audio), wrapped in `Strategy<T>`:

| Interface | Contract |
|---|---|
| `IAudioServiceStrategy` | `OnInitialize`, the master control, `CreateVolumeControl`, `CreateVoice(sound, offset, duration)`, `ShouldPlayMusic`. |
| `ISoundStrategy` | The decoded audio, shared by every voice made from it. The framework never copies it per voice or per play. |
| `IVoiceStrategy` | `Play`, `Stop`, `Pause`, `SetLooping`, `SetPlaybackRate`, `SetVolumeControl`, `IsPlaying`. |
| `IMusicStrategy` | The music state machine (below): `Play`, `Prepare`, `Unprepare`, `Stop`, `Pause`, `State`, `IsPlaying`, `IsPaused`, `Duration`. |
| `IVolumeControlStrategy` | `Volume`, `Parent`, `FadeTo(volume, duration, curve)`. |

The invariants every implementation keeps:

- **Voice creation is where the cost is.** `CreateVoice` turns the segment into whatever the platform
  plays (a frame range, a pinned pointer and length, a scheduled buffer, a JavaScript object) and clamps
  it to the decoded audio. After that, `Play`, `Stop` and `Pause` allocate nothing on the managed heap.
- **`IsPlaying` is true as soon as `Play` returns**, not when an audio thread gets to it, and turns false
  at the segment's end (unless looping), on `Stop` and on `Pause`.
- **A rate is heard at once, and a cue ends when it ends.** `SetPlaybackRate` on a playing voice
  changes what's heard within an audio buffer, with no jump in the sound, and `IsPlaying` turns false
  at the segment's length divided by the rate it played at.
- **Music never blocks its caller.** Every music operation can be called from any thread, in any order,
  any number of times, including from lifecycle callbacks where blocking risks an ANR. File reads,
  decoding and native setup and teardown run on a background queue.
- **Unavailable audio is silence.** No call a game makes, and no task it awaits, depends on the platform's
  audio output being up.

### Music's state machine

[`MusicState`](../../framework/Platform/Audio/MusicState.cs) is `Unloaded`, `Loaded` (metadata only: no
file handle, decoder or buffer), `Preparing`, `Prepared`, `Playing` or `Paused`. Buffers are held for two
independent reasons: a *prepare retain* (taken by `Prepare`, released by `Unprepare`) and a *playback
retain* (taken by `Play`, released by `Stop`, never by `Pause`). They're freed only when both are
released, so prepare, play, stop and unprepare are safe in any order: stopping a prepared track keeps its
memory for the next latency-free `Play`, and whichever release comes last frees it.

`IsPlaying` and `IsPaused` report what the game last asked for, not how far the track has got: a track
is playing from the accepted `Play`, while it's still preparing or waiting for a browser gesture.

The native platforms share one implementation,
[`MusicStrategyBase`](../../platform/shared/Audio/MusicStrategyBase.cs). It owns the state, the retains
and an `AsyncOperationQueue` that serializes acquire and release work off the calling thread. An epoch
counter, bumped by anything that invalidates an in-flight acquire, makes a stale continuation do
nothing. Each platform supplies `OnAcquireAsync`, `OnReleaseAsync` and four cheap output primitives
(start, resume, pause, stop). A failed acquire is reported and drops both retains, so the track settles
back to `Loaded`. The web runs the same state machine in JavaScript, with the same numeric states.

An audio service that pauses and resumes music around system events implements
[`IMusicPlaybackListener`](../../platform/shared/Audio/IMusicPlaybackListener.cs). A track reports the
game's requests to it (`CanPlay`, `OnPlay`, `OnPause`, `OnStop`); the service's own pauses and resumes
call the track directly and aren't reported. So every service keeps the same three sets: the active
tracks, the tracks it paused itself (the only ones it resumes) and, on Android, the tracks waiting for
audio focus. A game's own pause takes a track out of the auto-resume sets. Tracks the game has unloaded
are pruned before every sweep, so a sweep never reaches released native objects.

### Volume

On desktop, Android and iOS a control is a
[`PolledVolumeControlStrategy`](../../platform/shared/Audio/PolledVolumeControlStrategy.cs). The game
thread writes a level, or a fade (from, to, start, duration, curve); audio threads poll it while
something plays and compute the volume from those fields. The writer brackets them with a sequence
counter, so a reader on another thread retries instead of seeing half of a fade, and reading never
writes. `ReadEffectiveVolume` multiplies a control's volume through its parents and the master. Because
the volume is read while audio plays, a change or a fade reaches a voice that's already playing, with
no game-thread work per frame beyond what each platform's update does.

On the web, fades can't be polled: Web Audio schedules gain changes ahead of time. Each control is a
`GainNode`, connected to its parent's, and
[`VolumeControlStrategy`](../../platform/webgl/Audio/VolumeControlStrategy.cs) samples the curve in
managed code (128 points) and schedules it with `setValueCurveAtTime`, starting from the gain the node
has at that moment. A game's own `FadeCurve` therefore works in the browser too.

## Formats and decoding

The content pipeline encodes each item for its platform (see the
[content pipeline guide](../guide/content-pipeline.md)), and each platform decodes what it was given:

| | Lossless sounds | Lossy sounds | Music |
|---|---|---|---|
| Desktop | FLAC (or WAV), 44.1 kHz | Ogg Opus, 48 kHz | Ogg Opus, 48 kHz |
| Android | FLAC (or WAV), 48 kHz | Ogg Opus, 48 kHz | Ogg Opus, 48 kHz |
| iOS | FLAC (or WAV), 44.1 kHz | AAC, or Opus in CAF | AAC, or Ogg Opus when the item asks |
| Web | FLAC (or WAV), 44.1 kHz | Ogg Opus, 48 kHz | Ogg Opus, 48 kHz |

A standalone `SoundContent` item is always lossless, so it plays with no delay.

Managed decoding uses the [DotGame.AudioCodecs](https://github.com/hotchaipro/DotGame.AudioCodecs)
package (Opus, FLAC and WAV), referenced through
[`eng/DotGame.AudioCodecs.targets`](../../eng/DotGame.AudioCodecs.targets): the package by default, or a
clone's source when `DotGameAudioCodecsDir` is set, so a codec change and the framework change that needs
it build together. Only the platforms that decode in managed code set `DotGameManagedAudioDecoding`,
which makes the shared
[`DotGame.Framework.Platform.Common.projitems`](../../platform/shared/DotGame.Framework.Platform.Common.projitems)
bring in two classes:

- [`SoundDecoder`](../../platform/shared/Audio/SoundDecoder.cs) decodes a whole WAV, FLAC or Ogg Opus file
  to 16-bit interleaved PCM, optionally on the pinned heap. Desktop and Android decode every sound with it,
  and iOS its lossless sounds, so a sound has the same samples on all three.
- [`OpusMusicReader`](../../platform/shared/Audio/OpusMusicReader.cs) streams an Ogg Opus track as 16-bit
  PCM at 48 kHz, with sample-exact seeks for start and loop points (a seek starts decoding 400 ms early,
  so a loop has no seam). It reads the file on the first read or seek and holds it in memory while the
  track is in use: compressed music is small, and holding it keeps file reads out of the audio path.
  Desktop and Android use it.

iOS decodes lossy sounds and music with `AVAudioFile`, which leaves out the encoder's priming and
padding, so a segment's offsets land on the same samples as in the source. The web decodes in the
browser, so a web game's download doesn't carry the codecs at all.

## Desktop

Desktop uses SDL3's audio. SDL mixes every open stream into the device itself.

- **Sounds.** The PCM is on the pinned heap
  ([`SoundStrategy`](../../platform/desktop/Audio/SoundStrategy.cs)), because SDL reads it while it plays.
- **Voices.** Each [`VoiceStrategy`](../../platform/desktop/Audio/VoiceStrategy.cs) opens its own
  `SDL_AudioStream` on the default playback device when it's created, in the sound's format. `Play` clears
  the stream and queues a pointer into the pinned PCM with `SDL_PutAudioStreamDataNoCopy`: no copy, no
  allocation. A looping voice keeps a quarter of a second queued ahead, topped up each frame, so only a
  frame longer than that leaves a gap.
- **Rate.** `SDL_SetAudioStreamFrequencyRatio` on the voice's stream: SDL resamples as it reads. A
  stream that resamples holds its last frames back for the frames it expects next, so a cue that isn't
  looping is flushed as it's queued (`SDL_FlushAudioStream`); without that the queue never empties and
  the voice never finishes. A looping voice's quarter second ahead is in the sound's own bytes, so it's
  multiplied by the rate.
- **Volume.** The audio service keeps a list of voices that have played and, once a frame
  (`UpdateVoices`, on the game thread), sets each one's stream gain from its control, dropping the voices
  that have finished. There's deliberately no SDL get callback: managed code on SDL's audio thread would
  run every device buffer for every voice, hold the device's lock through any garbage collection, and a
  voice dropped without being disposed would leave SDL calling a collected delegate.
- **Music.** [`MusicStrategy`](../../platform/desktop/Audio/MusicStrategy.cs) opens its stream on acquire
  and runs a decode thread of its own that keeps a quarter of a second queued, setting the gain as it
  goes. Decoding in the stream's callback would put a slow decode or a cold disk read on SDL's audio
  thread, holding the device lock, and an exception there would end the process.
- **Lifecycle.** When the window is minimized or hidden the service pauses playing music, and resumes
  what it paused when the window is shown. `ShouldPlayMusic` is always true.

## Android

Every sound effect plays through **one output stream, mixed in managed code**. A voice is a plain
managed object that costs nothing on the platform, so a game can make as many as it likes.

- **[`AudioOutput`](../../platform/android/Audio/AudioOutput.cs)** owns the one `AudioTrack`:
  `MODE_STREAM`, `ENCODING_PCM_FLOAT`, stereo, at the device's native output rate
  (`PROPERTY_OUTPUT_SAMPLE_RATE`), with `PERFORMANCE_MODE_LOW_LATENCY` from Android 8, and
  `USAGE_GAME` / `CONTENT_TYPE_SONIFICATION`. Matching the native rate and burst size
  (`PROPERTY_OUTPUT_FRAMES_PER_BUFFER`) is what gets the system's fast mixer path.
- **The audio thread**, "DotGame Audio Mixer", at `THREAD_PRIORITY_URGENT_AUDIO`, is started by the first
  `Play` and makes every `AudioTrack` call. It loops: mix one burst into a preallocated `float[]`, copy it
  into a direct `ByteBuffer`, and write it, blocking. (Writing a `float[]` would copy it through a fresh
  Java array every burst.) The track's queue starts at two bursts in a buffer with room for eight; every
  16 bursts the thread checks `getUnderrunCount`, and if it has risen (a GC pause, a busy device) the
  queue grows by a burst, up to its capacity.
- **Idle.** After 15 seconds of silence the thread pauses and flushes the track and parks; the next voice
  to play wakes it. Fifteen seconds is long enough that a tap in a slow-paced game doesn't find the output
  asleep, and short enough that an idle game costs no CPU. A wake is published before the parked check,
  and the thread checks for one before parking, so a voice started as it decides to park is never missed.
- **[`AudioMixer`](../../platform/android/Audio/AudioMixer.cs)** holds the voices in a copy-on-write array:
  creating or disposing a voice allocates, under a lock; the audio thread reads the array without one.
  `Mix` clears the burst and adds every playing voice into it. Voices sum in float, and Android clamps
  float output, so there's no limiter.
- **Sounds are converted once, at load.** [`SoundLoader`](../../platform/android/Audio/SoundLoader.cs)
  decodes on the thread pool, at most three at a time so a large load doesn't take every core while the
  game starts. [`PcmConverter`](../../platform/android/Audio/PcmConverter.cs) then converts to float at
  the output rate. Sounds are built at 48 kHz, the rate most devices mix at, so that's usually only the
  conversion to float, eight samples at a time; a device that mixes at another rate gets the sound
  resampled with a 4-point cubic Hermite interpolator. Mono stays mono and is written to both channels;
  more than two channels keep the first two. Mixing is then a multiply-add per sample, with no rate or
  format conversion on the audio thread.
- **[`VoiceStrategy`](../../platform/android/Audio/VoiceStrategy.cs)** holds its sound's samples, its
  segment as a frame range, its volume control and a play position. The game thread drives it with
  generation counters and volatile flags: `Play` bumps the play generation, `Stop` marks the current
  generation stopped, `Pause` sets a flag, and the audio thread marks a generation finished when it
  reaches the segment's end. Neither side locks, and `Play` allocates nothing.
- **Volume.** The mixer reads each playing voice's effective volume once per burst and ramps linearly
  from the last burst's value across this one, so changes and fades never click and need no game-thread
  polling. The left and right gains are kept apart, though they're the same, so that a pan would
  change what they're set to and not the mixing.
- **Rate.** A voice's rate is a volatile field read once per burst. At 1, with the position on a whole
  frame, the samples are added as they are, as before. Otherwise the position is fractional and steps
  by the rate, and each output frame is read through the same 4-point Hermite interpolator the sounds
  are converted with; the frames past a segment's ends are its end frames, or its other end's when it
  loops, so a loop has no seam. A rate changed mid-play carries on from the position it had reached.

The framework tests compile `AudioMixer`, `VoiceStrategy`, `SoundStrategy` and `PcmConverter` in, since
they have no Android types, and test mixing and conversion on the desktop.

**Music** keeps a streaming `AudioTrack` per track
([`MusicStrategy`](../../platform/android/Audio/MusicStrategy.cs)): 16-bit, `USAGE_GAME` /
`CONTENT_TYPE_MUSIC`, with a 400 ms buffer and a refill thread at `THREAD_PRIORITY_AUDIO` that decodes
100 ms at a time from `OpusMusicReader`. The track and the thread are made on acquire, on the background
queue, so neither track construction nor the thread join runs on the game thread or a lifecycle callback.
A game has one or two tracks, far from any track limit.

### Audio focus

Sound effects never take audio focus and mix with whatever the player is listening to. Only music takes
focus, when it starts ([`AudioServiceStrategy`](../../platform/android/Audio/AudioServiceStrategy.cs)):

| Event | What happens |
| --- | --- |
| Launch, a resume with no music to restore, a sound effect | No focus is taken; other apps keep playing |
| Music starts with no other audio playing | Focus is taken |
| Music starts while other audio plays, or in the background | Refused: `ShouldPlayMusic` is false, and a `Play` is dropped rather than queued |
| The last music stops or ends, or the activity pauses | Focus is given up |
| Another app takes focus | Playing music pauses; it resumes on a focus gain, or once other audio ends after a permanent loss |
| The activity resumes with music the service paused | It resumes, unless other audio is playing: then it waits until that ends |

"Other audio" means active players with `USAGE_MEDIA` or `USAGE_VOICE_COMMUNICATION`, reported by an
`AudioPlaybackCallback`, so `ShouldPlayMusic` reads a cached value and no frame calls into the system.
The game's own tracks use `USAGE_GAME`, so they never count (`AudioManager.isMusicActive` can't tell them
apart). An ad's video does count, so music waits for it. Only the game's own activity drives the
lifecycle sweeps: an ad's or a billing screen's resume over it doesn't bring the music back. Below
Android 8 there's neither the focus request API nor the playback callback, and music plays regardless.

## iOS

**One audio thread owns the audio system.** The audio service
([`AudioServiceStrategy`](../../platform/ios/Audio/AudioServiceStrategy.cs)) starts a background thread,
"DotGame Audio", in `OnInitialize` and returns at once. Everything that can block on the audio server
runs there: setting the session category and activating it, deactivating it in the background, building
and starting the one `AVAudioEngine`, attaching and connecting player nodes, starting them, stopping
released ones, and restarting the engine. The game thread and the main thread's notifications only set
flags and wake it. An exception on the audio thread is reported and the pass is retried, since one
escaping would end the process.

**Probe before the engine.** Before it builds the engine, the audio thread plays two silent buffers
through an `AudioQueue` and waits for the first to finish. A finished buffer means IO cycles are
running, so the `AURemoteIO` output unit the engine makes next will answer. The reason is that
`AURemoteIO` aborts the process when an RPC to a stalled audio server times out, on whichever thread made
the call, and the engine's first touch of its output (`MainMixerNode`, which builds the output node) is
such a call. Moving engine calls to a background thread alone would only move the abort; the output unit
must not be touched until audio IO is known to be running. An `AudioQueue` that can't reach the server
waits rather than aborts, and it waits on the audio thread, which has nothing else to do while output is
down. If the queue can't start (the session is inactive, a call holds audio, the app is in the
background), the pass ends and is retried in 5 seconds, or sooner when a route change, an interruption
ending, the app becoming active or a media-services reset wakes the thread. This matters on devices too:
the audio server stalls or resets there as well.

**One engine, one node pool.** Voices and music tracks borrow `AVAudioPlayerNode`s from one pool on the
shared engine (`AcquireSlot`, `ReleaseSlot`). A node is connected once, with its slot's format (rate and
channel count vary from file to file), because reconnecting a node while the engine runs throws; a free
slot is reused only for the same format, and nodes are never detached, because detaching a node while
the engine renders crashes. A released slot's node is stopped on the audio thread before the slot is
free again. A node the audio thread hasn't wired yet counts as absent ([`IPlayerNodeOwner`](../../platform/ios/Audio/IPlayerNodeOwner.cs)
tells its owner when it's wired, when the engine restarts and how to apply its volume).

**Starting a node.** `AVAudioPlayerNode.Play` waits for the output's next IO cycle; if the server stops
running IO cycles it waits about 2 seconds and throws. So the game thread never starts a node: a voice or
track asks the audio thread (`StartSlotNode`), which checks the start is still wanted and, if the start
throws, restarts the engine. A voice's node then keeps running between plays
([`VoiceStrategy`](../../platform/ios/Audio/VoiceStrategy.cs)): `Play` schedules the segment's buffer with
`AVAudioPlayerNodeBufferOptions.Interrupts`, which replaces what the node plays within a render cycle,
and `Stop` interrupts with an empty buffer. Neither waits for IO, so rapid replays start at once. The
voices of a segment share one float buffer
([`SoundStrategy.GetSegmentBuffer`](../../platform/ios/Audio/SoundStrategy.cs)), converted once when the
first voice is made, so extra voices cost a node each, not a copy of the audio.

**Rate.** A voice plays at a rate other than as recorded through an `AVAudioUnitVarispeed` between its
node and the mixer. A node is connected once, so the unit can't be added to a slot that has none: a
slot is made with a unit or without one and keeps that kind (`AcquireSlot(format, owner, hasRateUnit)`),
and the first time a voice's rate leaves 1 it stops, takes a slot with a unit and releases its own
([`VoiceStrategy.SetPlaybackRate`](../../platform/ios/Audio/VoiceStrategy.cs)). It keeps that slot,
back at 1 too. A play made before the audio thread has wired the new node starts when it's wired, as
a pool's new voice does. So voices that never change their rate, which is most of them, play straight
into the mixer as they always have, and no unit runs for them. The unit's rate is a parameter, set on
the game thread and again as the node is wired. The node's volume still reaches the mixer through the
unit, and the buffer's completion still comes when the audio has played at its rate (both measured
on the simulator, with a tap on the mixer: `docs/design/sound-playback-rate.md`).

**Volume.** A player node has no render callback to poll from, so a second thread, "DotGame Audio
Volume", applies each playing voice's and track's volume to its node about every 15 ms while anything
plays, and sleeps until the next start when nothing does.

**Music** ([`MusicStrategy`](../../platform/ios/Audio/MusicStrategy.cs)) opens its `AVAudioFile` and takes
a node from the pool on acquire, and never touches the engine itself. Acquiring never waits for the
output, so `PrepareAsync` completes and `Play` reaches `Playing` as usual while audio is down; games await
`PrepareAsync` during startup, and a track that waited for the output would hold the game there. Until
its node is wired the track is silent; when the audio thread wires it, the track schedules its audio
and, if it's playing, starts.

**Session and recovery.** The category comes from `iOSGameEngine.Options.AudioCategory`
([`AudioCategory`](../../platform/ios/AudioCategory.cs)): `Ambient` by default, which mixes with other
apps' audio and respects the silent switch. `ShouldPlayMusic` is false while
`SecondaryAudioShouldBeSilencedHint` is set, a value read on activation and kept up to date by its
notification, so the game thread never queries the session.

- *IO returns after a start without it*: the probe's buffer finishes, the engine is built, and voices and
  waiting music come alive.
- *The app returns to the foreground, an interruption ends, the route changes*: the audio thread
  activates the session and restarts the engine, then posts the resume of auto-paused music to the main
  thread, so music never resumes into an inactive session. A `Began` delivered late, for an interruption
  that came while the app was suspended, is treated as already over (no `Ended` follows it), and becoming
  active always tries again, since not every `Began` gets an `Ended`.
- *An engine configuration change* (a different output route, for example): the engine restarts on the audio
  thread, and each owner starts its node again.
- *A media-services reset*: the engine and its nodes belong to the dead server. They're abandoned, kept
  referenced and never called, since a call or a release could reach the server; every slot gets a fresh
  node, the probe runs again, and active music schedules its audio again from its start position.

## Web

The browser does the audio work in its own process, so nothing the game thread does waits on it.
[`audio.js`](../../platform/webgl/wwwroot/_dotgame/audio.js) owns one `AudioContext`; the managed
strategies in [`platform/webgl/Audio`](../../platform/webgl/Audio) hold opaque handles to its objects
through [`AudioBindings`](../../platform/webgl/Audio/AudioBindings.cs).

- **Sounds** are decoded by `decodeAudioData` into an `AudioBuffer`, shared by every voice, and
  resampled once at load if it isn't at the context's rate, so a play never pays for resampling.
- **Voices.** A voice is a cached `GainNode` connected to its volume control's node (or the master's).
  Web Audio source nodes are one-shot, so each `Play` makes an `AudioBufferSourceNode` in JavaScript over
  the shared buffer; nothing is allocated on the managed side.
- **Rate.** The voice's rate is each source's `playbackRate`, set as the source is made and on the
  playing source when it changes. A source's offset, duration and loop points are in seconds of the
  buffer, which the rate doesn't change. The time the slice began (`startedAt`, which a pause and a
  change of looping read the position from) is kept in the rate's own terms and moved when the rate
  changes, so the position stays where it was.
- **Music** streams through Web Audio alone: a worker
  ([`music-worker.js`](../../platform/webgl/wwwroot/_dotgame/music-worker.js)) fetches the Ogg file,
  demuxes it and decodes it with WebCodecs' `AudioDecoder`, applying the range and loop policy, and an
  `AudioWorkletProcessor` ([`music-processor.js`](../../platform/webgl/wwwroot/_dotgame/music-processor.js))
  plays the frames. The page's main thread only relays decoded buffers, transferred without copies, and
  the worker keeps a bounded window decoded, so a whole track is never held as PCM. Nothing is fetched
  before prepare or play. A browser without WebCodecs' Opus decoder, `AudioWorklet` or `Worker` plays no
  music.
- **The gesture.** A browser won't start audio before the player interacts with the page. Music played
  before then is queued per track and started after the first `pointerup`, `touchend`, `click` or
  `keydown`; WebKit doesn't accept `pointerdown` for this. A sound played before then is dropped, as it
  would be late. `ShouldPlayMusic` is always true.
- **Hidden pages.** The context is suspended while the page is hidden and resumed on return, with music
  engines told to stop consuming their queues first, since a hidden page's worker is frozen. Overlapping
  suspends and resumes are coalesced so the context ends in the state asked for last.
- **Rebuilding.** An `AudioNode` belongs to its context for good. When WebKit leaves a context
  `interrupted`, or a context comes back from a suspend with its output permanently later (a larger device
buffer, which no resume or rescheduling fixes), `audio.js` rebuilds the whole graph
  on a fresh context, replacing the context-bound fields of every voice, control, buffer and stream in
  place, so the managed handles stay valid and the game doesn't notice beyond music restarting from its
  prepared start.

## When audio is unavailable

Every platform runs silently when its audio system is down, and comes back without the game doing
anything.

| Platform | While the audio system is unavailable | When it comes back |
|---|---|---|
| Desktop | `SDL_OpenAudioDeviceStream` returns no stream: that voice stays silent and `IsPlaying` false; music's acquire fails and is reported. SDL opens devices on its own thread and never blocks the caller on a missing device. | Default-device streams follow the system's default device. A voice whose stream never opened stays silent until it's made again. |
| Android | If the output track can't be created or a write fails, the audio thread ends, voices are silent, and the next sound played at least a second later tries again. A music track whose `AudioTrack` can't be made is silent. Music doesn't start while another app holds focus. | One track to reopen, at the next sound. Focus regained resumes music. |
| iOS | Voices and tracks are plain objects with unwired nodes: voices are silent and `IsPlaying` stays false; music prepares and plays as usual, silently. Nothing touches the output unit. | The probe succeeds, the engine is built, and every node is wired; playing music starts. |
| Web | The context stays `suspended` or `interrupted`; the game thread is never blocked. | `audio.js` resumes the context at the next gesture, rebuilding it if WebKit left it interrupted. |

A failure in audio code that would otherwise be lost (on an audio thread, in a music acquire) goes
through `GameHost.ReportAudioFailure`: in a Release build it's reported and the game runs on silently;
in a Debug build it ends the game, so it can't be missed.

## Why

**One mixed output on Android.** A voice per platform track would need one `AudioTrack` for every voice
a game makes, whether it ever plays or not. Android caps tracks per process (AudioFlinger allows about 40
client tracks, and static buffers share a memory budget), and the cap varies by device, so no per-game
voice limit is safe. Each failed track creation also takes tens of milliseconds on the calling thread.
The alternatives:

| | Verdict |
|---|---|
| A managed mixer on one `MODE_STREAM` track | Chosen: about two bursts of latency, adaptive; one thread doing a multiply-add per sample per playing voice; no allocation after load; cheap voices, as on iOS and desktop |
| Static tracks built on first play, with a cap and voice stealing | A hidden cap and cross-sound stealing no other platform has, a JNI and binder call on the game thread for each voice's first play, and a safe cap that varies by device |
| `android.media.SoundPool` | It decodes from files itself (no shared PCM, no segments without temporary files), fixes its stream count at creation, and can't report when a sound ends, so `IsPlaying` couldn't be honest |
| AAudio through P/Invoke | Lowest latency, but its data callback would run managed code on a native real-time thread, where a GC suspension is an audible glitch; and the framework supports Android versions before AAudio. The mixer only needs a small output interface, so this stays possible without touching voices |

Converted float PCM takes about twice the memory of the 16-bit PCM, a few MB for a game's effects, which
is cheaper than the native buffers per voice it avoids. A GC pause longer than the buffered audio still
causes a click; the adaptive queue absorbs repeats.

**One engine on iOS, owned by one thread.** An engine per music track would mean every track builds,
starts and stops an engine, each touching the output unit from its own thread, and after a configuration
change each would have to be restarted by itself. One engine, touched only by the audio thread, leaves a
single place where the output unit is called after startup, off the game thread.

**Silence rather than waiting.** A game awaits content and music preparation during startup, so if any
of those waited for the audio output, a device without audio would hold the game on its launch screen.
Making unavailable audio look like silence keeps every platform's behavior the same: the game can't tell,
and doesn't need to.

**Polling volume on native platforms.** A fade evaluated by the thread that renders audio follows the
audio's own clock and needs nothing from the game thread per frame, and one implementation of the curves
serves desktop, Android and iOS. The web is the exception because its gain changes must be scheduled
ahead, and it samples the same managed curve.

## Limits

- On iOS, if the audio server stalls *after* output is up, without a reset, an engine restart on the audio
  thread can still reach the RPC timeout. It's the only call that reaches the output unit after startup,
  it's off the game thread, and most such stalls end in a media-services reset, which is handled.
- A desktop voice whose stream couldn't open stays silent until it's made again; voices don't reopen
  their streams.
- Playing a sound faster than it was recorded folds its highest frequencies back as noise (aliasing):
  no platform's resampler here filters first. It isn't heard on most effects up to about an octave.
- On iOS, the first time a voice's rate leaves 1 while it plays, what it was playing is cut off (it
  moves to a slot with a varispeed unit). What a varispeed unit costs on a device hasn't been
  measured; only voices that use a rate have one.
- Music has no playback rate, and nothing has a pan.
- On Android, sound effects take no audio focus and music takes it only to play; there's no ducking.
- On the web, a sound played before the player's first gesture is dropped, and browsers without WebCodecs'
  Opus decoder, `AudioWorklet` or `Worker` play no music.
