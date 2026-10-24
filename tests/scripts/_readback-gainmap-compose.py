#!/usr/bin/env python
"""Readback helpers for verify-gainmap-compose.ps1 (stdio only, no files created).

Modes:
  mpf  <ultrahdr.jpg>
      Parse the MPF APP2 index and prove that the secondary (gain map) image is a
      real JPEG reachable by its own offset: SOI at offset, EOI at offset+size,
      and offset+size == EOF (appended after the primary, as Ultra HDR requires).
      Prints key=value lines; exit 0 only when all structural checks pass.

  psnr <reference.rgb> <test.rgb>
      PSNR (8-bit, peak=255) between two raw rgb24 planes of identical size.
      Prints PSNR=<dB>; exit 2 on size mismatch / missing file.

Why a separate file: the gate needs an *independent* MPF walking (exiftool only
reports "NumberOfImages"), and the base-fidelity number must come from the same
byte planes the gate decoded, not from an encoder self-report.
"""
import math
import struct
import sys


def read_bytes(path):
    with open(path, "rb") as f:
        return f.read()


def fail(msg):
    print("ERROR=" + msg)
    return 1


def parse_int(b, off, n, endian):
    return int.from_bytes(b[off:off + n], endian)


def cmd_mpf(path):
    b = read_bytes(path)
    i = b.find(b"MPF\x00")
    if i < 0:
        return fail("no MPF APP2 segment")
    tiff = i + 4
    if b[tiff:tiff + 2] not in (b"II", b"MM"):
        return fail("MPF TIFF header missing")
    endian = "little" if b[tiff:tiff + 2] == b"II" else "big"
    ifd_off = parse_int(b, tiff + 4, 4, endian)
    ifd = tiff + ifd_off
    if ifd + 2 > len(b):
        return fail("MP index IFD out of range")
    n = parse_int(b, ifd, 2, endian)

    mp_off = mp_count = None
    for k in range(n):
        e = ifd + 2 + k * 12
        if e + 12 > len(b):
            return fail("MP index IFD entry out of range")
        tag = parse_int(b, e, 2, endian)
        typ = parse_int(b, e + 2, 2, endian)
        cnt = parse_int(b, e + 4, 4, endian)
        val = parse_int(b, e + 8, 4, endian)
        if tag == 0xB002:          # MPEntry
            if typ != 7 or cnt % 16 != 0:
                return fail("MPEntry has unexpected type/count")
            mp_count, mp_off = cnt, val

    if mp_count is None or mp_off is None:
        return fail("MPEntry tag 0xB002 absent")
    if mp_count < 32:
        return fail("MPF declares fewer than two images")

    imgs = []
    base = tiff + mp_off
    for k in range(mp_count // 16):
        e = base + k * 16
        if e + 16 > len(b):
            return fail("MPEntry array out of range")
        # MPEntry fields are always big-endian, independent of the TIFF endianness.
        attr, size, off = struct.unpack(">III", b[e:e + 12])
        imgs.append((attr, size, off))

    prim_off, prim_size = imgs[0][2], imgs[0][1]
    sec_off, sec_size = imgs[1][2], imgs[1][1]
    sec_abs = tiff + sec_off
    ok = True
    print("MPFImages=%d" % len(imgs))
    print("PrimarySize=%d" % prim_size)
    print("SecondaryOffset=%d" % sec_abs)
    print("SecondarySize=%d" % sec_size)
    # The primary entry carries offset 0 (it sits at the start of the MP stream).
    print("PrimaryOffsetZero=%d" % (1 if prim_off == 0 else 0))
    ok &= prim_off == 0
    if sec_abs < 0 or sec_abs + sec_size > len(b):
        print("SecondaryInRange=0")
        return 1
    print("SecondaryInRange=1")
    soi = b[sec_abs:sec_abs + 2] == b"\xff\xd8"
    eoi = b[sec_abs + sec_size - 2:sec_abs + sec_size] == b"\xff\xd9"
    tail = sec_abs + sec_size == len(b)
    print("SecondarySOI=%d" % (1 if soi else 0))
    print("SecondaryEOI=%d" % (1 if eoi else 0))
    print("SecondaryAtTail=%d" % (1 if tail else 0))
    ok &= soi and eoi and tail
    return 0 if ok else 1


def cmd_psnr(ref_path, test_path):
    a, b = read_bytes(ref_path), read_bytes(test_path)
    if len(a) != len(b) or len(a) == 0:
        print("ERROR=size mismatch %d vs %d" % (len(a), len(b)))
        return 2
    se = 0
    for x, y in zip(a, b):
        d = x - y
        se += d * d
    mse = se / float(len(a))
    db = 99.0 if mse == 0 else 10.0 * math.log10(255.0 * 255.0 / mse)
    print("PSNR=%.2f" % db)
    return 0


def main(argv):
    if len(argv) >= 2 and argv[0] == "mpf":
        return cmd_mpf(argv[1])
    if len(argv) >= 3 and argv[0] == "psnr":
        return cmd_psnr(argv[1], argv[2])
    print("usage: _readback-gainmap-compose.py mpf <file.jpg> | psnr <ref.rgb> <test.rgb>")
    return 3


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
