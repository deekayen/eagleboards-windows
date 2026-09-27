#!/usr/bin/env bash
# make-icon.sh -- rebuild EagleBoards.ico from EagleBoards.svg.
#
#   bash scripts/make-icon.sh
#
# The SVG is the source; the .ico is committed so the build needs no drawing
# tools. Run this after editing the SVG. Draws with rsvg-convert (librsvg)
# where it is installed, otherwise with Microsoft Edge or Chrome headless, so
# a Windows machine needs nothing added; and python3 (or python). Each size
# is a PNG entry, which Windows has read since Vista.
#
# The SVG is a copy of eagleboards-shared/artwork/EagleBoards.svg, as the Mac
# version's Artwork/EagleBoards.svg is; keep the three in step.
#
# Fine engraving turns to noise below 256 px, so 16-64 px are drawn from a
# plainer cut of the same SVG, chosen by a stylesheet added to it: bold
# feather splits and no fine texture.
set -euo pipefail

cd "$(dirname "$0")/.."
assets=src/EagleBoards.App/Assets
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

plain='.fine{display:none}.bold{display:inline}.relief{filter:url(#cast-small)}'
# The stylesheet goes just inside the opening <svg> tag on line 1.
sed "1s|>|><style>$plain</style>|" "$assets/EagleBoards.svg" > "$work/plain.svg"
cp "$assets/EagleBoards.svg" "$work/full.svg"

sizes=(16 20 24 32 40 48 64 256)
cut_for() { if [ "$1" -le 64 ]; then echo plain; else echo full; fi; }

# The Microsoft Store's python3 is a stub that only offers to install Python.
python=""
for p in python3 python; do
    if "$p" -c "import zlib" >/dev/null 2>&1; then python=$p; break; fi
done
[ -n "$python" ] || { echo "make-icon.sh: needs python3 (or python)" >&2; exit 1; }

if command -v rsvg-convert >/dev/null 2>&1; then
    for size in "${sizes[@]}"; do
        rsvg-convert -w "$size" -h "$size" "$work/$(cut_for "$size").svg" -o "$work/$size.png"
    done
else
    browser=""
    for b in "/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe" \
             "/c/Program Files/Microsoft/Edge/Application/msedge.exe" \
             "/c/Program Files/Google/Chrome/Application/chrome.exe" \
             "$(command -v microsoft-edge chromium google-chrome 2>/dev/null | head -1)"; do
        if [ -n "$b" ] && [ -x "$b" ]; then browser=$b; break; fi
    done
    [ -n "$browser" ] || { echo "make-icon.sh: needs rsvg-convert, Edge or Chrome" >&2; exit 1; }

    # Headless windows have a minimum size, so every size goes on one page at
    # a fixed place, is photographed once, and is cut out below. The browser
    # lights the cast metal a little paler than librsvg at 16 px.
    dir=$work
    command -v cygpath >/dev/null 2>&1 && dir=$(cygpath -m "$work")
    x=0
    places=()
    {
        echo '<!doctype html><style>html,body{margin:0;background:transparent}img{position:absolute;top:0}</style>'
        for size in "${sizes[@]}"; do
            echo "<img src=\"$(cut_for "$size").svg\" style=\"left:${x}px;width:${size}px;height:${size}px\">"
            places+=("$x")
            x=$((x + size + 8))
        done
    } > "$work/sheet.html"
    # A headless browser that can't draw the page waits rather than exits.
    timeout 120 "$browser" --headless --disable-gpu --hide-scrollbars --force-device-scale-factor=1 \
        --default-background-color=00000000 --user-data-dir="$dir/profile" \
        --window-size="$((x + 8)),300" --screenshot="$dir/sheet.png" "file:///$dir/sheet.html" >/dev/null 2>&1
    [ -s "$work/sheet.png" ] || { echo "make-icon.sh: $browser drew nothing" >&2; exit 1; }

    "$python" - "$work" "${sizes[*]}" "${places[*]}" <<'PY'
import struct, sys, zlib
work, sizes, places = sys.argv[1], [int(s) for s in sys.argv[2].split()], [int(p) for p in sys.argv[3].split()]

def chunks(data):
    pos = 8
    while pos < len(data):
        length, kind = struct.unpack(">I4s", data[pos:pos + 8])
        yield kind, data[pos + 8:pos + 8 + length]
        pos += 12 + length

data = open(f"{work}/sheet.png", "rb").read()
idat = b""
for kind, body in chunks(data):
    if kind == b"IHDR":
        width, height, depth, colour, _, _, interlace = struct.unpack(">IIBBBBB", body)
    elif kind == b"IDAT":
        idat += body
assert (depth, colour, interlace) == (8, 6, 0), "expected a plain 8-bit RGBA PNG"

# Undo the PNG row filters (RFC 2083 6.2) to get the RGBA rows back, as far
# down as the tallest icon reaches.
raw, stride, rows, prev, i = zlib.decompress(idat), width * 4, [], bytearray(width * 4), 0
for _ in range(max(sizes)):
    kind, line = raw[i], bytearray(raw[i + 1:i + 1 + stride])
    i += 1 + stride
    for x in range(stride):
        a = line[x - 4] if x >= 4 else 0
        b = prev[x]
        c = prev[x - 4] if x >= 4 else 0
        if kind == 1:
            line[x] = (line[x] + a) & 255
        elif kind == 2:
            line[x] = (line[x] + b) & 255
        elif kind == 3:
            line[x] = (line[x] + ((a + b) >> 1)) & 255
        elif kind == 4:
            p = a + b - c
            pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
            line[x] = (line[x] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
    rows.append(bytes(line))
    prev = line

def chunk(kind, body):
    return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)

for size, left in zip(sizes, places):
    pixels = b"".join(b"\0" + rows[y][left * 4:(left + size) * 4] for y in range(size))
    png = (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(pixels, 9)) + chunk(b"IEND", b""))
    open(f"{work}/{size}.png", "wb").write(png)
PY
fi

"$python" - "$work" "$assets/EagleBoards.ico" "${sizes[@]}" <<'PY'
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
