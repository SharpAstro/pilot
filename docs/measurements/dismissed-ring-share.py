"""Share of rings dismissed, from the node's card log (pilot #799's Done-when: under 10% of rings dismissed).

Usage:  pilot cards --all --output-format plain | python -I dismissed-ring-share.py [--from-id 1700]

Reads the plain list of `pilot cards --all`: a ring line is `c<id>  RING  <title>` with a trailing
`[closed: <outcome>, by <who>]` once closed. Counts the closed rings whose outcome starts `dismissed`
over all rings from the given card id on. Read it once before the release (59 of 295, 20.0% from c1700),
and once a week after: that is the "after" share the Done-when asks for.
"""
import re
import sys

from_id = int(sys.argv[sys.argv.index("--from-id") + 1]) if "--from-id" in sys.argv else 0
line_re = re.compile(r"^c(\d+)\s+RING\s+(.*)$")
rings = dismissed = 0
for line in sys.stdin:
    m = line_re.match(line.rstrip("\n"))
    if not m or int(m.group(1)) < from_id:
        continue
    rings += 1
    if re.search(r"\[closed: dismissed", m.group(2)):
        dismissed += 1
print(f"{dismissed} of {rings} rings from c{from_id} dismissed ({100 * dismissed / rings:.1f}%)" if rings else "no rings")
