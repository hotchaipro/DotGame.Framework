# Third-party notices

DotGame.Framework is licensed under the MIT License (see `LICENSE`). It includes or
redistributes the third-party software below, under the licenses shown. The imaging library,
DotGame.Imaging, and the audio codecs, DotGame.AudioCodecs (whose Opus codec is a port of
libopus), are repositories of their own, with their own notices.

## SDL 3.4.4

- Source: https://github.com/libsdl-org/SDL
- License: zlib
- Used in: the desktop platform (prebuilt libraries in `native/sdl3`)

```
Copyright (C) 1997-2026 Sam Lantinga <slouken@libsdl.org>
  
This software is provided 'as-is', without any express or implied
warranty.  In no event will the authors be held liable for any damages
arising from the use of this software.

Permission is granted to anyone to use this software for any purpose,
including commercial applications, and to alter it and redistribute it
freely, subject to the following restrictions:
  
1. The origin of this software must not be misrepresented; you must not
   claim that you wrote the original software. If you use this software
   in a product, an acknowledgment in the product documentation would be
   appreciated but is not required. 
2. Altered source versions must be plainly marked as such, and must not be
   misrepresented as being the original software.
3. This notice may not be removed or altered from any source distribution.
```

## HIDAPI (bundled in SDL)

- Source: https://github.com/libusb/hidapi
- License: HIDAPI original license (chosen from its three licenses)
- Used in: the SDL libraries

```
 HIDAPI - Multi-Platform library for
 communication with HID devices.

 Copyright 2009, Alan Ott, Signal 11 Software.
 All Rights Reserved.
 
 This software may be used by anyone for any reason so
 long as the copyright notice in the source files
 remains intact.
```

## yuv2rgb (bundled in SDL)

- Source: https://github.com/descampsa/yuv2rgb
- License: BSD-3-Clause
- Used in: the SDL libraries

```
Copyright (c) 2016, Adrien Descamps
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

* Neither the name of yuv2rgb nor the names of its
  contributors may be used to endorse or promote products derived from
  this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## SDL3-CS 3.4.18

- Source: https://github.com/edwardgushchin/SDL3-CS
- License: zlib
- Used in: the desktop platform (NuGet package)

```
Copyright (C) 2024-2026 Eduard Gushchin <eduardgushchin@yandex.ru>
  
This software is provided 'as-is', without any express or implied
warranty.  In no event will the authors be held liable for any damages
arising from the use of this software.

Permission is granted to anyone to use this software for any purpose,
including commercial applications, and to alter it and redistribute it
freely, subject to the following restrictions:
  
1. The origin of this software must not be misrepresented; you must not
   claim that you wrote the original software. If you use this software
   in a product, an acknowledgment in the product documentation would be
   appreciated but is not required. 
2. Altered source versions must be plainly marked as such, and must not be
   misrepresented as being the original software.
3. This notice may not be removed or altered from any source distribution.
```

## Mesa 26.2.6 (spirv2dxil)

- Source: https://gitlab.freedesktop.org/mesa/mesa (tag `mesa-26.2.6`), with the two patches in
  `native/spirv2dxil/patches`
- License: MIT (the spirv-to-dxil, NIR, SPIR-V and utility sources it's built from; see Mesa's
  `docs/license.rst`)
- Used in: the content pipeline's DXIL step (prebuilt `spirv2dxil` in `native/spirv2dxil`, built by
  `native/spirv2dxil/build.ps1`)

```
Copyright © Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a
copy of this software and associated documentation files (the "Software"),
to deal in the Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the
Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice (including the next
paragraph) shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.  IN NO EVENT SHALL
THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
IN THE SOFTWARE.
```

## DirectX Shader Compiler: DxilHash.cpp

- Source: https://github.com/microsoft/DirectXShaderCompiler (`lib/DxilHash/DxilHash.cpp`)
- License: University of Illinois/NCSA Open Source License
- Used in: the content build tool, ported to C# (`pipeline/tool/Graphics/DxilContainerHash.cs`), to sign DXIL

```
DxilHash.cpp: Copyright (C) Microsoft Corporation. All rights reserved.

University of Illinois/NCSA
Open Source License

Copyright (c) 2003-2015 University of Illinois at Urbana-Champaign.
All rights reserved.

Developed by:

    LLVM Team

    University of Illinois at Urbana-Champaign

    http://llvm.org

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal with
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do
so, subject to the following conditions:

    * Redistributions of source code must retain the above copyright notice,
      this list of conditions and the following disclaimers.

    * Redistributions in binary form must reproduce the above copyright notice,
      this list of conditions and the following disclaimers in the
      documentation and/or other materials provided with the distribution.

    * Neither the names of the LLVM Team, University of Illinois at
      Urbana-Champaign, nor the names of its contributors may be used to
      endorse or promote products derived from this Software without specific
      prior written permission.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.  IN NO EVENT SHALL THE
CONTRIBUTORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS WITH THE
SOFTWARE.
```

## .NET wasmbrowser template

- Source: https://github.com/dotnet/runtime (the `wasmbrowser` project template)
- License: MIT (.NET Foundation and Contributors)
- Used in: the WebGL platform's startup script (`platform/webgl/wwwroot/_dotgame/main.js`), which
  follows the template

```
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Not redistributed

- **Build and test only:** Roslyn (Microsoft.CodeAnalysis.CSharp), Microsoft.CodeAnalysis.BannedApiAnalyzers,
  Microsoft.Build.Utilities.Core, xunit and the .NET Test SDK, all MIT. They are build-time
  dependencies and are not copied into any game.
- **External tools** the content pipeline runs, installed by `pipeline/install.sh` or
  `install.cmd` or by the developer when building from this repository:
  astcenc (Apache-2.0), slangc (Apache-2.0 with LLVM exception) and SPIRV-Cross (Apache-2.0).
  None is part of this repository. The packages' `DotGame.Framework.Build.Tools.<rid>` carry them,
  with their own notices.
