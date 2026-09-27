#!/usr/bin/env python3
"""Extract named struct/union/typedef-struct layouts from every object in an ar archive.

Output: {type_name: [size, [[member, offset, bitsize], ...]]}. Types whose layout differs
between objects of the same archive are reported with all variants (e.g. per-TU private
definitions); the comparison uses only names with a single consistent layout.
Usage: dwarf_layouts.py ARCHIVE OUT.json
"""
import io, json, sys
from concurrent.futures import ProcessPoolExecutor
sys.path.insert(0, '/home/juzoka/.cache/terraria-switch-build/runtime-fix/python')
from elftools.elf.elffile import ELFFile


def members_of(archive_bytes, start, size, name):
    data = archive_bytes[start:start + size]
    out = {}
    elf = ELFFile(io.BytesIO(data))
    if not elf.has_dwarf_info():
        return name, out
    dwarf = elf.get_dwarf_info()
    for cu in dwarf.iter_CUs():
        for die in cu.iter_DIEs():
            if die.tag not in ('DW_TAG_structure_type', 'DW_TAG_union_type'):
                continue
            if 'DW_AT_declaration' in die.attributes or 'DW_AT_byte_size' not in die.attributes:
                continue
            tname = die.attributes.get('DW_AT_name')
            if tname is None:
                parent = die.get_parent()
                if parent is not None and parent.tag == 'DW_TAG_typedef' and 'DW_AT_name' in parent.attributes:
                    tname = parent.attributes['DW_AT_name']
                else:
                    continue
            tname = tname.value.decode(errors='replace')
            fields = []
            for child in die.iter_children():
                if child.tag != 'DW_TAG_member':
                    continue
                mname = child.attributes.get('DW_AT_name')
                loc = child.attributes.get('DW_AT_data_member_location')
                off = loc.value if loc is not None and isinstance(loc.value, int) else None
                bits = child.attributes.get('DW_AT_bit_size')
                bitoff = child.attributes.get('DW_AT_data_bit_offset')
                fields.append([mname.value.decode(errors='replace') if mname else None, off,
                               bits.value if bits else None, bitoff.value if bitoff else None])
            key = (die.tag[7], die.attributes['DW_AT_byte_size'].value, json.dumps(fields))
            out.setdefault(tname, set()).add(key)
    return name, {k: sorted(v) for k, v in out.items()}


def members(archive):
    data = open(archive, 'rb').read()
    assert data[:8] == b'!<arch>\n'
    pos, items, longnames = 8, [], b''
    while pos < len(data):
        header = data[pos:pos + 60]
        name = header[:16].decode().strip()
        size = int(header[48:58].decode().strip())
        body = pos + 60
        if name == '//':
            longnames = data[body:body + size]
        elif name not in ('/', '/SYM64/') and data[body:body + 4] == b'\x7fELF':
            if name.startswith('/') and name[1:].isdigit():
                off = int(name[1:]); name = longnames[off:longnames.index(b'/\n', off)].decode()
            items.append((body, size, name.rstrip('/')))
        pos = body + size + (size & 1)
    return data, items


def main():
    archive, out = sys.argv[1], sys.argv[2]
    data, items = members(archive)
    merged = {}
    with ProcessPoolExecutor(max_workers=12) as pool:
        futures = [pool.submit(members_of, data, s, n, name) for s, n, name in items]
        for f in futures:
            _, layouts = f.result()
            for k, variants in layouts.items():
                merged.setdefault(k, set()).update(tuple(v) for v in variants)
    result = {k: sorted(list(v)) for k, v in merged.items()}
    json.dump({'archive': archive, 'objects': len(items), 'types': result}, open(out, 'w'))
    print(f'{archive}: {len(items)} objects, {len(result)} named types, '
          f'{sum(1 for v in result.values() if len(v) > 1)} with >1 layout variant')


if __name__ == '__main__':
    main()
