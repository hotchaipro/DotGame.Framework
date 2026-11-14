# repository.sh: the source repository a package names, for nuget.org's "Source repository" and
# "Project website" links. Sourced by pack-lib.sh and pack-tools.sh.
#
# repository_xml <repository folder> prints the nuspec's <projectUrl> and <repository> elements: the
# repository's GitHub URL, from REPOSITORY_URL or else the folder's origin remote, and the commit it was
# packed from. It prints nothing for a remote that isn't on GitHub (a local folder's path, say), so no
# package names the machine it was packed on.
repository_xml() {
  local url=${REPOSITORY_URL:-$(git -C "$1" remote get-url origin 2>/dev/null || true)}
  case "$url" in
    git@github.com:*) url=https://github.com/${url#git@github.com:} ;;
    https://github.com/*) ;;
    *) return 0 ;;
  esac
  url=${url%.git}
  local commit
  commit=$(git -C "$1" rev-parse HEAD 2>/dev/null || true)
  echo "<projectUrl>$url</projectUrl>"
  echo "    <repository type=\"git\" url=\"$url.git\"${commit:+ commit=\"$commit\"} />"
}
