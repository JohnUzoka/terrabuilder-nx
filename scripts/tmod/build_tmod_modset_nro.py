"""Build one pinned mod set on tmod09, with exact .tmod DLLs compiled into the NRO.

Run in monobuild:local with the same mounts as build_tmod_nxfix_nro.py.
TMOD_VARIANT names a fresh /build/tmod/<variant> with input/modset.json:
  {"mod": "BossCursor", "label": "Boss Cursor", "aot_replacements": [],
   "replacements": {"System.Reflection.Metadata.dll": ["System.Reflection.Metadata.dll"]}}
input also contains tModLoader.dll, <mod>.tmod and all replacement DLLs.
Optional keys: "corelib": <name> uses /build/tmod/corelib/<name> (build_tmod_corelib_aot.py,
enlarged trampoline pools; tmod09's CoreLib object otherwise), and "llvm_modules": [compiled
DLL names] compiles those through Mono's LLVM backend (run in monobuild-llvm:local).
LLVM sidecars (<module>-llvm.o) link right after their module object; their absolute
.rodata pointers go to a writable section, as in vanilla build60+.
The external SD mod package must stay paired with this NRO; a different MVID
is rejected by Mono before interpretation can be used as a fallback.
"""
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shlex
import shutil
import subprocess

from tmod_file import extract_tmod_member

ROOT = Path('/build')
BASE = ROOT / 'tmod/tmod09'
VARIANT = os.environ['TMOD_VARIANT']
assert Path(VARIANT).name == VARIANT and VARIANT.startswith('tmod')
OUT = ROOT / 'tmod' / VARIANT
INPUT = OUT / 'input'
AOT = OUT / 'aot'
NATIVE = OUT / 'native'
ROMFS = OUT / 'romfs'
CROSS = '/mono-nx/dotnet_runtime/artifacts/bin/mono/linux.x64.Debug/cross/linux-x64/libnx-arm64/mono-aot-cross'
LLVM_DIR = ROOT / 'runtime-llvm/artifacts/bin/mono/linux.x64.Debug/cross/linux-x64/libnx-arm64'
env = dict(os.environ, PATH='/opt/devkitpro/devkitA64/bin:' + os.environ['PATH'], TOPDIR=str(NATIVE))


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def run(argv, label, cwd):
    process = subprocess.run(list(map(str, argv)), cwd=cwd, env=env, capture_output=True, text=True)
    (OUT / (label + '.log')).write_text(process.stdout + process.stderr)
    if process.returncode:
        raise RuntimeError(label + '\n' + process.stdout[-4000:] + process.stderr[-4000:])
    print('PASS ' + label, flush=True)


config = json.loads((INPUT / 'modset.json').read_text())
mod = config['mod']
assert mod.isidentifier(), 'Expected one named mod, no paths'
additional_mods = config.get('additional_mods', [])
assert len(additional_mods) == len(set(additional_mods)), 'Duplicate additional_mods'
assert mod not in additional_mods, 'Primary mod cannot be in additional_mods'
all_mods = [mod, *additional_mods]
for m in all_mods:
    assert m.isidentifier(), f'Invalid mod name: {m}'

mod_libraries = config.get('mod_libraries', [])
for lib in mod_libraries:
    assert lib['mod'] in all_mods, f'mod_libraries owner mod {lib["mod"]} not in modset'
    assert lib['member'].endswith('.dll'), f'mod_libraries member {lib["member"]} must be a .dll'

replacements = config.get('replacements', {})
aot_replacements = config.get('aot_replacements', [])
assert len(aot_replacements) == len(set(aot_replacements))
assert set(aot_replacements) <= replacements.keys()
assert 'tModLoader.dll' not in replacements and 'FNA.dll' not in replacements

extra_aot = config.get('extra_aot', [])
assert len(extra_aot) == len(set(extra_aot))

corelib_name = config.get('corelib')
llvm_modules = config.get('llvm_modules', [])
assert len(llvm_modules) == len(set(llvm_modules))

compile_fna = config.get('compile_fna', False) or ('FNA.dll' in llvm_modules) or ('FNA.dll' in extra_aot)
reuse_base = config.get('reuse_base')
reuse_modules = config.get('reuse_modules', [])
assert len(reuse_modules) == len(set(reuse_modules))

