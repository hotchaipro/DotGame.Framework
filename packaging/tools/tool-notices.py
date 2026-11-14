# tool-notices.py <output> <package id> <fetched folder> <tools folder>: THIRD-PARTY-NOTICES.md for a
# build machine's tool package.
#
# From what packaging/fetch-tools.py fetched: each tool's version, project and license
# (<fetched>/versions.json, from packaging/tool-sources.json) and its own license file
# (<fetched>/licenses/<tool>.txt) where its source has one. Mesa's section is the repository's. The
# Apache-2.0 and LLVM-exception texts (packaging/licenses) end the file. Fails on a file in the tools
# folder that it has no notice for, so nothing is shipped unlisted.
import json
import os
import re
import sys

output, package, fetched, tools = sys.argv[1:5]
here = os.path.dirname(os.path.abspath(__file__))
repo = os.path.dirname(os.path.dirname(here))
with open(os.path.join(fetched, "versions.json"), encoding="utf-8") as f:
    versions = json.load(f)


def text(path):
    with open(path, encoding="utf-8", errors="replace") as f:
        return f.read().replace("\r\n", "\n").strip("\n") + "\n"


def license_file(tool):
    path = os.path.join(fetched, "licenses", tool + ".txt")
    return text(path) if os.path.exists(path) else None


def mesa():
    lines = text(os.path.join(repo, "THIRD-PARTY-NOTICES.md")).split("\n")
    start = next(i for i, line in enumerate(lines) if line.startswith("## Mesa"))
    end = next(i for i in range(start + 1, len(lines)) if lines[i].startswith("## "))
    return "\n".join(lines[start:end]).rstrip() + "\n"


def entry(title, source, license, used, body=None):
    s = f"## {title}\n\n- Source: {source}\n- License: {license}\n- Used in: {used}\n"
    if body:
        s += f"\n```\n{body}```\n"
    return s


def tool(key, title, used):
    v = versions[key]
    return entry(f"{title} {v['version']}", v["project"], v["license"], used, license_file(key))


files = sorted(f for _, _, names in os.walk(tools) for f in names)
known = re.compile(r"^(slangc|spirv-cross|spirv2dxil|astcenc)(\.exe)?$|^(lib)?slang[\w.\-]*\.(dll|dylib|so[\d.]*)$")
unlisted = [f for f in files if not known.match(f)]
if unlisted:
    sys.exit(f"tool-notices.py: no notice for {', '.join(unlisted)}")

parts = [
    "# Third-party notices\n",
    f"{package} redistributes the third-party tools below, which DotGame's content pipeline runs at build "
    "time, under the licenses shown. Nothing in it ships in a game. The Apache-2.0 license and its LLVM "
    "exception are at the end.\n",
    tool("slang", "Slang", "`slangc` and the `slang` libraries it loads"),
    tool("spirv-cross", "SPIRV-Cross", "`spirv-cross`"),
    mesa(),
    tool("astcenc", "astcenc", "`astcenc`"),
]
parts += [
    "## Apache License 2.0\n\n```\n" + text(os.path.join(repo, "packaging", "licenses", "Apache-2.0.txt")) + "```\n",
    "## LLVM exception to the Apache License 2.0\n\n```\n"
    + text(os.path.join(repo, "packaging", "licenses", "LLVM-exception.txt")) + "```\n",
]
with open(output, "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(parts))
