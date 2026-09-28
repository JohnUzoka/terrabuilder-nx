"""Build58 native link/package: Release CoreLib/framework + embedded-BCL launcher.

Runs INSIDE localhost/monobuild:local with /build, /work (ro), /mono-nx (ro),
/mono-nx/native (mono-nx fork clone, ro), /fna-install (ro).
Changes vs build52: main.o (MONO_NX_EMBEDDED_BCL=1) and all seven AOT objects.
The other 17 launcher objects, libraries, runtime archive, linker script/specs
and flags are build52's. A control replay of build52 must reproduce its
allocated ELF sections exactly before the candidate is trusted.
"""
import hashlib, json, os, shlex, shutil, struct, subprocess, sys
from pathlib import Path

ROOT = Path('/build')
VARIANT_DIR = ROOT / 'release58' / os.environ.get('R58_VARIANT', '')
OUT = VARIANT_DIR / 'native'
ORIGINAL_LINK_BASE = ROOT / 'aot42-windows'
BASE = ROOT / 'hint52/aot-final'
CANDIDATE = VARIANT_DIR / 'aot-final'
CONTROL = ROOT / 'hint52/native/candidate'
sys.path.insert(0, str(ROOT / 'runtime-fix/python'))
from elftools.elf.elffile import ELFFile


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def allocated(path):
    rows = {}
    with Path(path).open('rb') as stream:
        image = ELFFile(stream)
        for section in image.iter_sections():
            if not section['sh_flags'] & 2:
                continue
            digest = None
            if section['sh_type'] != 'SHT_NOBITS':
                stream.seek(section['sh_offset'])
                state, remaining = hashlib.sha256(), section['sh_size']
                while remaining:
                    data = stream.read(min(1 << 20, remaining)); assert data
                    state.update(data); remaining -= len(data)
                digest = state.hexdigest()
            rows[section.name] = dict(address=section['sh_addr'], size=section['sh_size'], sha256=digest)
    return rows


assert not OUT.exists(), 'fresh native directory required'
assert json.loads((CANDIDATE / 'build-manifest.json').read_text())['status'] == 'complete'
OUT.mkdir(parents=True)
commands = []
env = dict(os.environ, TOPDIR=str(OUT))


def run(argv, label):
    result = subprocess.run(list(map(str, argv)), env=env, cwd=OUT, capture_output=True, text=True)
    (OUT / (label + '.log')).write_text(result.stdout + result.stderr)
    commands.append(dict(argv=list(map(str, argv)), exit=result.returncode, log=label + '.log'))
    if result.returncode:
        raise RuntimeError(label + '\n' + result.stdout + result.stderr)
    print('PASS ' + label, flush=True)


# Launcher main.o: identical recipe to build42's (byte-verified), plus the new flag,
# against build58's registration header.
header58 = (CANDIDATE / 'mono_aot_modules.h').read_text().splitlines()[1:]
header52 = (BASE / 'mono_aot_modules.h').read_text().splitlines()[-7:]
assert header58[:7] == header52, 'AOT registration symbols of the seven build52 modules changed'
extra_symbols = header58[7:]
# Extra framework modules (build59): every object not among build52's seven.
base_objects = {Path(v).name for v in shlex.split((ROOT / 'recovery46/link-arguments.txt').read_text().split('LIBS=', 1)[1].splitlines()[0])
                if v.startswith(str(ORIGINAL_LINK_BASE) + '/') and v.endswith('.o')}
# LLVM sidecars (build60) carry no registration of their own: they are linked right
# after their module object and are not part of the registered/verified module set.
llvm_objects = sorted(CANDIDATE.glob('*-llvm.o'))
extra_objects = sorted(p for p in CANDIDATE.glob('*.o') if p.name not in base_objects and p not in llvm_objects)
assert len(extra_objects) == len(extra_symbols), (extra_objects, extra_symbols)
main_obj = OUT / 'main.o'
# R58_MAIN_DEFINES adds launcher-only defines, e.g. "-DMONO_NX_GC_STATS=1" (build66).
run(['/build/release58/compile_main.sh', '/work/native/interpreter/source/main.c', main_obj,
     '-DMONO_NX_EMBEDDED_BCL=1', '-DMONO_NX_FATAL_DIAG=1', *os.environ.get('R58_MAIN_DEFINES', '').split(),
     '-I' + str(CANDIDATE)], 'compile-main')

recorded = {}
for line in (ROOT / 'recovery46/link-arguments.txt').read_text().splitlines():
    key, value = line.split('=', 1)
    recorded[key] = shlex.split(value)
objects_dir = ROOT / 'nochroma42/native/interpreter/build'
objects = [objects_dir / name for name in recorded['OBJECTS']]
assert len(objects) == 18 and all(p.is_file() for p in objects)
shutil.copy2(ROOT / 'hint52/native/aot-method-tables.ld', OUT / 'aot-method-tables.ld')
assert (OUT / 'aot-method-tables.ld').read_bytes() == (ROOT / 'nochroma42/native/interpreter/aot-method-tables.ld').read_bytes()
# R58_RUNTIME=release (build65): the candidate links the Release native Mono runtime and its
# four statically linked components instead of the Debug-config ones. Same fork commit and
# allocator patch; struct layouts verified identical. The control replay keeps build52's.
RUNTIME_SWAP = {}
if os.environ.get('R58_RUNTIME') == 'release':
    release_lib = ROOT / 'runtime-source/artifacts/obj/mono/libnx.arm64.Release/out/lib'
    RUNTIME_SWAP = {'/build/runtime-fix/libmonosgen-2.0.a': str(ROOT / 'runtime-release/libmonosgen-2.0-release.a')}
    for component in ('debugger', 'diagnostics_tracing-stub', 'hot_reload-stub', 'marshal-ilgen'):
        name = f'libmono-component-{component}-static.a'
        RUNTIME_SWAP[f'/mono-nx/dotnet_runtime/artifacts/obj/mono/libnx.arm64.Debug/out/lib/{name}'] = str(release_lib / name)
    assert all(key in recorded['LIBS'] for key in RUNTIME_SWAP), 'recorded runtime library paths changed'
    assert all(Path(value).is_file() for value in RUNTIME_SWAP.values())
