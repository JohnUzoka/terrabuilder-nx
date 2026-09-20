#!/usr/bin/env python3
"""Run inside monobuild with project /work, cache /build and read-only SDK /mono-nx."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import platform


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--input', type=Path, default=Path('/build/aot42-windows/runtime-romfs/Terraria.exe'))
    p.add_argument('--fna', type=Path, default=Path('/build/aot42-windows/runtime-romfs/FNA.dll'))
    p.add_argument('--inspect', action='store_true')
    a = p.parse_args()
    root = a.output.resolve()
    if root.exists():
        raise RuntimeError('runner output must be fresh; no overwrite')
    root.mkdir(parents=True)
    initial = {str(x): sha(x) for x in (a.input, a.fna)}
    env = dict(os.environ, DOTNET_TieredCompilation='0', DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1')
    commands = []
    def run(args, label, expected=0):
        result = subprocess.run(args, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        (root / (label + '.log')).write_text(result.stdout)
        commands.append(dict(argv=args, exit=result.returncode, log=label + '.log'))
        if result.returncode != expected:
            raise RuntimeError(label + '\n' + result.stdout)
        print('PASS ' + label, flush=True)
        return result.stdout
    dotnet = '/root/.dotnet/dotnet'
    run([dotnet, 'build', str(Path(__file__).with_name('PatchFrameProfile.csproj')), '-c', 'Release', '-o', str(root / 'tool'), '-p:BaseIntermediateOutputPath=' + str(root / 'obj') + '/', '-p:CecilPath=/mono-nx/dotnet_runtime/artifacts/bin/Mono.Linker/Debug/net9.0/Mono.Cecil.dll'], 'build')
    tool = [dotnet, str(root / 'tool/PatchFrameProfile.dll')]
    if a.inspect:
        run(tool + ['inspect', str(a.input), str(a.fna), str(root / 'inspection')], 'inspect')
        return
    def accept(source, fna, output):
        return tool + ['accept', str(source), str(fna), str(output)]
    for name in ('accepted', 'repeat'):
        run(accept(a.input, a.fna, root / name), name)
    for name in ('Terraria.exe', 'FrameProfileProbe.dll'):
        if sha(root / 'accepted' / name) != sha(root / 'repeat' / name):
            raise RuntimeError('nondeterministic ' + name)
    bad = root / 'modified.exe'
    bad.write_bytes(a.input.read_bytes() + b'\0')
    cases = [('modified-input', bad, a.fna, root / 'bad-input'), ('already-patched', root / 'accepted/Terraria.exe', a.fna, root / 'bad-patched'), ('existing-output', a.input, a.fna, root / 'accepted'), ('input-alias', a.input, a.fna, a.input)]
    alias = root / 'alias'
    alias.symlink_to(root / 'accepted', target_is_directory=True)
    cases.append(('symlink-output', a.input, a.fna, alias))
    invalid_fna = root / 'modified-fna.dll'
    invalid_fna.write_bytes(a.fna.read_bytes() + b'\0')
    cases.append(('modified-fna', a.input, invalid_fna, root / 'bad-fna'))
    accepted_sha = sha(root / 'accepted/Terraria.exe')
    for label, source, fna, output in cases:
        run(accept(source, fna, output), 'reject-' + label, 1)
    if initial != {str(x): sha(x) for x in (a.input, a.fna)} or accepted_sha != sha(root / 'accepted/Terraria.exe'):
        raise RuntimeError('inputs or accepted artifact changed')
    source_dir = Path(__file__).resolve().parent
    sources = sorted(source_dir.glob('*.cs')) + [source_dir / 'PatchFrameProfile.csproj', source_dir / 'run.py', source_dir.parent / 'patch_time_logger/PeResources.cs']
    source_hashes = {str(path): sha(path) for path in sources}
    cpu = next((line.partition(':')[2].strip() for line in Path('/proc/cpuinfo').read_text().splitlines() if line.startswith('model name')), 'unknown')
    result = dict(passed=True, originals=initial, sha256=accepted_sha, reproducible=True, negativeGuards=[x[0] for x in cases], sourceHashes=source_hashes, toolSha256=sha(root / 'tool/PatchFrameProfile.dll'), host=dict(platform=platform.platform(), cpu=cpu), commands=commands)
    (root / 'runner-results.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
