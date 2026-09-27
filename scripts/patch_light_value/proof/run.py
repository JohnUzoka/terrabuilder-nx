#!/usr/bin/env python3
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parent
CACHE = Path('/build') if Path('/build/runtime-source/.dotnet/dotnet').is_file() else Path(os.environ.get('TERRABUILDER_CACHE', Path.home() / '.cache/terraria-switch-build'))
DOTNET = CACHE / 'runtime-source/.dotnet/dotnet'
CECIL = CACHE / 'ui48-patcher-final/support/Mono.Cecil.dll'
FNA = CACHE / 'hint52/aot-final/runtime-romfs/FNA.dll'
ORIGINAL = CACHE / 'hint52/aot-final/runtime-romfs/Terraria.exe'
ORIGINAL_SHA = '90b135121829d6650ce0e4f8ddb1395108615aa7d46ae487d279bc3a18698b22'
CANDIDATE_SHA = '4c02e40e6b0502d311764d1c4cb0eaebe0fa17d194c61976250378a90be7e55f'
FNA_SHA = '15427b2cb4c7952160f4cc124711f29b428459949304ceb0d16436e68a01882f'
SOURCE_NAMES = ('ColorProof.csproj', 'Fixture.cs', 'Program.cs', 'Projection.cs', 'run.py')
CONTROL_NAMES = ('wrong-channel', 'wrong-clamp', 'wrong-coordinate', 'duplicate-engine-call', 'early-brightness')


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def source_hash(hashes):
    return hashlib.sha256(''.join(name + '\0' + value + '\n' for name, value in sorted(hashes.items())).encode()).hexdigest()


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def save(path, value):
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + '\n')


