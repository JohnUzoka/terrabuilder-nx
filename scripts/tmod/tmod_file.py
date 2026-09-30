"""Read the pinned tModLoader 2026.7 .tmod format for AOT input binding.

Format: tModLoader commit 666f69962d3bdffde54fc14025f02634965b4e7c,
patches/tModLoader/Terraria/ModLoader/Core/TmodFile.cs (Save/Read/GetStream).
This does not change, sign, or execute mod packages.
"""
import hashlib
import struct
import zlib
from pathlib import Path


def extract_tmod_member(path, member):
    """Verify the complete content hash and return one exact uncompressed member."""
    path = Path(path)
    total = path.stat().st_size
    with path.open('rb') as stream:
        def take(size):
            if size < 0 or size > total - stream.tell():
                raise ValueError('Invalid .tmod field length')
            data = stream.read(size)
            if len(data) != size:
                raise ValueError('Truncated .tmod')
            return data

        def integer():
            return struct.unpack('<i', take(4))[0]

        def text():
            length = 0
            for shift in range(0, 35, 7):
                byte = take(1)[0]
                if shift == 28 and byte > 7:
                    raise ValueError('Invalid .tmod string length')
                length |= (byte & 0x7f) << shift
                if not byte & 0x80:
                    return take(length).decode('utf-8')
            raise ValueError('Invalid .tmod string length')

        if take(4) != b'TMOD':
            raise ValueError('Not a .tmod container')
        loader_version = text()
        if tuple(map(int, loader_version.split('.'))) < (0, 11):
            raise ValueError('Legacy .tmod containers are not supported')
        expected_hash = take(20)
        take(256)  # Signature is retained in the package; no authenticity claim here.
        data_length = integer()
        start = stream.tell()
        if data_length != total - start:
            raise ValueError('Incorrect .tmod data length')
        actual_hash = hashlib.file_digest(stream, 'sha1').digest()
        if actual_hash != expected_hash:
            raise ValueError('Incorrect .tmod content hash')
        stream.seek(start)
        name, version = text(), text()
        count = integer()
        if count < 0 or count > (total - stream.tell()) // 9:
            raise ValueError('Invalid .tmod member count')
        entries = {}
        offset = 0
        for _ in range(count):
            entry, length, stored = text(), integer(), integer()
            if entry in entries or length < 0 or stored < 0:
                raise ValueError('Invalid or duplicate .tmod member')
            entries[entry] = (offset, length, stored)
            offset += stored
        data_start = stream.tell()
        if data_start + offset != total:
            raise ValueError('Incorrect .tmod member data extent')
        offset, length, stored = entries[member]
        stream.seek(data_start + offset)
        data = take(stored)
        if length != stored:
            decoder = zlib.decompressobj(-15)
            data = decoder.decompress(data, length + 1)
            if not decoder.eof or decoder.unused_data or decoder.unconsumed_tail:
                raise ValueError('Invalid .tmod deflate stream')
        if len(data) != length:
            raise ValueError('Incorrect .tmod uncompressed member size')
        return data, {'name': name, 'version': version, 'loader_version': loader_version,
                      'content_sha1': expected_hash.hex(), 'member': member,
                      'member_sha256': hashlib.sha256(data).hexdigest(), 'member_bytes': length}
