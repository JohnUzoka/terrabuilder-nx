"""Read the pinned tModLoader 2026.7 .tmod format for AOT input binding.

Format: tModLoader commit 666f69962d3bdffde54fc14025f02634965b4e7c,
patches/tModLoader/Terraria/ModLoader/Core/TmodFile.cs (Save/Read/GetStream).
Derived packages are unsigned local copies; this never claims signature authenticity.
"""
import hashlib
from io import BytesIO
import struct
import zlib
from pathlib import Path


def _take(stream, size, end):
    if size < 0 or size > end - stream.tell():
        raise ValueError('Invalid .tmod field length')
    data = stream.read(size)
    if len(data) != size:
        raise ValueError('Truncated .tmod')
    return data


def _text(stream, end):
    length = 0
    for shift in range(0, 35, 7):
        byte = _take(stream, 1, end)[0]
        if shift == 28 and byte > 7:
            raise ValueError('Invalid .tmod string length')
        length |= (byte & 0x7f) << shift
        if not byte & 0x80:
            return _take(stream, length, end).decode('utf-8')
    raise ValueError('Invalid .tmod string length')


def _encoded_text(text):
    data = text.encode('utf-8')
    length = len(data)
    prefix = bytearray()
    while length >= 0x80:
        prefix.append((length & 0x7f) | 0x80)
        length >>= 7
    prefix.append(length)
    return bytes(prefix) + data


def _directory(stream, total):
    if _take(stream, 4, total) != b'TMOD':
        raise ValueError('Not a .tmod container')
    loader_version = _text(stream, total)
    if tuple(map(int, loader_version.split('.'))) < (0, 11):
        raise ValueError('Legacy .tmod containers are not supported')
    expected_hash = _take(stream, 20, total)
    _take(stream, 256, total)
    data_length = struct.unpack('<i', _take(stream, 4, total))[0]
    start = stream.tell()
    if data_length != total - start:
        raise ValueError('Incorrect .tmod data length')
    if hashlib.file_digest(stream, 'sha1').digest() != expected_hash:
        raise ValueError('Incorrect .tmod content hash')
    stream.seek(start)
    name, version = _text(stream, total), _text(stream, total)
    count = struct.unpack('<i', _take(stream, 4, total))[0]
    if count < 0 or count > (total - stream.tell()) // 9:
        raise ValueError('Invalid .tmod member count')
    entries = {}
    offset = 0
    for _ in range(count):
        entry = _text(stream, total)
        length, stored = struct.unpack('<ii', _take(stream, 8, total))
        if entry in entries or length < 0 or stored < 0:
            raise ValueError('Invalid or duplicate .tmod member')
        entries[entry] = (offset, length, stored)
        offset += stored
    data_start = stream.tell()
    if data_start + offset != total:
        raise ValueError('Incorrect .tmod member data extent')
    return entries, data_start, {'name': name, 'version': version,
                               'loader_version': loader_version, 'content_sha1': expected_hash.hex()}


def extract_tmod_member(path, member):
    """Verify the complete content hash and return one exact uncompressed member."""
    path = Path(path)
    with path.open('rb') as stream:
        total = path.stat().st_size
        entries, data_start, info = _directory(stream, total)
        offset, length, stored = entries[member]
        stream.seek(data_start + offset)
        data = _take(stream, stored, total)
        if length != stored:
            decoder = zlib.decompressobj(-15)
            data = decoder.decompress(data, length + 1)
            if not decoder.eof or decoder.unused_data or decoder.unconsumed_tail:
                raise ValueError('Invalid .tmod deflate stream')
        if len(data) != length:
            raise ValueError('Incorrect .tmod uncompressed member size')
        return data, dict(info, member=member, member_sha256=hashlib.sha256(data).hexdigest(),
                          member_bytes=length)

def replace_tmod_member(path, member, data, output):
    """Write a new unsigned package, retaining every other member's stored bytes."""
    path, output = Path(path), Path(output)
    with path.open('rb') as source:
        total = path.stat().st_size
        entries, data_start, info = _directory(source, total)
        if member not in entries:
            raise KeyError(member)
        compressor = zlib.compressobj(wbits=-15)
        compressed = compressor.compress(data) + compressor.flush()
        replacement = compressed if len(compressed) < len(data) else data
        directory = bytearray(_encoded_text(info['name']) + _encoded_text(info['version']))
        directory.extend(struct.pack('<i', len(entries)))
        stored_total = 0
        for name, (_, length, stored) in entries.items():
            if name == member:
                length, stored = len(data), len(replacement)
            directory.extend(_encoded_text(name))
            directory.extend(struct.pack('<ii', length, stored))
            stored_total += stored
        with output.open('xb') as target:
            target.write(b'TMOD' + _encoded_text(info['loader_version']))
            hash_offset = target.tell()
            target.write(bytes(20 + 256))
            target.write(struct.pack('<i', len(directory) + stored_total))
            digest = hashlib.sha1()

            def emit(chunk):
                target.write(chunk)
                digest.update(chunk)

            emit(directory)
            for name, (offset, _, stored) in entries.items():
                if name == member:
                    emit(replacement)
                else:
                    source.seek(data_start + offset)
                    remaining = stored
                    while remaining:
                        chunk = _take(source, min(remaining, 1024 * 1024), total)
                        emit(chunk)
                        remaining -= len(chunk)
            target.seek(hash_offset)
            target.write(digest.digest())
    return output


def read_tmod_info(path):
    """Read the pinned BuildProperties.Info tag stream, without executing mod code."""
    data, package = extract_tmod_member(path, 'Info')
    stream = BytesIO(data)
    list_tags = {'dllReferences', 'modReferences', 'weakReferences', 'sortAfter', 'sortBefore'}
    string_tags = {'author', 'version', 'displayName', 'homepage', 'description',
                   'eacPath', 'buildVersion', 'modSource'}
    flag_tags = {'noCompile': ('noCompile', True), '!playableOnPreview': ('playableOnPreview', False),
                 'translationMod': ('translationMod', True), '!hideCode': ('hideCode', False),
                 '!hideResources': ('hideResources', False), 'includeSource': ('includeSource', True)}
    properties = {tag: [] for tag in list_tags}
    properties.update(name=package['name'], version=package['version'],
                      loader_version=package['loader_version'], side=0,
                      noCompile=False, playableOnPreview=True, translationMod=False,
                      hideCode=True, hideResources=True, includeSource=False)
    seen = set()
    while True:
        tag = _text(stream, len(data))
        if not tag:
            if stream.tell() != len(data):
                raise ValueError('Trailing data in .tmod Info')
            break
        if tag in seen:
            raise ValueError('Duplicate .tmod Info tag: ' + tag)
        seen.add(tag)
        if tag in list_tags:
            while True:
                value = _text(stream, len(data))
                if not value:
                    break
                properties[tag].append(value)
        elif tag in string_tags:
            properties[tag] = _text(stream, len(data))
        elif tag == 'side':
            properties[tag] = _take(stream, 1, len(data))[0]
            if properties[tag] not in range(4):
                raise ValueError('Unknown .tmod ModSide')
        elif tag in flag_tags:
            key, value = flag_tags[tag]
            properties[key] = value
        else:
            raise ValueError('Unsupported .tmod Info tag: ' + tag)
    if properties['version'] != package['version']:
        raise ValueError('.tmod Info version differs from container version')
    return properties
