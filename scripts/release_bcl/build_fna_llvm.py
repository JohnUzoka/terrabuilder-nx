"""Rebuild ONE module through LLVM; reuse every other AOT object from a verified base.

Build61 = 59b + LLVM FNA, build62 = 60b + LLVM FNA, build64 = 62 + LLVM CoreLib.
Run in localhost/monobuild-llvm:local with the release58 container mounts.
No proprietary assembly is transformed or recompiled by the FNA/CoreLib experiments.
The existing release58 native linker packages the result after AOT verification.
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
# Base variant (verified; target module not yet LLVM) and output variant. Defaults: build61.
# build62: FNA_LLVM_BASE=v60b FNA_LLVM_BASE_NRO_SHA=<60b sha> FNA_LLVM_VARIANT=v62 FNA_LLVM_TITLE=...
# build64: FNA_LLVM_MODULE=System.Private.CoreLib FNA_LLVM_BASE=v62 ... (CoreLib keeps its
#          build58/59b AOT options: ntrampolines=65536 + FNA_LLVM_CORELIB_EXTRA pools).
MODULE_NAME = os.environ.get('FNA_LLVM_MODULE', 'FNA')
BASE_VARIANT = os.environ.get('FNA_LLVM_BASE', 'v59b')
OUT_VARIANT = os.environ.get('FNA_LLVM_VARIANT', 'v61')
TITLE = os.environ.get('FNA_LLVM_TITLE', 'Terraria 61 LLVM FNA')
SOURCE = ROOT / 'release58' / BASE_VARIANT / 'aot-final'
VARIANT = ROOT / 'release58' / OUT_VARIANT
OUT = VARIANT / 'aot-final'
LLVM = ROOT / 'runtime-llvm/artifacts/bin/mono/linux.x64.Debug/cross/linux-x64/libnx-arm64'
INPUT_SHA = {'FNA': '15427b2cb4c7952160f4cc124711f29b428459949304ceb0d16436e68a01882f',
             'System.Private.CoreLib': 'c29bc7f8d4ee3d7620dfbaa61a7ecfd19e8dcba572b41d8f95c28b3ee9009df8',
             'Terraria': '90b135121829d6650ce0e4f8ddb1395108615aa7d46ae487d279bc3a18698b22'}[MODULE_NAME]
# Replacement input (build70: patched Terraria.exe). The base's LLVM object for that module is
# recompiled from the replacement, which also replaces the RomFS copy and its preparation record.
# FNA_LLVM_REPLACE_INPUT=<path under /build> FNA_LLVM_REPLACE_SHA=<sha256> FNA_LLVM_REPLACE_MVID=<mvid>
REPLACE_INPUT = os.environ.get('FNA_LLVM_REPLACE_INPUT')
REPLACE_SHA = os.environ.get('FNA_LLVM_REPLACE_SHA')
REPLACE_MVID = os.environ.get('FNA_LLVM_REPLACE_MVID', '').lower()
assert not REPLACE_INPUT or (REPLACE_SHA and REPLACE_MVID), 'replacement needs its SHA256 and MVID'
CORELIB_EXTRA = os.environ.get('FNA_LLVM_CORELIB_EXTRA',
                               'nimt-trampolines=8192,ngsharedvt-trampolines=4096,nunbox-arbitrary-trampolines=2048')
BASE_NRO_SHA = os.environ.get('FNA_LLVM_BASE_NRO_SHA', 'cdc7def4c2536b5013a4e40a306916bdcbdf25df8305848100fc8bb989541702')


def main():
    assert not OUT.exists(), 'fresh output directory required'
    baseline = json.loads((SOURCE.parent / 'artifact-verification.json').read_text())
    assert baseline['passed'] and baseline['candidateNroSha256'] == BASE_NRO_SHA
    assert sha256(SOURCE.parent / 'native/candidate/mono_nx_fna.nro') == BASE_NRO_SHA
    report = json.loads((SOURCE / 'build-manifest.json').read_text())
    assert report['status'] == 'complete'
    all_modules = [report['corelib'], *report['modules']]
    target_module = next(m for m in all_modules if m['assembly']['name'] == MODULE_NAME)
    assert REPLACE_INPUT or not target_module.get('llvm_object'), f'base already has LLVM {MODULE_NAME}'
    is_corelib = target_module is report['corelib']
    assert target_module['assembly']['sha256'] == INPUT_SHA
    assert sha256(Path(target_module['assembly']['path'])) == INPUT_SHA
    for module in all_modules:
        assert sha256(Path(module['object'])) == module['object_sha256']
        assert sha256(Path(module['assembly']['path'])) == module['assembly']['sha256']
        if module.get('llvm_object'):
            assert sha256(Path(module['llvm_object'])) == module['llvm_object_sha256']

    OUT.mkdir(parents=True)
    logs, temporary = OUT / 'logs', OUT / 'tmp'
    logs.mkdir()
    temporary.mkdir()
    shutil.copytree(SOURCE / 'runtime-romfs', OUT / 'runtime-romfs', copy_function=os.link)
    if REPLACE_INPUT:
        assert sha256(Path(REPLACE_INPUT)) == REPLACE_SHA
        staged = OUT / 'runtime-romfs' / Path(target_module['assembly']['path']).name
        staged.unlink()  # hardlink to the base payload; never write through it
        shutil.copy2(REPLACE_INPUT, staged)
        preparation = json.loads(Path(report['preparation_manifest']).read_text())
        entries = [a for a in preparation['staged_assemblies'] if a['name'] == MODULE_NAME]
        assert len(entries) == 1 and entries[0]['sha256'] == INPUT_SHA
        replaced = dict(entries[0], path=str(staged), sha256=REPLACE_SHA, mvid=REPLACE_MVID,
                        size=staged.stat().st_size)
        entries[0].update(replaced)
        target_module['assembly'] = dict(target_module['assembly'], **{k: replaced[k] for k in ('path', 'sha256', 'mvid', 'size')})
        target_module.pop('llvm_object', None)
        target_module.pop('llvm_object_sha256', None)
        report['preparation_manifest'] = str(OUT / 'preparation.json')
        write_json(OUT / 'preparation.json', preparation)
    reused = []
    for module in all_modules:
        if module is target_module:
            continue
        source = Path(module['object'])
        target = OUT / source.name
        os.link(source, target)
        module['reused_from_object'] = str(source)
        module['object'] = str(target)
        entry = {'assembly': module['assembly']['name'], 'sha256': module['object_sha256']}
        if module.get('llvm_object'):
            # Existing LLVM sidecar (e.g. 60b's Terraria) is reused verbatim with its owner.
            llvm_source = Path(module['llvm_object'])
            llvm_target = OUT / llvm_source.name
            os.link(llvm_source, llvm_target)
            module['llvm_object'] = str(llvm_target)
            module['symbol_object'] = str(llvm_target)
            entry['llvm_sha256'] = module['llvm_object_sha256']
        reused.append(entry)
    report.update(status='incomplete', header=str(OUT / 'mono_aot_modules.h'))
    report['experiment'] = {
        'build': OUT_VARIANT, 'parent': str(SOURCE / 'build-manifest.json'),
        'change': (f'{MODULE_NAME} replaced by {REPLACE_SHA} and recompiled through LLVM' if REPLACE_INPUT
                   else f'{MODULE_NAME}-only LLVM AOT') + f'; all other AOT objects, LLVM sidecars and RomFS reused from {BASE_VARIANT}',
        'reused_objects': reused,
    }
    write_json(OUT / 'build-manifest.json', report)
    environment = dict(os.environ, TMPDIR=str(temporary), LD_LIBRARY_PATH=str(LLVM))
    environment['PATH'] = '/opt/devkitpro/devkitA64/bin:/opt/devkitpro/tools/bin:' + environment['PATH']
    source = Path(target_module['assembly']['path'])
    target = OUT / Path(target_module['object']).name
    sidecar = OUT / (source.name + '-llvm.o')
    pending, llvm_pending = Path(str(target) + '.pending'), Path(str(sidecar) + '.pending')
    module_temp = temporary / ('llvm-' + source.name)
    module_temp.mkdir()
    if is_corelib:
        # Same options as build_aot.py's CoreLib step (inlining on, build38/59b trampoline pools).
        paths = ['--path=' + str(source.parent)]
        aot_opts = 'full,interp,static,ntrampolines=65536,' + (CORELIB_EXTRA + ',' if CORELIB_EXTRA else '')
    else:
        paths = ['--path=' + path for path in report['reference_directories']]
        aot_opts = 'full,interp,static,'
    command = [
        'stdbuf', '-oL', '-eL', str(LLVM / 'mono-aot-cross'), '--llvm', *paths,
        '--aot=' + aot_opts + 'outfile=' + str(pending)
        + ',llvm-path=' + str(LLVM) + '/,llvm-outfile=' + str(llvm_pending)
        + ',temp-path=' + str(module_temp) + ',tool-prefix=aarch64-none-elf-', str(source),
    ]
    print(f'{MODULE_NAME}-only LLVM AOT start; {len(all_modules) - 1} other objects reused unchanged from {BASE_VARIANT}', flush=True)
    log = logs / (source.name + '.log')
    run_logged(command, log, environment)
    with llvm_pending.open('rb') as stream:
        header = stream.read(20)
    assert header[:6] == b'\x7fELF\x02\x01' and header[16:20] == b'\x01\x00\xb7\x00'
    target_module.update(method_counts(log))
    target_module.update(inspect_object(pending, '/opt/devkitpro/devkitA64/bin/aarch64-none-elf-nm',
                                        logs / (source.name + '.nm.log'), llvm_pending))
    # Mono's global symbol prefix is the module name with '.'->'_', except CoreLib: "corlib".
    prefix = 'corlib' if is_corelib else target_module['symbol'][len('mono_aot_module_'):-len('_info')]
    own = subprocess.run(['/opt/devkitpro/devkitA64/bin/aarch64-none-elf-nm', '-g', '--defined-only', str(pending)],
                         capture_output=True, text=True, check=True).stdout
    assert f' T mono_aot_{prefix}jit_code_start\n' in own, f'main object lacks mono_aot_{prefix}jit_code_start'
    assert sha256(source) == (REPLACE_SHA or INPUT_SHA)
    pending.replace(target)
    llvm_pending.replace(sidecar)
    target_module.update(command=command, log=str(log), optimizations=['--llvm'], object=str(target),
                         llvm_object=str(sidecar), llvm_object_sha256=sha256(sidecar),
                         symbol_object=str(sidecar), status='externally_compiled' if is_corelib else 'compiled')
    preparation = json.loads(Path(report['preparation_manifest']).read_text())
    metadata = validate_runtime_metadata(preparation, all_modules, OUT)
    for module in all_modules:
        assert sha256(Path(module['object'])) == module['object_sha256']
        if module.get('llvm_object'):
            assert sha256(Path(module['llvm_object'])) == module['llvm_object_sha256']
    symbols = [module['symbol'] for module in all_modules]
    assert len(set(symbols)) == len(all_modules)
    header_text = '/* Generated only after all AArch64 objects passed verification. */\n'
    header_text += ''.join(f'REGISTER_AOT_MODULE({symbol});\n' for symbol in symbols)
    assert header_text == (SOURCE / 'mono_aot_modules.h').read_text()
    report.update(status='complete', runtime_metadata_manifest=str(metadata), inputs_verified_unchanged=True)
    write_json(OUT / 'build-manifest.json', report)
    (OUT / 'mono_aot_modules.h').write_text(header_text)
    print(f"{MODULE_NAME} LLVM AOT PASS: {target_module['compiled_methods']}/{target_module['total_methods']}; {len(all_modules)} module bindings verified", flush=True)
    environment.update(R58_VARIANT=OUT_VARIANT, R58_TITLE=TITLE)
    subprocess.run([sys.executable, str(ROOT / 'release58/build_native.py')],
                   env=environment, check=True)


if __name__ == '__main__':
    main()
