#!/usr/bin/env bash
# make-icon.sh -- rebuild EagleBoards.ico from EagleBoards.svg.
#
#   bash scripts/make-icon.sh
#
# The SVG is the source; the .ico is committed so the build needs no drawing
# tools. Run this after editing the SVG. Needs rsvg-convert (librsvg) and
# python3. Each size is a PNG entry, which Windows has read since Vista.
set -euo pipefail

cd "$(dirname "$0")/.."
assets=src/EagleBoards.App/Assets
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

sizes=(16 20 24 32 40 48 64 256)
for size in "${sizes[@]}"; do
    rsvg-convert -w "$size" -h "$size" "$assets/EagleBoards.svg" -o "$work/$size.png"
done

python3 - "$work" "$assets/EagleBoards.ico" "${sizes[@]}" <<'PY'
import struct, sys
work, out, sizes = sys.argv[1], sys.argv[2], [int(s) for s in sys.argv[3:]]
images = [open(f"{work}/{s}.png", "rb").read() for s in sizes]
header = struct.pack("<HHH", 0, 1, len(images))
offset = len(header) + 16 * len(images)
entries = b""
for size, data in zip(sizes, images):
    dim = 0 if size >= 256 else size  # 0 means 256 in an ICO directory
    entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
    offset += len(data)
with open(out, "wb") as f:
    f.write(header + entries + b"".join(images))
PY
echo "wrote $assets/EagleBoards.ico"