def main():
    parser = argparse.ArgumentParser(description='Exact52/exact57 Lighting.GetColor body proof with actual pinned FNA; no Switch performance claim')
    parser.add_argument('--input', type=Path, default=ORIGINAL)
    parser.add_argument('--candidate', type=Path, required=True)
    parser.add_argument('--candidate-sha', default=CANDIDATE_SHA, help='Compatibility pin; must equal the fixed exact57 candidate')
    parser.add_argument('--fna', type=Path, default=FNA)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--seed', type=int, default=570052)
    parser.add_argument('--repeats', type=int, default=3)
    parser.add_argument('--timing', action='store_true', help='Also collect the separate paired host microbenchmark; inconclusive, not an acceptance/performance gate')
    parser.add_argument('--iterations', type=int, default=1000000)
    parser.add_argument('--samples', type=int, default=17)
    parser.add_argument('--cpu', type=int)
    args = parser.parse_args()
    output = Path(os.path.abspath(args.output))
    require(not any(p.is_symlink() for p in (output, *output.parents)), 'no output symlink aliases')
    require(not output.exists(), 'fresh proof output required')
    repository = ROOT.parents[2]
    require(not output.is_relative_to(repository) and not repository.is_relative_to(output), 'proof output must not overlap source tree')
    require(args.candidate_sha == CANDIDATE_SHA, 'candidate pin must be exact57')
    require(0 <= args.seed <= 0xffffffff and args.repeats >= 3, 'uint32 seed and at least three repetitions required')
    require(min(args.iterations, args.samples) > 0, 'positive timing counts required')
    if args.cpu is not None:
        require(args.cpu in os.sched_getaffinity(0), 'requested CPU not allowed')
        os.sched_setaffinity(0, {args.cpu})
    # Check each role separately: aliases must not collapse different image pins.
    pins = (
        ('dotnet', DOTNET, '11e4b2ad384bfa6c1adf01b0adbbe9dfe5d673ddbdfdbde0412411ac4e027520'),
        ('cecil', CECIL, 'd864ae1b39be10eaf671cd4a8a5e6bd2613740ca57b294c400776f8617dc5355'),
        ('input', args.input.absolute(), ORIGINAL_SHA),
        ('candidate', args.candidate.absolute(), CANDIDATE_SHA),
        ('fna', args.fna.absolute(), FNA_SHA),
    )
    for role, path, expected in pins:
        require(path.is_file() and sha(path) == expected, role + ' pin mismatch: ' + str(path))
        require(not path.resolve().is_relative_to(output), 'output overlaps ' + role)
    sources = {name: sha(ROOT / name) for name in SOURCE_NAMES}
    output.mkdir(parents=True)
    for name in ('tmp', 'cli-home', 'nuget'):
        (output / name).mkdir()
    environment = dict(os.environ, DOTNET_ROOT=str(DOTNET.parent), DOTNET_CLI_HOME=str(output / 'cli-home'), NUGET_PACKAGES=str(output / 'nuget'), TMPDIR=str(output / 'tmp'), DOTNET_TieredCompilation='0', DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1')
    commands = []

    def run(argv, name):
        argv = list(map(str, argv))
        process = subprocess.run(argv, env=environment, cwd=ROOT, capture_output=True, text=True)
        (output / (name + '.log')).write_text(process.stdout + process.stderr)
        commands.append(dict(argv=argv, exit=process.returncode, log=name + '.log'))
        save(output / 'commands.json', commands)
        require(process.returncode == 0, name + '\n' + process.stdout + process.stderr)
        print('PASS ' + name, flush=True)

    run([DOTNET, 'build', ROOT / 'ColorProof.csproj', '-c', 'Release', '-o', output / 'bin', '-p:BaseIntermediateOutputPath=' + str(output / 'obj') + '/', '-p:CecilPath=' + str(CECIL), '-p:FnaPath=' + str(args.fna.absolute()), '-p:ImportDirectoryBuildProps=false', '-p:ImportDirectoryBuildTargets=false', '-p:ImportDirectoryPackagesProps=false'], 'build-proof')
    build_artifacts = {p.relative_to(output).as_posix(): sha(p) for p in sorted((output / 'bin').iterdir()) if p.is_file()}
    invocation = [DOTNET, output / 'bin/ColorProof.dll', args.input.absolute(), args.candidate.absolute(), CANDIDATE_SHA, args.fna.absolute(), output, args.seed, args.iterations, args.samples, args.repeats]
    if args.timing:
        invocation.append('--timing')
    run(invocation, 'execute-proof')
    require(sources == {name: sha(ROOT / name) for name in SOURCE_NAMES}, 'proof source changed during run')
    for role, path, expected in pins:
        require(sha(path) == expected, role + ' changed during run')
    require(build_artifacts == {name: sha(output / name) for name in build_artifacts}, 'built artifact changed during run')

    def load(name):
        return json.loads((output / name).read_text())

    summary = load('summary.json')
    projection = load('projection.json')
    behavior = load('behavior.json')
    boundaries = load('boundary-checks.json')
    controls = load('negative-controls.json')
    allocation = load('zero-allocation.json')
    for label, result in (('summary', summary), ('projection', projection), ('behavior', behavior), ('boundaries', boundaries), ('controls', controls), ('allocation', allocation)):
        require(result.get('passed') is True, label + ' did not pass')
    for result in (summary, projection):
        require(result.get('originalSha256') == ORIGINAL_SHA and result.get('candidateSha256') == CANDIDATE_SHA and result.get('fnaSha256') == FNA_SHA, 'proof image identity mismatch')
    require(summary['behavior'] == behavior and summary['negativeControlCount'] == 5 and summary['zeroMeasuredAllocation'] is True, 'summary disagrees with mandatory evidence')
    require(behavior['cases'] == 17350 and behavior['lanes'] == 2 and behavior['repeats'] == args.repeats and behavior['pairedComparisons'] == 17350 * 2 * args.repeats, 'behavior coverage differs')
    require(len(behavior['observationDigests']) == args.repeats and len(set(behavior['observationDigests'])) == 1, 'observation repetitions differ')
    require(sha(output / 'scenarios.jsonl') == behavior['observationDigests'][0], 'raw observation digest mismatch')
    require(boundaries['roundtrips'] == 4127 and boundaries['colorSize'] == 4 and boundaries['vector3Size'] == 12, 'actual FNA boundary checks differ')
    require(projection['exactOpcodeComparison'] is True and projection['probeSha256'] == sha(output / 'Color57ActualBodies.dll'), 'serialized projection identity mismatch')
    receipts = {row['name']: row for row in projection['receipts']}
    require(set(receipts) == {'OriginalGetter', 'CandidateGetter', 'Original', 'Candidate', 'Replica', 'OriginalStress', 'CandidateStress'}, 'projection receipt set differs')
    for name, receipt in receipts.items():
        candidate = name.startswith('Candidate')
        expected_mvid = 'b6af8788-d1e2-ad83-4d3d-2896548ad830' if candidate else 'df61a8d1-0622-9555-82c2-a2118b6c9bc4'
        require(receipt['sourceMvid'] == expected_mvid and receipt['sourceSha256'] == (CANDIDATE_SHA if candidate else ORIGINAL_SHA), 'projected source identity mismatch')
        expected_instructions = 2 if name.endswith('Getter') else 67 if candidate else 65
        require(len(receipt['instructions']) == expected_instructions, 'projected instruction count differs')
        require(all(row['expected'] == row['actual'] for row in receipt['instructions']), 'source-mapped instruction differs')
    require([row['name'] for row in controls['results']] == list(CONTROL_NAMES), 'negative control set differs')
    for row in controls['results']:
        require(row['detected'] is True and row['mismatches'] > 0, 'ineffective negative control')
        require(row['projection']['before'] != row['projection']['after'] and row['projection']['imageSha256'] == sha(output / ('Control-' + row['name'] + '.dll')), 'mutant artifact identity mismatch')
    require(all(allocation[key] == value for key, value in {'workloads': 5, 'bodies': 3, 'samples': 3, 'callsPerSample': 10000, 'measuredSamples': 45, 'measuredInvocations': 450000, 'measuredBytes': 0}.items()), 'allocation coverage differs')
    require(len(allocation['rows']) == 45 and all(row['measuredBytes'] == 0 for row in allocation['rows']), 'body allocation observed')
    require((output / 'timing.json').exists() == args.timing, 'optional timing evidence differs')
    stable_results = ('behavior.json', 'boundary-checks.json', 'negative-controls.json', 'scenarios.jsonl', 'zero-allocation.json')
    artifact_names = ('Color57ActualBodies.dll',) + tuple('Control-' + name + '.dll' for name in CONTROL_NAMES) + stable_results
    artifacts = build_artifacts | {name: sha(output / name) for name in artifact_names}
    receipt = dict(
        passed=True, inputSha256=ORIGINAL_SHA, candidateSha256=CANDIDATE_SHA, fnaSha256=FNA_SHA,
        inputMvid=receipts['Original']['sourceMvid'], candidateMvid=receipts['Candidate']['sourceMvid'],
        sourceHash=source_hash(sources), sourceHashes=sources, artifacts=artifacts,
        caseCount=behavior['cases'], lanes=behavior['lanes'], repetitions=behavior['repeats'],
        pairedComparisons=behavior['pairedComparisons'], seed=args.seed, observationDigests=behavior['observationDigests'],
        originalInstructions=len(receipts['Original']['instructions']), candidateInstructions=len(receipts['Candidate']['instructions']),
        fnaRoundtrips=boundaries['roundtrips'],
        negativeControls=dict(passed=True, count=len(controls['results']), detected=sum(row['detected'] for row in controls['results']), casesPerControl=behavior['cases'], comparisons=behavior['cases'] * len(controls['results']), mismatches={row['name']: row['mismatches'] for row in controls['results']}),
        zeroAllocation={key: allocation[key] for key in ('passed', 'workloads', 'bodies', 'samples', 'callsPerSample', 'measuredSamples', 'measuredInvocations', 'measuredBytes')},
        hostOnly=True, arm64Claim=False, fpsClaim=False, optionalTimingCollected=args.timing,
        performanceConclusion='No performance acceptance: prior paired host timing was inconclusive; optional timing remains host-only and Switch performance is unknown.',
    )
    manifest = dict(passed=True, sourceHashes=sources, sourceHash=receipt['sourceHash'], buildArtifacts=build_artifacts,
                    pinnedDependencies={role: dict(path=str(path), sha256=expected) for role, path, expected in pins},
                    cpuAffinity=sorted(os.sched_getaffinity(0)), arguments={key: str(value) if isinstance(value, Path) else value for key, value in vars(args).items()},
                    commands=commands, resultHashes={p.name: sha(p) for p in sorted(output.iterdir()) if p.is_file()}, hostOnly=True, arm64Claim=False, fpsClaim=False)
    save(output / 'build-manifest.json', manifest)
    save(output / 'proof-results.json', receipt)
    print('PROOF ' + str(output / 'proof-results.json') + ' SHA256 ' + sha(output / 'proof-results.json'), flush=True)


if __name__ == '__main__':
    main()
