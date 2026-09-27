"""Build58: AOT the unchanged build52 game against a Release CoreLib/framework.

Runs INSIDE localhost/monobuild:local with /build, /work (ro), /mono-nx (ro).
Only the managed BCL flavour changes (Debug -> Release). Game bytes, compiler,
AOT options (CoreLib ntrampolines=65536; others full,interp,static) and the
native runtime stay as in build52.

Outputs <variant dir>/aot-final/ with the seven objects, header, manifests, and
runtime-romfs/ (game + Release framework at root + mono/lib_net9.0 CoreLib for the
MONO_NX_EMBEDDED_BCL launcher).

Environment: R58_VARIANT (subdirectory under release58/, default: none = 58d);
R58_CORELIB_INLINE=1 compiles CoreLib with the compiler's default inlining
(58e experiment) instead of --optimize=-inline.
"""
import hashlib, json, os, shutil, subprocess, sys
from pathlib import Path

ROOT = Path('/build')
VARIANT_DIR = ROOT / 'release58' / os.environ.get('R58_VARIANT', '')
OUT = VARIANT_DIR / 'aot-final'
CORELIB_INLINE = os.environ.get('R58_CORELIB_INLINE') == '1'
# R58_EXTRA_MODULES="System.Linq.dll System.Collections.dll" also AOT-compiles those
# RomFS-root framework assemblies (build59 step: remove their interpreter fallback).
EXTRA_MODULES = os.environ.get('R58_EXTRA_MODULES', '').split()
# R58_LLVM_MODULES="Terraria" compiles those assemblies with Mono's LLVM backend using the
# LLVM-enabled cross compiler built in /build/runtime-llvm (build60 experiment).
LLVM_MODULES = os.environ.get('R58_LLVM_MODULES', '').split()
LLVM_DIR = ROOT / 'runtime-llvm/artifacts/bin/mono/linux.x64.Debug/cross/linux-x64/libnx-arm64'
# R58_LLVM_AOT_EXTRA appends --aot options for the LLVM modules only (build63: Cortex-A57 tuning,
# 'llvmopts=-mtriple=aarch64-none-elf -mcpu=cortex-a57,llvmllc=-mcpu=cortex-a57').
LLVM_AOT_EXTRA = os.environ.get('R58_LLVM_AOT_EXTRA', '')
# R58_CORELIB_AOT_EXTRA="nimt-trampolines=8192,..." appends CoreLib AOT options. All Mono
# "numerous" trampoline pools live in CoreLib's image; 59 exhausted IMT (type 2, 512).
CORELIB_AOT_EXTRA = os.environ.get('R58_CORELIB_AOT_EXTRA', '')
BASE = ROOT / 'hint52/aot-final'
REL_BIN = ROOT / 'runtime-source/artifacts/bin'
REL_CORE_DIR = REL_BIN / 'mono/libnx.arm64.Release'
REL_FW = REL_BIN / 'runtime/net9.0-libnx-Release-arm64'
SDK = Path('/mono-nx/dotnet_runtime/artifacts/bin')
DBG_FW = SDK / 'runtime/net9.0-libnx-Debug-arm64'
DBG_CORE = SDK / 'mono/libnx.arm64.Debug/System.Private.CoreLib.dll'
CROSS = SDK / 'mono/linux.x64.Debug/cross/linux-x64/libnx-arm64/mono-aot-cross'
DOTNET = ROOT / 'runtime-source/.dotnet/dotnet'
GAME_SHA = '90b135121829d6650ce0e4f8ddb1395108615aa7d46ae487d279bc3a18698b22'  # accepted52 Terraria.exe
CROSS_SHA = 'c7c41a54a6d2479a0e7d62f4c9c526f17787a3d628055d4338c3ad99eb91081c'


def sha(path):
    with open(path, 'rb') as s:
        return hashlib.file_digest(s, 'sha256').hexdigest()


def run(cmd, log):
    with open(log, 'w') as s:
        r = subprocess.run(list(map(str, cmd)), stdout=s, stderr=subprocess.STDOUT, env=ENV)
    if r.returncode:
        raise SystemExit(f'FAILED ({r.returncode}): {" ".join(map(str, cmd))}; see {log}')


