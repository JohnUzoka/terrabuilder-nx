#!/usr/bin/env python3
"""Emit/prove the measurement-only update56 candidate from the exact keep52 pair."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess

INPUT_SHA = '90b135121829d6650ce0e4f8ddb1395108615aa7d46ae487d279bc3a18698b22'
RELOGIC_SHA = '856438fe15b91b2bc1972f8ebd710cb3ef0ba2d6d7900e65d33bbf2e3c8d68c8'
FNA_SHA = '15427b2cb4c7952160f4cc124711f29b428459949304ceb0d16436e68a01882f'
CORE_SHA = 'ddcbdd24a642d9bc923aab3df519c68803b51afccfe252b5846e9898eea191fd'
CECIL_SHA = 'd864ae1b39be10eaf671cd4a8a5e6bd2613740ca57b294c400776f8617dc5355'
DOTNET_SHA = '11e4b2ad384bfa6c1adf01b0adbbe9dfe5d673ddbdfdbde0412411ac4e027520'
DOTNET = Path('/build/runtime-source/.dotnet/dotnet')
CORE = Path('/mono-nx/dotnet_runtime/artifacts/bin/mono/libnx.arm64.Debug/System.Private.CoreLib.dll')
CECIL = Path('/mono-nx/dotnet_runtime/artifacts/bin/Mono.Linker/Debug/net9.0/Mono.Cecil.dll')


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def save(path, value):
    path.write_text(json.dumps(value, sort_keys=True, indent=2) + '\n')


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
    pinned = [(args.input, INPUT_SHA), (relogic, RELOGIC_SHA), (args.fna, FNA_SHA), (CORE, CORE_SHA), (CECIL, CECIL_SHA), (DOTNET, DOTNET_SHA)]
    if any(sha(path) != expected for path, expected in pinned):
        raise RuntimeError('exact52 game/ReLogic/FNA and pinned CoreLib/Cecil/dotnet required')
    source = Path(__file__).resolve().parent

    def source_hashes():
        sources = list(source.glob('*.cs')) + list(source.glob('*.py')) + list(source.glob('*.json')) + [source / 'PatchUpdateProfile.csproj', source.parent / 'analyze_update_profile.py']
        sources += list((source.parent / 'patch_frame_profile').glob('*.cs'))
        sources += [source.parent / 'patch_frame_profile/PatchFrameProfile.csproj', source.parent / 'patch_tile_profile/Common.cs', source.parent / 'patch_render_profile/RenderRawSignatures.cs', source.parent / 'patch_time_logger/PeResources.cs']
        return {str(path): sha(path) for path in sorted(set(sources))}

    hashes = source_hashes()
    originals = {str(path): sha(path) for path, _ in pinned}
    root.mkdir(parents=True)
    manifest = root / 'source-manifest.json'
    save(manifest, hashes)
    env = dict(os.environ, DOTNET_ROOT=str(DOTNET.parent), DOTNET_TieredCompilation='0', DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1', TMPDIR='/build/tmp', UPDATE56_SOURCE_MANIFEST=str(manifest), UPDATE56_REPORT_PROOF=str(source / 'prove_update_reports.py'))
    commands = []

    def run(argv, label, expected=0, reason=None):
        result = subprocess.run([str(value) for value in argv], env=env, capture_output=True, text=True)
        text = result.stdout + result.stderr
        (root / (label + '.log')).write_text(text)
        commands.append(dict(argv=[str(value) for value in argv], exit=result.returncode, log=label + '.log'))
        save(root / 'commands.json', commands)
        if result.returncode != expected or reason is not None and reason not in text:
            raise RuntimeError(label + '\n' + text)
        print('PASS ' + label, flush=True)

    cecil = '-p:CecilPath=' + str(CECIL)
    support = root / 'support/PatchFrameProfile.dll'
    run([DOTNET, 'build', source.parent / 'patch_frame_profile/PatchFrameProfile.csproj', '-c', 'Release', '-o', root / 'support', '-p:BaseIntermediateOutputPath=' + str(root / 'support-obj') + '/', cecil], 'build-shared-support')
    env['FRAME_PROFILE_TOOL'] = str(support)
    run([DOTNET, 'build', source / 'PatchUpdateProfile.csproj', '-c', 'Release', '-o', root / 'tool', '-p:BaseIntermediateOutputPath=' + str(root / 'obj') + '/', cecil, '-p:FrameToolPath=' + str(support), '-p:SourceManifestHash=' + sha(manifest), '-p:SupportArtifactHash=' + sha(support)], 'build56')
    if hashes != source_hashes():
        raise RuntimeError('source changed during build')
    build_artifacts = {str(path): sha(path) for directory in ('tool', 'support') for path in sorted((root / directory).rglob('*')) if path.is_file()}
    artifact_manifest = root / 'build-artifacts.json'
    save(artifact_manifest, build_artifacts)
    env['UPDATE56_BUILD_ARTIFACTS'] = str(artifact_manifest)
    tool = [DOTNET, root / 'tool/PatchUpdateProfile.dll']
    mode = 'emit' if args.inspection_only else 'accept'
    run(tool + [mode, args.input, args.fna, root / ('inspection' if args.inspection_only else 'accepted')], mode)
    identity = json.loads((root / ('inspection/inspection-only.json' if args.inspection_only else 'accepted/acceptance.json')).read_text())
    candidate_sha = identity['outputSha256']
    if args.inspection_only:
        if originals != {str(path): sha(path) for path, _ in pinned} or hashes != source_hashes() or any(sha(Path(path)) != digest for path, digest in build_artifacts.items()):
            raise RuntimeError('source/input/build changed during inspection')
        if sha(root / 'inspection/proof-pair/Terraria.exe') != candidate_sha or sha(root / 'inspection/proof-pair/ReLogic.dll') != RELOGIC_SHA:
            raise RuntimeError('inspection emission differs from receipt')
        save(root / 'runner-inspection-only.json', dict(passed=True, accepted=False, hardwarePending=True, measurementOnly=True, performanceAdopted=False, sha256=candidate_sha, sourceHash=sha(manifest), sourceHashes=hashes, commands=commands))
        return
    run(tool + ['accept', args.input, args.fna, root / 'repeat'], 'repeat')
    artifacts = ['Terraria.exe', 'ReLogic.dll'] + [str(path.relative_to(root / 'accepted')) for path in sorted((root / 'accepted/proof').rglob('*')) if path.suffix in ('.dll', '.exe')]
    if len(artifacts) < 3:
        raise RuntimeError('actual executable proof artifact missing')
    for name in artifacts:
        if not (root / 'repeat' / name).is_file() or sha(root / 'accepted' / name) != sha(root / 'repeat' / name):
            raise RuntimeError('nondeterministic accepted/proof artifact: ' + name)
    run(tool + ['prove', args.input, root / 'accepted', args.fna, root / 'supplied-proof'], 'supplied-candidate-proof')
    accepted = root / 'accepted/Terraria.exe'
    accepted_hashes = {'Terraria.exe': candidate_sha, 'ReLogic.dll': RELOGIC_SHA}
    bad = root / 'unsupported.exe'; bad.write_bytes(args.input.read_bytes() + b'\0')
    bad_fna = root / 'unsupported-fna.dll'; bad_fna.write_bytes(args.fna.read_bytes() + b'\0')
    bad_library = root / 'unsupported-library'; bad_library.mkdir()
    os.link(args.input, bad_library / 'Terraria.exe')
    (bad_library / 'ReLogic.dll').write_bytes(relogic.read_bytes() + b'\0')
    bad_resolution = root / 'unsupported-resolution'; bad_resolution.mkdir()
    os.link(args.input, bad_resolution / 'Terraria.exe'); os.link(relogic, bad_resolution / 'ReLogic.dll')
    (bad_resolution / 'FNA.dll').write_bytes(args.fna.read_bytes() + b'\0')
    alias = root / 'alias'; alias.symlink_to(root / 'accepted', target_is_directory=True)
    dangling = root / 'dangling'; dangling.symlink_to(root / 'not-created', target_is_directory=True)
    hardlink = root / 'input-hardlink.exe'; os.link(args.input, hardlink)
    # Unsupported input stays adjacent to a valid library, ensuring hash rejection
    # (not a missing-dependency exception) is the reason for the negative result.
    (root / 'ReLogic.dll').write_bytes(relogic.read_bytes())
    cases = [
        ('unsupported', bad, args.fna, root / 'reject-unsupported', 'pinned52 game/ReLogic/FNA required'),
        ('already-patched', accepted, args.fna, root / 'reject-patched', 'pinned52 game/ReLogic/FNA required'),
        ('existing-output', args.input, args.fna, root / 'accepted', 'fresh non-alias output required'),
        ('input-output-alias', args.input, args.fna, args.input, 'fresh non-alias output required'),
        ('symlink-output', args.input, args.fna, alias, 'fresh non-alias output required'),
        ('symlink-ancestor', args.input, args.fna, alias / 'new-output', 'symlink output ancestor forbidden'),
        ('dangling-output', args.input, args.fna, dangling, 'fresh non-alias output required'),
        ('hardlink-output', args.input, args.fna, hardlink, 'fresh non-alias output required'),
        ('unsupported-fna', args.input, bad_fna, root / 'reject-fna', 'pinned52 game/ReLogic/FNA required'),
        ('unsupported-relogic', bad_library / 'Terraria.exe', args.fna, root / 'reject-relogic', 'pinned52 game/ReLogic/FNA required'),
        ('resolver-fna', bad_resolution / 'Terraria.exe', args.fna, root / 'reject-resolver-fna', 'resolver FNA differs from pinned dependency'),
    ]
    for label, input_path, fna, output, reason in cases:
        run(tool + ['accept', input_path, fna, output], 'guard-' + label, 1, reason)
    bad_manifest = root / 'incorrect-source-manifest.json'
    invalid_hashes = dict(hashes); invalid_hashes[str(source / 'UpdatePatcher.cs')] = '0' * 64
    save(bad_manifest, invalid_hashes); env['UPDATE56_SOURCE_MANIFEST'] = str(bad_manifest)
    run(tool + ['accept', args.input, args.fna, root / 'reject-source'], 'guard-source-hash', 1, 'source manifest differs from compiled tool')
    env['UPDATE56_SOURCE_MANIFEST'] = str(manifest)
    bad_artifacts = root / 'incorrect-build-artifacts.json'
    invalid_artifacts = dict(build_artifacts); invalid_artifacts[str(root / 'tool/PatchUpdateProfile.dll')] = '0' * 64
    save(bad_artifacts, invalid_artifacts); env['UPDATE56_BUILD_ARTIFACTS'] = str(bad_artifacts)
    run(tool + ['accept', args.input, args.fna, root / 'reject-artifact'], 'guard-artifact-hash', 1, 'built artifact changed')
    env['UPDATE56_BUILD_ARTIFACTS'] = str(artifact_manifest)
    bad_support = root / 'incorrect-support.dll'; bad_support.write_bytes(support.read_bytes() + b'\0')
    support_artifacts = dict(build_artifacts); support_artifacts[str(bad_support)] = sha(bad_support)
    save(root / 'incorrect-support-manifest.json', support_artifacts)
    env['FRAME_PROFILE_TOOL'] = str(bad_support); env['UPDATE56_BUILD_ARTIFACTS'] = str(root / 'incorrect-support-manifest.json')
    run(tool + ['accept', args.input, args.fna, root / 'reject-support'], 'guard-support-binding', 1, 'support artifact differs from compiled dependency')
    env['FRAME_PROFILE_TOOL'] = str(support); env['UPDATE56_BUILD_ARTIFACTS'] = str(artifact_manifest)
    bad_pair = root / 'incorrect-pair'; bad_pair.mkdir()
    (bad_pair / 'Terraria.exe').write_bytes(accepted.read_bytes() + b'\0')
    (bad_pair / 'ReLogic.dll').write_bytes(relogic.read_bytes())
    run(tool + ['prove', args.input, bad_pair, args.fna, root / 'reject-supplied'], 'guard-supplied-image', 1, 'supplied image differs from exact current-source emission')
    (bad_pair / 'Terraria.exe').write_bytes(accepted.read_bytes())
    (bad_pair / 'ReLogic.dll').write_bytes(relogic.read_bytes() + b'\0')
    run(tool + ['prove', args.input, bad_pair, args.fna, root / 'reject-supplied-library'], 'guard-supplied-relogic', 1, 'supplied ReLogic is not byte-identical52')
    for directory in root.glob('reject-*'):
        if (directory / 'Terraria.exe').exists() or (directory / 'ReLogic.dll').exists() or (directory / 'acceptance.json').exists():
            raise RuntimeError('unverified pair published: ' + str(directory))
    if originals != {str(path): sha(path) for path, _ in pinned} or hashes != source_hashes() or any(sha(Path(path)) != digest for path, digest in build_artifacts.items()):
        raise RuntimeError('source/input/build changed')
    for name, expected in accepted_hashes.items():
        path = root / 'accepted' / name
        if sha(path) != expected or path.stat().st_mode & 0o222 or path.stat().st_nlink != 1:
            raise RuntimeError('accepted image changed, writable or hardlinked')
    report = dict(passed=True, hardwarePending=True, performanceAdopted=False, sha256=candidate_sha, gameSha256=candidate_sha, gameMvid=identity['outputMvid'], outputSha256=candidate_sha, outputMvid=identity['outputMvid'], fnaSha256=FNA_SHA, targetCorelibSha256=CORE_SHA, proof=identity['proof'], measurementOnly=True, reLogicSha256=RELOGIC_SHA, sourceHash=sha(manifest), sourceHashes=hashes, originals=originals, reproducible=True, readonly=True, negativeGuards=[case[0] for case in cases] + ['source-hash', 'artifact-hash', 'support-binding', 'supplied-image', 'supplied-relogic'], deterministicArtifacts=artifacts, commands=commands, buildArtifacts=build_artifacts, host=platform.platform())
    save(root / 'runner-results.json', report)
    print('ACCEPTED PAIR ' + str(root / 'accepted') + ' game=' + candidate_sha + ' ReLogic=' + RELOGIC_SHA)


if __name__ == '__main__':
    main()
