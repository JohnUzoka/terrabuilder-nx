"""AOT-compile CoreLib for tModLoader modset builds with enlarged runtime trampoline pools.

All of Mono's "numerous" trampoline pools live in CoreLib's AOT image. tmod02-12c reused
tmod02's CoreLib object with the compiler defaults, and hardware (logT12.txt) aborted in
Save & Quit with "Ran out of trampolines of type 1 ... (limit 4096)" (static RGCTX).
Vanilla builds 58+ already raised the specific/IMT/GSharedVT/unbox pools; modded
tModLoader also needs the static-RGCTX pool raised.

Env: CORELIB_VARIANT (output name under /build/tmod/corelib), CORELIB_LLVM=1 to compile
through Mono's LLVM backend (run in localhost/monobuild-llvm:local; emits a -llvm.o
sidecar, as vanilla build64). Output: object(s) + manifest.json. Input is the exact
CoreLib shipped in the tModLoader RomFS.
"""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile

ROOT = Path('/build')
VARIANT = os.environ['CORELIB_VARIANT']
assert Path(VARIANT).name == VARIANT
LLVM = os.environ.get('CORELIB_LLVM') == '1'
OUT = ROOT / 'tmod/corelib' / VARIANT
CORELIB = ROOT / 'tmod/tmod09/romfs/mono/lib_net9.0/System.Private.CoreLib.dll'
CORELIB_SHA256 = 'c29bc7f8d4ee3d7620dfbaa61a7ecfd19e8dcba572b41d8f95c28b3ee9009df8'
SDK_CROSS = Path('/mono-nx/dotnet_runtime/artifacts/bin/mono/linux.x64.Debug/cross/linux-x64/libnx-arm64/mono-aot-cross')
LLVM_DIR = ROOT / 'runtime-llvm/artifacts/bin/mono/linux.x64.Debug/cross/linux-x64/libnx-arm64'
POOLS = ('ntrampolines=65536,nrgctx-trampolines=65536,nimt-trampolines=16384,'
         'ngsharedvt-trampolines=8192,nunbox-arbitrary-trampolines=4096')


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


assert sha(CORELIB) == CORELIB_SHA256, 'unexpected CoreLib input'
OUT.mkdir(parents=True)
obj = OUT / 'System.Private.CoreLib.dll.o'
options = 'full,interp,static,' + POOLS + ',outfile=' + str(obj) + ',tool-prefix=aarch64-none-elf-'
env = dict(os.environ, PATH='/opt/devkitpro/devkitA64/bin:' + os.environ['PATH'])
if LLVM:
    sidecar = OUT / 'System.Private.CoreLib.dll-llvm.o'
    temp = Path(tempfile.mkdtemp(dir=OUT))
    options += f',llvm-path={LLVM_DIR}/,llvm-outfile={sidecar},temp-path={temp}'
    command = [str(LLVM_DIR / 'mono-aot-cross'), '--llvm', '--path=' + str(CORELIB.parent), '--aot=' + options, str(CORELIB)]
    env['LD_LIBRARY_PATH'] = str(LLVM_DIR)
else:
    command = [str(SDK_CROSS), '--path=' + str(CORELIB.parent), '--aot=' + options, str(CORELIB)]
result = subprocess.run(command, env=env, capture_output=True, text=True)
(OUT / 'aot.log').write_text(result.stdout + result.stderr)
if result.returncode:
    raise SystemExit('CoreLib AOT failed\n' + result.stdout[-4000:] + result.stderr[-4000:])
assert sha(CORELIB) == CORELIB_SHA256, 'CoreLib input changed during AOT'
compiled = next(line for line in result.stdout.splitlines() if line.startswith('Compiled:'))
manifest = {'input': str(CORELIB), 'input_sha256': CORELIB_SHA256, 'llvm': LLVM, 'pools': POOLS,
            'command': command, 'compiled': compiled,
            'aot_object': str(obj), 'aot_object_sha256': sha(obj)}
if LLVM:
    manifest.update(llvm_object=str(sidecar), llvm_object_sha256=sha(sidecar))
(OUT / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
print('PASS CoreLib AOT', VARIANT, compiled, flush=True)
