# Audio

There are two kinds of audio. A [`Sound`](../api/Framework/Sound.md) is decoded into memory
and plays immediately, for effects. [`Music`](../api/Framework/Music.md) streams as it plays, for
long tracks. Add them to your project:

```xml
<ItemGroup>
  <SoundContent Include="Content\Sound\*.wav" />
  <MusicContent Include="Content\Music\*.flac" />
</ItemGroup>
```

The pipeline encodes each for the platform, so you use the same code everywhere.

## Sounds

A sound plays through a [`SoundVoice`](../api/Framework/SoundVoice.md). Create voices up front, so
that playing allocates nothing:

```csharp
protected override async Task OnStart()
{
    var sound = await this.Content.LoadAsync<Sound>("Sound/jump");
    this.jumpVoice = this.Audio.CreateVoice(sound);
}

private void Jump()
{
    this.jumpVoice.Play();
}
```

A voice plays one copy at a time, and playing it again restarts it. For overlapping copies, make
several voices and take turns:

```csharp
protected override async Task OnStart()
{
    var sound = await this.Content.LoadAsync<Sound>("Sound/coin");
    for (int i = 0; i < this.coinVoices.Length; i++)
    {
        this.coinVoices[i] = this.Audio.CreateVoice(sound);
    }
}

private void PlayCoin()
{
    this.coinVoices[this.nextCoinVoice].Play();
    this.nextCoinVoice = (this.nextCoinVoice + 1) % this.coinVoices.Length;
}
```

## Music

```csharp
protected override async Task OnStart()
{
    this.music = await this.Content.LoadAsync<Music>("Music/theme");
    this.music.Play(loop: true);
}
```

`Pause` keeps the position for `Resume`, and `Stop` doesn't.

### Loops with an intro

Music loops with no gap on every platform. For a track with an intro that plays once, give the point
to loop back to, and the end of the loop if it isn't the end of the file:

```csharp
this.music.Play(
    loopPoint: TimeSpan.FromSeconds(0.857),
    end: TimeSpan.FromSeconds(14.571),
    loop: true);
```

### Starting without a delay

Streaming music has to fetch and decode its start. To start exactly on time, on a game event for
example, prepare it in advance with the same arguments. `Unprepare` releases the buffers if you don't
play it after all:

```csharp
private async Task GetReadyToPlay()
{
    await this.music.PrepareAsync(loop: true);
}

private void StartMusicNow()
{
    this.music.Play(loop: true);
}
```

[`IsPlaying`](../api/Framework/Music.md#isplaying) is `true` from the `Play` call, and
[`IsPaused`](../api/Framework/Music.md#ispaused) from the `Pause` call, even while the music is still
loading: they say what you asked for. Music that's loading starts by itself, unless you paused it. To
know it's ready, await `PrepareAsync`.

### Going to the background

You don't need to do anything: music pauses when the game is [hidden](game-loop.md#pausing-resuming-and-stopping)
and resumes on return. Music that plays while the game is only inactive keeps playing.

## Volume and fades

Voices and music play through a [`VolumeControl`](../api/Framework/VolumeControl.md). Make one for
each kind of audio your settings offer. Everything is also under
[`Audio.MasterVolumeControl`](../api/Framework/AudioService.md#mastervolumecontrol):

```csharp
protected override async Task OnStart()
{
    this.musicVolume = this.Audio.CreateVolumeControl();
    this.effectsVolume = this.Audio.CreateVolumeControl();

    var music = await this.Content.LoadAsync<Music>("Music/theme");
    music.VolumeControl = this.musicVolume;

    var sound = await this.Content.LoadAsync<Sound>("Sound/jump");
    var voice = this.Audio.CreateVoice(sound);
    voice.VolumeControl = this.effectsVolume;
}

private void ApplySettings(float musicLevel, bool muted)
{
    // Setting the volume is immediate
    this.musicVolume.Volume = musicLevel;
    this.effectsVolume.Volume = muted ? 0 : 1;
}

private void FadeOutForLevelEnd()
{
    // A fade runs on its own, and a new one starts from wherever the last one got to
    this.musicVolume.FadeTo(0, TimeSpan.FromSeconds(2), FadeCurve.SCurve);
}
```

Give [`CreateVolumeControl`](../api/Framework/AudioService.md#createvolumecontrol) a parent when you make a
control, so you can have a group
for the level's sounds under the effects control. Every volume in the chain multiplies, and a control
without a parent is under `MasterVolumeControl`. Dispose a control after what's under it: what's still
under it goes silent.

## Higher, lower, faster, slower

A voice's [`PlaybackRate`](../api/Framework/SoundVoice.md#playbackrate) is how fast it plays its
sound, as a multiple of the recorded speed. 1 is as recorded; 2 is twice as fast, an octave higher and
half as long; 0.5 is half as fast, an octave lower and twice as long. The pitch and the length always
change together. The rate stays on the voice until you change it, and a change while the voice plays
is heard at once. It's clamped to 0.25 to 4.

A sound that repeats (a footstep, a coin) tires the ear at one pitch. A few percent of difference from
one play to the next is enough:

```csharp
private void PlayCoin()
{
    var voice = this.coinVoices[this.nextCoinVoice];
    voice.PlaybackRate = 0.96f + (0.08f * Random.Shared.NextSingle());
    voice.Play();
    this.nextCoinVoice = (this.nextCoinVoice + 1) % this.coinVoices.Length;
}
```

For a musical step, a rate of `MathF.Pow(2f, semitones / 12f)` is that many semitones up (or down, for
a negative number): one recorded note can play a tune.

Playing a sound much faster than it was recorded can add a harsh edge to a bright sound; if you need
it more than about an octave up, record it higher. On iOS, the first time a voice's rate leaves 1 the
voice moves to another player, which cuts off what it was playing, so set the rate before you play.
`Music` has no rate.

## Looping a sound

Play a voice with `Play(loop: true)` for an engine's hum or ambience. It plays until you call `Stop`. For
long tracks, use `Music`.

## The player's own music

On a phone, the player may be listening to their own music. Check
[`Audio.ShouldPlayMusic`](../api/Framework/AudioService.md#shouldplaymusic) before starting
yours, and on iOS choose how the game's audio mixes with
[`AudioCategory`](../api/iOS/AudioCategory.md):

```csharp
private void StartMusic()
{
    if (this.Audio.ShouldPlayMusic)
    {
        this.music.Play(loop: true);
    }
}
```

To start your music once theirs ends, read the same property in `OnUpdate` until it's clear. On iOS and
Android, music already playing is paused for other audio and carries on after it by itself.

## Audio on the web

A browser won't play audio until the player has interacted with the page. Start music as you normally
would, and the framework plays it after the player's first gesture. A sound played before that gesture
is dropped, as it would be late by then.

## See also

- [`AudioService`](../api/Framework/AudioService.md), [`Music`](../api/Framework/Music.md),
  [`SoundVoice`](../api/Framework/SoundVoice.md)
- The framework sample's Audio page
