# Design note: playback rate for sounds, and pan

Status: proposed, 2026-11-17. Nothing here is built.

In short: add `SoundVoice.PlaybackRate` now, on its own. Design `Pan` now (it's below) and build it
later, when a game needs it. No helper class for semitones.

## The need

A voice plays its sound as recorded. Two things games do all the time need more:

- **Variation.** A sound that repeats (a block popping, a coin landing, a footstep) is tiring at one
  pitch. A few percent of difference from play to play is what makes it sound alive.
- **Melody from one sample.** A creature's gibberish voice needs each syllable a little higher or lower
  than the last, so a line rises to a question or falls to its end. Today that takes one file per pitch
  (Blockdown's guide has 12 syllables at fixed pitches).

Both are the same capability: play the samples faster or slower, which raises or lowers the pitch and
shortens or lengthens the sound by the same factor. Every platform the framework runs on can do it.

## Proposal

### Framework: a property on the voice

```csharp
public sealed class SoundVoice
{
    /// <summary>
    /// How fast the voice plays its sound, as a multiple of the recorded speed: 1 is as recorded,
    /// 2 is twice as fast and an octave higher, 0.5 half as fast and an octave lower. Pitch and
    /// length change together. Setting it while the voice plays takes effect at once.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a number.</exception>
    public float PlaybackRate { get; set; }      // default 1

    public const float MinPlaybackRate = 0.25f;  // two octaves down
    public const float MaxPlaybackRate = 4f;     // two octaves up
}
```

What it means, the same on every platform:

- It is the voice's, like `IsLooping` and `VolumeControl`: `Play`, `Stop`, `Pause` and `Resume` leave it
  as it is. A new voice has 1.
- A value outside the range is clamped; NaN throws. This is what `VolumeControl.Volume` does.
- The segment lasts its duration divided by the rate, and `IsPlaying` turns false then. A looping voice
  loops at the rate.
- A change while playing is heard within one audio buffer, without a click. There is no glide: a game
  that wants one sets the property each frame.
- `Music` has no rate in this version.

The range is what the narrowest platform offers (iOS's varispeed unit, 0.25 to 4), so no platform
clamps where another doesn't.

Internally, `IVoiceStrategy` gains `SetPlaybackRate(float rate)`. It's an internal interface, so it can
change freely.

### Toolkit and engine: one overload each

```csharp
// DotGame.Toolkit, SoundPool
public void Play(float volume = 1f);                 // as it is
public void Play(float volume, float playbackRate);  // new

// HotChai.Game, SoundPlayer: the same overload beside each existing Play
```

The pool sets the voice's rate on every play (1 from the existing overload), since a pooled voice keeps
whatever the last play gave it.

Usage:

```csharp
pop.Play(1f, 0.94f + (0.12f * random.NextSingle()));          // a little different each time
syllable.Play(0.8f, MathF.Pow(2f, melody[i] / 12f));          // a line that rises at its end, in semitones
voice.PlaybackRate = 1.5f;                                    // a voice the game holds itself
```

## Precedent

How other frameworks and the platforms beneath them expose the same thing. All of them resample: pitch
and length change together.

| API | Member | Unit | Normal | Range | Where it's set |
|---|---|---|---|---|---|
| **This proposal** | `SoundVoice.PlaybackRate` | multiplier | 1 | 0.25 to 4, clamped | property on the voice; `Play(volume, playbackRate)` on the pool |
| MonoGame, FNA (XNA) | `SoundEffectInstance.Pitch`, `SoundEffect.Play(volume, pitch, pan)` | octaves | 0 | −1 to 1, throws outside | property on the instance; arguments to the one-shot |
| Unity | `AudioSource.pitch` | multiplier | 1 | −3 to 3, clamped | property on the source |
| Godot 4 | `AudioStreamPlayer.pitch_scale`; `AudioStreamRandomizer.random_pitch` | multiplier | 1 | above 0 | property on the player; variation on the stream resource |
| libGDX | `Sound.play(volume, pitch, pan)`, `setPitch(id, pitch)` | multiplier | 1 | 0.5 to 2 | arguments to play; setter by instance id |
| SFML, raylib, LÖVE | `setPitch`, `SetSoundPitch`, `Source:setPitch` | multiplier | 1 | above 0 | setter on the sound |
| OpenAL | `AL_PITCH` | multiplier | 1 | above 0 | source parameter |
| FMOD Core | `Channel::setPitch` | multiplier | 1 | | setter on the channel |
| Bevy | `PlaybackSettings.speed`, `AudioSink::set_speed` | multiplier | 1 | | settings struct at play; setter while playing |
| Phaser, Howler | `rate` (Phaser also `detune`, cents) | multiplier | 1 | Howler 0.5 to 4 | config object at play; setter |
| Web Audio | `AudioBufferSourceNode.playbackRate`, `.detune` | multiplier; cents | 1; 0 | unbounded | parameters on the source node, the two compounded |
| SDL3 | `SDL_SetAudioStreamFrequencyRatio` | multiplier | 1 | 0.01 to 100 | setter on the stream |
| Android `SoundPool` | `play(…, rate)`, `setRate` | multiplier | 1 | 0.5 to 2 | argument to play; setter by stream id |
| Apple | `AVAudioUnitVarispeed.rate` | multiplier | 1 | 0.25 to 4 | property on the unit |

Sources: [MonoGame](https://docs.monogame.net/api/Microsoft.Xna.Framework.Audio.SoundEffectInstance.html)
and [XNA](https://learn.microsoft.com/en-us/previous-versions/windows/xna/dd254914(v=xnagamestudio.41)),
[Unity](https://docs.unity.com/en-us/engine/current/script-reference/unityengine/audiosource/pitch),
[Godot](https://docs.godotengine.org/en/latest/classes/class_audiostreamrandomizer.html),
[libGDX](https://javadoc.io/static/com.badlogicgames.gdx/gdx/1.13.0/com/badlogic/gdx/audio/Sound.html),
[Web Audio](https://developer.mozilla.org/en-US/docs/Web/API/AudioBufferSourceNode),
[SDL3](https://wiki.libsdl.org/SDL3/SDL_SetAudioStreamFrequencyRatio). The MonoGame, Unity, Godot's
randomizer, libGDX, Web Audio and SDL3 rows were checked against those pages on 2026-11-17; the other
rows are from memory and should be checked before this note is cited elsewhere.

What the table says:

1. **The unit is settled: a multiplier, 1 as recorded.** Every row but one. XNA's octaves are the
   one exception, and MonoGame only keeps them for compatibility.
2. **The shape is settled too: state on the playing thing**, with the one-shot convenience taking it as
   an argument. That's a property on `SoundVoice` and an overload on `SoundPool.Play`.
3. **The name is not settled.** Game frameworks say *pitch*; the platforms say *rate*, *speed* or
   *ratio*.

### The name: `PlaybackRate`, not `Pitch`

`Pitch` is the word most game developers would look for. It's also the only one of the two with two
meanings in the framework's own neighbourhood: a developer from MonoGame reads `Pitch = 0` as normal
and `Pitch = 1` as an octave up; a developer from Unity reads `pitch = 1` as normal. Whichever unit
`Pitch` took here, one of them would write a bug that compiles.

Every API that says rate, speed or ratio means a multiplier with 1 as normal, so `PlaybackRate` can
only be read one way. It also says what happens: the sound gets shorter as it gets higher, which
`Pitch` hides. The documentation comment and the guide say "pitch", so a search finds it.

The case against: in media players (`HTMLMediaElement.playbackRate`, `AVAudioPlayer.rate`) rate
changes speed and keeps the pitch. For sample playback (Web Audio's source node, SDL, `SoundPool`,
varispeed) it never does, and a wrong guess is heard on the first play.

If `Pitch` is preferred for familiarity, it should be the multiplier (Unity's and Godot's meaning), and
nothing else in this note changes.

### Semitones: no helper class

An earlier draft of this note had a public `AudioMath` class with `RateFromSemitones` and
`RateFromCents`, for a game that thinks in musical steps. It's withdrawn:

- **It isn't needed.** The conversion is one expression, `MathF.Pow(2f, semitones / 12f)`, and the
  guide can give it where it explains the rate.
- **Nobody else has one.** None of the APIs in the table offers a function that turns semitones into
  a rate. Where musical units exist they are a second property on the playing thing (Web Audio's
  `detune`, Phaser's `detune`, Godot's `random_pitch_semitones`), and Unity, MonoGame, libGDX, SDL
  and FMOD Core have nothing: their users write the expression. (Godot's `linear_to_db` and
  `db_to_linear` are the nearest thing, and they are for volume.)
- **It's against the framework's grain.** The audio API is six types that each stand for something
  that plays or is controlled. A static class of arithmetic would be the first public type that does
  neither, added for one line of arithmetic, and kept forever.

A second property (`Detune`) isn't proposed either: two values that multiply together are two places
for a setting to hide. If musical units prove common in games, the place for them is the toolkit,
beside `SoundPool`, where a mistake costs less than in the framework.

## Pan

### Recommendation: design it now, build it later

Rate and pan look related, and in the API they are: each is one number on a voice. Underneath they
have nothing in common. Rate is resampling; pan is a gain for each output channel. Building them
together saves no work, with two exceptions noted below, and pan has a cost that rate doesn't:

| Platform | Pan needs | Size |
|---|---|---|
| Android | Two gains per voice in the framework's mixer where there is one. | small |
| iOS | `AVAudioPlayerNode.pan`, set where the volume is set. | small, if it works through the varispeed unit (below) |
| Web | A `StereoPannerNode` for each voice, or a splitter, two gains and a merger for an exact law. | small |
| Desktop | **SDL has no pan.** A stream has one gain (`SDL_SetAudioStreamGain`); only SDL_mixer pans, and the framework doesn't use it. A desktop voice queues a pointer into the shared PCM without copying, so there's nowhere to scale a channel. Pan there means a voice's stream becomes stereo and plays a panned copy made at `Play` (which then can't follow a change while it plays), or desktop voices move to a mixer of the framework's own, as Android's are. | large |

So pan is small on three platforms and a change of design on the fourth, and no game is waiting for
it: Blockdown is played on a phone's speaker, where pan is barely heard. A minimal framework shouldn't
carry an API that has no user and a platform that can't honour it yet.

The two exceptions, where doing a little now is cheaper than doing it later:

1. **The iOS trial build for rate should try pan and volume too.** Rate puts a varispeed unit between
   a voice's player node and the mixer. A player node's volume and pan are applied on the mixer's
   input, and Apple's documentation doesn't say whether that still holds with a unit in between. If
   it doesn't, the design for rate has to change (it would break today's volume), and it's the same
   trial that answers it for pan.
2. **The Android mixer's loop is being rewritten for rate.** Written with a gain for each channel
   (both the same for now), pan later is a change of two numbers there, not a second rewrite.

### The shape, so that rate leaves room for it

```csharp
public sealed class SoundVoice
{
    /// <summary>
    /// Where the voice sits between the left and right speakers: -1 is all to the left, 0 is as
    /// recorded, 1 is all to the right.
    /// </summary>
    public float Pan { get; set; }      // default 0; clamped to -1..1; NaN throws
}
```

- **Name and range are settled by precedent**: `Pan`, −1 to 1, 0 in the middle, in MonoGame
  (`SoundEffectInstance.Pan`, and the third argument of `Play(volume, pitch, pan)`), Unity
  (`panStereo`), libGDX, FMOD Core, Web Audio (`StereoPannerNode.pan`) and Apple (`AVAudioMixing.pan`).
  raylib is the one exception (0 to 1, 0.5 in the middle). Godot and OpenAL have no pan: they place a
  sound in space. The MonoGame, libGDX, Web Audio and raylib entries were checked on 2026-11-17; the
  rest are from memory.
- **The law has to be chosen, because the platforms differ.** Proposed: at 0 the sound is exactly as
  it is without pan, and toward one side the other channel fades out in a straight line while the
  near one stays as it is (a balance control; a mono sound counts as the same in both channels). It
  changes nothing for sounds that aren't panned, and it never makes a sound louder. The alternative,
  equal power, keeps loudness steady across the sweep but is 3 dB down in the middle for a mono sound
  (Web Audio's `StereoPannerNode` works this way), so adding the property would quieten every centred
  sound unless the unpanned case is made special.
- **Where the platform has its own law (iOS), the middle and the two ends are guaranteed and the
  curve between is the platform's.** For sound effects the difference isn't heard. This would be the
  first place the framework's audio isn't the same on every platform, and the documentation should
  say so.
- **In the pool, pan is where positional arguments stop** (rule 5 below): `Play(volume, playbackRate)`
  doesn't grow a third float. A game that pans holds the voice, or the pool gains a settings struct.

### When pan is built

Order: Android (nearly free after rate), web, iOS, and desktop last, with its design decided on its
own: whether desktop voices should move to a managed mixer is a larger question than pan, and pan
alone shouldn't force it.

## Growing without breaking

Audio will gain more per-voice controls (pan first, most likely). The rules this proposal follows, so
that each addition is only an addition:

1. **A new control is a new property on `SoundVoice` whose default changes nothing.** `Play(bool loop)`
   never gains parameters. Code written today keeps compiling and keeps sounding the same.
2. **One unit per kind of value.** Levels and rates are multipliers with 1 as unchanged, as `Volume`
   is. A pan would be −1 to 1 with 0 as centre, as everywhere in the table.
3. **Values are clamped and NaN throws**, as `Volume` does, on every platform alike.
4. **Convenience methods grow by overload, never by adding an optional parameter** to an existing
   method: that changes the method's signature, which breaks compiled callers.
5. **Positional arguments stop at two.** `Play(volume, playbackRate)` is where XNA and libGDX began;
   both went on to `Play(volume, pitch, pan)`, three floats that are easy to swap. If the pool needs a
   third per-play value, it takes a settings struct in a new overload (Bevy's `PlaybackSettings`,
   Phaser's config) and the two-float overload stays.
6. **The platform contract is internal.** `IVoiceStrategy` can gain members and change them.

What stays possible later, each without touching what's proposed here:

| Later | How it fits |
|---|---|
| `SoundVoice.Pan` | another property, rule 1; designed above |
| A rate for a group (slow motion for every sound) | a rate on a shared control, multiplied with the voice's, as volumes multiply through `VolumeControl` |
| Variation built into a pool or a sound bank's cue | a property on `SoundPool`, or a field in the bank's content, that the pool applies when it plays (Godot's `AudioStreamRandomizer`) |
| `Music.PlaybackRate` | the same name and meaning; it needs the decoders' output resampled on each platform |
| A glide to a rate | a method beside the property, as `VolumeControl.FadeTo` is beside `Volume` |
| Changing pitch without changing length | an effect, as in Unity and Godot, with a name of its own. `PlaybackRate` doesn't take the word *pitch*, so it stays free for this |

## Implementation

| Platform | How | Size |
|---|---|---|
| Web | `AudioBufferSourceNode.playbackRate`: set when `audio.js` makes the source for a play, and on the current source when the property changes. The node's `onended` already follows the rate. | small |
| Desktop | `SDL_SetAudioStreamFrequencyRatio` on the voice's stream. `IsPlaying` reads the stream's queue, so it follows. The looping voice's quarter second queued ahead becomes a quarter second times the rate. | small |
| Android | The framework's own mixer. A voice's play position becomes fractional and steps by the rate; a voice at a rate other than 1 reads through the 4-point Hermite interpolator `PcmConverter` already has, and a voice at 1 keeps today's loop. The rate is a volatile field read once per burst, like the volume. | medium |
| iOS | An `AVAudioUnitVarispeed` between each voice slot's player node and the mixer, connected once when the slot is made, so the rule that nodes are never reconnected holds. Its rate is set as the node's volume is. Music slots get none. | medium, with questions |

Open questions, all iOS, to answer with a throwaway build on a device before committing to the design:

- What a varispeed unit per voice slot costs when its rate is 1, and whether its `bypass` removes that.
- Whether the voice's end is still reported at the right moment with the unit downstream of the node.
- Whether setting the unit's rate from the volume thread can reach the audio server (it must not block).
- Whether the player node's volume (used today) and pan (for later) still apply with the unit between
  the node and the mixer. If the volume doesn't, the volume moves to the unit's output or the unit
  goes elsewhere, and this design changes.

If a unit per slot is too dear, the fallback is internal: two kinds of slot, and a voice moves to one
with a unit the first time its rate leaves 1 while it's stopped. The public API is the same either way.

Quality: playing a sound faster than recorded can alias (high frequencies fold back as noise). Hermite
interpolation without a filter is fine for effects up to about an octave; the documentation should say
that large upward shifts of bright sounds are better made as separate files.

Tests: the Android mixer already runs in the framework's tests on the desktop. There: a segment at rate
2 ends in half the frames and at 0.5 in twice; a sine at rate 2 has twice the zero crossings; a rate
changed mid-play leaves no jump in the output; a rate outside the range is clamped and NaN throws. The
other platforms are checked by ear and by `IsPlaying`'s timing in the sample game.

## Order of work

1. Framework: the property, the Android mixer (with a gain for each channel) and its tests, web,
   desktop.
2. iOS, after the questions above are answered on a device, pan and volume among them.
3. Toolkit `SoundPool.Play(volume, playbackRate)`, then HotChai.Game's `SoundPlayer`.
4. Guide and architecture pages (`docs/guide/audio.md`, `docs/architecture/audio.md`), API reference.
5. Blockdown: the guide's syllables take their melody from the rate; repeated effects vary by a few
   percent.
