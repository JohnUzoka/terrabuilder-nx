#!/usr/bin/env python3
"""Emit or verify the instrument-free light-value57 A/B candidate; keep52 remains active."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess

INPUT_SHA = '90b135121829d6650ce0e4f8ddb1395108615aa7d46ae487d279bc3a18698b22'
CANDIDATE_SHA = '4c02e40e6b0502d311764d1c4cb0eaebe0fa17d194c61976250378a90be7e55f'
CANDIDATE_MVID = 'b6af8788-d1e2-ad83-4d3d-2896548ad830'
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
    with path.open('x') as stream:
        stream.write(json.dumps(value, sort_keys=True, indent=2) + '\n')


def fresh(path):
    if path.exists() or path.is_symlink() or any(parent.is_symlink() for parent in path.parents):
        raise RuntimeError('fresh non-alias output required')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--input', type=Path, default=Path('/build/hint52/aot-final/runtime-romfs/Terraria.exe'))
    parser.add_argument('--fna', type=Path, default=Path('/build/hint52/aot-final/runtime-romfs/FNA.dll'))
    parser.add_argument('--inspection-only', action='store_true')
    args = parser.parse_args()
    if platform.system() != 'Linux':
        raise RuntimeError('pinned Linux monobuild required')
    root = args.output.absolute()
    fresh(root)
    args.input = args.input.absolute()
    args.fna = args.fna.absolute()
    relogic = args.input.parent / 'ReLogic.dll'
    # Roles are deliberately independent even when callers reuse one pathname.
    pinned = [('game', args.input, INPUT_SHA), ('ReLogic', relogic, RELOGIC_SHA), ('FNA', args.fna, FNA_SHA), ('CoreLib', CORE, CORE_SHA), ('Cecil', CECIL, CECIL_SHA), ('dotnet', DOTNET, DOTNET_SHA)]

    def check_pins():
        for role, path, expected in pinned:
            if sha(path) != expected:
                raise RuntimeError('pinned role differs: ' + role)
        adjacent = args.input.parent / 'FNA.dll'
        if adjacent.exists() and sha(adjacent) != FNA_SHA:
            raise RuntimeError('resolver FNA differs from pinned dependency')

    check_pins()
    source = Path(__file__).resolve().parent

    def source_hashes():
        sources = list(source.glob('*.cs')) + list(source.glob('*.py')) + [source / 'PatchLightValue.csproj']
        sources += list((source / 'proof').glob('*.cs')) + list((source / 'proof').glob('*.py')) + [source / 'proof/ColorProof.csproj']
        sources += list((source.parent / 'patch_frame_profile').glob('*.cs'))
        sources += [source.parent / name for name in ('patch_frame_profile/PatchFrameProfile.csproj', 'patch_tile_stack_state/AuditCanonical.cs', 'patch_tile_profile/Common.cs', 'patch_render_profile/RenderRawSignatures.cs', 'patch_time_logger/PeResources.cs')]
        return {str(path): sha(path) for path in sorted(set(sources))}

    hashes = source_hashes()
    originals = [dict(role=role, path=str(path), sha256=sha(path)) for role, path, _ in pinned]
    root.mkdir(parents=True)
    manifest = root / 'source-manifest.json'
    save(manifest, hashes)
    source_manifest_hash = sha(manifest)
    env = dict(os.environ, DOTNET_ROOT=str(DOTNET.parent), DOTNET_TieredCompilation='0', DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1', TMPDIR='/build/tmp', LIGHT57_SOURCE_MANIFEST=str(manifest))
    commands = []

    def run(argv, label, expected=0, reason=None):
        result = subprocess.run([str(value) for value in argv], env=env, capture_output=True, text=True)
        text = result.stdout + result.stderr
        (root / (label + '.log')).write_text(text)
        commands.append(dict(argv=[str(value) for value in argv], exit=result.returncode, log=label + '.log'))
        if result.returncode != expected or reason is not None and reason not in text:
            save(root / 'failed-commands.json', commands)
            raise RuntimeError(label + '\n' + text)
        print('PASS ' + label, flush=True)

    cecil = '-p:CecilPath=' + str(CECIL)
    support = root / 'support/PatchFrameProfile.dll'
    run([DOTNET, 'build', source.parent / 'patch_frame_profile/PatchFrameProfile.csproj', '-c', 'Release', '-o', root / 'support', '-p:BaseIntermediateOutputPath=' + str(root / 'support-obj') + '/', cecil], 'build-shared-support')
    env['FRAME_PROFILE_TOOL'] = str(support)
    run([DOTNET, 'build', source / 'PatchLightValue.csproj', '-c', 'Release', '-o', root / 'tool', '-p:BaseIntermediateOutputPath=' + str(root / 'obj') + '/', cecil, '-p:FrameToolPath=' + str(support), '-p:SourceManifestHash=' + sha(manifest), '-p:SupportArtifactHash=' + sha(support)], 'build57')
    if hashes != source_hashes():
        raise RuntimeError('source changed during build')
    build_artifacts = {str(path): sha(path) for directory in ('tool', 'support') for path in sorted((root / directory).rglob('*')) if path.is_file()}
    artifact_manifest = root / 'build-artifacts.json'
    save(artifact_manifest, build_artifacts)
    artifact_manifest_hash = sha(artifact_manifest)
    env['LIGHT57_BUILD_ARTIFACTS'] = str(artifact_manifest)
    tool = [DOTNET, root / 'tool/PatchLightValue.dll']

    def final_guards():
        check_pins()
        if sha(manifest) != source_manifest_hash or sha(artifact_manifest) != artifact_manifest_hash:
            raise RuntimeError('source/build manifest changed during verification')
        if hashes != source_hashes() or any(sha(Path(path)) != digest for path, digest in build_artifacts.items()):
            raise RuntimeError('source/build changed during verification')

    if args.inspection_only:
        run(tool + ['emit', args.input, args.fna, root / 'inspection'], 'emit-inspection')
        final_guards()
        for name, expected in [('Terraria.exe', CANDIDATE_SHA), ('ReLogic.dll', RELOGIC_SHA)]:
            path = root / 'inspection/proof-pair' / name
            if sha(path) != expected or path.stat().st_mode & 0o222 or path.stat().st_nlink != 1:
                raise RuntimeError('inspection image differs, writable or linked')
        save(root / 'runner-inspection-only.json', dict(passed=True, accepted=False, hardwarePending=True, performanceAdopted=False, optimizationAdopted=False, activePerformanceBaseline=52, sha256=CANDIDATE_SHA, outputMvid=CANDIDATE_MVID, sourceHash=sha(manifest), sourceHashes=hashes, commands=commands))
        return

    first = root / 'work-accepted'
    repeat = root / 'work-repeat'
    run(tool + ['verify', args.input, args.fna, first], 'verify')
    run(tool + ['verify', args.input, args.fna, repeat], 'repeat')
    identity = json.loads((first / 'validation.json').read_text())
    artifacts = ['proof-pair/Terraria.exe', 'proof-pair/ReLogic.dll']
    proof_receipt = json.loads((first / 'proof/proof-results.json').read_text())
    artifacts += ['proof/' + name for name in proof_receipt['artifacts']]
    if not any(Path(name).suffix in ('.dll', '.exe') for name in proof_receipt['artifacts']):
        raise RuntimeError('actual executable proof artifact missing')
    for name in artifacts:
        if sha(first / name) != sha(repeat / name):
            raise RuntimeError('nondeterministic image/proof artifact: ' + name)
    run(tool + ['prove', args.input, first / 'proof-pair', args.fna, root / 'supplied-proof'], 'supplied-candidate-proof')
    verified_files = {}
    for directory in (first, repeat, root / 'supplied-proof'):
        receipt = json.loads((directory / 'validation.json').read_text())
        if not receipt['passed'] or receipt['accepted'] or receipt['sourceHash'] != source_manifest_hash or receipt['artifactHash'] != artifact_manifest_hash or receipt['outputSha256'] != CANDIDATE_SHA:
            raise RuntimeError('validation identity/binding differs')
        for name in artifacts + ['validation.json', 'preparation.json', 'static-negative/results.json', 'proof/proof-results.json', 'proof/proof.json']:
            verified_files[directory / name] = sha(directory / name)
    candidate = first / 'proof-pair/Terraria.exe'
    bad = root / 'unsupported.exe'; bad.write_bytes(args.input.read_bytes() + b'\0')
    bad_fna = root / 'unsupported-fna.dll'; bad_fna.write_bytes(args.fna.read_bytes() + b'\0')
    bad_library = root / 'unsupported-library'; bad_library.mkdir()
    os.link(args.input, bad_library / 'Terraria.exe')
    (bad_library / 'ReLogic.dll').write_bytes(relogic.read_bytes() + b'\0')
    bad_resolution = root / 'unsupported-resolution'; bad_resolution.mkdir()
    os.link(args.input, bad_resolution / 'Terraria.exe'); os.link(relogic, bad_resolution / 'ReLogic.dll')
    (bad_resolution / 'FNA.dll').write_bytes(args.fna.read_bytes() + b'\0')
    alias = root / 'alias'; alias.symlink_to(first, target_is_directory=True)
    dangling = root / 'dangling'; dangling.symlink_to(root / 'not-created', target_is_directory=True)
    hardlink = root / 'input-hardlink.exe'; os.link(args.input, hardlink)
    (root / 'ReLogic.dll').write_bytes(relogic.read_bytes())
    cases = [
        ('unsupported', bad, args.fna, root / 'reject-unsupported', 'pinned52 game/ReLogic/FNA required'),
        ('already-patched', candidate, args.fna, root / 'reject-patched', 'pinned52 game/ReLogic/FNA required'),
        ('existing-output', args.input, args.fna, first, 'fresh non-alias output required'),
        ('input-output-alias', args.input, args.fna, args.input, 'fresh non-alias output required'),
        ('symlink-output', args.input, args.fna, alias, 'fresh non-alias output required'),
        ('symlink-ancestor', args.input, args.fna, alias / 'new-output', 'symlink output ancestor forbidden'),
        ('dangling-output', args.input, args.fna, dangling, 'fresh non-alias output required'),
        ('hardlink-output', args.input, args.fna, hardlink, 'fresh non-alias output required'),
        ('unsupported-fna', args.input, bad_fna, root / 'reject-fna', 'pinned52 game/ReLogic/FNA required'),
        ('unsupported-relogic', bad_library / 'Terraria.exe', args.fna, root / 'reject-relogic', 'pinned52 game/ReLogic/FNA required'),
        ('resolver-fna', bad_resolution / 'Terraria.exe', args.fna, root / 'reject-resolver-fna', 'resolver FNA differs from pinned dependency'),
        ('duplicate-role-game-fna', args.input, args.input, root / 'reject-duplicate-role', 'pinned52 game/ReLogic/FNA required'),
    ]
    for label, input_path, fna, output, reason in cases:
        run(tool + ['verify', input_path, fna, output], 'guard-' + label, 1, reason)
    # Exercise the Python entry point's independent pin and freshness checks too.
    python_cases = [
        ('python-duplicate-role', ['--input', args.fna, '--fna', args.fna, '--output', root / 'reject-python-role'], 'pinned role differs: game'),
        ('python-output-symlink', ['--output', alias / 'new-output'], 'fresh non-alias output required'),
        ('python-output-hardlink', ['--output', hardlink], 'fresh non-alias output required'),
    ]
    for label, arguments, reason in python_cases:
        run(['python3', source / 'run.py'] + arguments, 'guard-' + label, 1, reason)
    bad_manifest = root / 'incorrect-source-manifest.json'
    invalid_hashes = dict(hashes); invalid_hashes[str(source / 'Program.cs')] = '0' * 64
    save(bad_manifest, invalid_hashes); env['LIGHT57_SOURCE_MANIFEST'] = str(bad_manifest)
    run(tool + ['verify', args.input, args.fna, root / 'reject-source'], 'guard-source-hash', 1, 'source manifest differs from compiled tool')
    env['LIGHT57_SOURCE_MANIFEST'] = str(manifest)
    bad_artifacts = root / 'incorrect-build-artifacts.json'
    invalid_artifacts = dict(build_artifacts); invalid_artifacts[str(root / 'tool/PatchLightValue.dll')] = '0' * 64
    save(bad_artifacts, invalid_artifacts); env['LIGHT57_BUILD_ARTIFACTS'] = str(bad_artifacts)
    run(tool + ['verify', args.input, args.fna, root / 'reject-artifact'], 'guard-artifact-hash', 1, 'built artifact changed')
    env['LIGHT57_BUILD_ARTIFACTS'] = str(artifact_manifest)
    bad_support = root / 'incorrect-support.dll'; bad_support.write_bytes(support.read_bytes() + b'\0')
    support_artifacts = dict(build_artifacts); support_artifacts[str(bad_support)] = sha(bad_support)
    save(root / 'incorrect-support-manifest.json', support_artifacts)
    env['FRAME_PROFILE_TOOL'] = str(bad_support); env['LIGHT57_BUILD_ARTIFACTS'] = str(root / 'incorrect-support-manifest.json')
    run(tool + ['verify', args.input, args.fna, root / 'reject-support'], 'guard-support-binding', 1, 'support artifact differs from compiled dependency')
    env['FRAME_PROFILE_TOOL'] = str(support); env['LIGHT57_BUILD_ARTIFACTS'] = str(artifact_manifest)
    bad_pair = root / 'incorrect-pair'; bad_pair.mkdir()
    (bad_pair / 'Terraria.exe').write_bytes(candidate.read_bytes() + b'\0')
    (bad_pair / 'ReLogic.dll').write_bytes(relogic.read_bytes())
    run(tool + ['prove', args.input, bad_pair, args.fna, root / 'reject-supplied'], 'guard-supplied-image', 1, 'supplied image differs from exact current-source emission')
    (bad_pair / 'Terraria.exe').write_bytes(candidate.read_bytes())
    (bad_pair / 'ReLogic.dll').write_bytes(relogic.read_bytes() + b'\0')
    run(tool + ['prove', args.input, bad_pair, args.fna, root / 'reject-supplied-library'], 'guard-supplied-relogic', 1, 'supplied ReLogic is not byte-identical52')
    for directory in root.glob('reject-*'):
        if any((directory / name).exists() for name in ('Terraria.exe', 'ReLogic.dll', 'acceptance.json', 'proof-pair')):
            raise RuntimeError('unverified pair published: ' + str(directory))
    final_guards()
    if any(sha(path) != digest for path, digest in verified_files.items()):
        raise RuntimeError('verified proof/image/receipt changed before publication')
    for directory in (first, repeat):
        for name, expected in [('Terraria.exe', CANDIDATE_SHA), ('ReLogic.dll', RELOGIC_SHA)]:
            path = directory / 'proof-pair' / name
            if sha(path) != expected or path.stat().st_mode & 0o222 or path.stat().st_nlink != 1:
                raise RuntimeError('verified image changed, writable or hardlinked')
    guards = [case[0] for case in cases] + [case[0] for case in python_cases] + ['source-hash', 'artifact-hash', 'support-binding', 'supplied-image', 'supplied-relogic']
    # No accepted receipt or top-level deployable pair exists before every gate above.
    for directory, name in ((first, 'accepted'), (repeat, 'repeat')):
        final = root / name
        fresh(final)
        directory.rename(final)
        for image in ('Terraria.exe', 'ReLogic.dll'):
            (final / 'proof-pair' / image).rename(final / image)
        (final / 'proof-pair').rmdir()
        receipt = json.loads((final / 'validation.json').read_text())
        receipt.update(accepted=True, negativeGuards=guards, deterministicRepeat=True, suppliedCandidateVerified=True)
        save(final / 'acceptance.json', receipt)
    report = dict(passed=True, accepted=True, hardwarePending=True, performanceAdopted=False, optimizationAdopted=False, activePerformanceBaseline=52, inputSha256=INPUT_SHA, sha256=CANDIDATE_SHA, outputSha256=CANDIDATE_SHA, outputMvid=CANDIDATE_MVID, gameSha256=CANDIDATE_SHA, gameMvid=CANDIDATE_MVID, reLogicSha256=RELOGIC_SHA, fnaSha256=FNA_SHA, targetCorelibSha256=CORE_SHA, sourceHash=sha(manifest), sourceHashes=hashes, proof=identity['proof'], originals=originals, reproducible=True, readonly=True, unchangedCalls=4, changedOriginalBodies=1, negativeGuards=guards, deterministicArtifacts=[name.removeprefix('proof-pair/') for name in artifacts], commands=commands, buildArtifacts=build_artifacts, host=platform.platform())
    save(root / 'runner-results.json', report)
    save(root / 'commands.json', commands)
    print('ACCEPTED A/B PAIR ' + str(root / 'accepted') + ' game=' + CANDIDATE_SHA + ' ReLogic=' + RELOGIC_SHA)


if __name__ == '__main__':
    main()
