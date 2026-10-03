"""Build tModLoader experimental NRO (tmod02 candidate).
Links System.Private.CoreLib, tModLoader.dll, FNA.dll AOT objects with Release Mono runtime (b8a2fe0c2dd).
Stages RomFS with full Terraria content + tModLoader compatibility files + assemblies.
"""
import hashlib, json, os, shlex, shutil, struct, subprocess, sys
from pathlib import Path

ROOT = Path('/build')
TMOD_DIR = ROOT / 'tmod'
OUT = TMOD_DIR / 'tmod02' / 'native'
AOT_DIR = TMOD_DIR / 'tmod02' / 'aot'
ROMFS = TMOD_DIR / 'tmod02' / 'romfs'
ORIGINAL_LINK_BASE = ROOT / 'aot42-windows'
BASE = ROOT / 'hint52/aot-final'
CONTROL = ROOT / 'hint52/native/candidate'

def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()

OUT.mkdir(parents=True, exist_ok=True)
commands = []
env = dict(os.environ, TOPDIR=str(OUT))

def run(argv, label):
    result = subprocess.run(list(map(str, argv)), env=env, cwd=OUT, capture_output=True, text=True)
    (OUT / (label + '.log')).write_text(result.stdout + result.stderr)
    commands.append(dict(argv=list(map(str, argv)), exit=result.returncode, log=label + '.log'))
    if result.returncode:
        raise RuntimeError(label + '\n' + result.stdout + result.stderr)
    print('PASS ' + label, flush=True)

# 1. Compile launcher main_tmod.o
main_obj = OUT / 'main_tmod.o'
run(['/build/release58/compile_main.sh', str(TMOD_DIR / 'launcher_build/main_tmod.c'), str(main_obj),
     '-DMONO_NX_EMBEDDED_BCL=1', '-DMONO_NX_FATAL_DIAG=1',
     '-I' + str(TMOD_DIR / 'tmod_aot')], 'compile-main')

# 2. Linker configuration
recorded = {}
for line in (ROOT / 'recovery46/link-arguments.txt').read_text().splitlines():
    key, value = line.split('=', 1)
    recorded[key] = shlex.split(value)
objects_dir = ROOT / 'nochroma42/native/interpreter/build'
objects = [objects_dir / name for name in recorded['OBJECTS']]

shutil.copy2(ROOT / 'hint52/native/aot-method-tables.ld', OUT / 'aot-method-tables.ld')

release_lib = ROOT / 'runtime-source/artifacts/obj/mono/libnx.arm64.Release/out/lib'
RUNTIME_SWAP = {'/build/runtime-fix/libmonosgen-2.0.a': str(ROOT / 'runtime-release/libmonosgen-2.0-release.a')}
for component in ('debugger', 'diagnostics_tracing-stub', 'hot_reload-stub', 'marshal-ilgen'):
    name = f'libmono-component-{component}-static.a'
    RUNTIME_SWAP[f'/mono-nx/dotnet_runtime/artifacts/obj/mono/libnx.arm64.Debug/out/lib/{name}'] = str(release_lib / name)

objs = [main_obj if o.name == 'main.o' else o for o in objects]
aot_objects = [
    str(AOT_DIR / 'System.Private.CoreLib.dll.o'),
    str(AOT_DIR / 'tModLoader.dll.o'),
    str(AOT_DIR / 'FNA.dll.o')
]
aot_objects.sort(key=lambda p: Path(p).name)

libraries = []
for value in recorded['LIBS']:
    if value.startswith(str(ORIGINAL_LINK_BASE) + '/') and value.endswith('.o'):
        if len(libraries) == 0 or libraries[-1] is not None:
            libraries.append(None)
        continue
    libraries.append(RUNTIME_SWAP.get(value, value))

i = libraries.index(None)
libraries[i:i + 1] = aot_objects

flags = [f for f in recorded['LDFLAGS']]

folder = OUT / 'candidate'
folder.mkdir(parents=True, exist_ok=True)
run(['/opt/devkitpro/devkitA64/bin/aarch64-none-elf-gcc', *flags, *objs, *recorded['LIBPATHS'], *libraries,
     '-o', folder / 'tmodloader.elf'], 'link-candidate')

with (CONTROL / 'mono_nx_fna.nro').open('rb') as stream:
    header = stream.read(128); assert header[16:20] == b'NRO0'
    image_size = struct.unpack_from('<I', header, 24)[0]
    stream.seek(image_size); assets = stream.read(56); assert assets[:4] == b'ASET'
    icon_offset, icon_size, nacp_offset, nacp_size = struct.unpack_from('<4Q', assets, 8)
    stream.seek(image_size + nacp_offset); original_nacp = stream.read(nacp_size)
    stream.seek(image_size + icon_offset); icon = stream.read(icon_size)

nacp = bytearray(original_nacp)
new_title = b'tModLoader NX (Zero-Mod)'
for language in range(16):
    start = language * 0x300
    nacp[start:start + 0x200] = new_title.ljust(0x200, b'\0')

nacp_path = folder / 'tmodloader.nacp'
nacp_path.write_bytes(nacp)
icon_path = OUT / 'icon.jpg'
icon_path.write_bytes(icon)

run(['/opt/devkitpro/tools/bin/elf2nro', str(folder / 'tmodloader.elf'), str(folder / 'tmodloader.nro'),
     '--nacp=' + str(nacp_path), '--romfsdir=' + str(ROMFS), '--icon=' + str(icon_path)], 'package-candidate')

print(f"Candidate NRO created at: {folder / 'tmodloader.nro'}")
print(f"Size: {(folder / 'tmodloader.nro').stat().st_size} bytes")
print(f"SHA256: {sha(folder / 'tmodloader.nro')}")
