"""NRO control data for the tModLoader launcher, generated at build time.

tmod_nacp() rebuilds the NACP the tModLoader builds have always used: devkitPro's
`nacptool --create <title> Workshop 46A` (12 language slots, its default save-data sizes,
rating ages and logo fields), with the title then written to all 16 language slots the way
build_tmod_modset_nro.py retitles it. tmod_nacp("tModLoader NX 09 (Zero-Mod)") is
byte-identical to the engineering cache's tmod09 tmodloader.nacp.

write_icon() makes the 256x256 icon from the tModLoader logo that ships in the downloaded
tModLoader release (Libraries/Native/tModLoader256.bmp): the 32-bit BMP is composited onto
black and written as a baseline JPEG (4:4:4, IJG quality 90). The logo is tModLoader's, so it
is produced on the user's machine and never committed.

Stdlib only; same inputs give the same bytes (integer colour conversion and DCT).
"""
from __future__ import annotations

import struct
from pathlib import Path

NACP_SIZE = 0x4000
NACP_AUTHOR = "Workshop"
NACP_VERSION = "46A"
RELEASE_ICON = "Libraries/Native/tModLoader256.bmp"
ICON_SIZE = 256
ICON_QUALITY = 90

# nacptool --create: the fields it sets besides title/author/version (offsets into the NACP).
_NACPTOOL_FIELDS = (
    (0x3025, bytes([1])),                       # StartupUserAccount: required
    (0x302C, struct.pack("<I", 0x0BFF)),        # SupportedLanguageFlag
    (0x3036, bytes([1])),                       # DataLossConfirmation
    (0x3040, bytes([12, 0xFF, 0xFF, 10, 0xFF, 12, 12, 12, 12, 12, 13, 13]).ljust(0x20, b"\xff")),  # RatingAge
    (0x3080, struct.pack("<Q", 0x03E00000)),    # UserAccountSaveDataSize
    (0x3088, struct.pack("<Q", 0x00180000)),    # UserAccountSaveDataJournalSize
    (0x30F0, bytes([2, 1])),                    # LogoType, LogoHandling
)
_NACPTOOL_LANGUAGES = 12


def tmod_nacp(title: str, author: str = NACP_AUTHOR, version: str = NACP_VERSION) -> bytes:
    title_bytes, author_bytes = title.encode(), author.encode()
    if len(title_bytes) >= 0x200 or len(author_bytes) >= 0x100 or len(version.encode()) >= 0x10:
        raise ValueError("NACP title, author or version too long")
    nacp = bytearray(NACP_SIZE)
    for language in range(16):
        start = language * 0x300
        nacp[start:start + 0x200] = title_bytes.ljust(0x200, b"\0")
        if language < _NACPTOOL_LANGUAGES:
            nacp[start + 0x200:start + 0x300] = author_bytes.ljust(0x100, b"\0")
    for offset, value in _NACPTOOL_FIELDS:
        nacp[offset:offset + len(value)] = value
    nacp[0x3060:0x3070] = version.encode().ljust(0x10, b"\0")
    return bytes(nacp)


def write_nacp(out: Path, title: str) -> Path:
    out.write_bytes(tmod_nacp(title))
    return out


