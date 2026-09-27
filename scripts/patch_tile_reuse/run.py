#!/usr/bin/env python3
"""Accept a clean42-derived scratch experiment in monobuild; never overwrite artifacts."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--input', type=Path, default=Path('/build/aot42-windows/runtime-romfs/Terraria.exe'))
    parser.add_argument('--fna', type=Path, default=Path('/build/aot42-windows/runtime-romfs/FNA.dll'))
    parser.add_argument('--frame-probe', type=Path, default=Path('/build/tile-cost45/final-run/accepted/FrameProfileProbe.dll'))
    args = parser.parse_args()
    root = args.output
    if root.exists() or root.is_symlink():
        raise RuntimeError('fresh output required')
    root.mkdir(parents=True)
    originals = {str(path): sha(path) for path in (args.input, args.fna, args.frame_probe)}
    env = dict(os.environ, DOTNET_TieredCompilation='0', DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1', TMPDIR='/build/tmp', REUSE_FRAME_PROBE=str(args.frame_probe))
    commands = []

    def run(argv, label, expected=0):
        result = subprocess.run([str(value) for value in argv], env=env, capture_output=True, text=True)
        (root / (label + '.log')).write_text(result.stdout + result.stderr)
        commands.append(dict(argv=[str(value) for value in argv], exit=result.returncode, log=label + '.log'))
        if result.returncode != expected:
            raise RuntimeError(label + '\n' + result.stdout + result.stderr)
        print('PASS ' + label, flush=True)

    dotnet = '/root/.dotnet/dotnet'
    source = Path(__file__).resolve().parent
    cecil = '-p:CecilPath=/mono-nx/dotnet_runtime/artifacts/bin/Mono.Linker/Debug/net9.0/Mono.Cecil.dll'
    support = root / 'support/PatchFrameProfile.dll'
    run([dotnet, 'build', source.parent / 'patch_frame_profile/PatchFrameProfile.csproj', '-c', 'Release', '-o', root / 'support', '-p:BaseIntermediateOutputPath=' + str(root / 'support-obj') + '/', cecil], 'build-shared-support')
    env['FRAME_PROFILE_TOOL'] = str(support)
    run([dotnet, 'build', source / 'PatchTileReuse.csproj', '-c', 'Release', '-o', root / 'tool', '-p:BaseIntermediateOutputPath=' + str(root / 'obj') + '/', cecil, '-p:FrameToolPath=' + str(support)], 'build46')
    tool = [dotnet, root / 'tool/PatchTileReuse.dll']
    def accept(game, fna, output):
        return tool + ['accept', game, fna, output]
    for label in ('accepted', 'repeat'):
        run(accept(args.input, args.fna, root / label), label)
    artifacts = ['Terraria.exe', 'TileReuseProbe.dll']
    for name in artifacts:
        if sha(root / 'accepted' / name) != sha(root / 'repeat' / name):
            raise RuntimeError('nondeterministic accepted artifact: ' + name)
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
    accepted_hash = sha(root / 'accepted/Terraria.exe')
    for label, game, fna, output in cases:
        run(accept(game, fna, output), 'guard-' + label, 1)
    if originals != {str(path): sha(path) for path in (args.input, args.fna, args.frame_probe)}:
        raise RuntimeError('source input changed')
    if sha(root / 'accepted/Terraria.exe') != accepted_hash or (root / 'accepted/Terraria.exe').stat().st_mode & 0o222:
        raise RuntimeError('accepted image changed or writable')
    sources = sorted(source.glob('*.cs')) + [source / 'run.py', source / 'PatchTileReuse.csproj']
    sources += sorted((source.parent / 'patch_frame_profile').glob('*.cs'))
    sources += [source.parent / 'patch_frame_profile/PatchFrameProfile.csproj', source.parent / 'patch_tile_profile/Common.cs', source.parent / 'patch_tile_profile/TileFixture.cs', source.parent / 'patch_tile_cost_profile/CostFixture.cs', source.parent / 'patch_time_logger/PeResources.cs']
    report = dict(passed=True, reproducible=True, readonly=True, sha256=accepted_hash, originals=originals, negativeGuards=[case[0] for case in cases], deterministicArtifacts=artifacts, commands=commands, sourceHashes={str(path): sha(path) for path in sources}, fixtureInfrastructureOnly=str(args.frame_probe), toolSha256=sha(root / 'tool/PatchTileReuse.dll'))
    (root / 'runner-results.json').write_text(json.dumps(report, indent=2) + '\n')
    print('ACCEPTED ' + str(root / 'accepted/Terraria.exe') + ' sha256=' + accepted_hash)


if __name__ == '__main__':
    main()
