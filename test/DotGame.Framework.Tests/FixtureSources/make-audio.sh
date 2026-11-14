#!/bin/bash
# make-audio.sh <work folder>: the Ogg Opus fixtures in TestData/Opus, made from synthesized sources:
# what OpusMusicReaderTests streams, seeks and decodes. DotGame.AudioCodecs checks its decoder against
# libopus itself, so these are only files libopus encoded, of every kind music and sounds can be.
#
# Needs python3 with numpy (synth.py) and ffmpeg with libopus.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
mkdir -p "$1" && cd "$1"
mkdir -p wav opus
python3 "$here/synth.py" wav

B=(-hide_banner -loglevel error -y)
F=(-fflags +bitexact -map_metadata -1)

# Opus: CELT (music), SILK (narrow and wide band, 60 ms), hybrid, mono and stereo, 2.5 to 60 ms
# frames, and a stream that switches between hybrid and CELT
enc() { out=$1; src=$2; shift 2; ffmpeg "${B[@]}" -i "wav/$src" -c:a libopus "$@" "${F[@]}" "opus/$out.opus"; }
enc celt-stereo-112k music-st-4s.wav -application audio -b:a 112k
enc celt-mono-64k music-mono-3s.wav -application audio -b:a 64k
enc celt-2_5ms music-st-2s.wav -application audio -b:a 128k -frame_duration 2.5
enc celt-60ms music-st-2s.wav -application audio -b:a 96k -frame_duration 60
enc hybrid-mono speech-mono.wav -application voip -b:a 32k
enc hybrid-stereo speech-music-st-3s.wav -application audio -b:a 40k
enc silk-nb-mono speech-mono.wav -application voip -b:a 8k -cutoff 4000
enc silk-wb-mono speech-mono.wav -application voip -b:a 16k -cutoff 8000
enc silk-stereo speech-st.wav -application voip -b:a 20k -cutoff 8000
enc silk-60ms speech-mono.wav -application voip -b:a 12k -cutoff 8000 -frame_duration 60