for filename, destinations in replacements.items():
    assert Path(filename).name == filename and filename.endswith('.dll')
    assert destinations
    for destination in destinations:
        relative = PurePosixPath(destination)
        assert not relative.is_absolute() and '..' not in relative.parts
        assert (BASE / 'romfs' / destination).is_file(), destination
for directory in (AOT, NATIVE, ROMFS, OUT / 'sdcard', OUT / 'extracted'):
    assert not directory.exists(), f'Fresh output required: {directory}'

mod_bytes_by_name = {}
mod_info_by_name = {}
external_members = {}

for m in all_mods:
    pkg = INPUT / (m + '.tmod')
    mbr = m + '.dll'
    assert mbr not in external_members, f'Duplicate assembly name: {mbr}'
    m_bytes, m_info = extract_tmod_member(pkg, mbr)
    assert m_info['name'] == m and m_info['loader_version'] == '2026.7.3.0', m_info
    m_input = INPUT / mbr
    if m_input.exists():
        assert m_input.read_bytes() == m_bytes, f'Provided DLL {mbr} differs from actual .tmod member'
    else:
        m_input.write_bytes(m_bytes)
    mod_bytes_by_name[mbr] = m_bytes
    mod_info_by_name[m] = m_info
    external_members[mbr] = {'mod': m, 'member': mbr, 'package': pkg}

for lib in mod_libraries:
    owner = lib['mod']
    pkg = INPUT / (owner + '.tmod')
    mbr = lib['member']
    filename = PurePosixPath(mbr).name
    assert filename not in external_members, f'Duplicate assembly name: {filename}'
    lib_bytes, lib_info = extract_tmod_member(pkg, mbr)
    lib_input = INPUT / filename
    if lib_input.exists():
        assert lib_input.read_bytes() == lib_bytes, f'Provided DLL {filename} differs from actual .tmod member'
    else:
        lib_input.write_bytes(lib_bytes)
    mod_bytes_by_name[filename] = lib_bytes
    external_members[filename] = {'mod': owner, 'member': mbr, 'package': pkg}

if not (INPUT / 'FNA.dll').exists():
    shutil.copy2(BASE / 'romfs/FNA.dll', INPUT / 'FNA.dll')

for filename in extra_aot:
    assert Path(filename).name == filename and filename.endswith('.dll')
    assert filename != 'FNA.dll', 'Use compile_fna or llvm_modules for FNA'
    assert filename not in replacements, f'{filename} is in replacements'
    if not (INPUT / filename).exists():
        assert (BASE / 'romfs' / filename).is_file(), f'Extra AOT DLL missing from BASE romfs: {filename}'
        shutil.copy2(BASE / 'romfs' / filename, INPUT / filename)

compiled = ['tModLoader.dll']
if compile_fna:
    compiled.append('FNA.dll')
compiled.extend(aot_replacements)
compiled.extend(extra_aot)
compiled.extend(external_members.keys())
assert len(compiled) == len(set(compiled)), 'Duplicate compiled assembly'
input_hashes = {name: sha(INPUT / name) for name in set(compiled) | replacements.keys() | {'FNA.dll'}}
baseline = json.loads((BASE / 'manifest.json').read_text())
binding_base = baseline['provenance_and_binding']
AOT.mkdir()

for name in ('System.Private.CoreLib', *(['FNA'] if not compile_fna else [])):
    source = Path(binding_base[name]['aot_object'])
    if name == 'System.Private.CoreLib' and corelib_name:
        corelib = json.loads((ROOT / 'tmod/corelib' / corelib_name / 'manifest.json').read_text())
        assert corelib['input_sha256'] == sha(BASE / 'romfs/mono/lib_net9.0/System.Private.CoreLib.dll')
        source = Path(corelib['aot_object'])
        assert sha(source) == corelib['aot_object_sha256']
        if corelib['llvm']:
            assert sha(corelib['llvm_object']) == corelib['llvm_object_sha256']
            shutil.copy2(corelib['llvm_object'], AOT / (name + '.dll-llvm.o'))
    else:
        assert sha(source) == binding_base[name]['aot_object_sha256']
    shutil.copy2(source, AOT / (name + '.dll.o'))

