"""Decode Mono's image_table to verify every AOT dependency, not only its own MVID.

Pinned runtime aot-compiler.c emit_image_table/encode_int/encode_string:
little-endian count, four NUL strings, 8-byte alignment, flags + four version ints.
Standard library only: the CLI runs this on the host.
"""
import struct

SECTION_HEADER = struct.Struct('<IIQQQQIIQQ')  # name type flags addr offset size link info addralign entsize
SYMBOL = struct.Struct('<IBBHQQ')  # name info other shndx value size
SHT_SYMTAB = 2
SHT_SYMTAB_SHNDX = 18
SHN_XINDEX = 0xffff


def image_table_offsets(stream, path):
    """File offsets (start, end) of the image_table symbol and of the end of its section."""
    header = stream.read(64)
    if len(header) != 64 or header[:6] != b'\x7fELF\x02\x01':
        raise ValueError(f'Not a little-endian ELF64 object: {path}')
    section_offset = struct.unpack_from('<Q', header, 0x28)[0]
    entry_size, count = struct.unpack_from('<HH', header, 0x3a)
    if entry_size != SECTION_HEADER.size or not section_offset:
        raise ValueError(f'Unsupported ELF section headers in {path}')

    def read(offset, size):
        stream.seek(offset)
        data = stream.read(size)
        if len(data) != size:
            raise ValueError(f'ELF data outside the file: {path}')
        return data

    if count == 0:  # extended numbering (LLVM sidecars): the count is section 0's sh_size
        count = SECTION_HEADER.unpack(read(section_offset, entry_size))[5]
    sections = list(SECTION_HEADER.iter_unpack(read(section_offset, entry_size * count)))
    symtabs = [index for index, section in enumerate(sections) if section[1] == SHT_SYMTAB]
    if len(symtabs) != 1:
        raise ValueError(f'Expected one symbol table in {path}')
    symtab = sections[symtabs[0]]
    names = read(sections[symtab[6]][4], sections[symtab[6]][5])
    matches = [(index, symbol) for index, symbol in enumerate(SYMBOL.iter_unpack(read(symtab[4], symtab[5])))
               if names.startswith(b'image_table\0', symbol[0])]
    if len(matches) != 1:
        raise ValueError(f'Expected one image_table in {path}')
    index, symbol = matches[0]
    shndx = symbol[3]
    if shndx == SHN_XINDEX:
        extended = [section for section in sections if section[1] == SHT_SYMTAB_SHNDX and section[6] == symtabs[0]]
        if len(extended) != 1:
            raise ValueError(f'image_table section index missing in {path}')
        shndx = struct.unpack('<I', read(extended[0][4] + 4 * index, 4))[0]
    if not 0 < shndx < len(sections):
        raise ValueError(f'image_table is not in a section of {path}')
    section = sections[shndx]
    return section[4] + symbol[4] - section[3], section[4] + section[5]


def image_dependencies(path):
    with open(path, 'rb') as stream:
        base, end = image_table_offsets(stream, path)
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
