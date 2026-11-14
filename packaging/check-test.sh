#!/bin/bash
# check-test.sh [folder of .nupkg]: check.sh catches what it's for. From a copy of the DotGame.Framework
# package (which passes), each case plants one problem and expects check.sh to fail, naming it, or plants
# something allowed and expects it to pass. The names are placeholders, in a names file written here.
#
#   packaging/check-test.sh artifacts/packages
set -uo pipefail
REPO=$(cd "$(dirname "$0")/.." && pwd)
FOLDER=${1:-$REPO/artifacts/packages}
SOURCE=$(ls "$FOLDER"/DotGame.Framework.[0-9]*.nupkg | head -1)
PY=$(command -v python3 || command -v python)
T=$(mktemp -d)
trap 'rm -rf "$T"' EXIT
pass=0; fail=0
unset DOTGAME_FORBIDDEN_NAMES_FILE DOTGAME_CHECK_DRY_RUN

NAMES=$T/names.txt
cat > "$NAMES" <<'TXT'
# A private name (which is also the owner's), and a build machine's user
zorbo
jdoe
# The copyright holder, and the owner's public repositories
allow zorbo games inc
allow zorbo/public\.
TXT

# plant <case> <expect pass|fail> <message check.sh must print, or ""> <python statements on z (a ZipFile
# to write) and src (the source)> [check.sh's options, by default --names]
plant() {
  local name=$1 expect=$2 message=$3 code=$4
  local options=("${@:5}")
  [ $# -ge 5 ] || options=(--names "$NAMES")
  rm -rf "$T/case" && mkdir -p "$T/case"
  "$PY" -c "
import zipfile, sys
src = zipfile.ZipFile(sys.argv[1])
out = sys.argv[2]
z = zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED)
def copy(skip=()):
    for e in src.namelist():
        if e not in skip:
            z.writestr(e, src.read(e))
$code
z.close()
" "$SOURCE" "$T/case/$(basename "$SOURCE")"
  output=$("$REPO/packaging/check.sh" ${options[@]+"${options[@]}"} "$T/case" 2>&1); status=$?
  if { [ $expect = pass ] && [ $status -eq 0 ]; } || { [ $expect = fail ] && [ $status -ne 0 ]; }; then
    if [ -z "$message" ] || grep -qF "$message" <<< "$output"; then echo "PASS  $name"; pass=$((pass+1)); return; fi
  fi
  echo "FAIL  $name"; echo "$output" | head -5 | sed 's/^/        /'; fail=$((fail+1))
}

plant "the package as built passes" pass "" "copy()"
plant "the copyright holder's name is allowed" pass "" "
copy(['README.md'])
z.writestr('README.md', src.read('README.md') + b'\nCopyright (c) 2026 Zorbo Games Inc.\n')"
plant "a public repository's URL is allowed" pass "" "
copy(['README.md'])
z.writestr('README.md', src.read('README.md') + b'\nhttps://github.com/zorbo/Public.Library\n')"
plant "a private repository's URL fails" fail "README.md (utf-8)" "
copy(['README.md'])
z.writestr('README.md', src.read('README.md') + b'\nhttps://github.com/zorbo/Private.Library\n')"
plant "a planted name in a file fails" fail "README.md (utf-8)" "
copy(['README.md'])
z.writestr('README.md', src.read('README.md') + b'\nSee Zorbo.Example.Engine for more.\n')"
plant "an allowed name beside a forbidden one still fails" fail "README.md (utf-8)" "
copy(['README.md'])
z.writestr('README.md', src.read('README.md') + b'\nZorbo Games Inc. and Zorbo.Example\n')"
plant "a planted name in UTF-16, as an assembly holds it, fails" fail "(utf-16-le)" "
copy(['lib/net11.0/DotGame.Framework.xml'])
z.writestr('lib/net11.0/DotGame.Framework.xml', src.read('lib/net11.0/DotGame.Framework.xml') + 'Zorbo.Example.Tests'.encode('utf-16-le'))"
plant "a planted build path naming the user fails" fail "README.md (utf-8)" "
copy(['README.md'])
z.writestr('README.md', src.read('README.md') + b'\n/Users/jdoe/Documents/GitHub/x.cs\n')"
plant "a planted file name naming the private side fails" fail "a file's name" "
copy()
z.writestr('lib/net11.0/zorbo.txt', b'x')"
plant "an unlisted file fails" fail "doesn't allow: lib/net11.0/Extra.dll" "
copy()
z.writestr('lib/net11.0/Extra.dll', b'MZ')"
plant "a listed file in the wrong folder fails" fail "doesn't allow: build/tool/DotGame.Framework.dll" "
copy()
z.writestr('build/tool/DotGame.Framework.dll', src.read('lib/net11.0/DotGame.Framework.dll'))"
plant "a package with no contents section fails" fail "no section for DotGame.Unknown" "
copy(['DotGame.Framework.nuspec'])
z.writestr('DotGame.Framework.nuspec', src.read('DotGame.Framework.nuspec').replace(b'<id>DotGame.Framework</id>', b'<id>DotGame.Unknown</id>'))"
plant "with no names file, the check fails" fail "no names file" "copy()" --contents "$REPO/packaging/contents.txt"
plant "a dry run checks only the contents, and says so" pass "a dry run" "
copy(['README.md'])
z.writestr('README.md', src.read('README.md') + b'\nZorbo.Example\n')" --dry-run
plant "a dry run still fails an unlisted file" fail "doesn't allow: lib/net11.0/Extra.dll" "
copy()
z.writestr('lib/net11.0/Extra.dll', b'MZ')" --dry-run
printf '# nothing\nallow anything\n' > "$T/empty.txt"
plant "a names file that forbids nothing fails" fail "forbids nothing" "copy()" --names "$T/empty.txt"
output=$(DOTGAME_FORBIDDEN_NAMES_FILE=$NAMES "$REPO/packaging/check.sh" "$T/case" 2>&1)
if [ $? -eq 0 ] && grep -q "0 problems" <<< "$output"; then echo "PASS  the names file can come from DOTGAME_FORBIDDEN_NAMES_FILE"; pass=$((pass+1)); else echo "FAIL  the names file can come from DOTGAME_FORBIDDEN_NAMES_FILE"; fail=$((fail+1)); fi

echo "$pass passed, $fail failed"
[ $fail -eq 0 ]
