#!/usr/bin/env bash
#
# Installs the tools used by the DotGame content pipeline on macOS and Linux:
#
#   astcenc      ASTC texture encoding for Android and iOS (Arm's astc-encoder)
#   slangc       Slang shader compiler
#   spirv-cross  SPIR-V to MSL / GLSL cross-compiler
#
# DXIL for Direct3D 12 comes from Mesa's spirv2dxil, which is checked in under
# native/spirv2dxil/ for every build machine, so it needs no install.
#
# WebP textures are encoded by the build tool itself (DotGame.Imaging), so there's no cwebp. On Linux,
# unzip, curl and a C++ compiler come from the distribution's package manager
# (apt, dnf, pacman or zypper; uses sudo). The shader tools are installed per
# user, without sudo, into ${XDG_DATA_HOME:-~/.local/share}/DotGame/ContentPipelineTools
# with links in ~/.local/bin: slangc and astcenc are pinned, checksum-verified
# release downloads, and spirv-cross is built from pinned source with the
# system C++ compiler (no distribution packages it consistently). On macOS,
# astcenc is the same pinned release, linked into ~/.local/bin too.
#
# Tools that are already on PATH are left alone. Re-running the script is safe.

set -euo pipefail

VULKAN_SDK_VERSION_MACOS=1.4.341.1

SLANG_VERSION=2026.19
SLANG_SHA256_X64=bd7afcb0956dee9304f44ed115e9aaa7140704271b5c3294bcb237e3ae67adc7
SLANG_SHA256_ARM64=f49229eb9606b47b122e1d8789be46a7bcd6bb2ffa94b06fae6d544a4e6df637

# astcenc names its executable for the instruction set it uses: astcenc-avx2 on
# x64, astcenc-neon on arm64, astcenc (universal) on macOS, as ExternalTools.props
# expects. Its output is the same from every one.
ASTCENC_VERSION=5.7.0
ASTCENC_SHA256_LINUX_X64=6c6b299b17608e1ee74db9c491cdc793b12b2b104a4b52222e358d78ae0662a9
ASTCENC_SHA256_LINUX_ARM64=43f2544127f40340768e7cac317df74101a0b0e30619fe68c6e2628ceaa91692
ASTCENC_SHA256_MACOS=374b2f0aea1d3ab5f849784666d6ece6e27b4bc1f945e5c90d6eeabf00e955f8

SPIRV_CROSS_TAG=vulkan-sdk-1.4.363.0
SPIRV_CROSS_SHA256=f708982e88b763ef5b0394ed468fc2c3628f68cdab89f2e7a946cd628c04e721

info() { printf '\033[36m%s\033[0m\n' "$*"; }
warn() { printf '\033[33mwarning: %s\033[0m\n' "$*" >&2; }
fail() { printf '\033[31merror: %s\033[0m\n' "$*" >&2; exit 1; }
have() { command -v "$1" >/dev/null 2>&1; }

# ─── macOS ───

# slangc and spirv-cross: no Homebrew formula holds both, and the Vulkan SDK does
install_vulkan_sdk_macos() {
    if have slangc && have spirv-cross; then
        info "shader tools: already installed"
        return
    fi
    local version=$VULKAN_SDK_VERSION_MACOS
    local root="$HOME/VulkanSDK/$version"
    info "Installing the Vulkan SDK $version in $root"
    curl -fL --retry 3 --progress-bar -o "$WORK_DIR/vulkansdk.zip" \
        "https://sdk.lunarg.com/sdk/download/$version/mac/vulkansdk-macos-$version.zip"
    unzip -q "$WORK_DIR/vulkansdk.zip" -d "$WORK_DIR/vulkansdk"
    local installer
    installer="$(find "$WORK_DIR/vulkansdk" -type f -path '*/Contents/MacOS/*' | head -1)"
    [ -n "$installer" ] || fail "the Vulkan SDK download holds no installer."
    "$installer" --root "$root" --accept-licenses --default-answer --confirm-command install
    local tool
    for tool in slangc spirv-cross; do
        [ -x "$root/macOS/bin/$tool" ] || fail "the Vulkan SDK has no $tool."
        link_tool "$root/macOS/bin/$tool" "$tool"
    done
}

