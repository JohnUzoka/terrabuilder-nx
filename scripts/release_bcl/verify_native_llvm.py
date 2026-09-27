"""LLVM-aware copy of ab46/verify_pair.verify_native (build60).

Identical checks, except method-table CALL26 entries whose target symbol is undefined
in the module object (LLVM-compiled methods) are resolved through the linked ELF symbol
table and must land in .text. ab46/verify_pair.py stays untouched for builds 46-59.
"""
import struct
from pathlib import Path
import sys
ROOT = Path('/home/juzoka/.cache/terraria-switch-build')
sys.path.insert(0, str(ROOT / 'ab46'))
from verify_pair import sha, ELFFile


def verify_native_llvm(image_path, objects, manifest):
    groups = {name: [] for name in ('jit_code_start', 'jit_code_end', 'method_addresses', 'mono_aot_file_info')}
    exports = {}
    with image_path.open('rb') as stream:
        image = ELFFile(stream)
        assert image.header['e_machine'] == 'EM_AARCH64' and image.little_endian
        table = dict(image.get_section_by_name('.mono_aot_method_tables').header)
        text_section = image.get_section_by_name('.text')
        text_lo, text_hi = text_section['sh_addr'], text_section['sh_addr'] + text_section['sh_size']
        assert table['sh_flags'] == 2
        segments = [dict(segment.header) for segment in image.iter_segments() if segment['p_type'] == 'PT_LOAD']
        assert all(segment['p_flags'] & 7 != 7 for segment in segments)
        segment = next(s for s in segments if s['p_vaddr'] <= table['sh_addr'] and table['sh_addr'] + table['sh_size'] <= s['p_vaddr'] + s['p_filesz'])
        assert segment['p_flags'] == 4
        linked_symbols = {}
        for symbol in image.get_section_by_name('.symtab').iter_symbols():
            if symbol.name and symbol['st_shndx'] != 'SHN_UNDEF':
                linked_symbols.setdefault(symbol.name, symbol['st_value'])
            if symbol.name in groups:
                groups[symbol.name].append(symbol['st_value'])
            elif symbol.name.startswith('mono_aot_module_'):
                exports[symbol.name] = symbol['st_value']
    modules = sorted([manifest['corelib'], *manifest['modules']], key=lambda module: Path(module['object']).name)
    assert all(len(values) == len(modules) for values in groups.values())
    result = {'image': str(image_path), 'table_address': hex(table['sh_addr']), 'table_bytes': table['sh_size'], 'table_segment_flags': 4, 'has_rwx_segment': False, 'modules': []}
    cursor = table['sh_addr']
    with image_path.open('rb') as linked:
        for index, module in enumerate(modules):
            source_path = objects / Path(module['object']).name
            assert sha(source_path) == module['object_sha256']
            start, end, address = (groups[name][index] for name in ('jit_code_start', 'jit_code_end', 'method_addresses'))
            export = exports[module['symbol']]
            segment = next(s for s in segments if s['p_vaddr'] <= export and export + 8 <= s['p_vaddr'] + s['p_filesz'])
            linked.seek(segment['p_offset'] + export - segment['p_vaddr'])
            info = struct.unpack('<Q', linked.read(8))[0]
            assert info == groups['mono_aot_file_info'][index]
            segment = next(s for s in segments if s['p_vaddr'] <= info and info + 72 <= s['p_vaddr'] + s['p_filesz'])
            linked.seek(segment['p_offset'] + info - segment['p_vaddr'] + 48)
            assert struct.unpack('<QQQ', linked.read(24)) == (start, end, address)
            with source_path.open('rb') as source:
                obj = ELFFile(source)
                symtab = obj.get_section_by_name('.symtab')
                source_table = obj.get_section_by_name('.data.rel.ro')
                origin = symtab.get_symbol_by_name('jit_code_start')
                assert len(origin) == 1
                offset_start, text_index = origin[0]['st_value'], origin[0]['st_shndx']
                original, alignment = source_table.data(), source_table['sh_addralign']
                relocations, symbols = obj.get_section_by_name('.rela.data.rel.ro').data(), symtab.data()
                strtab = obj.get_section(symtab['sh_link']).data()
            cursor = (cursor + alignment - 1) // alignment * alignment
            assert cursor == address and len(original) == module['method_table']['bytes']
            cursor += len(original)
            linked.seek(table['sh_offset'] + address - table['sh_addr'])
            words = linked.read(len(original))
            assert len(words) == len(original)
            marked = bytearray(len(words) // 4)
            count = maximum = llvm_count = 0
            for offset, relocation, addend in struct.iter_unpack('<QQq', relocations):
                assert relocation & 0xffffffff == 283 and offset % 4 == 0 and 0 <= offset < len(words) and not marked[offset // 4]
                symbol = struct.unpack_from('<IBBHQQ', symbols, (relocation >> 32) * 24)
                if symbol[3] == 0 and module.get('llvm_object'):
                    # LLVM-compiled method: undefined here, defined in the module's -llvm.o sidecar.
                    name = strtab[symbol[0]:strtab.index(b'\0', symbol[0])].decode()
                    expected = linked_symbols[name] + addend
                    llvm_count += 1
                else:
                    assert symbol[3] == text_index
                    expected = start + symbol[4] - offset_start + addend
                instruction = struct.unpack_from('<I', words, offset)[0]
                assert instruction & 0xfc000000 == 0x94000000
                displacement = instruction & 0x03ffffff
                if displacement & 0x02000000:
                    displacement -= 0x04000000
                actual = address + offset + displacement * 4
                assert actual == expected and (start <= actual < end or (module.get('llvm_object') and text_lo <= actual < text_hi))
                marked[offset // 4] = 1
                count += 1
                maximum = max(maximum, abs(displacement * 4))
            fallback = 0
            for word_index, flag in enumerate(marked):
                if not flag:
                    assert struct.unpack_from('<I', words, word_index * 4)[0] == struct.unpack_from('<I', original, word_index * 4)[0]
                    fallback += 1
            assert count == module['compiled_methods'] == module['method_table']['relocation_count']
            assert fallback == module['method_table']['resolved_sentinels']
            result['modules'].append({'object': source_path.name, 'sha256': module['object_sha256'], 'native_start': hex(start), 'table': hex(address), 'methods_verified': count, 'fallbacks_verified': fallback, 'maximum_branch_reach_bytes': maximum, 'module_info_pointer_verified': True, 'llvm_methods_verified': llvm_count})
    assert cursor == table['sh_addr'] + table['sh_size']
    result['total_methods_verified'] = sum(module['methods_verified'] for module in result['modules'])
    result['total_fallbacks_verified'] = sum(module['fallbacks_verified'] for module in result['modules'])
    return result