assert set(reuse_modules) <= set(compiled), 'reuse_modules must name compiled DLLs'
if reuse_modules:
    assert reuse_base, 'reuse_base required when reuse_modules specified'
    reuse_manifest = json.loads((ROOT / 'tmod' / reuse_base / 'manifest.json').read_text())
    reuse_bindings = reuse_manifest['provenance_and_binding']
    for name in reuse_modules:
        mod_key = name.removesuffix('.dll')
        assert mod_key in reuse_bindings, f'Module {name} not found in {reuse_base} binding'
        rec = reuse_bindings[mod_key]
        assert rec['compiler_input_sha256'] == input_hashes[name], f'Input hash mismatch for reuse of {name}'
        src_obj = Path(rec['aot_object'])
        assert sha(src_obj) == rec['aot_object_sha256'], f'AOT object hash mismatch for reuse of {name}'
        shutil.copy2(src_obj, AOT / (name + '.o'))
        if 'llvm_object' in rec:
            src_llvm = Path(rec['llvm_object'])
            assert sha(src_llvm) == rec['llvm_object_sha256'], f'LLVM object hash mismatch for reuse of {name}'
            shutil.copy2(src_llvm, AOT / (name + '-llvm.o'))
        reuse_log = ROOT / 'tmod' / reuse_base / f'aot-{name}.log'
        if reuse_log.is_file():
            shutil.copy2(reuse_log, OUT / f'aot-{name}.log')
        print(f'PASS reuse aot-{name} from {reuse_base} (verified hash {rec["compiler_input_sha256"][:12]}...)', flush=True)
paths = ['--path=/build/runtime-source/artifacts/bin/mono/libnx.arm64.Release',
         '--path=' + str(INPUT),
         '--path=/build/runtime-source/artifacts/bin/runtime/net9.0-libnx-Release-arm64',
         '--path=/build/tmod/flat_libs']


assert set(llvm_modules) <= set(compiled), 'llvm_modules must name compiled DLLs'


def compile_one(name):
    options = f'full,interp,static,outfile={AOT}/{name}.o,tool-prefix=aarch64-none-elf-'
    if name not in llvm_modules:
        run([CROSS, *paths, '--aot=' + options, INPUT / name], 'aot-' + name, OUT)
        return
    temp = OUT / ('llvm-tmp-' + name)
    temp.mkdir()
    options += f',llvm-path={LLVM_DIR}/,llvm-outfile={AOT}/{name}-llvm.o,temp-path={temp}'
    run_env = dict(env, LD_LIBRARY_PATH=str(LLVM_DIR))
    process = subprocess.run(list(map(str, [LLVM_DIR / 'mono-aot-cross', '--llvm', *paths, '--aot=' + options, INPUT / name])),
                             cwd=OUT, env=run_env, capture_output=True, text=True)
    (OUT / ('aot-' + name + '.log')).write_text(process.stdout + process.stderr)
    if process.returncode:
        raise RuntimeError('aot-' + name + '\n' + process.stdout[-4000:] + process.stderr[-4000:])
    shutil.rmtree(temp)
    print('PASS aot-' + name + ' (LLVM)', flush=True)


to_compile = [name for name in compiled if name not in reuse_modules]
if to_compile:
    with ThreadPoolExecutor(max_workers=max(1, len(to_compile))) as pool:
        list(pool.map(compile_one, to_compile))
assert all(sha(INPUT / name) == digest for name, digest in input_hashes.items()), 'AOT input changed'
NATIVE.mkdir()
modules = ['System.Private.CoreLib']
if not compile_fna:
    modules.append('FNA')
for filename in compiled:
    modules.append(filename.removesuffix('.dll'))
assert len(modules) == len(set(modules)), 'Duplicate module in registration'
assert all(name.replace('.', '_').isidentifier() for name in modules)
(NATIVE / 'mono_aot_modules.h').write_text(''.join(
    'REGISTER_AOT_MODULE(mono_aot_module_' + name.replace('.', '_') + '_info);\n' for name in modules))
run([ROOT / 'release58/compile_main.sh', ROOT / 'tmod/launcher_build/main_tmod.c', NATIVE / 'main_tmod.o',
     '-DMONO_NX_EMBEDDED_BCL=1', '-DMONO_NX_FATAL_DIAG=1', '-I' + str(NATIVE)], 'compile-main', NATIVE)