def _read_bmp_over_black(path: Path) -> tuple[int, int, list[tuple[int, int, int]]]:
    data = path.read_bytes()
    if data[:2] != b"BM":
        raise ValueError(f"not a BMP file: {path}")
    pixel_offset, header_size = struct.unpack_from("<II", data, 10)
    width, height, _planes, bpp, compression = struct.unpack_from("<iiHHI", data, 18)
    if bpp != 32 or compression not in (0, 3):
        raise ValueError(f"unsupported BMP format (bpp {bpp}, compression {compression}): {path}")
    if compression == 3:
        if header_size >= 56:
            masks = struct.unpack_from("<4I", data, 54)
        else:
            masks = struct.unpack_from("<3I", data, 14 + header_size) + (0,)
    else:
        masks = (0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000 if header_size >= 56 else 0)

    def channel(mask: int) -> tuple[int, int]:
        shift = (mask & -mask).bit_length() - 1
        return shift, mask >> shift

    (rs, rm), (gs, gm), (bs, bm) = channel(masks[0]), channel(masks[1]), channel(masks[2])
    alpha = channel(masks[3]) if masks[3] else None
    rows = abs(height)
    pixels = []
    for row in range(rows):
        source = row if height < 0 else rows - 1 - row
        base = pixel_offset + source * width * 4
        for value in struct.unpack_from(f"<{width}I", data, base):
            r = ((value >> rs) & rm) * 255 // rm
            g = ((value >> gs) & gm) * 255 // gm
            b = ((value >> bs) & bm) * 255 // bm
            if alpha:
                a = ((value >> alpha[0]) & alpha[1]) * 255 // alpha[1]
                r, g, b = ((c * a + 127) // 255 for c in (r, g, b))
            pixels.append((r, g, b))
    return width, rows, pixels


_ZIGZAG = (
    0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
    12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
    35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
    58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
)
# ITU T.81 Annex K example tables, natural (row-major) order.
_LUMA_QUANT = (
    16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55,
    14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
    18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92,
    49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
)
_CHROMA_QUANT = (
    17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99,
    24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
    *(99,) * 32,
)
_DC_LUMA = ((0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0), tuple(range(12)))
_DC_CHROMA = ((0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0), tuple(range(12)))
_AC_LUMA = ((0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D), bytes.fromhex(
    "01020300041105122131410613516107227114328191a1082342b1c11552d1f02433627282090a161718191a25262728292a3435363738393a"
    "434445464748494a535455565758595a636465666768696a737475767778797a838485868788898a92939495969798999aa2a3a4a5a6a7a8a9"
    "aab2b3b4b5b6b7b8b9bac2c3c4c5c6c7c8c9cad2d3d4d5d6d7d8d9dae1e2e3e4e5e6e7e8e9eaf1f2f3f4f5f6f7f8f9fa"))
_AC_CHROMA = ((0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77), bytes.fromhex(
    "000102031104052131061241510761711322328108144291a1b1c109233352f0156272d10a162434e125f11718191a262728292a35363738"
    "393a434445464748494a535455565758595a636465666768696a737475767778797a82838485868788898a92939495969798999aa2a3a4a5a6"
    "a7a8a9aab2b3b4b5b6b7b8b9bac2c3c4c5c6c7c8c9cad2d3d4d5d6d7d8d9dae2e3e4e5e6e7e8e9eaf2f3f4f5f6f7f8f9fa"))
# Orthonormal 8-point DCT-II basis scaled by 2^13: round(c(u)/2 * cos((2x+1)u*pi/16) * 8192).
_DCT = (
    (2896, 2896, 2896, 2896, 2896, 2896, 2896, 2896),
    (4017, 3406, 2276, 799, -799, -2276, -3406, -4017),
    (3784, 1567, -1567, -3784, -3784, -1567, 1567, 3784),
    (3406, -799, -4017, -2276, 2276, 4017, 799, -3406),
    (2896, -2896, -2896, 2896, 2896, -2896, -2896, 2896),
    (2276, -4017, 799, 3406, -3406, -799, 4017, -2276),
    (1567, -3784, 3784, -1567, -1567, 3784, -3784, 1567),
    (799, -2276, 3406, -4017, 4017, -3406, 2276, -799),
)


def _scaled_quant(table: tuple[int, ...], quality: int) -> tuple[int, ...]:
    scale = 5000 // quality if quality < 50 else 200 - 2 * quality
    return tuple(min(255, max(1, (q * scale + 50) // 100)) for q in table)


def _huffman_codes(spec: tuple[tuple[int, ...], bytes | tuple[int, ...]]) -> dict[int, tuple[int, int]]:
    counts, symbols = spec
    codes, code, k = {}, 0, 0
    for length, count in enumerate(counts, 1):
        for _ in range(count):
            codes[symbols[k]] = (code, length)
            code += 1
            k += 1
        code <<= 1
    return codes


def _round_div(value: int, divisor: int) -> int:
    return (value + divisor // 2) // divisor if value >= 0 else -((-value + divisor // 2) // divisor)


class _BitWriter:
    def __init__(self) -> None:
        self.out = bytearray()
        self.acc = 0
        self.bits = 0

    def write(self, code: int, length: int) -> None:
        self.acc = (self.acc << length) | code
        self.bits += length
        while self.bits >= 8:
            self.bits -= 8
            byte = (self.acc >> self.bits) & 0xFF
            self.out.append(byte)
            if byte == 0xFF:
                self.out.append(0)
        self.acc &= (1 << self.bits) - 1

    def flush(self) -> bytes:
        if self.bits:
            self.write((1 << (8 - self.bits)) - 1, 8 - self.bits)
        return bytes(self.out)


def _magnitude(value: int) -> tuple[int, int]:
    size = abs(value).bit_length()
    return size, (value if value >= 0 else value + (1 << size) - 1)


def encode_jpeg(width: int, height: int, pixels: list[tuple[int, int, int]], quality: int = ICON_QUALITY) -> bytes:
    """Baseline JFIF, 4:4:4, Annex K Huffman tables; width and height multiples of 8."""
    if width % 8 or height % 8:
        raise ValueError("image dimensions must be multiples of 8")
    quant = (_scaled_quant(_LUMA_QUANT, quality), _scaled_quant(_CHROMA_QUANT, quality))
    dc = (_huffman_codes(_DC_LUMA), _huffman_codes(_DC_CHROMA))
    ac = (_huffman_codes(_AC_LUMA), _huffman_codes(_AC_CHROMA))
    planes = ([], [], [])
    for r, g, b in pixels:
        planes[0].append((19595 * r + 38470 * g + 7471 * b + 32768) >> 16)
        planes[1].append((-11059 * r - 21709 * g + 32768 * b + (128 << 16) + 32767) >> 16)
        planes[2].append((32768 * r - 27439 * g - 5329 * b + (128 << 16) + 32767) >> 16)
    writer = _BitWriter()
    previous_dc = [0, 0, 0]
    for by in range(0, height, 8):
        for bx in range(0, width, 8):
            for component in range(3):
                plane, table = planes[component], 0 if component == 0 else 1
                block = [[plane[(by + y) * width + bx + x] - 128 for x in range(8)] for y in range(8)]
                rows = [[sum(c * v for c, v in zip(basis, line)) for basis in _DCT] for line in block]
                coefficients = [0] * 64
                for u in range(8):
                    basis = _DCT[u]
                    for v in range(8):
                        total = sum(basis[y] * rows[y][v] for y in range(8))
                        index = u * 8 + v
                        coefficients[index] = _round_div(total, quant[table][index] << 26)
                diff = coefficients[0] - previous_dc[component]
                previous_dc[component] = coefficients[0]
                size, bits = _magnitude(diff)
                writer.write(*dc[table][size])
                if size:
                    writer.write(bits, size)
                run = 0
                for k in range(1, 64):
                    value = coefficients[_ZIGZAG[k]]
                    if value == 0:
                        run += 1
                        continue
                    while run > 15:
                        writer.write(*ac[table][0xF0])
                        run -= 16
                    size, bits = _magnitude(value)
                    writer.write(*ac[table][(run << 4) | size])
                    writer.write(bits, size)
                    run = 0
                if run:
                    writer.write(*ac[table][0x00])
    scan = writer.flush()

    def segment(marker: int, payload: bytes) -> bytes:
        return struct.pack(">HH", 0xFF00 | marker, len(payload) + 2) + payload

    dqt = b"".join(bytes([index]) + bytes(table[z] for z in _ZIGZAG) for index, table in enumerate(quant))
    sof = struct.pack(">BHHB", 8, height, width, 3) + bytes([1, 0x11, 0, 2, 0x11, 1, 3, 0x11, 1])
    dht = b"".join(bytes([cls_id]) + bytes(spec[0]) + bytes(spec[1])
                   for cls_id, spec in ((0x00, _DC_LUMA), (0x10, _AC_LUMA), (0x01, _DC_CHROMA), (0x11, _AC_CHROMA)))
    sos = bytes([3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0])
    return (b"\xff\xd8" + segment(0xE0, b"JFIF\0\x01\x01\x00\x00\x01\x00\x01\x00\x00")
            + segment(0xDB, dqt) + segment(0xC0, sof) + segment(0xC4, dht) + segment(0xDA, sos)
            + scan + b"\xff\xd9")


def write_icon(release_dir: Path, out: Path) -> Path:
    """Write the NRO icon from the tModLoader release unpacked at `release_dir`."""
    width, height, pixels = _read_bmp_over_black(release_dir / RELEASE_ICON)
    if (width, height) != (ICON_SIZE, ICON_SIZE):
        raise ValueError(f"{RELEASE_ICON} is {width}x{height}, expected {ICON_SIZE}x{ICON_SIZE}")
    out.write_bytes(encode_jpeg(width, height, pixels))
    return out
