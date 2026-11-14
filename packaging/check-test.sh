#!/bin/bash
# check-test.sh [folder of .nupkg]: check.sh catches what it's for. From a copy of the DotGame.Framework
# package (which passes), each case plants one problem and expects check.sh to fail, naming it.
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

# plant <case> <expect pass|fail> <message check.sh must print, or ""> <python statements on z (a ZipFile
# to write) and src (the source)>
plant() {
  local name=$1 expect=$2 message=$3 code=$4
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
  output=$("$REPO/packaging/check.sh" "$T/case" 2>&1); status=$?
  if { [ $expect = pass ] && [ $status -eq 0 ]; } || { [ $expect = fail ] && [ $status -ne 0 ]; }; then
    if [ -z "$message" ] || grep -qF "$message" <<< "$output"; then echo "PASS  $name"; pass=$((pass+1)); return; fi
  fi
  echo "FAIL  $name"; echo "$output" | head -5 | sed 's/^/        /'; fail=$((fail+1))
}

plant "the package as built passes" pass "" "copy()"
plant "an unlisted file fails" fail "doesn't allow: lib/net11.0/Extra.dll" "
copy()
z.writestr('lib/net11.0/Extra.dll', b'MZ')"
plant "a listed file in the wrong folder fails" fail "doesn't allow: build/tool/DotGame.Framework.dll" "
copy()
z.writestr('build/tool/DotGame.Framework.dll', src.read('lib/net11.0/DotGame.Framework.dll'))"
plant "a package with no contents section fails" fail "no section for DotGame.Unknown" "
copy(['DotGame.Framework.nuspec'])
z.writestr('DotGame.Framework.nuspec', src.read('DotGame.Framework.nuspec').replace(b'<id>DotGame.Framework</id>', b'<id>DotGame.Unknown</id>'))"

echo "$pass passed, $fail failed"
[ $fail -eq 0 ]