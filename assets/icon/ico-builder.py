#!/usr/bin/env python3
"""Assemble a Windows PNG-embedded .ico from a list of PNG files.

ICO format reference: https://en.wikipedia.org/wiki/ICO_(file_format)

PNG-embedded entries (Vista+) are valid for any size and required for >=256.
Width/height bytes are 1-byte fields, so 256 is encoded as 0.

Usage:
    ico-builder.py <output.ico> <png1> [png2 ...]
"""

import struct
import sys
from pathlib import Path


def main(argv: list[str]) -> int:
    if len(argv) < 3:
        print("usage: ico-builder.py <output.ico> <png1> [png2 ...]", file=sys.stderr)
        return 2

    out_path = Path(argv[1])
    png_paths = [Path(p) for p in argv[2:]]
    if not png_paths:
        print("at least one PNG is required", file=sys.stderr)
        return 2

    images: list[tuple[int, bytes]] = []
    for p in png_paths:
        data = p.read_bytes()
        if data[:8] != b"\x89PNG\r\n\x1a\n":
            print(f"{p}: not a PNG", file=sys.stderr)
            return 1
        # PNG IHDR is the first chunk after the signature; width is bytes 16..20.
        width = struct.unpack(">I", data[16:20])[0]
        images.append((width, data))

    images.sort(key=lambda t: t[0])
    count = len(images)
    header = struct.pack("<HHH", 0, 1, count)

    entry_size = 16
    data_offset = len(header) + count * entry_size
    entries = bytearray()
    blobs = bytearray()

    for width, data in images:
        # 1-byte width/height fields; 256 wraps to 0 by spec.
        w_byte = 0 if width >= 256 else width
        h_byte = w_byte
        entries += struct.pack(
            "<BBBBHHII",
            w_byte,         # width
            h_byte,         # height
            0,              # palette colors (0 for true color)
            0,              # reserved
            1,              # color planes
            32,             # bits per pixel
            len(data),      # bytes in data
            data_offset,    # offset to PNG data
        )
        blobs += data
        data_offset += len(data)

    out_path.write_bytes(header + bytes(entries) + bytes(blobs))
    print(f"wrote {out_path} ({count} sizes, {out_path.stat().st_size} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
