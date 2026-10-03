"""Build72 = build70 + three runtime-side performance fixes (no game code changes).

1. CoreLib recompiled by the patched LLVM cross compiler: under full-coop suspend the
   managed allocator no longer brackets the TLAB bump with the critical-region flag, and
   its release barrier is `dmb ishst` (mono-coop-managed-allocator.patch).
2. FNA.dll gets [SuppressGCTransition] on 16 main-thread-only FNA3D state/draw P/Invokes
   (scripts/fna_suppress_gc), recompiled through LLVM. MVID unchanged.
3. System.Text.RegularExpressions and System.Collections.Concurrent are AOT-compiled
   (LLVM) instead of interpreted (ChatManager.ParseMessage's Regex).

Every other object, LLVM sidecar and RomFS file is hard-linked from build70.
Run in localhost/monobuild-llvm:local with the release58 container mounts; the native
link/package step was release58/build_native.py with the R58_* environment (removed;
the CLI now links with scripts/native/link_nro.py).
"""
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, '/work/scripts')
from compile_terraria_aot import (
    inspect_object, method_counts, run_logged, sha256,
    validate_runtime_metadata, write_json,
)

ROOT = Path('/build')
SOURCE = ROOT / 'release58/v70/aot-final'
VARIANT = ROOT / 'release58' / os.environ.get('V72_VARIANT', 'v72')
OUT = VARIANT / 'aot-final'
LLVM = ROOT / 'runtime-llvm/artifacts/bin/mono/linux.x64.Debug/cross/linux-x64/libnx-arm64'
NM = '/opt/devkitpro/devkitA64/bin/aarch64-none-elf-nm'
PATCHED_FNA = ROOT / 'release58/v72-inputs/FNA.dll'
PATCHED_FNA_SHA = 'a0741caf876cebd4674a8532ed1830c9a3dcb631ca6eb04a14d7eb65df7ab427'
CORELIB_SHA = 'c29bc7f8d4ee3d7620dfbaa61a7ecfd19e8dcba572b41d8f95c28b3ee9009df8'
FNA_SHA = '15427b2cb4c7952160f4cc124711f29b428459949304ceb0d16436e68a01882f'
NEW_MODULES = ['System.Text.RegularExpressions.dll', 'System.Collections.Concurrent.dll']
CORELIB_OPTS = ('full,interp,static,ntrampolines=65536,nimt-trampolines=8192,'
                'ngsharedvt-trampolines=4096,nunbox-arbitrary-trampolines=2048,')


def compile_llvm(module, source, paths, aot_opts, environment, logs, temporary):
    target = OUT / (source.name + '.o')
    sidecar = OUT / (source.name + '-llvm.o')
    pending, llvm_pending = Path(str(target) + '.pending'), Path(str(sidecar) + '.pending')
    module_temp = temporary / ('llvm-' + source.name)
    module_temp.mkdir()
    command = [
        'stdbuf', '-oL', '-eL', str(LLVM / 'mono-aot-cross'), '--llvm', *paths,
        '--aot=' + aot_opts + 'outfile=' + str(pending)
        + ',llvm-path=' + str(LLVM) + '/,llvm-outfile=' + str(llvm_pending)
        + ',temp-path=' + str(module_temp) + ',tool-prefix=aarch64-none-elf-', str(source),
    ]
    print(f'LLVM AOT {source.name} start', flush=True)
    log = logs / (source.name + '.log')
    run_logged(command, log, environment)
    with llvm_pending.open('rb') as stream:
        header = stream.read(20)
    assert header[:6] == b'\x7fELF\x02\x01' and header[16:20] == b'\x01\x00\xb7\x00'
    module.update(method_counts(log))
    module.update(inspect_object(pending, NM, logs / (source.name + '.nm.log'), llvm_pending))
    prefix = 'corlib' if module['assembly']['name'] == 'System.Private.CoreLib' \
        else module['symbol'][len('mono_aot_module_'):-len('_info')]
    own = subprocess.run([NM, '-g', '--defined-only', str(pending)], capture_output=True, text=True, check=True).stdout
    assert f' T mono_aot_{prefix}jit_code_start\n' in own, f'main object lacks mono_aot_{prefix}jit_code_start'
    pending.replace(target)
    llvm_pending.replace(sidecar)
    module.update(command=command, log=str(log), optimizations=['--llvm'], object=str(target),
                  llvm_object=str(sidecar), llvm_object_sha256=sha256(sidecar), symbol_object=str(sidecar))
    module.pop('reused_from_object', None)
    print(f"LLVM AOT {source.name} PASS: {module['compiled_methods']}/{module['total_methods']}", flush=True)


