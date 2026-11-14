# readme.py <output> <package id> <description> <body> [NAME=value]...: a package's README.md.
#
# The README is the package's name and description, then <body> (a file in packaging/readme, or "" for
# none) with each @NAME@ replaced by its value, then packaging/readme/about.md. @VERSION@ is the
# VERSION environment variable. Fails on a placeholder left unreplaced.
import os
import re
import sys

output, package, description, body = sys.argv[1:5]
values = dict(arg.split("=", 1) for arg in sys.argv[5:])
values["VERSION"] = os.environ["VERSION"]
here = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "readme")


def read(name):
    with open(os.path.join(here, name), encoding="utf-8") as f:
        return f.read().strip() + "\n"


parts = [f"# {package}\n", description + "\n"]
if body:
    parts.append(read(body))
parts.append(read("about.md"))
text = "\n".join(parts)
for name, value in values.items():
    text = text.replace(f"@{name}@", value)
left = re.findall(r"@[A-Z]+@", text)
if left:
    sys.exit(f"readme.py: {package}: unreplaced {', '.join(sorted(set(left)))}")
with open(output, "w", encoding="utf-8", newline="\n") as f:
    f.write(text)
