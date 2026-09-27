#!/usr/bin/env python3
"""Build49 patch-time acceptance in monobuild; /work project, /build cache, /mono-nx read-only SDK."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess

INPUT_SHA = 'd4c767a545d5f1a4cacbb6b0d3b16be68b6d1b9af1962d0cd439c3b7946b7298'
FNA_SHA = '15427b2cb4c7952160f4cc124711f29b428459949304ceb0d16436e68a01882f'


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--input', type=Path, default=Path('/build/aot42-windows/runtime-romfs/Terraria.exe'))
    parser.add_argument('--fna', type=Path, default=Path('/build/aot42-windows/runtime-romfs/FNA.dll'))
    parser.add_argument('--inspection-only', action='store_true', help='Run serialized audits without proof or accepted-image publication')
    args = parser.parse_args()
    root = args.output.absolute()
    if root.exists() or root.is_symlink() or any(parent.is_symlink() for parent in root.parents):
        raise RuntimeError('fresh non-alias output required')
    if sha(args.input) != INPUT_SHA or sha(args.fna) != FNA_SHA:
        raise RuntimeError('pinned clean42 and FNA required')
    source = Path(__file__).resolve().parent

    def source_hashes():
        sources = sorted(source.glob('*.cs')) + sorted(source.glob('*.py')) + [source / 'PatchTileHelpers.csproj', source.parent / 'analyze_tile_helpers.py']
        sources += sorted((source.parent / 'patch_frame_profile').glob('*.cs'))
        sources += [source.parent / 'patch_frame_profile/PatchFrameProfile.csproj', source.parent / 'patch_time_logger/PeResources.cs', source.parent / 'patch_tile_profile/Common.cs', source.parent / 'patch_tile_profile/TileFixture.cs', source.parent / 'patch_tile_cost_profile/CostFixture.cs']
        return {str(path): sha(path) for path in sorted(set(sources))}

    hashes = source_hashes()
    root.mkdir(parents=True)
    manifest = root / 'source-manifest.json'
    manifest.write_text(json.dumps(hashes, sort_keys=True, indent=2) + '\n')
    initial = {str(path): sha(path) for path in (args.input, args.fna)}
    env = dict(os.environ, DOTNET_TieredCompilation='0', DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1', TMPDIR='/build/tmp', TILE49_SOURCE_MANIFEST=str(manifest))
    commands = []

    def run(argv, label, expected=0):
        result = subprocess.run([str(value) for value in argv], env=env, capture_output=True, text=True)
        (root / (label + '.log')).write_text(result.stdout + result.stderr)
        commands.append(dict(argv=[str(value) for value in argv], exit=result.returncode, log=label + '.log'))
        (root / 'commands.json').write_text(json.dumps(commands, indent=2) + '\n')
        if result.returncode != expected:
            raise RuntimeError(label + '\n' + result.stdout + result.stderr)
        print('PASS ' + label, flush=True)

    dotnet = '/root/.dotnet/dotnet'
    cecil = '-p:CecilPath=/mono-nx/dotnet_runtime/artifacts/bin/Mono.Linker/Debug/net9.0/Mono.Cecil.dll'
    support = root / 'support/PatchFrameProfile.dll'
    run([dotnet, 'build', source.parent / 'patch_frame_profile/PatchFrameProfile.csproj', '-c', 'Release', '-o', root / 'support', '-p:BaseIntermediateOutputPath=' + str(root / 'support-obj') + '/', cecil], 'build-shared-support')
    env['FRAME_PROFILE_TOOL'] = str(support)
    run([dotnet, 'build', source / 'PatchTileHelpers.csproj', '-c', 'Release', '-o', root / 'tool', '-p:BaseIntermediateOutputPath=' + str(root / 'obj') + '/', cecil, '-p:FrameToolPath=' + str(support)], 'build49')
    if hashes != source_hashes():
        raise RuntimeError('source changed while building')
    tool = [dotnet, root / 'tool/PatchTileHelpers.dll']
    mode = 'emit' if args.inspection_only else 'accept'
    run(tool + [mode, args.input, args.fna, root / ('inspection' if args.inspection_only else 'accepted')], mode)
    if args.inspection_only:
        (root / 'runner-inspection-only.json').write_text(json.dumps(dict(accepted=False, sourceHash=sha(manifest), sourceHashes=hashes, commands=commands), indent=2) + '\n')
        return
    run(tool + ['accept', args.input, args.fna, root / 'repeat'], 'repeat')
    accepted = root / 'accepted/Terraria.exe'
    artifacts = ['Terraria.exe']
    for name in artifacts:
        if sha(root / 'accepted' / name) != sha(root / 'repeat' / name):
            raise RuntimeError('nondeterministic artifact: ' + name)
    # Compare every proof DLL produced by the proof, without prescribing its filename.
    for path in sorted((root / 'accepted').glob('*.dll')):
        if not (root / 'repeat' / path.name).is_file() or sha(path) != sha(root / 'repeat' / path.name):
            raise RuntimeError('nondeterministic proof assembly: ' + path.name)
        artifacts.append(path.name)
    bad = root / 'unsupported.exe'; bad.write_bytes(args.input.read_bytes() + b'\0')
    bad_fna = root / 'unsupported-fna.dll'; bad_fna.write_bytes(args.fna.read_bytes() + b'\0')
    alias = root / 'alias'; alias.symlink_to(root / 'accepted', target_is_directory=True)
    dangling = root / 'dangling'; dangling.symlink_to(root / 'not-created', target_is_directory=True)
    hardlink = root / 'input-hardlink.exe'; os.link(args.input, hardlink)
    cases = [
        ('unsupported', bad, args.fna, root / 'reject-unsupported'),
        ('already-patched', accepted, args.fna, root / 'reject-patched'),
        ('existing-output', args.input, args.fna, root / 'accepted'),
        ('input-output-alias', args.input, args.fna, args.input),
        ('symlink-output', args.input, args.fna, alias),
        ('symlink-ancestor', args.input, args.fna, alias / 'new-output'),
        ('dangling-output', args.input, args.fna, dangling),
        ('hardlink-output', args.input, args.fna, hardlink),
        ('unsupported-fna', args.input, bad_fna, root / 'reject-fna'),
    ]
    accepted_hash = sha(accepted)
    for label, input_path, fna, output in cases:
        run(tool + ['accept', input_path, fna, output], 'guard-' + label, 1)
    # A changed build manifest must prevent acceptance before any accepted image appears.
    bad_manifest = root / 'incorrect-source-manifest.json'
    invalid_hashes = dict(hashes); invalid_hashes[str(source / 'TileHelperPatcher.cs')] = '0' * 64
    bad_manifest.write_text(json.dumps(invalid_hashes, sort_keys=True, indent=2) + '\n')
    env['TILE49_SOURCE_MANIFEST'] = str(bad_manifest)
    run(tool + ['accept', args.input, args.fna, root / 'reject-source'], 'guard-source-hash', 1)
    env['TILE49_SOURCE_MANIFEST'] = str(manifest)
    if (root / 'reject-source/Terraria.exe').exists():
        raise RuntimeError('unverified image was published')
    if initial != {str(path): sha(path) for path in (args.input, args.fna)} or accepted_hash != sha(accepted) or hashes != source_hashes():
        raise RuntimeError('input, accepted image or source modified')
    if accepted.stat().st_mode & 0o222:
        raise RuntimeError('accepted image must be read-only')
    report = dict(passed=True, hardwarePending=True, sha256=accepted_hash, reproducible=True, readonly=True, originals=initial, sourceHash=sha(manifest), sourceHashes=hashes, negativeGuards=[case[0] for case in cases] + ['source-hash'], deterministicArtifacts=artifacts, commands=commands, host=dict(platform=platform.platform()), toolSha256=sha(root / 'tool/PatchTileHelpers.dll'), supportSha256=sha(support))
    (root / 'runner-results.json').write_text(json.dumps(report, indent=2) + '\n')
    print('ACCEPTED ' + str(accepted) + ' sha256=' + accepted_hash)


if __name__ == '__main__':
    main()