if llvm_objects:
    # Mono's LLVM output keeps absolute pointers in .rodata (148 for Terraria). A PIE NRO
    # must relocate them at load time, so that .rodata goes into the writable data segment.
    # The pattern only matches *-llvm.o, so the build52 control replay is unaffected.
    with (OUT / 'aot-method-tables.ld').open('a') as script:
        script.write('\n/* Mono LLVM AOT sidecars: absolute pointers in .rodata need load-time relocation. */\n'
                     'SECTIONS\n{\n    .mono_llvm_rodata : ALIGN(16)\n    {\n'
                     '        /* A writable input first makes ld mark the output section SHF_WRITE. */\n'
                     '        *-llvm.o(.data .data.*)\n        *-llvm.o(.rodata .rodata.*)\n'
                     '    } :data\n}\nINSERT BEFORE .data.rel.ro;\n')

for variant in ('control-replay', 'candidate'):
    folder = OUT / variant
    folder.mkdir()
    flags = [('-Wl,-Map,' + str(OUT / (variant + '.map'))) if f.startswith('-Wl,-Map,') else f for f in recorded['LDFLAGS']]
    objs = [main_obj if (variant == 'candidate' and o.name == 'main.o') else o for o in objects]
    libraries, aot_objects = [], []
    for value in recorded['LIBS']:
        if value.startswith(str(ORIGINAL_LINK_BASE) + '/') and value.endswith('.o'):
            aot_objects.append(str((CANDIDATE if variant == 'candidate' else BASE) / Path(value).name))
            if len(aot_objects) == 1:
                libraries.append(None)  # placeholder: the AOT objects stay contiguous here
            continue
        libraries.append(RUNTIME_SWAP.get(value, value) if variant == 'candidate' else value)
    # The native verifier maps linked method tables to modules by object name order,
    # so extra framework modules join the recorded (name-sorted) AOT run in sorted order.
    if variant == 'candidate':
        aot_objects += [str(p) for p in extra_objects]
    aot_objects.sort(key=lambda p: Path(p).name)
    if variant == 'candidate':
        # each sidecar directly follows its module object (name-sorted run unchanged)
        for sidecar in llvm_objects:
            owner = str(CANDIDATE / sidecar.name.replace('-llvm.o', '.o'))
            aot_objects.insert(aot_objects.index(owner) + 1, str(sidecar))
    i = libraries.index(None)
    libraries[i:i + 1] = aot_objects
    run(['/opt/devkitpro/devkitA64/bin/aarch64-none-elf-gcc', *flags, *objs, *recorded['LIBPATHS'], *libraries,
         '-o', folder / 'mono_nx_fna.elf'], 'link-' + variant)
    if variant == 'control-replay':
        assert allocated(CONTROL / 'mono_nx_fna.elf') == allocated(folder / 'mono_nx_fna.elf'), 'build52 replay differs'
        print('PASS exact52 allocated-section replay', flush=True)

with (CONTROL / 'mono_nx_fna.nro').open('rb') as stream:
    header = stream.read(128); assert header[16:20] == b'NRO0'
    image_size = struct.unpack_from('<I', header, 24)[0]
    stream.seek(image_size); assets = stream.read(56); assert assets[:4] == b'ASET'
    icon_offset, icon_size, nacp_offset, nacp_size = struct.unpack_from('<4Q', assets, 8)
    stream.seek(image_size + nacp_offset); original_nacp = stream.read(nacp_size)
    stream.seek(image_size + icon_offset); icon = stream.read(icon_size)
nacp = bytearray(original_nacp)
new_title = os.environ.get('R58_TITLE', 'Terraria 58 Release BCL').encode()
changed = []
for language in range(16):
    start = language * 0x300
    old = bytes(nacp[start:start + 0x200]).split(b'\0', 1)[0]
    assert old in (b'', b'Terraria 52 Hint Guard')
    if old:
        nacp[start:start + 0x200] = new_title.ljust(0x200, b'\0'); changed.append(language)
assert 0 in changed
nacp_path = OUT / 'candidate/mono_nx_fna.nacp'; nacp_path.write_bytes(nacp)
icon_path = OUT / 'icon.jpg'; icon_path.write_bytes(icon)
run(['/opt/devkitpro/tools/bin/elf2nro', OUT / 'candidate/mono_nx_fna.elf', OUT / 'candidate/mono_nx_fna.nro',
     '--nacp=' + str(nacp_path), '--romfsdir=' + str(CANDIDATE / 'runtime-romfs'), '--icon=' + str(icon_path)], 'package-candidate')
report = dict(passed=True, controlAllocatedSectionsIdentical=True, commands=commands, title=new_title.decode(),
              mainObjectSha256=sha(main_obj), candidateElfSha256=sha(OUT / 'candidate/mono_nx_fna.elf'),
              candidateNroSha256=sha(OUT / 'candidate/mono_nx_fna.nro'),
              candidateNroBytes=(OUT / 'candidate/mono_nx_fna.nro').stat().st_size)
(OUT / 'native-build.json').write_text(json.dumps(report, indent=2) + '\n')
print('Native58 link/package PASS', flush=True)