install_macos() {
    have brew || fail "Homebrew is required (https://brew.sh)."
    WORK_DIR="$(mktemp -d)"
    trap 'rm -rf "$WORK_DIR"' EXIT
    install_vulkan_sdk_macos
    install_astcenc
    check_path
}

# ─── Linux ───

TOOLS_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/DotGame/ContentPipelineTools"
BIN_DIR="$HOME/.local/bin"
SUDO=""

# Returns success when the tool should be installed by this script: it is
# missing, or it is a link this script created earlier (so pinned version
# updates are applied on re-run).
needs_tool() {
    local path
    path="$(command -v "$1" 2>/dev/null)" || return 0
    if [ "$path" = "$BIN_DIR/$1" ] && [ -L "$path" ]; then
        return 0
    fi
    info "$1: already installed ($path)"
    return 1
}

install_packages() {
    local need_media=0 need_cxx=0
    have unzip || need_media=1
    if needs_tool spirv-cross >/dev/null && ! have c++; then
        need_cxx=1
    fi

    if [ $need_media -eq 0 ] && [ $need_cxx -eq 0 ] && have curl; then
        info "unzip, curl and a C++ compiler: already installed"
        return
    fi

    if [ "$(id -u)" -ne 0 ]; then
        have sudo || fail "sudo is required to install packages (or run this script as root)."
        SUDO=sudo
    fi

    local packages=()
    if have apt-get; then
        [ $need_media -eq 1 ] && packages+=(unzip)
        [ $need_cxx -eq 1 ] && packages+=(g++)
        have curl || packages+=(curl)
        info "Installing packages with apt-get: ${packages[*]}"
        $SUDO apt-get update
        $SUDO apt-get install -y "${packages[@]}"
    elif have dnf; then
        [ $need_media -eq 1 ] && packages+=(unzip)
        [ $need_cxx -eq 1 ] && packages+=(gcc-c++)
        have curl || packages+=(curl)
        info "Installing packages with dnf: ${packages[*]}"
        $SUDO dnf install -y "${packages[@]}"
    elif have pacman; then
        [ $need_media -eq 1 ] && packages+=(unzip)
        [ $need_cxx -eq 1 ] && packages+=(gcc)
        have curl || packages+=(curl)
        info "Installing packages with pacman: ${packages[*]}"
        $SUDO pacman -S --needed --noconfirm "${packages[@]}"
    elif have zypper; then
        [ $need_media -eq 1 ] && packages+=(unzip)
        [ $need_cxx -eq 1 ] && packages+=(gcc-c++)
        have curl || packages+=(curl)
        info "Installing packages with zypper: ${packages[*]}"
        $SUDO zypper --non-interactive install "${packages[@]}"
    else
        fail "no supported package manager (apt-get, dnf, pacman, zypper) found. Install unzip, curl and a C++ compiler, then re-run this script."
    fi
}

# download <url> <sha256> <file>
download() {
    info "Downloading $1"
    curl -fL --retry 3 --progress-bar -o "$3" "$1"
    local actual
    if have sha256sum; then
        actual="$(sha256sum "$3" | cut -d' ' -f1)"
    else
        actual="$(shasum -a 256 "$3" | cut -d' ' -f1)"
    fi
    [ "$actual" = "$2" ] || fail "checksum mismatch for $1 (expected $2, got $actual)."
}

link_tool() {
    mkdir -p "$BIN_DIR"
    ln -sfn "$1" "$BIN_DIR/$2"
    info "$2: installed ($1)"
}

install_slang() {
    needs_tool slangc || return 0
    local arch sha
    case "$(uname -m)" in
        x86_64 | amd64) arch=x86_64; sha=$SLANG_SHA256_X64 ;;
        aarch64 | arm64) arch=aarch64; sha=$SLANG_SHA256_ARM64 ;;
        *) warn "slangc: no prebuilt Slang for $(uname -m); install it from https://github.com/shader-slang/slang/releases"; return ;;
    esac
    local dir="$TOOLS_DIR/slang-$SLANG_VERSION"
    if [ ! -x "$dir/bin/slangc" ]; then
        download "https://github.com/shader-slang/slang/releases/download/v$SLANG_VERSION/slang-$SLANG_VERSION-linux-$arch-glibc-2.28.tar.gz" "$sha" "$WORK_DIR/slang.tar.gz"
        rm -rf "$dir" && mkdir -p "$dir"
        tar -xzf "$WORK_DIR/slang.tar.gz" -C "$dir"
    fi
    link_tool "$dir/bin/slangc" slangc
}