def main():
    assert not VARIANT.exists(), 'fresh output directory required'
    report = json.loads((SOURCE / 'build-manifest.json').read_text())
    assert report['status'] == 'complete'
    all_modules = [report['corelib'], *report['modules']]
    for module in all_modules:
        assert sha256(Path(module['object'])) == module['object_sha256']
        assert sha256(Path(module['assembly']['path'])) == module['assembly']['sha256']
        if module.get('llvm_object'):
            assert sha256(Path(module['llvm_object'])) == module['llvm_object_sha256']
    corelib = report['corelib']
    fna = next(m for m in report['modules'] if m['assembly']['name'] == 'FNA')
    assert corelib['assembly']['sha256'] == CORELIB_SHA and fna['assembly']['sha256'] == FNA_SHA
    assert sha256(PATCHED_FNA) == PATCHED_FNA_SHA

    OUT.mkdir(parents=True)
    logs, temporary = OUT / 'logs', OUT / 'tmp'
    logs.mkdir()
    temporary.mkdir()
    shutil.copytree(SOURCE / 'runtime-romfs', OUT / 'runtime-romfs', copy_function=os.link)
    romfs_fna = OUT / 'runtime-romfs/FNA.dll'
    romfs_fna.unlink()
    shutil.copyfile(PATCHED_FNA, romfs_fna)

    rebuilt = {'System.Private.CoreLib', 'FNA'}
    reused = []
    for module in all_modules:
        if module['assembly']['name'] in rebuilt:
            continue
        for key in ('object', 'llvm_object'):
            if module.get(key):
                source = Path(module[key])
                os.link(source, OUT / source.name)
                module[key] = str(OUT / source.name)
        if module.get('llvm_object'):
            module['symbol_object'] = module['llvm_object']
        reused.append({'assembly': module['assembly']['name'], 'sha256': module['object_sha256']})

    # The patched FNA.dll becomes the staged metadata provider (same name and MVID).
    preparation = json.loads((SOURCE / 'preparation.json').read_text())
    staged = {Path(a['path']).name: a for a in preparation['staged_assemblies']}
    fna_entry = staged['FNA.dll']
    assert fna_entry['mvid'] == fna['assembly']['mvid']
    fna_entry.update(path=str(romfs_fna), sha256=PATCHED_FNA_SHA, size=romfs_fna.stat().st_size)
    write_json(OUT / 'preparation.json', preparation)
    fna['assembly'] = dict(fna_entry)

    report.update(status='incomplete', header=str(OUT / 'mono_aot_modules.h'),
                  preparation_manifest=str(OUT / 'preparation.json'))
    report['experiment'] = {
        'build': VARIANT.name, 'parent': str(SOURCE / 'build-manifest.json'),
        'change': 'CoreLib recompiled (coop managed allocator), FNA with SuppressGCTransition on 16 FNA3D '
                  'P/Invokes, new LLVM modules ' + ', '.join(NEW_MODULES) + '; everything else reused from v70',
        'reused_objects': reused,
    }
    write_json(OUT / 'build-manifest.json', report)

    environment = dict(os.environ, TMPDIR=str(temporary), LD_LIBRARY_PATH=str(LLVM))
    environment['PATH'] = '/opt/devkitpro/devkitA64/bin:/opt/devkitpro/tools/bin:' + environment['PATH']
    # v70's reference order, with v60b's RomFS swapped for v72's (identical but the patched FNA).
    reference_dirs = [str(OUT / 'runtime-romfs') if path.endswith('/runtime-romfs') else path
                      for path in report['reference_directories']]
    assert reference_dirs.count(str(OUT / 'runtime-romfs')) == 1
    report['reference_directories'] = reference_dirs
    reference_paths = ['--path=' + path for path in reference_dirs]

    new_modules = []
    for filename in NEW_MODULES:
        assembly = staged[filename]
        module = {'assembly': dict(assembly)}
        compile_llvm(module, OUT / 'runtime-romfs' / filename, reference_paths,
                     'full,interp,static,', environment, logs, temporary)
        module['status'] = 'compiled'
        new_modules.append(module)
    compile_llvm(fna, romfs_fna, reference_paths, 'full,interp,static,', environment, logs, temporary)
    fna['status'] = 'compiled'
    corelib_source = Path(corelib['assembly']['path'])
    compile_llvm(corelib, corelib_source, ['--path=' + str(corelib_source.parent)], CORELIB_OPTS,
                 environment, logs, temporary)
    corelib['status'] = 'externally_compiled'

    report['modules'].extend(new_modules)
    all_modules = [report['corelib'], *report['modules']]
    metadata = validate_runtime_metadata(preparation, all_modules, OUT)
    symbols = [module['symbol'] for module in all_modules]
    assert len(set(symbols)) == len(all_modules)
    header_text = '/* Generated only after all AArch64 objects passed verification. */\n'
    header_text += ''.join(f'REGISTER_AOT_MODULE({symbol});\n' for symbol in symbols)
    base_header = (SOURCE / 'mono_aot_modules.h').read_text()
    assert header_text.startswith(base_header), 'existing registrations must be unchanged'
    assert sha256(PATCHED_FNA) == PATCHED_FNA_SHA and sha256(romfs_fna) == PATCHED_FNA_SHA
    report.update(status='complete', runtime_metadata_manifest=str(metadata), inputs_verified_unchanged=True)
    write_json(OUT / 'build-manifest.json', report)
    (OUT / 'mono_aot_modules.h').write_text(header_text)
    shutil.rmtree(temporary)
    print(f'v72 AOT PASS: {len(all_modules)} modules registered', flush=True)


if __name__ == '__main__':
    main()
