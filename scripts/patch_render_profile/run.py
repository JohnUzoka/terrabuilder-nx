#!/usr/bin/env python3
"""Accept measurement53 against the exact adopted52 pair in pinned monobuild."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess

INPUT_SHA = '90b135121829d6650ce0e4f8ddb1395108615aa7d46ae487d279bc3a18698b22'
FNA_SHA = '15427b2cb4c7952160f4cc124711f29b428459949304ceb0d16436e68a01882f'
RELOGIC_SHA = '856438fe15b91b2bc1972f8ebd710cb3ef0ba2d6d7900e65d33bbf2e3c8d68c8'
DOTNET = '/build/runtime-source/.dotnet/dotnet'


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--input', type=Path, default=Path('/build/hint52/aot-final/runtime-romfs/Terraria.exe'))
    parser.add_argument('--fna', type=Path, default=Path('/build/hint52/aot-final/runtime-romfs/FNA.dll'))
    parser.add_argument('--inspection-only', action='store_true')
    args = parser.parse_args()
    root = args.output.absolute()
    if root.exists() or root.is_symlink() or any(parent.is_symlink() for parent in root.parents):
        raise RuntimeError('fresh non-alias output required')
    relogic = args.input.parent / 'ReLogic.dll'
    resolved_fna = args.input.parent / 'FNA.dll'
    if sha(args.input) != INPUT_SHA or sha(args.fna) != FNA_SHA or sha(resolved_fna) != FNA_SHA or sha(relogic) != RELOGIC_SHA:
        raise RuntimeError('pinned52/FNA/ReLogic pair required')
    source = Path(__file__).resolve().parent
    analyzer = source.parent / 'analyze_render_profile.py'

    def source_hashes():
        sources = sorted(source.glob('*.cs')) + sorted(source.glob('*.py')) + [source / 'PatchRenderProfile.csproj', source / 'render53-schema.json', analyzer]
        sources += sorted((source.parent / 'patch_frame_profile').glob('*.cs'))
        sources += [source.parent / path for path in ('patch_frame_profile/PatchFrameProfile.csproj', 'patch_tile_profile/Common.cs', 'patch_tile_profile/TileFixture.cs', 'patch_tile_cost_profile/CostFixture.cs', 'patch_time_logger/PeResources.cs')]
        return {str(path): sha(path) for path in sorted(set(sources))}

    hashes = source_hashes()
    root.mkdir(parents=True)
    manifest = root / 'source-manifest.json'
    manifest.write_text(json.dumps(hashes, sort_keys=True, indent=2) + '\n')
    originals = {str(path): sha(path) for path in (args.input, args.fna, resolved_fna, relogic)}
    env = dict(os.environ, DOTNET_ROOT=str(Path(DOTNET).parent), DOTNET_TieredCompilation='0', DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1', TMPDIR='/build/tmp', RENDER53_SOURCE_MANIFEST=str(manifest), RENDER53_ANALYZER=str(analyzer), PYTHONDONTWRITEBYTECODE='1')
    commands = []

    def run(argv, label, expected=0, error=None):
        result = subprocess.run([str(value) for value in argv], env=env, capture_output=True, text=True)
        text = result.stdout + result.stderr
        (root / (label + '.log')).write_text(text)
        commands.append(dict(argv=[str(value) for value in argv], exit=result.returncode, log=label + '.log'))
        (root / 'commands.json').write_text(json.dumps(commands, indent=2) + '\n')
        if result.returncode != expected or error is not None and error not in text:
            raise RuntimeError(label + '\n' + text)
        print('PASS ' + label, flush=True)

    cecil = '-p:CecilPath=/mono-nx/dotnet_runtime/artifacts/bin/Mono.Linker/Debug/net9.0/Mono.Cecil.dll'
    support = root / 'support/PatchFrameProfile.dll'
    run([DOTNET, 'build', source.parent / 'patch_frame_profile/PatchFrameProfile.csproj', '-c', 'Release', '-o', root / 'support', '-p:BaseIntermediateOutputPath=' + str(root / 'support-obj') + '/', cecil], 'build-shared-support')
    env['FRAME_PROFILE_TOOL'] = str(support)
    run([DOTNET, 'build', source / 'PatchRenderProfile.csproj', '-c', 'Release', '-o', root / 'tool', '-p:BaseIntermediateOutputPath=' + str(root / 'obj') + '/', cecil, '-p:FrameToolPath=' + str(support)], 'build53')
    if hashes != source_hashes():
        raise RuntimeError('source changed during build')
    tool = [DOTNET, root / 'tool/PatchRenderProfile.dll']
    mode = 'emit' if args.inspection_only else 'accept'
    run(tool + [mode, args.input, args.fna, root / ('inspection' if args.inspection_only else 'accepted')], mode)
    if args.inspection_only:
        (root / 'runner-inspection-only.json').write_text(json.dumps(dict(accepted=False, measurementOnly=True, sourceHash=sha(manifest), sourceHashes=hashes, commands=commands), indent=2) + '\n')
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
    os.link(args.fna, bad_relogic / 'FNA.dll')
    (bad_relogic / 'ReLogic.dll').write_bytes(relogic.read_bytes() + b'\0')
    bad_resolved = root / 'unsupported-resolved-fna'
    bad_resolved.mkdir()
    os.link(args.input, bad_resolved / 'Terraria.exe')
    os.link(relogic, bad_resolved / 'ReLogic.dll')
    (bad_resolved / 'FNA.dll').write_bytes(args.fna.read_bytes() + b'\0')
    alias = root / 'alias'
    alias.symlink_to(root / 'accepted', target_is_directory=True)
    dangling = root / 'dangling'
    dangling.symlink_to(root / 'not-created', target_is_directory=True)
    hardlink = root / 'input-hardlink.exe'
    os.link(args.input, hardlink)
    cases = [
        ('unsupported', bad, args.fna, root / 'reject-unsupported', 'unsupported/already-profiled input'),
        ('already-patched', accepted, args.fna, root / 'reject-patched', 'unsupported/already-profiled input'),
        ('existing-output', args.input, args.fna, root / 'accepted', 'fresh non-alias output required'),
        ('input-output-alias', args.input, args.fna, args.input, 'fresh non-alias output required'),
        ('symlink-output', args.input, args.fna, alias, 'fresh non-alias output required'),
        ('symlink-ancestor', args.input, args.fna, alias / 'new-output', 'symlink output ancestor forbidden'),
        ('dangling-output', args.input, args.fna, dangling, 'fresh non-alias output required'),
        ('hardlink-output', args.input, args.fna, hardlink, 'fresh non-alias output required'),
        ('unsupported-fna', args.input, bad_fna, root / 'reject-fna', 'supplied/resolved FNA differs'),
        ('unsupported-relogic', bad_relogic / 'Terraria.exe', args.fna, root / 'reject-relogic', 'accepted52 ReLogic required'),
        ('unsupported-resolved-fna', bad_resolved / 'Terraria.exe', args.fna, root / 'reject-resolved-fna', 'supplied/resolved FNA differs'),
    ]
    accepted_hash = sha(accepted)
    for label, input_path, fna, output, error in cases:
        run(tool + ['accept', input_path, fna, output], 'guard-' + label, 1, error)
    bad_manifest = root / 'incorrect-source-manifest.json'
    changed_hashes = dict(hashes)
    changed_hashes[str(source / 'RenderPatcher.cs')] = '0' * 64
    bad_manifest.write_text(json.dumps(changed_hashes, sort_keys=True, indent=2) + '\n')
    env['RENDER53_SOURCE_MANIFEST'] = str(bad_manifest)
    run(tool + ['accept', args.input, args.fna, root / 'reject-source'], 'guard-source-hash', 1, 'source changed since build')
    env['RENDER53_SOURCE_MANIFEST'] = str(manifest)
    if (root / 'reject-source/Terraria.exe').exists():
        raise RuntimeError('unverified game was accepted')
    if originals != {str(path): sha(Path(path)) for path in originals} or accepted_hash != sha(accepted) or hashes != source_hashes():
        raise RuntimeError('input/accepted artifact/source changed')
    if accepted.stat().st_mode & 0o222:
        raise RuntimeError('accepted game must be read-only')
    report = dict(passed=True, measurementOnly=True, hardwarePending=True, performanceAdopted=False, activePerformanceBaseline=52, sha256=accepted_hash, reLogicSha256=sha(relogic), sourceHash=sha(manifest), sourceHashes=hashes, originals=originals, reproducible=True, readonly=True, negativeGuards=[case[0] for case in cases] + ['source-hash'], deterministicArtifacts=artifacts, commands=commands, dotnetSha256=sha(Path(DOTNET)), toolSha256=sha(root / 'tool/PatchRenderProfile.dll'), supportSha256=sha(support), host=platform.platform())
    (root / 'runner-results.json').write_text(json.dumps(report, indent=2) + '\n')
    print('ACCEPTED ' + str(accepted) + ' sha256=' + accepted_hash)


if __name__ == '__main__':
    main()
