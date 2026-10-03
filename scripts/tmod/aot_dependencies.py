"""Decode Mono's image_table to verify every AOT dependency, not only its own MVID.

Pinned runtime aot-compiler.c emit_image_table/encode_int/encode_string:
little-endian count, four NUL strings, 8-byte alignment, flags + four version ints.
"""
import struct

from elftools.elf.elffile import ELFFile


def image_dependencies(path):
    with open(path, 'rb') as stream:
        elf = ELFFile(stream)
        symbols = elf.get_section_by_name('.symtab').get_symbol_by_name('image_table')
        if not symbols or len(symbols) != 1:
            raise ValueError(f'Expected one image_table in {path}')
        symbol = symbols[0]
        section = elf.get_section(symbol['st_shndx'])
        base = section['sh_offset'] + symbol['st_value'] - section['sh_addr']
        end = section['sh_offset'] + section['sh_size']
        stream.seek(base)

        def integer():
            if stream.tell() + 4 > end:
                raise ValueError('Truncated image_table integer')
            return struct.unpack('<I', stream.read(4))[0]

        def text():
            value = bytearray()
            while stream.tell() < end:
                byte = stream.read(1)
                if byte == b'\0':
                    return value.decode('utf-8')
                value.extend(byte)
            raise ValueError('Unterminated image_table string')

        count = integer()
        if count > 4096:
            raise ValueError('Invalid image_table count')
        dependencies = []
        for _ in range(count):
            name, mvid, culture, token = (text() for _ in range(4))
            relative = stream.tell() - base
            stream.seek(base + ((relative + 7) & ~7))
            flags = integer()
            version = [integer() for _ in range(4)]
            dependencies.append({'name': name, 'mvid': mvid.lower(), 'culture': culture,
                                 'public_key_token': token, 'flags': flags,
                                 'version': '.'.join(map(str, version))})
        return dependencies
