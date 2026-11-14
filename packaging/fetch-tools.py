# fetch-tools.py <rid> <output folder>: a build machine's content tools, from the sources pinned in
# packaging/tool-sources.json, ready for pack-tools.sh.
#
#   python3 packaging/fetch-tools.py osx-arm64 artifacts/tools/osx-arm64
#
# Writes <output>/tools/ (the files the package carries) and <output>/licenses/<tool>.txt (each
# tool's own license file), and <output>/versions.json for the notices. Downloads are checked against
# their pinned SHA-256 and cached in <output>/../downloads. SPIRV-Cross is built from its pinned
# commit with CMake, or its Makefile where there's no CMake. spirv2dxil comes from
# native/spirv2dxil/<rid>, which the native-spirv2dxil workflow builds. Run it on the machine itself:
# each tool is built or downloaded for the machine it runs on.
import hashlib
import json
import os
import shutil
import stat
import subprocess
import sys
import tempfile
import urllib.request
import zipfile

RIDS = ("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-arm64")
rid, out = sys.argv[1], os.path.abspath(sys.argv[2])
if rid not in RIDS:
    sys.exit(f"fetch-tools.py: {rid} isn't a build machine ({', '.join(RIDS)})")
repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
with open(os.path.join(repo, "packaging", "tool-sources.json"), encoding="utf-8") as f:
    sources = json.load(f)
windows = rid.startswith("win-")
exe = ".exe" if windows else ""
tools = os.path.join(out, "tools")
licenses = os.path.join(out, "licenses")
downloads = os.path.join(os.path.dirname(out), "downloads")
shutil.rmtree(out, ignore_errors=True)
for d in (tools, licenses, downloads):
    os.makedirs(d, exist_ok=True)


def download(url, name, sha256):
    path = os.path.join(downloads, name)
    if not os.path.exists(path) or hashlib.sha256(open(path, "rb").read()).hexdigest() != sha256:
        print(f"downloading {url}")
        with urllib.request.urlopen(url) as response, open(path + ".part", "wb") as f:
            shutil.copyfileobj(response, f)
        os.replace(path + ".part", path)
    digest = hashlib.sha256(open(path, "rb").read()).hexdigest()
    if digest != sha256:
        sys.exit(f"fetch-tools.py: {name} has SHA-256 {digest}, not the pinned {sha256}")
    return path


def extract(archive, member, target):
    with zipfile.ZipFile(archive) as z, z.open(member) as src, open(target, "wb") as dst:
        shutil.copyfileobj(src, dst)
    os.chmod(target, os.stat(target).st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)


# Slang: slangc and the libraries it loads (from beside it: its rpath includes its own folder),
# not slang-llvm (107 MB, for host-CPU targets), gfx, slangd or slangi
slang = sources["slang"]
name, sha = slang["files"][rid]
archive = download(slang["url"].format(file=name), name, sha)
with zipfile.ZipFile(archive) as z:
    members = z.namelist()
    # Symbolic links (unversioned names) aren't wanted: slangc and the compiler load the real files' names
    links = {i.filename for i in z.infolist() if (i.external_attr >> 16) & 0o170000 == 0o120000}
wanted = [f"bin/slangc{exe}"]
for m in members:
    base = m.rsplit("/", 1)[-1]
    if windows and m.startswith("bin/") and base.endswith(".dll") and base.startswith("slang") and "llvm" not in base:
        wanted.append(m)
    elif not windows and m.startswith("lib/") and base.startswith("libslang") and "llvm" not in base and m not in links:
        wanted.append(m)
for m in wanted:
    extract(archive, m, os.path.join(tools, m.rsplit("/", 1)[-1]))
license = next((m for m in members if m.rsplit("/", 1)[-1] == "LICENSE"), None)
if license:
    extract(archive, license, os.path.join(licenses, "slang.txt"))

def checkout(name, repository, commit):
    # Outside the repository: CMake's Visual Studio probe runs MSBuild, which would pick up its Directory.Build files
    src = os.path.join(tempfile.gettempdir(), f"dotgame-{name}-{commit[:12]}")
    if not os.path.isdir(os.path.join(src, ".git")):
        shutil.rmtree(src, ignore_errors=True)
        subprocess.run(["git", "init", "-q", src], check=True)
        subprocess.run(["git", "-C", src, "fetch", "-q", "--depth", "1", repository, commit], check=True)
    subprocess.run(["git", "-C", src, "checkout", "-q", "--force", commit], check=True)
    return src


