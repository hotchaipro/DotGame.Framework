# notices.py <output> <package id> <section>...: a package's THIRD-PARTY-NOTICES.md, from the
# repository's notices files.
#
# Each section is "<notices file>#<heading>": the "## <heading>" section of that file (the repository's
# THIRD-PARTY-NOTICES.md, relative to it), up to the next "## " heading. A heading
# matches by its start, so "SDL 3" finds "## SDL 3.4.4". With no sections, the file says the package
# includes no third-party software. Fails if a section isn't found, so a renamed one can't drop out quietly.
import os
import sys

output, package, sections = sys.argv[1], sys.argv[2], sys.argv[3:]
repo = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def section(path, heading):
    with open(os.path.join(repo, path), encoding="utf-8") as f:
        lines = f.read().split("\n")
    for i, line in enumerate(lines):
        if line.startswith("## " + heading):
            end = next((j for j in range(i + 1, len(lines)) if lines[j].startswith("## ")), len(lines))
            return "\n".join(lines[i:end]).rstrip() + "\n"
    sys.exit(f"notices.py: no section '{heading}' in {path}")


parts = [f"# Third-party notices\n"]
if sections:
    parts.append(f"{package} is licensed under the MIT License. It includes or redistributes the "
                 "third-party software below, under the licenses shown.\n")
    for spec in sections:
        if spec.startswith("="):
            # A paragraph as written, such as a credit line a license asks for
            parts.append(spec[1:] + "\n")
            continue
        path, heading = spec.split("#", 1)
        parts.append(section(path, heading))
else:
    parts.append(f"{package} is licensed under the MIT License and includes no third-party software.\n")
with open(output, "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(parts))
