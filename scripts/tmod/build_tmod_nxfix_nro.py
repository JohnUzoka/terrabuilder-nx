"""Build a tmod candidate (tmod04+, name from TMOD_VARIANT): tmod03 with the NxFix-repaired
tModLoader.dll.

tModLoader.dll's IL changed (see scripts/tmod/nxfix), so its AOT object is recompiled from
the exact file that is packaged. If $TMOD_VARIANT/input/FNA.dll exists (patched by
scripts/patch_fna --tmodloader: Switch controller mapping, latched native button reads,
Game.Tick input hooks), FNA is recompiled from it too and the vanilla port's NxInputDiag.dll
is packaged; otherwise FNA.dll and its object are tmod02's. The CoreLib object and the
launcher object (main_tmod.o, which already registers NxInputDiag's internal calls) are
tmod02's, byte-identical; the link line is tmod02's.
RomFS = tmod03's (1.4.4.9 Content + tModLoader overlay) with tModLoader.dll (and patched FNA)
replaced and the vanilla port's NxCrypto.dll added (managed AES for player saves). NxCrypto and
NxInputDiag run interpreted, not AOT.

Run inside localhost/monobuild:local with /build = ~/.cache/terraria-switch-build and
/mono-nx = recovery46/sdk-pristine. Input: /build/tmod/$TMOD_VARIANT/input/tModLoader.dll from
    NxFix tmod03/romfs/tModLoader.dll $TMOD_VARIANT/input/tModLoader.dll \
        --nxcrypto hint52/aot-final/runtime-romfs/NxCrypto.dll --reference release/tModLoader.dll
"""
import hashlib, json, os, shlex, shutil, subprocess
from pathlib import Path

ROOT = Path('/build')
TMOD = ROOT / 'tmod'
SRC = TMOD / 'tmod02'
PAYLOAD = TMOD / 'tmod03'
VARIANT = os.environ['TMOD_VARIANT']
OUT = TMOD / VARIANT
INPUT = OUT / 'input'
AOT = OUT / 'aot'
NATIVE = OUT / 'native'
CROSS = '/mono-nx/dotnet_runtime/artifacts/bin/mono/linux.x64.Debug/cross/linux-x64/libnx-arm64/mono-aot-cross'
ORIGINAL_LINK_BASE = ROOT / 'aot42-windows'
CONTROL = ROOT / 'hint52/native/candidate'


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def run(argv, label, cwd):
    result = subprocess.run(list(map(str, argv)), env=env, cwd=cwd, capture_output=True, text=True)
    (OUT / (label + '.log')).write_text(result.stdout + result.stderr)
    if result.returncode:
        raise RuntimeError(label + '\n' + result.stdout[-4000:] + result.stderr[-4000:])
    print('PASS ' + label, flush=True)
    return result.stdout


manifest02 = json.loads((SRC / 'manifest.json').read_text())
binding02 = manifest02['provenance_and_binding']
assert sha(binding02['FNA']['aot_object']) == binding02['FNA']['aot_object_sha256']
assert sha(binding02['FNA']['compiler_input_dll']) == binding02['FNA']['compiler_input_sha256']
for stale in (AOT, NATIVE, OUT / 'romfs'):
    assert not stale.exists(), f'fresh {stale} required'
tml = INPUT / 'tModLoader.dll'
assert tml.is_file(), 'run NxFix first'
env = dict(os.environ, PATH='/opt/devkitpro/devkitA64/bin:' + os.environ['PATH'], TOPDIR=str(OUT))
env = dict(os.environ, PATH='/opt/devkitpro/devkitA64/bin:' + os.environ['PATH'], TOPDIR=str(NATIVE))
# 1. AOT: same compiler and options tmod02 used; FNA.dll sits beside tModLoader.dll so it resolves.
NXINPUTDIAG_SHA256 = '78cf76fa72ba2f61868b6650921dbfce235396d8c4519af5d9c87b58bc9ea591'
nxinputdiag = ROOT / 'hint52/aot-final/runtime-romfs/NxInputDiag.dll'
patched_fna = (INPUT / 'FNA.dll').exists()
if patched_fna:
    assert sha(nxinputdiag) == NXINPUTDIAG_SHA256, 'NxInputDiag.dll differs from the vanilla port copy'
    shutil.copy2(nxinputdiag, INPUT / 'NxInputDiag.dll')
else:
    shutil.copy2(SRC / 'input/FNA.dll', INPUT / 'FNA.dll')
fna = INPUT / 'FNA.dll'
tml_sha, fna_sha = sha(tml), sha(fna)
AOT.mkdir()
aot_paths = ['--path=/build/runtime-source/artifacts/bin/mono/libnx.arm64.Release',
             '--path=/build/runtime-source/artifacts/bin/runtime/net9.0-libnx-Release-arm64',
             '--path=/build/tmod/flat_libs', f'--path={INPUT}']
run([CROSS, *aot_paths, f'--aot=full,interp,static,outfile={AOT}/tModLoader.dll.o,tool-prefix=aarch64-none-elf-',
     tml], 'aot-tModLoader', cwd=OUT)
if patched_fna:
    run([CROSS, *aot_paths, f'--aot=full,interp,static,outfile={AOT}/FNA.dll.o,tool-prefix=aarch64-none-elf-',
         fna], 'aot-FNA', cwd=OUT)
else:
    shutil.copy2(SRC / 'aot/FNA.dll.o', AOT / 'FNA.dll.o')
assert sha(tml) == tml_sha and sha(fna) == fna_sha, 'compiler input changed during AOT'
shutil.copy2(SRC / 'aot/System.Private.CoreLib.dll.o', AOT / 'System.Private.CoreLib.dll.o')