# astcenc: the variant that runs on every CPU of the machine (SSE2 on x64, NEON on arm64; macOS's is universal).
# Arm's release builds, but on Linux built from the release's commit (see tool-sources.json).
astc = sources["astcenc"]
if rid in astc["build"]:
    isa = astc["build"][rid]
    src = checkout("astcenc", astc["repository"], astc["commit"])
    b = os.path.join(src, "build")
    shutil.rmtree(b, ignore_errors=True)
    subprocess.run(["cmake", "-S", src, "-B", b, "-DCMAKE_BUILD_TYPE=Release", f"-DASTCENC_ISA_{isa}=ON",
                    "-DASTCENC_WERROR=OFF", "-DCMAKE_EXE_LINKER_FLAGS=-static-libstdc++ -static-libgcc"],
                   check=True, stdout=subprocess.DEVNULL)
    subprocess.run(["cmake", "--build", b, "--parallel"], check=True, stdout=subprocess.DEVNULL)
    shutil.copy(os.path.join(b, "Source", "astcenc-" + isa.lower()), os.path.join(tools, "astcenc"))
    subprocess.run(["strip", os.path.join(tools, "astcenc")], check=True)
else:
    name, sha, member = astc["files"][rid]
    archive = download(astc["url"].format(file=name), name, sha)
    extract(archive, member, os.path.join(tools, "astcenc" + exe))

# SPIRV-Cross, from its pinned commit
sc = sources["spirv-cross"]
src = checkout("spirv-cross", sc["repository"], sc["commit"])
built = None
if shutil.which("cmake"):
    b = os.path.join(src, "build")
    subprocess.run(["cmake", "-S", src, "-B", b, "-DCMAKE_BUILD_TYPE=Release", "-DSPIRV_CROSS_CLI=ON",
                    "-DSPIRV_CROSS_ENABLE_TESTS=OFF", "-DSPIRV_CROSS_SHARED=OFF", "-DSPIRV_CROSS_STATIC=ON",
                    "-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded"], check=True, stdout=subprocess.DEVNULL)
    subprocess.run(["cmake", "--build", b, "--config", "Release", "--target", "spirv-cross", "--parallel"],
                   check=True, stdout=subprocess.DEVNULL)
    built = next(os.path.join(r, f) for r, _, fs in os.walk(b) for f in fs if f == "spirv-cross" + exe)
elif not windows:
    subprocess.run(["make", "-C", src, "-j", str(os.cpu_count() or 4)], check=True, stdout=subprocess.DEVNULL)
    built = os.path.join(src, "spirv-cross")
else:
    sys.exit("fetch-tools.py: building SPIRV-Cross on Windows needs CMake")
shutil.copy(built, os.path.join(tools, "spirv-cross" + exe))
if not windows:
    # Its symbols: about a third of the binary, and no use in a package
    subprocess.run(["strip", os.path.join(tools, "spirv-cross")], check=True)
if rid == "osx-arm64":
    # Arm's macOS build is universal: only this machine's half is wanted. Each half keeps its own signature.
    subprocess.run(["lipo", os.path.join(tools, "astcenc"), "-thin", "arm64", "-output", os.path.join(tools, "astcenc")], check=True)
shutil.copy(os.path.join(src, "LICENSE"), os.path.join(licenses, "spirv-cross.txt"))

# spirv2dxil, built by the native-spirv2dxil workflow
d = os.path.join(repo, sources["spirv2dxil"]["from"].format(rid=rid))
binary = os.path.join(d, "spirv2dxil" + exe)
if not os.path.exists(binary):
    sys.exit(f"fetch-tools.py: no spirv2dxil in {d}")
shutil.copy(binary, os.path.join(tools, "spirv2dxil" + exe))

for f in os.listdir(tools):
    p = os.path.join(tools, f)
    os.chmod(p, os.stat(p).st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)
with open(os.path.join(out, "versions.json"), "w", encoding="utf-8") as f:
    json.dump({"rid": rid, **{k: {"version": v["version"], "project": v["project"], "license": v["license"]}
                              for k, v in sources.items() if not k.startswith("_")}}, f, indent=2)
print(f"{rid}: {', '.join(sorted(os.listdir(tools)))}")
