"""Build one pinned mod set, with exact .tmod DLLs compiled into the NRO.

Run in monobuild-llvm:local with the CLI's mounts: the work directory at /build, this repo at
/work and the toolchain (with its tmod-runtime component) at /toolchain. TMOD_BASE is the base
stage (romfs/, flat_libs/, native/{icon.jpg,tmodloader.nacp}, manifest.json); TMOD_VARIANT names
a fresh /build/tmod/<variant> with input/modset.json:
  {"mod": "BossCursor", "label": "Boss Cursor", "aot_replacements": [],
   "replacements": {"System.Reflection.Metadata.dll": ["System.Reflection.Metadata.dll"]}}
input also contains tModLoader.dll, FNA.dll, <mod>.tmod and all replacement DLLs.
The CoreLib object is the base manifest's (tmod-runtime/corelib-aot: enlarged trampoline pools).
"llvm_modules": [compiled DLL names] compiles those through Mono's LLVM backend (the tmod-runtime
mono-aot-cross). A compiled framework DLL whose bytes, LLVM use and inline limit match a
tmod-runtime/framework-aot record reuses that object instead of compiling.
"aot_inline_limits": {"tModLoader.dll": 60} sets MONO_INLINELIMIT while compiling those DLLs:
the IL size below which Mono inlines a callee (defaults: 30 under LLVM AOT, 20 without).
"native": {"libnx_heap_permille": 625, "gc_stats": true} swaps the fork's 50/50 heap.o for
launcher/heap_split.c at that libnx share and enables the 5 s NX_GC memory log; "mesa_guard": true
(needs libnx_heap_permille) links launcher/mesa_guard.c, which moves Mesa's nouveau_mm caches onto
read-only guard pages, logs nouveau_bo_new failures and installs a logging exception handler
(diagnostic for tmod21-23); "nv_transfermem_mb": N (needs libnx_heap_permille) replaces libnx's
8 MB nvdrv transfer memory, which tmod21-23 exhausted; "frame_stats": true links
launcher/frame_stats.c (--wrap=SDL_GL_SwapWindow): a 5 s NX_FPS log of presented frames, frame-time
classes and nouveau_bo_new churn (it wraps nouveau_bo_new itself unless mesa_guard does).
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

from tmod_file import extract_tmod_member, loader_version_problem

ROOT = Path('/build')
BASE = Path(os.environ['TMOD_BASE'])
VARIANT = os.environ['TMOD_VARIANT']
assert Path(VARIANT).name == VARIANT and VARIANT.startswith('tmod')
OUT = ROOT / 'tmod' / VARIANT
INPUT = OUT / 'input'
AOT = OUT / 'aot'
NATIVE = OUT / 'native'
ROMFS = OUT / 'romfs'
SCRIPTS = Path(__file__).resolve().parent
COMPILE_MAIN = '/work/scripts/native/compile_main.sh'
SHARED_INCLUDE = '-I/work/native/shared'
TMOD_RUNTIME = Path('/toolchain/tmod-runtime')
CROSS = '/mono-nx/dotnet_runtime/artifacts/bin/mono/linux.x64.Debug/cross/linux-x64/libnx-arm64/mono-aot-cross'
# The tModLoader mono-aot-cross has an empty RUNPATH: it finds libc++ through LD_LIBRARY_PATH.
LLVM_DIR = TMOD_RUNTIME / 'aot-compiler'
env = dict(os.environ, PATH='/opt/devkitpro/devkitA64/bin:' + os.environ['PATH'], TOPDIR=str(NATIVE))


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def run(argv, label, cwd, extra_env=None):
    process = subprocess.run(list(map(str, argv)), cwd=cwd, env=dict(env, **extra_env) if extra_env else env,
                             capture_output=True, text=True)
    (OUT / (label + '.log')).write_text(process.stdout + process.stderr)
    if process.returncode:
        raise RuntimeError(label + '\n' + process.stdout[-4000:] + process.stderr[-4000:])
    print('PASS ' + label, flush=True)


config = json.loads((INPUT / 'modset.json').read_text())
mod = config.get('mod', '')
assert not mod or mod.isidentifier(), 'Expected one named mod, no paths'
additional_mods = config.get('additional_mods', [])
assert len(additional_mods) == len(set(additional_mods)), 'Duplicate additional_mods'
assert not mod or mod not in additional_mods, 'Primary mod cannot be in additional_mods'
all_mods = ([mod] if mod else []) + additional_mods
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
skip_aot_modules = config.get('skip_aot_modules', [])
assert len(skip_aot_modules) == len(set(skip_aot_modules))
assert not skip_aot_modules, 'tModLoader.dll AOT is required for this launcher'

llvm_modules = config.get('llvm_modules', [])
assert len(llvm_modules) == len(set(llvm_modules))
# The patched FNA (romfs:/ base directory, shared audio device) is always compiled with the set.
assert config.get('compile_fna') or 'FNA.dll' in llvm_modules, 'FNA.dll must be compiled (compile_fna or llvm_modules)'
aot_workers = int(config.get('aot_workers', 4))
assert aot_workers >= 1
aot_inline_limits = config.get('aot_inline_limits', {})
assert all(type(limit) is int and limit > 0 for limit in aot_inline_limits.values()), aot_inline_limits

for filename, destinations in replacements.items():
    assert Path(filename).name == filename and filename.endswith('.dll')
    assert destinations
    for destination in destinations:
        relative = PurePosixPath(destination)
        assert not relative.is_absolute() and '..' not in relative.parts
        assert (BASE / 'romfs' / destination).is_file(), destination
for directory in (AOT, NATIVE, ROMFS, OUT / 'sdcard', OUT / 'extracted'):
    assert not directory.exists(), f'Fresh output required: {directory}'
container_tmp = OUT / 'container-tmp'
container_tmp.mkdir(parents=True, exist_ok=True)
env['TMPDIR'] = str(container_tmp)

mod_bytes_by_name = {}
mod_info_by_name = {}
external_members = {}

for m in all_mods:
    pkg = INPUT / (m + '.tmod')
    mbr = m + '.dll'
    assert mbr not in external_members, f'Duplicate assembly name: {mbr}'
    m_bytes, m_info = extract_tmod_member(pkg, mbr)
    assert m_info['name'] == m and not loader_version_problem(m_info['loader_version']), m_info
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

assert (INPUT / 'FNA.dll').is_file(), 'input/FNA.dll (the shared-audio FNA) is required'

for filename in extra_aot:
    assert Path(filename).name == filename and filename.endswith('.dll')
    assert filename != 'FNA.dll', 'Use compile_fna or llvm_modules for FNA'
    assert filename not in replacements, f'{filename} is in replacements'
    if not (INPUT / filename).exists():
        assert (BASE / 'romfs' / filename).is_file(), f'Extra AOT DLL missing from BASE romfs: {filename}'
        shutil.copy2(BASE / 'romfs' / filename, INPUT / filename)

compiled = ['tModLoader.dll', 'FNA.dll']
compiled.extend(aot_replacements)
compiled.extend(extra_aot)
compiled.extend(external_members.keys())
assert len(compiled) == len(set(compiled)), 'Duplicate compiled assembly'
input_hashes = {name: sha(INPUT / name) for name in set(compiled) | replacements.keys() | {'FNA.dll'}}
baseline = json.loads((BASE / 'manifest.json').read_text())
binding_base = baseline['provenance_and_binding']
AOT.mkdir()

corelib = binding_base['System.Private.CoreLib']
assert corelib['compiler_input_sha256'] == sha(BASE / 'romfs/mono/lib_net9.0/System.Private.CoreLib.dll')
for key, suffix in (('aot_object', '.dll.o'), ('llvm_object', '.dll-llvm.o')):
    assert sha(corelib[key]) == corelib[key + '_sha256'], f'CoreLib {key} changed'
    shutil.copy2(corelib[key], AOT / ('System.Private.CoreLib' + suffix))

FRAMEWORK_AOT = TMOD_RUNTIME / 'framework-aot'
framework_aot = json.loads((FRAMEWORK_AOT / 'framework-aot.json').read_text())['modules']
reused = []
for name in compiled:
    rec = framework_aot.get(name.removesuffix('.dll'))
    if (rec is None or rec['compiler_input_sha256'] != input_hashes[name] or name in aot_inline_limits
            or ('llvm_object' in rec) != (name in llvm_modules)):
        continue
    for key, suffix in (('aot_object', '.o'), ('llvm_object', '-llvm.o')):
        if key in rec:
            assert sha(FRAMEWORK_AOT / rec[key]) == rec[key + '_sha256'], f'framework-aot {rec[key]} changed'
            shutil.copy2(FRAMEWORK_AOT / rec[key], AOT / (name + suffix))
    if 'log' in rec:
        shutil.copy2(FRAMEWORK_AOT / rec['log'], OUT / f'aot-{name}.log')
    reused.append(name)
    print(f'PASS reuse aot-{name} from tmod-runtime/framework-aot (input {input_hashes[name][:12]}...)', flush=True)
paths = ['--path=' + str(TMOD_RUNTIME / 'corelib'),
         '--path=' + str(INPUT),
         '--path=' + str(TMOD_RUNTIME / 'framework'),
         '--path=' + str(BASE / 'flat_libs')]


assert set(llvm_modules) <= set(compiled), 'llvm_modules must name compiled DLLs'
assert set(aot_inline_limits) <= set(compiled), 'aot_inline_limits must name compiled DLLs'


def compile_one(name):
    options = f'full,interp,static,outfile={AOT}/{name}.o,tool-prefix=aarch64-none-elf-'
    limit_env = {'MONO_INLINELIMIT': str(aot_inline_limits[name])} if name in aot_inline_limits else {}
    if name not in llvm_modules:
        run([CROSS, *paths, '--aot=' + options, INPUT / name], 'aot-' + name, OUT, limit_env)
    else:
        temp = OUT / ('llvm-tmp-' + name)
        temp.mkdir()
        options += f',llvm-path={LLVM_DIR}/,llvm-outfile={AOT}/{name}-llvm.o,temp-path={temp}'
        run_env = dict(env, LD_LIBRARY_PATH=str(LLVM_DIR), **limit_env)
        process = subprocess.run(list(map(str, [LLVM_DIR / 'mono-aot-cross', '--llvm', *paths, '--aot=' + options, INPUT / name])),
                                 cwd=OUT, env=run_env, capture_output=True, text=True)
        (OUT / ('aot-' + name + '.log')).write_text(process.stdout + process.stderr)
        if process.returncode:
            raise RuntimeError('aot-' + name + '\n' + process.stdout[-4000:] + process.stderr[-4000:])
        shutil.rmtree(temp)
        print('PASS aot-' + name + ' (LLVM)', flush=True)
    native_object = AOT / (name + '.o')
    if not native_object.is_file() or native_object.stat().st_size == 0:
        raise RuntimeError(f'aot-{name} completed without producing {native_object.name}')
    if name in llvm_modules:
        llvm_object = AOT / (name + '-llvm.o')
        if not llvm_object.is_file() or llvm_object.stat().st_size == 0:
            raise RuntimeError(f'aot-{name} completed without producing {llvm_object.name}')


to_compile = [name for name in compiled if name not in reused]
if to_compile:
    with ThreadPoolExecutor(max_workers=max(1, min(aot_workers, len(to_compile)))) as pool:
        list(pool.map(compile_one, to_compile))
assert all(sha(INPUT / name) == digest for name, digest in input_hashes.items()), 'AOT input changed'
NATIVE.mkdir()
modules = ['System.Private.CoreLib']
for filename in compiled:
    modules.append(filename.removesuffix('.dll'))
assert len(modules) == len(set(modules)), 'Duplicate module in registration'
assert all(name.replace('.', '_').isidentifier() for name in modules)
(NATIVE / 'mono_aot_modules.h').write_text(''.join(
    'REGISTER_AOT_MODULE(mono_aot_module_' + name.replace('.', '_') + '_info);\n' for name in modules))
native_config = config.get('native', {})
assert set(native_config) <= {'libnx_heap_permille', 'gc_stats', 'mesa_guard', 'nv_transfermem_mb', 'frame_stats', 'repo_nx_input', 'real_audio', 'debug_diagnostics', 'profiler'}, 'Unknown native key'
main_defines = ['-DMONO_NX_EMBEDDED_BCL=1', '-DMONO_NX_FATAL_DIAG=1']
if native_config.get('gc_stats'):
    main_defines.append('-DMONO_NX_GC_STATS=1')
if native_config.get('profiler'):
    main_defines.append('-DMONO_NX_PROFILER=1')
main_source = SCRIPTS / 'launcher/main_tmod.c'
if native_config.get('real_audio'):
    patched_main = NATIVE / 'main_tmod_real_audio.c'
    lines = main_source.read_text().splitlines(keepends=True)
    main_text = ''.join(line for line in lines if 'SDL_AUDIODRIVER", "dummy"' not in line)
    if 'SDL_AUDIODRIVER", "dummy"' in main_text:
        raise RuntimeError('failed to remove tModLoader dummy audio driver default')
    patched_main.write_text(main_text)
    main_source = patched_main
if native_config.get('profiler'):
    # tModLoader runs its game loop on the thread that calls mono_jit_exec on non-Windows
    # platforms: libnx's main thread, which is the thread nx_profiler samples.
    lines = main_source.read_text().splitlines(keepends=True)
    calls = [i for i, line in enumerate(lines) if line.lstrip().startswith('mono_jit_exec(')]
    if len(calls) != 1:
        raise RuntimeError(f'expected one mono_jit_exec call in {main_source}, found {len(calls)}')
    call = lines[calls[0]]
    indent = call[:len(call) - len(call.lstrip())]
    lines[calls[0]:calls[0] + 1] = [indent + 'nx_profiler_start();\n', call, indent + 'nx_profiler_stop();\n']
    patched_main = NATIVE / 'main_tmod_profiler.c'
    patched_main.write_text('void nx_profiler_start(void);\nvoid nx_profiler_stop(void);\n' + ''.join(lines))
    main_source = patched_main
if 'libnx_heap_permille' in native_config:
    # Without max-heap-size, SGen does not know where the fake-mmap region (heap_split.c) ends:
    # it lets the heap grow a third past what survived the last major collection, which after
    # world load lies beyond the region. Run 43 ran out of memory at its second autosave with
    # about 390 MB collectable. A cap alone is not enough: in Ryujinx (v17j, cap 880 MB) the
    # first autosave still failed, because the collection forced at the cap frees memory only
    # after its lazy/concurrent sweep, after the allocation has already failed. So, unless
    # /mono/gc_params.txt overrides it: cap the heap 160 MB below the region (the nursery,
    # SGen internals and region overhead are not counted), start major collections 256 MB
    # below the cap (soft-heap-limit), and sweep synchronously. v17k (same cap, these options
    # with a 132 MB gap) saved without error, and run 44 loaded its world and saved four times.
    # v18 narrowed the gap to 64 MB to cut the forced collections at each autosave; on hardware
    # (runs 45 and 46, caps 976 and 968 MB) world loading then ran out of memory with only
    # 500-620 MB in use. Below the soft limit majors are not forced and garbage piles up until a
    # load burst reaches the cap; above it every major is forced, which is what kept run 44 safe.
    main_text = main_source.read_text()
    gc_open = '    FILE *f = fopen(GC_PARAMS_PATH, "r");\n    if (!f)\n        return;\n'
    gc_function = 'static void apply_gc_params_file(void)\n'
    if main_text.count(gc_open) != 1 or main_text.count(gc_function) != 1:
        raise RuntimeError(f'expected one apply_gc_params_file opening /mono/gc_params.txt in {main_source}')
    gc_default = (
        'size_t mono_nx_mono_heap_bytes(void);\n'
        '#define NX_GC_RESERVE_MB 160\n'
        '#define NX_GC_SOFT_GAP_MB 256\n'
        'static void nx_default_gc_params(void)\n'
        '{\n'
        '    size_t region_mb = mono_nx_mono_heap_bytes() >> 20;\n'
        '    if (region_mb <= 2 * (NX_GC_RESERVE_MB + NX_GC_SOFT_GAP_MB))\n'
        '        return;\n'
        '    char value[128];\n'
        '    snprintf(value, sizeof value, "max-heap-size=%zum,soft-heap-limit=%zum,no-lazy-sweep,no-concurrent-sweep",\n'
        '        region_mb - NX_GC_RESERVE_MB, region_mb - NX_GC_RESERVE_MB - NX_GC_SOFT_GAP_MB);\n'
        '    setenv("MONO_GC_PARAMS", value, 1);\n'
        '    io_debugf("NX_GC params=%s (default for the %zu MB managed region)", value, region_mb);\n'
        '}\n\n')
    main_text = main_text.replace(gc_function, gc_default + gc_function).replace(
        gc_open, '    FILE *f = fopen(GC_PARAMS_PATH, "r");\n    if (!f) {\n        nx_default_gc_params();\n        return;\n    }\n')
    patched_main = NATIVE / 'main_tmod_gc.c'
    patched_main.write_text(main_text)
    main_source = patched_main
run([COMPILE_MAIN, main_source, NATIVE / 'main_tmod.o',
     *main_defines, '-I' + str(NATIVE), SHARED_INCLUDE], 'compile-main', NATIVE)
object_overrides = {'main.o': NATIVE / 'main_tmod.o'}
added_objects = {}
extra_ldflags = []
mesa_guard = native_config.get('mesa_guard', False)
assert not mesa_guard or 'libnx_heap_permille' in native_config, 'mesa_guard needs libnx_heap_permille'
nv_transfermem_mb = native_config.get('nv_transfermem_mb')
assert nv_transfermem_mb is None or 'libnx_heap_permille' in native_config, 'nv_transfermem_mb needs libnx_heap_permille'
assert nv_transfermem_mb is None or (isinstance(nv_transfermem_mb, int) and 8 <= nv_transfermem_mb <= 512), \
    'nv_transfermem_mb out of range'
if 'libnx_heap_permille' in native_config:
    permille = native_config['libnx_heap_permille']
    assert isinstance(permille, int) and 250 <= permille <= 850, 'libnx_heap_permille out of range'
    heap_defines = ['-DMONO_NX_LIBNX_HEAP_PERMILLE=%d' % permille]
    if mesa_guard:
        heap_defines.append('-DMONO_NX_GUARD_PAGES=4')
    if nv_transfermem_mb:
        heap_defines.append('-DMONO_NX_NV_TRANSFERMEM_MB=%d' % nv_transfermem_mb)
    run([COMPILE_MAIN, SCRIPTS / 'launcher/heap_split.c',
         NATIVE / 'heap_split.o', *heap_defines, SHARED_INCLUDE], 'compile-heap', NATIVE)
    object_overrides['heap.o'] = NATIVE / 'heap_split.o'
if mesa_guard:
    run([COMPILE_MAIN, SCRIPTS / 'launcher/mesa_guard.c',
         NATIVE / 'mesa_guard.o', SHARED_INCLUDE], 'compile-mesa-guard', NATIVE)
    added_objects['mesa_guard.o'] = NATIVE / 'mesa_guard.o'
    extra_ldflags.append('-Wl,' + ','.join('--wrap=nouveau_mm_' + name
                                          for name in ('create', 'allocate', 'free', 'free_work', 'destroy'))
                        + ',--wrap=nouveau_bo_new')
if native_config.get('frame_stats'):
    frame_defines = [] if mesa_guard else ['-DMONO_NX_FRAME_STATS_BO_WRAP=1']
    run([COMPILE_MAIN, SCRIPTS / 'launcher/frame_stats.c',
         NATIVE / 'frame_stats.o', *frame_defines, SHARED_INCLUDE], 'compile-frame-stats', NATIVE)
    added_objects['frame_stats.o'] = NATIVE / 'frame_stats.o'
    extra_ldflags.append('-Wl,--wrap=SDL_GL_SwapWindow' + ('' if mesa_guard else ',--wrap=nouveau_bo_new')
                         + ',--wrap=FNA3D_DrawIndexedPrimitives,--wrap=FNA3D_DrawInstancedPrimitives'
                         + ',--wrap=FNA3D_DrawPrimitives')
if native_config.get('repo_nx_input'):
    mono_inc = '/mono-nx/dotnet_runtime/artifacts/bin/mono/libnx.arm64.Debug/include/mono-2.0'
    nx_input_flags = [
        'aarch64-none-elf-gcc', '-march=armv8-a+crc+crypto', '-mtune=cortex-a57', '-mtp=soft',
        '-fPIE', '-g', '-O2', '-ffunction-sections', '-Wall', '-Wextra',
        '-D__SWITCH__', '-DMONO_NX_USE_AOT=1', '-DMONO_NX_GL_COMPAT=1', '-DMONO_NX_USE_ROMFS=1',
        '-DDLSHIM_SDL2=1', '-DDLSHIM_SDL2_IMAGE=1', '-DDLSHIM_OPENGL=1',
        '-DDLSHIM_FNA3D=1', '-DDLSHIM_FNA=1', '-DDLSHIM_STUBS=1', '-DDLSHIM_SDL3=1',
        '-DU_DISABLE_RENAMING=1', '-DMONO_NX_SDL_BUTTON_WRAP=1', '-DMONO_NX_SWKBD=1',
        '-I/work/native/shared', '-I/mono-nx/native/shared',
        '-I/mono-nx/native/shared/third_party/ini', '-I' + mono_inc, '-I/mono-nx/icu/libnx/include',
        '-I/opt/devkitpro/libnx/include', '-I/opt/devkitpro/portlibs/switch/include',
        '-I/opt/devkitpro/portlibs/switch/include/SDL2', '-I/fna-install/include',
        '-c', '/work/native/shared/nx_input.c', '-o', NATIVE / 'nx_input.o'
    ]
    if native_config.get('debug_diagnostics'):
        nx_input_flags.extend(['-DMONO_NX_PHASE_TIMING=1', '-DMONO_NX_GPU_TIMING=1'])
    run(nx_input_flags, 'compile-nx-input', NATIVE)
    object_overrides['nx_input.o'] = NATIVE / 'nx_input.o'
    # tModLoader's FNA reads buttons straight from SDL; nx_input.c handles Plus+Minus there.
    extra_ldflags.append('-Wl,--wrap=SDL_GameControllerGetButton')
    # The Switch keyboard for text boxes (MONO_NX_SWKBD) runs when an SDL_PollEvent drain
    # ends. Debug and profiler builds already wrap SDL_PollEvent for frame timing.
    if not any('--wrap=SDL_PollEvent' in flag for flag in config.get('extra_ldflags', [])):
        extra_ldflags.append('-Wl,--wrap=SDL_PollEvent')
launcher_config = config['launcher_objects']
assert set(launcher_config) <= {'manifest', 'extra_objects'}, 'Unknown launcher_objects key'
launcher_manifest = json.loads(Path(launcher_config['manifest']).read_text())
shutil.copy2('/work/native/interpreter/aot-method-tables.ld', NATIVE / 'aot-method-tables.ld')
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
for line in (SCRIPTS / 'launcher/link-arguments.txt').read_text().splitlines():
    key, value = line.split('=', 1)
    recorded[key] = shlex.split(value)
# Every launcher object comes from the launcher stage, except the ones compiled above.
launcher_records = launcher_manifest['objects']
for name in recorded['OBJECTS']:
    if name in object_overrides:
        continue
    rec = launcher_records[name]
    assert sha(rec['path']) == rec['sha256'], f'launcher object hash mismatch: {name}'
    object_overrides[name] = Path(rec['path'])
for name in launcher_config.get('extra_objects', []):
    assert name not in recorded['OBJECTS'], f'launcher extra is already in recorded objects: {name}'
    assert name not in added_objects, f'launcher extra conflicts with tmod object: {name}'
    rec = launcher_records[name]
    assert sha(rec['path']) == rec['sha256'], f'launcher extra hash mismatch: {name}'
    added_objects[name] = Path(rec['path'])
objects = [object_overrides[name] for name in recorded['OBJECTS']] + list(added_objects.values())
# The tmod-runtime archives on the link line must be the ones the base stage recorded.
runtime_provenance = baseline['runtime_provenance']
for value in recorded['LIBS']:
    if value.startswith(str(TMOD_RUNTIME / 'lib') + '/'):
        assert sha(value) == runtime_provenance['libraries'][Path(value).name], f'{value} differs from the base record'
libraries = list(recorded['LIBS'])
index = libraries.index('@AOT@')
aot_objects = sorted(str(AOT / (name + '.dll.o')) for name in modules)
for sidecar in sidecars:
    # Each sidecar directly follows its module object; the verifier maps tables by name order.
    owner = str(AOT / sidecar.name.replace('-llvm.o', '.o'))
    aot_objects.insert(aot_objects.index(owner) + 1, str(sidecar))
libraries[index:index + 1] = aot_objects
candidate = NATIVE / 'candidate'
candidate.mkdir()
for path in config.get('native_library_paths', []):
    assert Path(path).is_dir(), f'native library path missing: {path}'
for flag in config.get('extra_ldflags', []):
    assert isinstance(flag, str) and flag.startswith('-Wl,'), f'invalid extra_ldflag: {flag}'
extra_libpaths = ['-L' + path for path in config.get('native_library_paths', [])]
extra_ldflags.extend(config.get('extra_ldflags', []))
run(['aarch64-none-elf-gcc', *recorded['LDFLAGS'], *extra_ldflags, *objects, *extra_libpaths, *recorded['LIBPATHS'], *libraries,
     '-o', candidate / 'tmodloader.elf'], 'link-candidate', NATIVE)

shutil.copytree(BASE / 'romfs', ROMFS)
# Base RomFS files may be read-only; copies below overwrite some of them.
for path in ROMFS.rglob('*'):
    path.chmod(path.stat().st_mode | 0o200)
shutil.copy2(INPUT / 'tModLoader.dll', ROMFS / 'tModLoader.dll')
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

nacp = bytearray((BASE / 'native/tmodloader.nacp').read_bytes())
title = f'tModLoader NX {VARIANT[4:]} ({config["label"]})'.encode()
assert len(title) < 0x200
for language in range(16):
    nacp[language * 0x300:language * 0x300 + 0x200] = title.ljust(0x200, b'\0')
(candidate / 'tmodloader.nacp').write_bytes(nacp)
run(['/opt/devkitpro/tools/bin/elf2nro', candidate / 'tmodloader.elf', candidate / 'tmodloader.nro',
     '--nacp=' + str(candidate / 'tmodloader.nacp'), '--romfsdir=' + str(ROMFS),
     '--icon=' + str(BASE / 'native/icon.jpg')], 'package-candidate', NATIVE)

payload = ROMFS / 'mono/lib_net9.0/System.Private.CoreLib.dll'
binding = {'System.Private.CoreLib': dict(
    corelib, aot_object=str(AOT / 'System.Private.CoreLib.dll.o'), llvm_object=str(AOT / 'System.Private.CoreLib.dll-llvm.o'),
    romfs_payload_dll=str(payload), romfs_payload_sha256=sha(payload))}
for filename in compiled:
    name = filename.removesuffix('.dll')
    record = {'compiler_input_dll': str(INPUT / filename), 'compiler_input_sha256': input_hashes[filename],
              'aot_object': str(AOT / (filename + '.o')), 'aot_object_sha256': sha(AOT / (filename + '.o'))}
    if filename in llvm_modules:
        record.update(llvm_object=str(AOT / (filename + '-llvm.o')), llvm_object_sha256=sha(AOT / (filename + '-llvm.o')))
    if filename in aot_inline_limits:
        record['aot_inline_limit'] = aot_inline_limits[filename]
    if filename in reused:
        record['reused_from'] = 'tmod-runtime/framework-aot'
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
                'candidate_elf': {'path': str(candidate / 'tmodloader.elf'), 'sha256': sha(candidate / 'tmodloader.elf')}}
if mod:
    deliverables['sd_mod'] = {'path': str(staged_packages[mod]), 'sha256': sha(staged_packages[mod])}
if additional_mods:
    deliverables['sd_mods'] = {m: {'path': str(p), 'sha256': sha(p)} for m, p in staged_packages.items()}
manifest = {'candidate': VARIANT, 'base': str(BASE), 'content': baseline['content'], 'runtime_provenance': runtime_provenance,
            'provenance_and_binding': binding, 'modset': dict(config, package=mod_info_by_name[mod] if mod else None), 'reference_inputs': input_hashes,
            'managed_replacements': {name: {'sha256': input_hashes[name], 'destinations': paths}
                                     for name, paths in replacements.items()},
            'launcher_objects': {name: {'path': str(path), 'sha256': sha(path)} for name, path in {**object_overrides, **added_objects}.items()},
            'final_deliverables': deliverables, 'launcher_manifest': launcher_manifest}
(OUT / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
print('PASS fixed modset:', json.dumps(manifest['final_deliverables']), flush=True)
