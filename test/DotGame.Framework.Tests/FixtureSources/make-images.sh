#!/bin/bash
# make-images.sh <work folder>: the fixtures in TestData/Astc and TestData/Ktx2, from images.py's
# procedural images, and the expected values of AstcDecoderTests.
#
# Needs python3 with numpy and Pillow, astcenc 5.7.0 (ASTCENC, default astcenc) and a built content
# build tool (BUILD_TOOL, default the Debug build under pipeline/tool). Prints each .astc file's
# SHA-256 of astcenc's own decode (-dl, as 8-bit RGBA).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../../.." && pwd)
astcenc=${ASTCENC:-astcenc}
tool=${BUILD_TOOL:-$repo/pipeline/tool/bin/Debug/net11.0/DotGame.Framework.ContentPipeline.BuildTool.dll}
mkdir -p "$1" && cd "$1"
mkdir -p img astc ktx2 intermediate
python3 "$here/images.py" img

for n in shape flat painting; do
  for b in 4x4 6x6 8x8; do
    "$astcenc" -cl "img/$n.png" "astc/${n}_$b.astc" $b -thorough -silent
    "$astcenc" -dl "astc/${n}_$b.astc" "astc/${n}_$b.decoded.png" -silent
  done
done
python3 -c "
import glob, hashlib, os
from PIL import Image
for f in sorted(glob.glob('astc/*.decoded.png')):
    print(os.path.basename(f).replace('.decoded.png', '.astc'), hashlib.sha256(Image.open(f).convert('RGBA').tobytes()).hexdigest())
"

# KTX2: the content pipeline's own output for the shape, RGBA8 and ASTC 6x6
dotnet "$tool" texture --format ktx2 --compression Lossless --input img/shape.png --output ktx2/shape_rgba8.ktx2
dotnet "$tool" texture --format ktx2 --compression Lossy --quality 6 --gpu-compression astc --astcenc "$(command -v "$astcenc")" \
  --intermediate-dir intermediate --input img/shape.png --output ktx2/shape_astc6x6.ktx2
