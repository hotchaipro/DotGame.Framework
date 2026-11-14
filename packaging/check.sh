#!/bin/bash
# check.sh [--names <file> | --dry-run] [--contents <file>]... <folder or .nupkg>...: fails if a package
# holds a file that its contents list doesn't allow, or names anything the names file forbids.
#
#   packaging/check.sh --names names.txt artifacts/packages
#
# Contents: every file in a package must match a line of a contents file for that package (by default
# packaging/contents.txt; --contents, as often as needed, replaces it), and every package must have a
# section there. A new file in a package is then a reviewed diff.
#
# Names: every file in every package is searched, as UTF-8 and as UTF-16 (the strings in an assembly),
# ignoring case, for the patterns in the names file: --names, or DOTGAME_FORBIDDEN_NAMES_FILE. The
# list is kept by whoever publishes the packages, not in this repository. In it, each line is a regular
# expression to forbid, or "allow <expression>" for text a forbidden pattern may appear in (such as
# a copyright holder's name or a repository's URL); "#" starts a comment. Without a names file the
# check fails, unless --dry-run (or DOTGAME_CHECK_DRY_RUN=1) says only the contents are checked.
#
# packaging/check-test.sh checks that the checks fail when they should.
set -euo pipefail
PY=$(command -v python3 || command -v python)
exec "$PY" - "$(dirname "$0")/contents.txt" "$@" <<'PY'
import fnmatch, os, re, sys, zipfile

default_contents, args = sys.argv[1], sys.argv[2:]
names_file = os.environ.get("DOTGAME_FORBIDDEN_NAMES_FILE", "")
dry_run = os.environ.get("DOTGAME_CHECK_DRY_RUN", "") not in ("", "0")
contents_files, inputs = [], []
i = 0
while i < len(args):
    if args[i] == "--names":
        names_file, i = args[i + 1], i + 2
    elif args[i] == "--contents":
        contents_files.append(args[i + 1]); i += 2
    elif args[i] == "--dry-run":
        dry_run, i = True, i + 1
    elif args[i].startswith("--"):
        sys.exit(f"check.sh: unknown option {args[i]}")
    else:
        inputs.append(args[i]); i += 1
if not inputs:
    sys.exit("usage: check.sh [--names <file> | --dry-run] [--contents <file>]... <folder or .nupkg>...")
contents_files = contents_files or [default_contents]

forbidden, allowed = [], []
if names_file:
    with open(names_file, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            if line.startswith("allow "):
                allowed.append(re.compile(line[len("allow "):].strip(), re.IGNORECASE))
            else:
                forbidden.append(re.compile(line, re.IGNORECASE))
    if not forbidden:
        sys.exit(f"check.sh: {names_file} forbids nothing")
elif dry_run:
    print("check.sh: a dry run: the contents are checked, but not the names (no names file)")
else:
    sys.exit("check.sh: no names file (--names, or DOTGAME_FORBIDDEN_NAMES_FILE); --dry-run checks only the contents")


def pattern(glob):
    # "**" across folders, "*" within one, "{a,b}" either; everything else as written
    out, i = "", 0
    while i < len(glob):
        if glob.startswith("**", i):
            out, i = out + ".*", i + 2
        elif glob[i] == "*":
            out, i = out + "[^/]*", i + 1
        elif glob[i] == "{":
            end = glob.index("}", i)
            out, i = out + "(?:" + "|".join(re.escape(p) for p in glob[i + 1:end].split(",")) + ")", end + 1
        else:
            out, i = out + re.escape(glob[i]), i + 1
    return re.compile(out + r"\Z")


sections = []
for contents_file in contents_files:
    current = None
    with open(contents_file, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            if line.startswith("[") and line.endswith("]") and not line.startswith("[Content_Types]"):
                current = (line[1:-1], [])
                sections.append(current)
            else:
                current[1].append(pattern(line))


def named(text):
    """The forbidden matches in text that no allowed match covers: (start, end) each."""
    spans = [m.span() for a in allowed for m in a.finditer(text)]
    found = []
    for f in forbidden:
        for m in f.finditer(text):
            if not any(s <= m.start() and m.end() <= e for s, e in spans):
                found.append(m.span())
    return found


packages = []
for path in inputs:
    if os.path.isdir(path):
        packages += sorted(os.path.join(path, f) for f in os.listdir(path) if f.endswith(".nupkg"))
    else:
        packages.append(path)

problems = 0
for path in packages:
    name = os.path.basename(path)
    with zipfile.ZipFile(path) as package:
        entries = package.namelist()
        nuspec = next(e for e in entries if e.endswith(".nuspec") and "/" not in e)
        package_id = re.search(r"<id>([^<]+)</id>", package.read(nuspec).decode("utf-8")).group(1)
        mine = [s for s in sections if s[0] != "*" and fnmatch.fnmatchcase(package_id, s[0])]
        if not mine:
            print(f"{name}: no section for {package_id} in {', '.join(contents_files)}")
            problems += 1
        rules = [p for s in sections if s[0] == "*" or s in mine for p in s[1]]
        for entry in entries:
            if entry.endswith("/"):
                continue
            if not any(rule.match(entry) for rule in rules):
                print(f"{name}: a file the contents list doesn't allow: {entry}")
                problems += 1
            if not forbidden:
                continue
            if named(entry):
                print(f"{name}: a file's name: {entry}")
                problems += 1
            data = package.read(entry)
            for encoding in ("utf-8", "utf-16-le"):
                text = data.decode(encoding, errors="ignore")
                for start, end in named(text):
                    print(f"{name}: {entry} ({encoding}): ...{text[max(0, start - 40):end + 40]!r}...")
                    problems += 1
print(f"{len(packages)} packages checked, {problems} problems")
sys.exit(1 if problems else 0)
PY
