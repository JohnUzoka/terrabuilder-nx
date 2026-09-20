#!/usr/bin/env python3
"""Focused44 acceptance inside monobuild: /work project, /build cache, /mono-nx SDK read-only."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--input', type=Path, default=Path('/build/aot43-primitives/runtime-romfs/Terraria.exe'))
    parser.add_argument('--fna', type=Path, default=Path('/build/aot43-primitives/runtime-romfs/FNA.dll'))
    args = parser.parse_args()
    root = args.output
    if root.exists() or root.is_symlink():
        raise RuntimeError('fresh output required; no overwrite/aliases')
    root.mkdir(parents=True)
    initial = {str(p): sha(p) for p in (args.input, args.fna)}
    env = dict(os.environ, DOTNET_TieredCompilation='0', DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1')
    commands = []

    def run(argv, label, expected=0):
        result = subprocess.run([str(x) for x in argv], env=env, capture_output=True, text=True)
        (root / (label + '.log')).write_text(result.stdout + result.stderr)
        commands.append(dict(argv=[str(x) for x in argv], exit=result.returncode, log=label + '.log'))
        if result.returncode != expected:
            raise RuntimeError(label + '\n' + result.stdout + result.stderr)
        print('PASS ' + label, flush=True)

    dotnet = '/root/.dotnet/dotnet'
    source = Path(__file__).resolve().parent
    cecil = '-p:CecilPath=/mono-nx/dotnet_runtime/artifacts/bin/Mono.Linker/Debug/net9.0/Mono.Cecil.dll'
    support = root / 'support/PatchFrameProfile.dll'
    run([dotnet, 'build', source.parent / 'patch_frame_profile/PatchFrameProfile.csproj', '-c', 'Release', '-o', root / 'support', '-p:BaseIntermediateOutputPath=' + str(root / 'support-obj') + '/', cecil], 'build-reviewed43-support')
    env['FRAME_PROFILE_TOOL'] = str(support)
    run([dotnet, 'build', source / 'PatchTileProfile.csproj', '-c', 'Release', '-o', root / 'tool', '-p:BaseIntermediateOutputPath=' + str(root / 'obj') + '/', cecil, '-p:FrameToolPath=' + str(support)], 'build44')
    tool = [dotnet, root / 'tool/PatchTileProfile.dll']

    def accept(input_path, fna, output):
        return tool + ['accept', input_path, fna, output]

    for label in ('accepted', 'repeat'):
        run(accept(args.input, args.fna, root / label), label)
    artifacts = ['Terraria.exe', 'TileProfileProbe.dll', 'FrameProfileProbe.dll']
    for name in artifacts:
        if sha(root / 'accepted' / name) != sha(root / 'repeat' / name):
            raise RuntimeError('nondeterministic artifact: ' + name)
    bad = root / 'unsupported.exe'
    bad.write_bytes(args.input.read_bytes() + b'\0')
    bad_fna = root / 'unsupported-fna.dll'
    bad_fna.write_bytes(args.fna.read_bytes() + b'\0')
    alias = root / 'alias'
    alias.symlink_to(root / 'accepted', target_is_directory=True)
    hardlink = root / 'input-hardlink.exe'
    os.link(args.input, hardlink)
    cases = [
        ('unsupported', bad, args.fna, root / 'reject-unsupported'),
        ('already-patched', root / 'accepted/Terraria.exe', args.fna, root / 'reject-patched'),
        ('existing-output', args.input, args.fna, root / 'accepted'),
        ('input-output-alias', args.input, args.fna, args.input),
        ('symlink-output', args.input, args.fna, alias),
        ('hardlink-output', args.input, args.fna, hardlink),
        ('unsupported-fna', args.input, bad_fna, root / 'reject-fna'),
    ]
    immutable_hash = sha(root / 'accepted/Terraria.exe')
    for label, input_path, fna, output in cases:
        run(accept(input_path, fna, output), 'guard-' + label, 1)
    if initial != {str(p): sha(p) for p in (args.input, args.fna)} or immutable_hash != sha(root / 'accepted/Terraria.exe'):
        raise RuntimeError('input or accepted image modified')
    if (root / 'accepted/Terraria.exe').stat().st_mode & 0o222:
        raise RuntimeError('accepted image must be read-only')
    sources = sorted(source.glob('*.cs')) + [source / 'run.py', source / 'PatchTileProfile.csproj']
    sources += sorted((source.parent / 'patch_frame_profile').glob('*.cs'))
    sources += [source.parent / 'patch_frame_profile/PatchFrameProfile.csproj', source.parent / 'patch_time_logger/PeResources.cs']
    report = dict(passed=True, sha256=immutable_hash, reproducible=True, readonly=True, originals=initial,
                  negativeGuards=[c[0] for c in cases], deterministicArtifacts=artifacts,
                  sourceHashes={str(p): sha(p) for p in sources}, commands=commands,
                  host=dict(platform=platform.platform()), toolSha256=sha(root / 'tool/PatchTileProfile.dll'),
                  supportSha256=sha(support))
    (root / 'runner-results.json').write_text(json.dumps(report, indent=2) + '\n')
    print('ACCEPTED ' + str(root / 'accepted/Terraria.exe') + ' sha256=' + immutable_hash)


if __name__ == '__main__':
    main()