install_astcenc() {
    local name asset sha
    case "$(uname -s)-$(uname -m)" in
        Darwin-*) name=astcenc; asset=macos-universal; sha=$ASTCENC_SHA256_MACOS ;;
        Linux-x86_64 | Linux-amd64) name=astcenc-avx2; asset=linux-x64; sha=$ASTCENC_SHA256_LINUX_X64 ;;
        Linux-aarch64 | Linux-arm64) name=astcenc-neon; asset=linux-arm64; sha=$ASTCENC_SHA256_LINUX_ARM64 ;;
        *) warn "astcenc: no prebuilt astcenc for $(uname -s) $(uname -m); install it from https://github.com/ARM-software/astc-encoder/releases"; return ;;
    esac
    needs_tool "$name" || return 0
    local dir="$TOOLS_DIR/astcenc-$ASTCENC_VERSION"
    if [ ! -x "$dir/bin/$name" ]; then
        download "https://github.com/ARM-software/astc-encoder/releases/download/$ASTCENC_VERSION/astcenc-$ASTCENC_VERSION-$asset.zip" "$sha" "$WORK_DIR/astcenc.zip"
        rm -rf "$dir" && mkdir -p "$dir"
        unzip -q "$WORK_DIR/astcenc.zip" -d "$dir"
        chmod +x "$dir/bin/"*
    fi
    link_tool "$dir/bin/$name" "$name"
}

install_spirv_cross() {
    needs_tool spirv-cross || return 0
    local dir="$TOOLS_DIR/spirv-cross-$SPIRV_CROSS_TAG"
    if [ ! -x "$dir/bin/spirv-cross" ]; then
        have c++ || fail "a C++ compiler (c++) is required to build spirv-cross."
        download "https://github.com/KhronosGroup/SPIRV-Cross/archive/refs/tags/$SPIRV_CROSS_TAG.tar.gz" "$SPIRV_CROSS_SHA256" "$WORK_DIR/spirv-cross.tar.gz"
        tar -xzf "$WORK_DIR/spirv-cross.tar.gz" -C "$WORK_DIR"
        local src="$WORK_DIR/SPIRV-Cross-$SPIRV_CROSS_TAG"
        info "Building spirv-cross $SPIRV_CROSS_TAG"
        local sources=(main spirv_cfg spirv_cpp spirv_cross spirv_cross_parsed_ir spirv_cross_util spirv_glsl spirv_hlsl spirv_msl spirv_parser spirv_reflect)
        printf '%s\n' "${sources[@]}" |
            xargs -P "$(nproc 2>/dev/null || echo 4)" -I{} c++ -O2 -std=c++11 -c "$src/{}.cpp" -o "$src/{}.o"
        mkdir -p "$dir/bin"
        local objects=() s
        for s in "${sources[@]}"; do objects+=("$src/$s.o"); done
        c++ -o "$dir/bin/spirv-cross" "${objects[@]}"
    fi
    link_tool "$dir/bin/spirv-cross" spirv-cross
}

check_path() {
    case ":$PATH:" in
        *":$BIN_DIR:"*) ;;
        *)
            if [ -d "$BIN_DIR" ]; then
                echo
                warn "add $BIN_DIR to PATH (for example in ~/.profile), then open a new shell:"
                echo "    export PATH=\"$BIN_DIR:\$PATH\""
            fi
            ;;
    esac
}

install_linux() {
    install_packages
    WORK_DIR="$(mktemp -d)"
    trap 'rm -rf "$WORK_DIR"' EXIT
    install_slang
    install_spirv_cross
    install_astcenc
    check_path
}

case "$(uname -s)" in
    Darwin) install_macos ;;
    Linux) install_linux ;;
    *) fail "unsupported OS: $(uname -s). On Windows, run install.cmd." ;;
esac

info "Content pipeline tools are installed."