assert not OUT.exists(), 'fresh output directory required'
assert sha(BASE / 'runtime-romfs/Terraria.exe') == GAME_SHA
assert sha(CROSS) == CROSS_SHA
OUT.mkdir(parents=True)
logs = OUT / 'logs'; logs.mkdir()
(OUT / 'tmp').mkdir()
ENV = dict(os.environ, TMPDIR=str(OUT / 'tmp'), DOTNET_ROOT=str(DOTNET.parent),
           DOTNET_CLI_HOME=str(OUT / 'dotnet-home'), DOTNET_CLI_TELEMETRY_OPTOUT='1',
           DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1', DOTNET_NOLOGO='1')

# 1. Stage RomFS: hardlink build52's payload, then swap every file that is a
#    byte-identical Debug framework copy for its Release counterpart.
game = OUT / 'runtime-romfs'
shutil.copytree(BASE / 'runtime-romfs', game, copy_function=os.link)
dbg_by_hash = {sha(p): p.name for p in DBG_FW.glob('*.dll')}
swapped, kept = [], []
for p in sorted(game.iterdir()):
    if p.suffix != '.dll':
        continue
    name = dbg_by_hash.get(sha(p))
    if name is None:
        kept.append(p.name)
        continue
    assert name == p.name, f'{p.name} matches Debug {name}'
    p.unlink()
    os.link(REL_FW / name, p)
    swapped.append(name)
# Embedded CoreLib for the launcher. The framework stays at RomFS root only:
# a second framework directory ahead of "/" would shadow the root overrides
# (patched mscorlib facade, legacy System.Drawing) -- build58's first hardware crash.
(game / 'mono/lib_net9.0').mkdir(parents=True)
os.link(REL_CORE_DIR / 'System.Private.CoreLib.dll', game / 'mono/lib_net9.0/System.Private.CoreLib.dll')

# 2. CoreLib AOT with build38's trampoline bank, and inlining OFF (58c).
#    58b crashed on hardware: mono_class_from_mono_type_internal "implement me 0x00"
#    (class.c:2324) right after AOT CultureInfo.GetCultureByName. Debug CoreLib's
#    unoptimized IL is too large for Mono's inliner (0 inlines in GetCultureData),
#    Release IL inlines 13 callees there incl. Dictionary<string,CultureData>
#    generics. -inline restores the codegen shape that ran on hardware since 38.
core_obj = OUT / 'System.Private.CoreLib.dll.o'
core_log = logs / 'System.Private.CoreLib.dll.log'
run(['stdbuf', '-oL', '-eL', CROSS, *([] if CORELIB_INLINE else ['--optimize=-inline']), '--path=' + str(REL_CORE_DIR),
     '--aot=full,interp,static,ntrampolines=65536,' + (CORELIB_AOT_EXTRA + ',' if CORELIB_AOT_EXTRA else '')
     + 'outfile=' + str(core_obj) + ',tool-prefix=aarch64-none-elf-',
     REL_CORE_DIR / 'System.Private.CoreLib.dll'], core_log)
print('CoreLib AOT done', flush=True)

# 3. Game modules via the project's verified helper (prepare, compile, MVID validation, header).
run([sys.executable, '/work/scripts/compile_terraria_aot.py', '--game-dir', game, '--output-dir', OUT,
     '--corelib-dir', REL_CORE_DIR, '--runtime-dir', REL_FW, '--cross-compiler', CROSS,
     '--cecil-path', SDK / 'Mono.Linker/Debug/net9.0/Mono.Cecil.dll', '--dotnet', DOTNET,
     '--corelib-object', core_obj, '--corelib-log', core_log, '--jobs', '3',
     *[flag for name in EXTRA_MODULES for flag in ('--extra-module', name)],
     *[flag for name in LLVM_MODULES for flag in ('--llvm-module', name)],
     *(['--llvm-compiler-dir', LLVM_DIR] if LLVM_MODULES else []),
     *(['--llvm-aot-extra', LLVM_AOT_EXTRA] if LLVM_AOT_EXTRA else [])], logs / 'compile_terraria_aot.log')

manifest = json.loads((OUT / 'build-manifest.json').read_text())
assert manifest['status'] == 'complete'
base = json.loads((BASE / 'build-manifest.json').read_text())
counts = {m['assembly']['name']: (m['compiled_methods'], m['total_methods']) for m in manifest['modules']}
base_counts = {m['assembly']['name']: (m['compiled_methods'], m['total_methods']) for m in base['modules']}
summary = dict(
    build=58, extra_modules=EXTRA_MODULES, llvm_modules=LLVM_MODULES,
    change='Release CoreLib/framework (managed only); game, compiler, AOT options, native runtime unchanged',
    game_sha256=GAME_SHA, release_corelib_sha256=sha(REL_CORE_DIR / 'System.Private.CoreLib.dll'),
    debug_corelib_sha256=sha(DBG_CORE), swapped_framework=len(swapped), kept_nonframework=kept,
    module_counts=counts, build52_module_counts=base_counts,
    corelib_counts=(manifest['corelib'].get('compiled_methods'), manifest['corelib'].get('total_methods')),
    header=(OUT / 'mono_aot_modules.h').read_text())
(OUT / 'release58-summary.json').write_text(json.dumps(summary, indent=2) + '\n')
print(json.dumps({k: summary[k] for k in ('module_counts', 'build52_module_counts', 'corelib_counts')}, indent=1), flush=True)
print('AOT58 PASS', flush=True)