shutil.copy2(ROOT / 'hint52/native/aot-method-tables.ld', NATIVE / 'aot-method-tables.ld')
sidecars = sorted(AOT.glob('*-llvm.o'))
if sidecars:
    # Mono's LLVM output keeps absolute pointers in .rodata; a PIE NRO relocates them at load,
    # so that .rodata joins the writable data segment (same script as vanilla build60+).
    with (NATIVE / 'aot-method-tables.ld').open('a') as script:
        script.write('\n/* Mono LLVM AOT sidecars: absolute pointers in .rodata need load-time relocation. */\n'
                     'SECTIONS\n{\n    .mono_llvm_rodata : ALIGN(16)\n    {\n'
                     '        /* A writable input first makes ld mark the output section SHF_WRITE. */\n'
                     '        *-llvm.o(.data .data.*)\n        *-llvm.o(.rodata .rodata.*)\n'
                     '    } :data\n}\nINSERT BEFORE .data.rel.ro;\n')
recorded = {}
for line in (ROOT / 'recovery46/link-arguments.txt').read_text().splitlines():
    key, value = line.split('=', 1)
    recorded[key] = shlex.split(value)
objects_dir = ROOT / 'nochroma42/native/interpreter/build'
objects = [NATIVE / 'main_tmod.o' if name == 'main.o' else objects_dir / name for name in recorded['OBJECTS']]
release_lib = ROOT / 'runtime-source/artifacts/obj/mono/libnx.arm64.Release/out/lib'
runtime = ROOT / 'runtime-release/libmonosgen-2.0-release.a'
assert sha(runtime) == baseline['runtime_provenance']['libmonosgen_sha256']
swap = {'/build/runtime-fix/libmonosgen-2.0.a': str(runtime)}
for component in ('debugger', 'diagnostics_tracing-stub', 'hot_reload-stub', 'marshal-ilgen'):
    name = f'libmono-component-{component}-static.a'
    swap[f'/mono-nx/dotnet_runtime/artifacts/obj/mono/libnx.arm64.Debug/out/lib/{name}'] = str(release_lib / name)
# "native_swaps": {recorded LIBS path: {"path": replacement archive, "sha256": ...}}, e.g. a
# System.Native archive with libnx filesystem fixes.
native_swaps = config.get('native_swaps', {})
for recorded_path, replacement in native_swaps.items():
    assert recorded_path in recorded['LIBS'], ('not a recorded link library', recorded_path)
    assert sha(replacement['path']) == replacement['sha256'], ('native swap changed', replacement['path'])
    swap[recorded_path] = replacement['path']
libraries = []
for value in recorded['LIBS']:
    if value.startswith('/build/aot42-windows/') and value.endswith('.o'):
        if not libraries or libraries[-1] is not None:
            libraries.append(None)
        continue
    libraries.append(swap.get(value, value))
index = libraries.index(None)
aot_objects = sorted(str(AOT / (name + '.dll.o')) for name in modules)
for sidecar in sidecars:
    # Each sidecar directly follows its module object; the verifier maps tables by name order.
    owner = str(AOT / sidecar.name.replace('-llvm.o', '.o'))
    aot_objects.insert(aot_objects.index(owner) + 1, str(sidecar))
libraries[index:index + 1] = aot_objects
candidate = NATIVE / 'candidate'
candidate.mkdir()
run(['aarch64-none-elf-gcc', *recorded['LDFLAGS'], *objects, *recorded['LIBPATHS'], *libraries,
     '-o', candidate / 'tmodloader.elf'], 'link-candidate', NATIVE)

shutil.copytree(BASE / 'romfs', ROMFS)
shutil.copy2(INPUT / 'tModLoader.dll', ROMFS / 'tModLoader.dll')
if compile_fna:
    shutil.copy2(INPUT / 'FNA.dll', ROMFS / 'FNA.dll')
for filename in extra_aot:
    shutil.copy2(INPUT / filename, ROMFS / filename)
for filename, destinations in replacements.items():
    for destination in destinations:
        shutil.copy2(INPUT / filename, ROMFS / destination)
sd_mods = OUT / 'sdcard/switch/tmodloader/Terraria/tModLoader/Mods'
sd_mods.mkdir(parents=True)
staged_packages = {}
for m in all_mods:
    pkg = INPUT / (m + '.tmod')
    sd_package = sd_mods / pkg.name
    shutil.copy2(pkg, sd_package)
    staged_packages[m] = sd_package
