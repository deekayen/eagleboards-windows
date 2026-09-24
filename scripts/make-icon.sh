#!/usr/bin/env bash
# make-icon.sh -- rebuild EagleBoards.ico from EagleBoards.svg.
#
#   bash scripts/make-icon.sh
#
# The SVG is the source; the .ico is committed so the build needs no drawing
# tools. Run this after editing the SVG. Needs rsvg-convert (librsvg) and
# python3. Each size is a PNG entry, which Windows has read since Vista.
#
# The Mac version (deekayen/eagleboards-macos) draws its icon from a copy of
# the same SVG, Artwork/EagleBoards.svg; keep the two in step.
#
# Fine engraving turns to noise below 256 px, so smaller sizes are drawn
# from a plainer cut of the same SVG, chosen by a stylesheet added to it:
#   40-64 px  bold feather splits and B S A, no fine texture
#   16-32 px  the same without the letters, which can't be read that small
set -euo pipefail

cd "$(dirname "$0")/.."
assets=src/EagleBoards.App/Assets
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

medium='.fine{display:none}.bold{display:inline}.relief{filter:url(#cast-small)}'
small='.fine,.letters{display:none}.bold{display:inline}.relief{filter:url(#cast-small)}'
# The stylesheet goes just inside the opening <svg> tag on line 1.
sed "1s|>|><style>$medium</style>|" "$assets/EagleBoards.svg" > "$work/medium.svg"
sed "1s|>|><style>$small</style>|" "$assets/EagleBoards.svg" > "$work/small.svg"

sizes=(16 20 24 32 40 48 64 256)
for size in "${sizes[@]}"; do
    if [ "$size" -le 32 ]; then svg="$work/small.svg"
    elif [ "$size" -le 64 ]; then svg="$work/medium.svg"
    else svg="$assets/EagleBoards.svg"
    fi
    rsvg-convert -w "$size" -h "$size" "$svg" -o "$work/$size.png"
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
