// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Numerics;
using System.Threading.Tasks;
using DotGame.Framework;
using DotGame.Framework.Audio;
using DotGame.Framework.Graphics;
using DotGame.Framework.Input;

namespace Sample.Pages;

/// <summary>
/// Sounds and music. A <see cref="Sound"/> is decoded into memory and played through
/// <see cref="SoundVoice"/>s; a voice plays one instance at a time, so overlapping plays need
/// several. <see cref="Music"/> streams, with an optional loop. Both can be routed through a
/// <see cref="VolumeControl"/>, which sets and fades the volume of everything that uses it.
/// </summary>
internal sealed class AudioPage : SamplePage
{
    // The music's loop: it plays its introduction once, then repeats from the loop point to the
    // end. Positions in samples at 44.1 kHz, converted to time.
    private static readonly TimeSpan LoopPoint = TimeSpan.FromSeconds(88_200 / 44_100.0);
    private static readonly TimeSpan LoopEnd = TimeSpan.FromSeconds(617_400 / 44_100.0);

    private readonly SoundVoice[][] _voices = new SoundVoice[2][];
    private readonly int[] _nextVoice = new int[2];
    private Music _music = null!;
    private VolumeControl _musicVolume = null!;
    private VolumeControl _soundVolume = null!;
    private bool _musicFadedOut;
    private bool _soundsMuted;
    private bool _allFadedOut;

    private RectangleInt _allButton;
    private RectangleInt _sound1Button;
    private RectangleInt _sound2Button;
    private RectangleInt _playButton;
    private RectangleInt _stopButton;
    private RectangleInt _fadeButton;
    private RectangleInt _muteButton;

    public AudioPage(
        FrameworkSample game)
        : base(game)
    {
    }

    public override string Title => "Audio";

    public override string Hint => "Tap a sound quickly: three voices each, so plays overlap.";

    public override async Task Load()
    {
        var game = this.Game;
        this._musicVolume = game.Audio.CreateVolumeControl();
        this._soundVolume = game.Audio.CreateVolumeControl();

        string[] names = ["Sound/boing", "Sound/chime"];
        for (int i = 0; i < names.Length; i++)
        {
            var sound = await game.Content.LoadAsync<Sound>(names[i]);

            // Voices are created up front, so playing allocates nothing
            this._voices[i] = new SoundVoice[3];
            for (int v = 0; v < this._voices[i].Length; v++)
            {
                this._voices[i][v] = game.Audio.CreateVoice(sound);
                this._voices[i][v].VolumeControl = this._soundVolume;
            }
        }

        this._music = await game.Content.LoadAsync<Music>("Music/loop");
        this._music.VolumeControl = this._musicVolume;
    }

    public override void Enter()
    {
        // Preparing buffers the music's start in advance, so Play starts without a delay
        _ = this._music.PrepareAsync(loopPoint: LoopPoint, end: LoopEnd, loop: true);
    }

    public override void Leave()
    {
        this._music.Stop();
        this._music.Unprepare();
    }

    private void PlaySound(
        int index)
    {
        // Round-robin over the sound's voices: each play takes the next one
        var voices = this._voices[index];
        voices[this._nextVoice[index]].Play();
        this._nextVoice[index] = (this._nextVoice[index] + 1) % voices.Length;
    }

    private void ToggleMusic()
    {
        var audio = this.Game.Audio;
        if (this._music.IsPlaying)
        {
            // Pausing keeps the position, for Resume
            this._music.Pause();
        }
        else if (audio.ShouldPlayMusic)
        {
            // ShouldPlayMusic is false on iOS and Android while the player's own music plays
            if (this._music.IsPaused)
            {
                this._music.Resume();
            }
            else
            {
                this._music.Play(loopPoint: LoopPoint, end: LoopEnd, loop: true);
            }
        }
    }

