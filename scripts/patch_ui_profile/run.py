#!/usr/bin/env python3
"""Build51 measurement-only acceptance against adopted50 in the pinned monobuild container."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess

INPUT_SHA = '06b11beb9c83fc14140754eb5a44c7fa0525d1877ea2fd0860a75b91f3d6f4f6'
FNA_SHA = '15427b2cb4c7952160f4cc124711f29b428459949304ceb0d16436e68a01882f'
RELOGIC_SHA = '2e7750fe79ba48bcca8e7ea5691f87c0e5887024b58552c433efb2e2ad060a65'
DOTNET = '/build/runtime-source/.dotnet/dotnet'


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--input', type=Path, default=Path('/build/gate50/aot-final/runtime-romfs/Terraria.exe'))
    parser.add_argument('--fna', type=Path, default=Path('/build/gate50/aot-final/runtime-romfs/FNA.dll'))
    parser.add_argument('--inspection-only', action='store_true')
    args = parser.parse_args()
    root = args.output.absolute()
    if root.exists() or root.is_symlink() or any(parent.is_symlink() for parent in root.parents):
        raise RuntimeError('fresh non-alias output required')
    relogic = args.input.parent / 'ReLogic.dll'
    if sha(args.input) != INPUT_SHA or sha(args.fna) != FNA_SHA or sha(relogic) != RELOGIC_SHA:
        raise RuntimeError('pinned50/FNA/ReLogic required')
    source = Path(__file__).resolve().parent
    analyzer = source.parent / 'analyze_ui_profile.py'

    def source_hashes():
        sources = sorted(source.glob('*.cs')) + sorted(source.glob('*.py')) + [source / 'PatchUIProfile.csproj', source / 'ui51-schema.json', analyzer]
        sources += sorted((source.parent / 'patch_frame_profile').glob('*.cs'))
        sources += [source.parent / 'patch_frame_profile/PatchFrameProfile.csproj', source.parent / 'patch_tile_profile/Common.cs', source.parent / 'patch_time_logger/PeResources.cs']
        return {str(path): sha(path) for path in sorted(set(sources))}

    hashes = source_hashes()
    root.mkdir(parents=True)
    manifest = root / 'source-manifest.json'
    manifest.write_text(json.dumps(hashes, sort_keys=True, indent=2) + '\n')
    originals = {str(path): sha(path) for path in (args.input, args.fna, relogic)}
    env = dict(os.environ, DOTNET_ROOT=str(Path(DOTNET).parent), DOTNET_TieredCompilation='0', DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1', TMPDIR='/build/tmp', UI51_SOURCE_MANIFEST=str(manifest), UI51_ANALYZER=str(analyzer), PYTHONDONTWRITEBYTECODE='1')
    commands = []

    def run(argv, label, expected=0):
        result = subprocess.run([str(value) for value in argv], env=env, capture_output=True, text=True)
        (root / (label + '.log')).write_text(result.stdout + result.stderr)
        commands.append(dict(argv=[str(value) for value in argv], exit=result.returncode, log=label + '.log'))
        (root / 'commands.json').write_text(json.dumps(commands, indent=2) + '\n')
        if result.returncode != expected:
            raise RuntimeError(label + '\n' + result.stdout + result.stderr)
        print('PASS ' + label, flush=True)

    cecil = '-p:CecilPath=/mono-nx/dotnet_runtime/artifacts/bin/Mono.Linker/Debug/net9.0/Mono.Cecil.dll'
    support = root / 'support/PatchFrameProfile.dll'
    run([DOTNET, 'build', source.parent / 'patch_frame_profile/PatchFrameProfile.csproj', '-c', 'Release', '-o', root / 'support', '-p:BaseIntermediateOutputPath=' + str(root / 'support-obj') + '/', cecil], 'build-shared-support')
    env['FRAME_PROFILE_TOOL'] = str(support)
    run([DOTNET, 'build', source / 'PatchUIProfile.csproj', '-c', 'Release', '-o', root / 'tool', '-p:BaseIntermediateOutputPath=' + str(root / 'obj') + '/', cecil, '-p:FrameToolPath=' + str(support)], 'build51')
    if hashes != source_hashes():
        raise RuntimeError('source changed during build')
    tool = [DOTNET, root / 'tool/PatchUIProfile.dll']
    mode = 'emit' if args.inspection_only else 'accept'
    run(tool + [mode, args.input, args.fna, root / ('inspection' if args.inspection_only else 'accepted')], mode)
    if args.inspection_only:
        (root / 'runner-inspection-only.json').write_text(json.dumps(dict(accepted=False, sourceHash=sha(manifest), sourceHashes=hashes, commands=commands), indent=2) + '\n')
        return
    run(tool + ['accept', args.input, args.fna, root / 'repeat'], 'repeat')
    accepted = root / 'accepted/Terraria.exe'
    artifacts = ['Terraria.exe'] + [str(path.relative_to(root / 'accepted')) for path in sorted((root / 'accepted/proof').rglob('*.dll'))]
    if len(artifacts) < 2:
        raise RuntimeError('actual full-method/runtime probe artifact missing')
    for name in artifacts:
        if not (root / 'repeat' / name).is_file() or sha(root / 'accepted' / name) != sha(root / 'repeat' / name):
            raise RuntimeError('nondeterministic accepted/proof artifact: ' + name)
    for role in ('accepted', 'repeat'):
        parser_proof = json.loads((root / role / 'proof/parser-proof.json').read_text())
        if not parser_proof['passed'] or parser_proof['accepted'] == 0 or parser_proof['rejected'] == 0:
            raise RuntimeError('actual serialized report parser proof missing')
    bad = root / 'unsupported.exe'
    bad.write_bytes(args.input.read_bytes() + b'\0')
    bad_fna = root / 'unsupported-fna.dll'
    bad_fna.write_bytes(args.fna.read_bytes() + b'\0')
    bad_relogic = root / 'unsupported-relogic'
    bad_relogic.mkdir()
    os.link(args.input, bad_relogic / 'Terraria.exe')
    (bad_relogic / 'ReLogic.dll').write_bytes(relogic.read_bytes() + b'\0')
    alias = root / 'alias'
    alias.symlink_to(root / 'accepted', target_is_directory=True)
    dangling = root / 'dangling'
    dangling.symlink_to(root / 'not-created', target_is_directory=True)
    hardlink = root / 'input-hardlink.exe'
    os.link(args.input, hardlink)
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
        ('unsupported-relogic', bad_relogic / 'Terraria.exe', args.fna, root / 'reject-relogic'),
    ]
    accepted_hash = sha(accepted)
    for label, input_path, fna, output in cases:
        run(tool + ['accept', input_path, fna, output], 'guard-' + label, 1)
    bad_manifest = root / 'incorrect-source-manifest.json'
    changed_hashes = dict(hashes)
    changed_hashes[str(source / 'UIProfilePatcher.cs')] = '0' * 64
    bad_manifest.write_text(json.dumps(changed_hashes, sort_keys=True, indent=2) + '\n')
    env['UI51_SOURCE_MANIFEST'] = str(bad_manifest)
    run(tool + ['accept', args.input, args.fna, root / 'reject-source'], 'guard-source-hash', 1)
    env['UI51_SOURCE_MANIFEST'] = str(manifest)
    if (root / 'reject-source/Terraria.exe').exists():
        raise RuntimeError('unverified game was accepted')
    if originals != {str(path): sha(path) for path in (args.input, args.fna, relogic)} or accepted_hash != sha(accepted) or hashes != source_hashes():
        raise RuntimeError('input/accepted artifact/source changed')
    if accepted.stat().st_mode & 0o222:
        raise RuntimeError('accepted game must be read-only')
    report = dict(passed=True, measurementOnly=True, hardwarePending=True, performanceAdopted=False, sha256=accepted_hash, sourceHash=sha(manifest), sourceHashes=hashes, originals=originals, reproducible=True, readonly=True, negativeGuards=[case[0] for case in cases] + ['source-hash'], deterministicArtifacts=artifacts, commands=commands, dotnetSha256=sha(Path(DOTNET)), toolSha256=sha(root / 'tool/PatchUIProfile.dll'), supportSha256=sha(support), host=platform.platform())
    (root / 'runner-results.json').write_text(json.dumps(report, indent=2) + '\n')
    print('ACCEPTED ' + str(accepted) + ' sha256=' + accepted_hash)


if __name__ == '__main__':
    main()