# 2. Link: tmod02's recorded arguments, launcher object and runtime swap.
recorded = {}
for line in (ROOT / 'recovery46/link-arguments.txt').read_text().splitlines():
    key, value = line.split('=', 1)
    recorded[key] = shlex.split(value)
objects_dir = ROOT / 'nochroma42/native/interpreter/build'
NATIVE.mkdir()
shutil.copy2(SRC / 'native/main_tmod.o', NATIVE / 'main_tmod.o')
shutil.copy2(ROOT / 'hint52/native/aot-method-tables.ld', NATIVE / 'aot-method-tables.ld')
objs = [NATIVE / 'main_tmod.o' if name == 'main.o' else objects_dir / name for name in recorded['OBJECTS']]
release_lib = ROOT / 'runtime-source/artifacts/obj/mono/libnx.arm64.Release/out/lib'
swap = {'/build/runtime-fix/libmonosgen-2.0.a': str(ROOT / 'runtime-release/libmonosgen-2.0-release.a')}
for component in ('debugger', 'diagnostics_tracing-stub', 'hot_reload-stub', 'marshal-ilgen'):
    name = f'libmono-component-{component}-static.a'
    swap[f'/mono-nx/dotnet_runtime/artifacts/obj/mono/libnx.arm64.Debug/out/lib/{name}'] = str(release_lib / name)
libraries = []
for value in recorded['LIBS']:
    if value.startswith(str(ORIGINAL_LINK_BASE) + '/') and value.endswith('.o'):
        if not libraries or libraries[-1] is not None:
            libraries.append(None)
        continue
    libraries.append(swap.get(value, value))
index = libraries.index(None)
libraries[index:index + 1] = sorted(str(AOT / n) for n in ('System.Private.CoreLib.dll.o', 'tModLoader.dll.o', 'FNA.dll.o'))
candidate = NATIVE / 'candidate'
candidate.mkdir()
run(['aarch64-none-elf-gcc', *recorded['LDFLAGS'], *objs, *recorded['LIBPATHS'], *libraries,
     '-o', candidate / 'tmodloader.elf'], 'link-candidate', cwd=NATIVE)

# 3. RomFS: tmod03's, with tModLoader.dll (and patched FNA) replaced (fresh copy; tmod03 untouched).
romfs = OUT / 'romfs'
shutil.copytree(PAYLOAD / 'romfs', romfs)
(romfs / 'tModLoader.dll').unlink()
shutil.copy2(tml, romfs / 'tModLoader.dll')
if patched_fna:
    (romfs / 'FNA.dll').unlink()
    shutil.copy2(fna, romfs / 'FNA.dll')
    shutil.copy2(nxinputdiag, romfs / 'NxInputDiag.dll')
NXCRYPTO_SHA256 = 'ad4d9c1120a5415face6e0a75dad137f92916929f1f1ff66087aab780ce285d1'
nxcrypto = ROOT / 'hint52/aot-final/runtime-romfs/NxCrypto.dll'
assert sha(nxcrypto) == NXCRYPTO_SHA256, 'NxCrypto.dll differs from the vanilla port copy'
shutil.copy2(nxcrypto, romfs / 'NxCrypto.dll')

# 4. Package with tmod03's NACP, retitled.
nacp = bytearray((PAYLOAD / 'native/candidate/tmodloader.nacp').read_bytes())
title = f'tModLoader NX {VARIANT[4:]} (Zero-Mod)'.encode()
for language in range(16):
    nacp[language * 0x300:language * 0x300 + 0x200] = title.ljust(0x200, b'\0')
(candidate / 'tmodloader.nacp').write_bytes(nacp)
run(['/opt/devkitpro/tools/bin/elf2nro', candidate / 'tmodloader.elf', candidate / 'tmodloader.nro',
     '--nacp=' + str(candidate / 'tmodloader.nacp'), '--romfsdir=' + str(romfs),
     '--icon=' + str(SRC / 'native/icon.jpg')], 'package-candidate', cwd=NATIVE)

nro = candidate / 'tmodloader.nro'
manifest = dict(manifest02, candidate=VARIANT, content=json.loads((PAYLOAD / 'manifest.json').read_text())['content'])
manifest['provenance_and_binding'] = {
    'tModLoader': {'compiler_input_dll': str(tml), 'compiler_input_sha256': tml_sha,
                   'romfs_payload_dll': str(romfs / 'tModLoader.dll'), 'romfs_payload_sha256': sha(romfs / 'tModLoader.dll'),
                   'aot_object': str(AOT / 'tModLoader.dll.o'), 'aot_object_sha256': sha(AOT / 'tModLoader.dll.o')},
    'FNA': {'compiler_input_dll': str(fna), 'compiler_input_sha256': fna_sha,
            'romfs_payload_dll': str(romfs / 'FNA.dll'), 'romfs_payload_sha256': sha(romfs / 'FNA.dll'),
            'aot_object': str(AOT / 'FNA.dll.o'), 'aot_object_sha256': sha(AOT / 'FNA.dll.o')},
    'System.Private.CoreLib': dict(binding02['System.Private.CoreLib'], aot_object=str(AOT / 'System.Private.CoreLib.dll.o')),
}
manifest['final_deliverables'] = {
    'candidate_nro': {'path': str(nro), 'bytes': nro.stat().st_size, 'sha256': sha(nro)},
    'candidate_elf': {'path': str(candidate / 'tmodloader.elf'), 'sha256': sha(candidate / 'tmodloader.elf')}}
(OUT / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
print(f'PASS {VARIANT}:', json.dumps(manifest['final_deliverables']), flush=True)
