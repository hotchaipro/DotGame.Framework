# Where the test fixtures come from

Every file in `../TestData` is made by the scripts here from synthesized or procedural sources:
tones, noise, filters and shapes computed from numbers with fixed seeds. No recording, artwork or
other third-party material is in them, so they're covered by this repository's MIT license.

| Fixtures | Made by | From |
|---|---|---|
| `TestData/Opus` | `make-audio.sh` (ffmpeg with libopus 1.6.1) | `synth.py`: synthesized music (chords, bass, a plucked melody, noise hi-hats), speech-like formant synthesis (no one's voice), and the two in one stream |
| `TestData/Astc`, `TestData/Ktx2` | `make-images.sh` (astcenc 5.7.0, the content build tool) | `images.py`: three 61 x 45 images, a shape with alpha, flat art and a painterly noise field |

The expected values in the tests come from reference implementations, not from the code under
test: ASTC hashes from astcenc's own decode. The Opus files are compared with DotGame.AudioCodecs's
decode of the whole file, a decoder that's checked against native libopus in its own repository.

```sh
test/DotGame.Framework.Tests/FixtureSources/make-audio.sh <work folder>
test/DotGame.Framework.Tests/FixtureSources/make-images.sh <work folder>
```