(sd_mods / 'enabled.json').write_text(json.dumps(all_mods) + '\n')

for mbr, ext_info in external_members.items():
    owner_pkg = staged_packages[ext_info['mod']]
    shipped_bytes, shipped_info = extract_tmod_member(owner_pkg, ext_info['member'])
    assert shipped_bytes == mod_bytes_by_name[mbr]
    extracted = OUT / 'extracted' / mbr
    extracted.parent.mkdir(parents=True, exist_ok=True)
    extracted.write_bytes(shipped_bytes)

nacp = bytearray((BASE / 'native/candidate/tmodloader.nacp').read_bytes())
title = f'tModLoader NX {VARIANT[4:]} ({config["label"]})'.encode()
assert len(title) < 0x200
for language in range(16):
    nacp[language * 0x300:language * 0x300 + 0x200] = title.ljust(0x200, b'\0')
(candidate / 'tmodloader.nacp').write_bytes(nacp)
run(['/opt/devkitpro/tools/bin/elf2nro', candidate / 'tmodloader.elf', candidate / 'tmodloader.nro',
     '--nacp=' + str(candidate / 'tmodloader.nacp'), '--romfsdir=' + str(ROMFS),
     '--icon=' + str(ROOT / 'tmod/tmod02/native/icon.jpg')], 'package-candidate', NATIVE)

binding = {}
for name in ('System.Private.CoreLib', *(['FNA'] if not compile_fna else [])):
    payload = ROMFS / ('FNA.dll' if name == 'FNA' else 'mono/lib_net9.0/System.Private.CoreLib.dll')
    object_path = AOT / (name + '.dll.o')
    binding[name] = dict(binding_base[name], aot_object=str(object_path), aot_object_sha256=sha(object_path),
                         romfs_payload_dll=str(payload), romfs_payload_sha256=sha(payload))
    sidecar = AOT / (name + '.dll-llvm.o')
    if sidecar.exists():
        binding[name].update(llvm_object=str(sidecar), llvm_object_sha256=sha(sidecar))
for filename in compiled:
    name = filename.removesuffix('.dll')
    record = {'compiler_input_dll': str(INPUT / filename), 'compiler_input_sha256': input_hashes[filename],
              'aot_object': str(AOT / (filename + '.o')), 'aot_object_sha256': sha(AOT / (filename + '.o'))}
    if filename in llvm_modules:
        record.update(llvm_object=str(AOT / (filename + '-llvm.o')), llvm_object_sha256=sha(AOT / (filename + '-llvm.o')))
    if filename in external_members:
        ext_info = external_members[filename]
        extracted_path = OUT / 'extracted' / filename
        sd_pkg = staged_packages[ext_info['mod']]
        record.update(external_payload_dll=str(extracted_path), external_payload_sha256=sha(extracted_path),
                      tmod_path=str(sd_pkg), tmod_sha256=sha(sd_pkg), tmod_member=ext_info['member'])
    else:
        record.update(romfs_payload_dll=str(ROMFS / filename), romfs_payload_sha256=sha(ROMFS / filename))
    binding[name] = record
nro = candidate / 'tmodloader.nro'
deliverables = {'candidate_nro': {'path': str(nro), 'bytes': nro.stat().st_size, 'sha256': sha(nro)},
                'candidate_elf': {'path': str(candidate / 'tmodloader.elf'), 'sha256': sha(candidate / 'tmodloader.elf')},
                'sd_mod': {'path': str(staged_packages[mod]), 'sha256': sha(staged_packages[mod])}}
if additional_mods:
    deliverables['sd_mods'] = {m: {'path': str(p), 'sha256': sha(p)} for m, p in staged_packages.items()}
manifest = {'candidate': VARIANT, 'content': baseline['content'], 'runtime_provenance': baseline['runtime_provenance'],
            'provenance_and_binding': binding, 'modset': dict(config, package=mod_info_by_name[mod]), 'reference_inputs': input_hashes,
            'managed_replacements': {name: {'sha256': input_hashes[name], 'destinations': paths}
                                     for name, paths in replacements.items()},
            'final_deliverables': deliverables}
(OUT / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
print('PASS fixed modset:', json.dumps(manifest['final_deliverables']), flush=True)
