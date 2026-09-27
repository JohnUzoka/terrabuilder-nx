#!/usr/bin/env python3
"""Accept only the pinned build42 GetValue entry-diagnostic deletion, never deploy it."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess

INPUT_HASH = '0ce46870f3f384b10f37da068bdbbbeeb567cb58aa69bb0ad0d6511305689ab6'


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def safe_path(path):
    if '..' in path.parts:
        raise RuntimeError('parent traversal forbidden')
    path = path.absolute()
    if any(part.is_symlink() for part in (path, *path.parents)):
        raise RuntimeError('symlink path forbidden: ' + str(path))
    return path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--input', type=Path, default=Path('/build/aot42-windows/runtime-romfs/System.ComponentModel.TypeConverter.dll'))
    parser.add_argument('--mode', choices=('inspect', 'accept'), default='accept')
    args = parser.parse_args()
    source = Path(__file__).resolve().parent
    input_path = safe_path(args.input)
    root = safe_path(args.output)
    if root.exists() or root.is_relative_to(input_path.parent):
        raise RuntimeError('fresh output outside the source runtime directory required')
    if not root.parent.is_dir():
        raise RuntimeError('output parent must already exist')
    root.mkdir(mode=0o700)
    sources = sorted(source.glob('*.cs')) + [source / 'run.py', source / 'PatchPropertyDiagnostics.csproj']
    sources += sorted((source.parent / 'patch_frame_profile').glob('*.cs'))
    sources += [source.parent / 'patch_frame_profile/PatchFrameProfile.csproj', source.parent / 'patch_tile_profile/Common.cs', source.parent / 'patch_time_logger/PeResources.cs']
    source_hashes = {str(path): sha(path) for path in sources}
    originals = {str(input_path): sha(input_path)}
    env = dict(os.environ, DOTNET_TieredCompilation='0', DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1', TMPDIR=str(root / 'tmp'))
    (root / 'tmp').mkdir()
    report = dict(passed=False, mode=args.mode, originals=originals, sourceHashes=source_hashes, commands=[], scope='Patch-time only. Zero strong-name signature slot retained, not cryptographically signed. No AOT, loader, hardware or gameplay acceptance claim.')

    def run(argv, label, expected=0, rejection=None):
        argv = [str(value) for value in argv]
        result = subprocess.run(argv, env=env, capture_output=True, text=True)
        text = result.stdout + result.stderr
        (root / (label + '.log')).write_text(text)
        report['commands'].append(dict(argv=argv, exit=result.returncode, log=label + '.log'))
        if result.returncode != expected or (rejection is not None and rejection not in text):
            raise RuntimeError(label + '\n' + text)
        print('PASS ' + label, flush=True)

    try:
        if originals[str(input_path)] != INPUT_HASH:
            raise RuntimeError('unsupported or already-patched input')
        dotnet = '/root/.dotnet/dotnet'
        cecil_path = Path('/mono-nx/dotnet_runtime/artifacts/bin/Mono.Linker/Debug/net9.0/Mono.Cecil.dll')
        cecil = '-p:CecilPath=' + str(cecil_path)
        report['cecilSha256'] = sha(cecil_path)
        support = root / 'support/PatchFrameProfile.dll'
        run([dotnet, 'build', source.parent / 'patch_frame_profile/PatchFrameProfile.csproj', '-c', 'Release', '-o', root / 'support', '-p:BaseIntermediateOutputPath=' + str(root / 'support-obj') + '/', cecil], 'build-shared-support')
        env['FRAME_PROFILE_TOOL'] = str(support)
        run([dotnet, 'build', source / 'PatchPropertyDiagnostics.csproj', '-c', 'Release', '-o', root / 'tool', '-p:BaseIntermediateOutputPath=' + str(root / 'obj') + '/', cecil, '-p:FrameToolPath=' + str(support)], 'build48')
        tool = [dotnet, root / 'tool/PatchPropertyDiagnostics.dll']
        report['toolSha256'] = sha(root / 'tool/PatchPropertyDiagnostics.dll')
        report['supportSha256'] = sha(support)
        run(tool + ['inspect', input_path, root / 'inspection'], 'inspect')
        if args.mode == 'inspect':
            report['passed'] = True
            return
        for label in ('accepted', 'repeat'):
            run(tool + ['accept', input_path, root / label], label)
        artifacts = ['System.ComponentModel.TypeConverter.dll', 'proof/DescriptorExactMethods.dll', 'proof/candidate-for-proof-only.dll']
        for name in artifacts:
            if (root / 'accepted' / name).read_bytes() != (root / 'repeat' / name).read_bytes():
                raise RuntimeError('nondeterministic accepted artifact: ' + name)
        image = root / 'accepted/System.ComponentModel.TypeConverter.dll'
        accepted_hash = sha(image)
        bad = root / 'unsupported.dll'
        bad.write_bytes(input_path.read_bytes() + b'\0')
        alias = root / 'alias'
        alias.symlink_to(root / 'accepted', target_is_directory=True)
        dangling = root / 'dangling'
        dangling.symlink_to(root / 'does-not-exist', target_is_directory=True)
        input_alias = root / 'input-symlink.dll'
        input_alias.symlink_to(input_path)
        hardlink = root / 'input-hardlink.dll'
        os.link(input_path, hardlink)
        empty = root / 'existing-empty'
        empty.mkdir()
        cases = [
            ('unsupported', bad, root / 'reject-unsupported', 'unsupported or already-patched input'),
            ('already-patched', image, root / 'reject-patched', 'unsupported or already-patched input'),
            ('existing-output', input_path, root / 'accepted', 'existing or aliased output forbidden'),
            ('existing-empty-output', input_path, empty, 'existing or aliased output forbidden'),
            ('input-output-alias', input_path, input_path, 'existing or aliased output forbidden'),
            ('symlink-output', input_path, alias, 'symlink path forbidden'),
            ('dangling-symlink-output', input_path, dangling, 'symlink path forbidden'),
            ('symlink-parent-output', input_path, alias / 'fresh', 'symlink path forbidden'),
            ('symlink-input', input_alias, root / 'reject-symlink-input', 'symlink path forbidden'),
            ('hardlink-output', input_path, hardlink, 'existing or aliased output forbidden'),
            ('source-runtime-output', input_path, input_path.parent / ('rejected-ui48-' + root.name), 'output in source runtime directory forbidden'),
            ('missing-parent', input_path, root / 'missing/fresh', 'output parent must already exist'),
            ('parent-traversal', input_path, str(root / 'missing') + '/../fresh', 'parent traversal forbidden'),
        ]
        try:
            for mode in ('inspect', 'accept'):
                for label, candidate, output, rejection in cases:
                    existed = os.path.lexists(output)
                    run(tool + [mode, candidate, output], 'guard-' + mode + '-' + label, 1, rejection)
                    if not existed and os.path.lexists(output):
                        raise RuntimeError('rejected invocation created an output: ' + str(output))
        finally:
            hardlink.unlink()
        for name in ('accepted', 'repeat'):
            path = root / name / 'System.ComponentModel.TypeConverter.dll'
            if sha(path) != accepted_hash or path.stat().st_mode & 0o222:
                raise RuntimeError('accepted image changed or writable')
        report.update(passed=True, reproducible=True, readonly=True, sha256=accepted_hash, negativeGuards=[case[0] for case in cases], guardModes=['inspect', 'accept'], deterministicArtifacts={name: sha(root / 'accepted' / name) for name in artifacts})
        print('ACCEPTED ' + str(image) + ' sha256=' + accepted_hash, flush=True)
    except Exception as error:
        report['error'] = str(error)
        raise
    finally:
        report['originalsAfter'] = {str(input_path): sha(input_path)}
        report['sourceHashesAfter'] = {str(path): sha(path) for path in sources}
        unchanged = report['originalsAfter'] == originals and report['sourceHashesAfter'] == source_hashes
        report['sourceInputsUnchanged'] = unchanged
        if not unchanged:
            report['passed'] = False
            report['error'] = 'source input or patch/proof/support sources changed'
        (root / 'runner-results.json').write_text(json.dumps(report, indent=2) + '\n')
        if not unchanged:
            raise RuntimeError(report['error'])


if __name__ == '__main__':
    main()