    public override void OnPointerEvent(
        PointerEvent e)
    {
        if (IsTap(e, this._sound1Button))
        {
            this.PlaySound(0);
        }
        else if (IsTap(e, this._sound2Button))
        {
            this.PlaySound(1);
        }
        else if (IsTap(e, this._playButton))
        {
            this.ToggleMusic();
        }
        else if (IsTap(e, this._stopButton))
        {
            this._music.Stop();
        }
        else if (IsTap(e, this._fadeButton))
        {
            // A fade runs on its own; a new one starts from wherever the last one got to
            this._musicFadedOut = !this._musicFadedOut;
            this._musicVolume.FadeTo(this._musicFadedOut ? 0 : 1, TimeSpan.FromSeconds(2), FadeCurve.SCurve);
        }
        else if (IsTap(e, this._muteButton))
        {
            // Setting the volume directly is immediate
            this._soundsMuted = !this._soundsMuted;
            this._soundVolume.Volume = this._soundsMuted ? 0 : 1;
        }
        else if (IsTap(e, this._allButton))
        {
            // Everything is under the master control, whatever its own control is doing
            this._allFadedOut = !this._allFadedOut;
            this.Game.Audio.MasterVolumeControl.FadeTo(this._allFadedOut ? 0 : 1, TimeSpan.FromSeconds(1));
        }
    }

    public override void OnKeyEvent(
        KeyEvent e)
    {
        if ((e.Kind == KeyEventKind.Pressed) && !e.IsRepeat)
        {
            switch (e.Key)
            {
                case Key.A:
                    this.PlaySound(0);
                    break;

                case Key.S:
                    this.PlaySound(1);
                    break;

                case Key.Space:
                    this.ToggleMusic();
                    break;
            }
        }
    }

    public override void Draw(
        SpriteBatch spriteBatch,
        RectangleInt bounds)
    {
        var game = this.Game;
        using var batch = spriteBatch.Begin();

        int x = bounds.X + 16;
        int y = bounds.Y + 16;
        this._sound1Button = new RectangleInt(x, y, 200, 90);
        this._sound2Button = new RectangleInt(x + 216, y, 200, 90);
        game.DrawButton(spriteBatch, this._sound1Button, "Sound 1 (A)", IsAnyPlaying(this._voices[0]));
        game.DrawButton(spriteBatch, this._sound2Button, "Sound 2 (S)", IsAnyPlaying(this._voices[1]));

        y += 120;
        game.Text.Draw(spriteBatch, $"Music: {(this._music.IsPlaying ? "playing" : this._music.IsPaused ? "paused" : "stopped")}", new Vector2(x, y), Color.White, 0.8f);
        game.Text.Draw(spriteBatch, $"Duration {this._music.Duration.TotalSeconds:0.0} s, looping from {LoopPoint.TotalSeconds:0.0} s", new Vector2(x, y + 30), Color.Gray, 0.6f);

        y += 70;
        this._playButton = new RectangleInt(x, y, 200, 56);
        this._stopButton = new RectangleInt(x + 216, y, 120, 56);
        game.DrawButton(spriteBatch, this._playButton, this._music.IsPlaying ? "Pause" : "Play (Space)", this._music.IsPlaying);
        game.DrawButton(spriteBatch, this._stopButton, "Stop");

        y += 72;
        this._fadeButton = new RectangleInt(x, y, 200, 56);
        this._muteButton = new RectangleInt(x + 216, y, 200, 56);
        game.DrawButton(spriteBatch, this._fadeButton, this._musicFadedOut ? "Fade in" : "Fade out");
        game.DrawButton(spriteBatch, this._muteButton, this._soundsMuted ? "Unmute" : "Mute sounds", this._soundsMuted);

        y += 72;
        this._allButton = new RectangleInt(x, y, 416, 56);
        game.DrawButton(spriteBatch, this._allButton, this._allFadedOut ? "Fade everything in" : "Fade everything out", this._allFadedOut);

        // The music's volume as it fades
        y += 80;
        game.Text.Draw(spriteBatch, "Music volume", new Vector2(x, y), Color.Gray, 0.6f);
        game.FillRectangle(spriteBatch, new RectangleInt(x, y + 24, 300, 12), Color.FromRgb(0x3C4664));
        game.FillRectangle(spriteBatch, new RectangleInt(x, y + 24, (int)(300 * this._musicVolume.Volume), 12), Color.FromRgb(0x5AB4F0));
    }

    private static bool IsAnyPlaying(
        SoundVoice[] voices)
    {
        foreach (var voice in voices)
        {
            if (voice.IsPlaying)
            {
                return true;
            }
        }

        return false;
    }
}
