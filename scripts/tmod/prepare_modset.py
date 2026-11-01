"""Stage exact .tmod packages, tune and lower their assemblies, and prepare build_tmod_modset_nro.py inputs.

Runs on the host. Inputs: the base stage (--base-dir: romfs/, flat_libs/), the unpacked tModLoader
release, the NxFix'd tModLoader.dll, scripts/tmod/base_modset.json, the toolchain's tmod-runtime,
.NET SDK and Mono.Cecil. The C# tools are built once into --tools (scripts/tmod/base_dlls.py).
The output variant folder must be fresh. Mod assemblies are tuned once first (mod_tuning: Debug-built
mods' DisableOptimizations flag, Fargo's banner timer). Materialized mod methods are repacked and
rescanned before final hook identities are baked; no assembly is changed after AOT compilation.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess

from base_dlls import build_tool, dotnet_env, tool_digest
from tmod_file import extract_tmod_member, loader_version_problem, read_tmod_info, replace_tmod_member

DETOUR = 'Libraries/monomod.runtimedetour/25.3.2/lib/net8.0/MonoMod.RuntimeDetour.dll'


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path, required=True, help='the variant folder (must not exist)')
    parser.add_argument('--mods-dir', type=Path, required=True)
    parser.add_argument('--primary', required=True)
    parser.add_argument('--label', required=True)
    parser.add_argument('--base-dir', type=Path, required=True, help='the base stage: romfs/ and flat_libs/')
    parser.add_argument('--release', type=Path, required=True, help='the unpacked tModLoader release')
    parser.add_argument('--config', type=Path, required=True, help='baseline modset.json (scripts/tmod/base_modset.json)')
    parser.add_argument('--tmod-runtime', type=Path, required=True, help='the toolchain tmod-runtime component')
    parser.add_argument('--dotnet', type=Path, required=True)
    parser.add_argument('--cecil', type=Path, required=True)
    parser.add_argument('--tools', type=Path, required=True, help='where the C# tools are built (shared, cached)')
    parser.add_argument('--game', type=Path, required=True,
                        help='NX/save-patched tModLoader.dll before any offline hook lowering')
    parser.add_argument('--interpret-hooks', action='store_true',
                        help='leave TerrariaHooks.dll interpreted (not AOT). Its ~35 MiB of generated '
                             'accessor/delegate code otherwise pushes large mod sets past the ±128 MiB '
                             'CALL26 reach of the AOT method tables; hardware performance is unmeasured.')
    args = parser.parse_args()
    game = args.game.resolve()
    out = args.out.resolve()
    if out.exists():
        parser.error(f'Fresh output variant required: {out}')
    release = args.release.resolve()
    stock_romfs = args.base_dir.resolve() / 'romfs'
    flat_libs = args.base_dir.resolve() / 'flat_libs'
    hooks = stock_romfs / 'TerrariaHooks.dll'
    detour = release / DETOUR
    dotnet = args.dotnet.resolve()
    config = json.loads(args.config.read_text())
    packages = {}
    for path in sorted(args.mods_dir.resolve().glob('*.tmod')):
        info = read_tmod_info(path)
        name = info['name']
        if not name.isidentifier():
            raise ValueError(f'Unsupported package identity: {name}')
        problem = loader_version_problem(info['loader_version'])
        if problem:
            raise ValueError(f'{name}: {problem}')
        if name.casefold() in {existing.casefold() for existing in packages}:
            raise ValueError(f'Duplicate mod package: {name}')
        packages[name] = {'source': path, 'source_sha256': sha(path), 'info': info}
    if args.primary not in packages:
        parser.error(f'Primary mod is missing from --mods-dir: {args.primary}')
    for name, package in packages.items():
        for reference in package['info']['modReferences']:
            dependency, separator, minimum = reference.partition('@')
            if dependency not in packages:
                raise ValueError(f'{name} requires missing mod {reference}')
            if separator:
                required = tuple(map(int, minimum.split('.')))
                actual = tuple(map(int, packages[dependency]['info']['version'].split('.')))
                if actual < required:
                    raise ValueError(f'{name} requires {reference}, supplied {actual}')
    inputs = out / 'input'
    extracted = out / 'mod-inputs'
    logs = out / 'logs'
    for directory in (inputs, extracted, logs):
        directory.mkdir(parents=True, exist_ok=False)
    modules = {}
    libraries = []
    for name, package in packages.items():
        staged = inputs / (name + '.tmod')
        shutil.copy2(package['source'], staged)
        package['staged'] = staged
        members = [name + '.dll']
        for library in package['info']['dllReferences']:
            if Path(library).name != library or not library.replace('.', '_').isidentifier():
                raise ValueError(f'Invalid bundled library name: {library}')
            member = 'lib/' + library + '.dll'
            members.append(member)
            libraries.append({'mod': name, 'member': member})
        for member in members:
            filename = Path(member).name
            if filename.casefold() in {existing.casefold() for existing in modules}:
                raise ValueError(f'Duplicate mod assembly: {filename}')
            data, member_info = extract_tmod_member(staged, member)
            path = extracted / filename
            path.write_bytes(data)
            modules[filename] = {'owner': name, 'member': member, 'path': path,
                                 'original_sha256': member_info['member_sha256']}
    environment = dotnet_env(dotnet, args.tools.resolve())
    commands = []

    def run(argv, label):
        argv = list(map(str, argv))
        commands.append({'label': label, 'argv': argv})
        with (logs / (label + '.log')).open('w') as stream:
            result = subprocess.run(argv, cwd=out, env=environment,
                                    stdout=stream, stderr=subprocess.STDOUT)
        if result.returncode:
            raise RuntimeError(f'{label} failed ({result.returncode}); see {logs / (label + ".log")}')
        print('PASS ' + label, flush=True)

    projects = {'scan': 'hook_scan', 'tune': 'mod_tuning', 'il': 'il_lowering', 'on': 'offline_hooks'}
    tools = {name: build_tool(project, dotnet=dotnet, cecil=args.cecil.resolve(), tools=args.tools.resolve(),
                              release=release)
             for name, project in projects.items()}
    tool_digests = {project: tool_digest(project) for project in projects.values()}

    def scan(label):
        inventory_path = out / (label + '.json')
        run([dotnet, tools['scan'], hooks, game,
             *[entry['path'] for entry in modules.values()], '-o', inventory_path], label)
        inventory = json.loads(inventory_path.read_text())
        if inventory['unsupported']:
            raise ValueError(f'Unsupported hooks: see {inventory_path}')
        for mod in inventory['mods']:
            entry = modules.get(Path(mod['dll']).name)
            if entry is None or sha(entry['path']) != mod['dll_sha256']:
                raise ValueError('Scanner assembly binding differs from extracted package')
            meta = packages[entry['owner']]['info']
            # Package metadata is authoritative; never use a build.txt from a source checkout.
            if mod['name'] == entry['owner']:
                mod['version'] = meta['version']
                mod['mod_references'] = [reference.split('@', 1)[0] for reference in meta['modReferences']]
                mod['dll_references'] = meta['dllReferences']
            else:
                mod['mod_references'] = []
                mod['dll_references'] = []
            mod['package_owner'] = entry['owner']
        inventory_path.write_text(json.dumps(inventory, indent=2) + '\n')
        return inventory_path

    def lower_il(inventory, directory, label):
        run([dotnet, tools['il'], '--inventory', inventory, '--tml-in', game,
             '--hooks-in', hooks, '--detour-in', detour, '--out-dir', directory,
             '--mods-dir', extracted, '--ref-dir', release], label)

    def repack(filename, data):
        entry = modules[filename]
        package = packages[entry['owner']]
        temporary = inputs / (entry['owner'] + '.repack.tmod')
        replace_tmod_member(package['staged'], entry['member'], data, temporary)
        temporary.replace(package['staged'])
        entry['path'].write_bytes(data)
        exact, _ = extract_tmod_member(package['staged'], entry['member'])
        if exact != data:
            raise ValueError('Repacked mod assembly differs from lowered input: ' + filename)

    # Tuning changes mod bytes and MVIDs, so it runs once before the first scan.
    tuned_dir = out / 'tuned'
    tuned_dir.mkdir()
    tuned = []
    for filename, entry in modules.items():
        run([dotnet, tools['tune'], entry['path'], tuned_dir / filename, '--ref-dir', extracted,
             '--ref-dir', stock_romfs, '--ref-dir', flat_libs], 'tune-' + filename.removesuffix('.dll'))
        data = (tuned_dir / filename).read_bytes()
        if data != entry['path'].read_bytes():
            repack(filename, data)
            tuned.append(filename)

    # Mod-side lowering (UnsafeAccessor bodies, runtime-detour adapters) changes mod MVIDs, and
    # baked IL-hook identities bind those MVIDs. Iterate scan -> IL -> detour adapters until a
    # whole round changes no mod assembly; that final round's outputs are the ones baked.
    modified = set(tuned)
    for round_number in range(1, 5):
        inventory = scan(f'inventory-{round_number}')
        il_output = out / f'il-{round_number}'
        lower_il(inventory, il_output, f'il-{round_number}')
        changed = []
        for filename in modules:
            lowered = il_output / filename
            if lowered.is_file() and sha(lowered) != sha(modules[filename]['path']):
                repack(filename, lowered.read_bytes())
                changed.append(filename)
        before = {filename: sha(entry['path']) for filename, entry in modules.items()}
        run([dotnet, tools['on'], '--lower-mod-detours', inventory,
             '--hooks-in', il_output / 'TerrariaHooks.dll', '--tml-in', il_output / 'tModLoader.dll',
             '--detour-in', il_output / 'MonoMod.RuntimeDetour.dll', '--mods-dir', extracted,
             '--ref-dir', stock_romfs, '--ref-dir', flat_libs], f'detours-{round_number}')
        for filename, entry in modules.items():
            if sha(entry['path']) != before[filename]:
                repack(filename, entry['path'].read_bytes())
                changed.append(filename)
        modified.update(changed)
        if not changed:
            break
    else:
        raise ValueError('Mod lowering did not reach a fixed point in 4 rounds')
    modified = sorted(modified)
    combined = out / 'combined'
    run([dotnet, tools['on'], il_output / 'TerrariaHooks.dll', combined / 'TerrariaHooks.dll',
         il_output / 'tModLoader.dll', combined / 'tModLoader.dll', '--inventory', inventory,
         '--detour-in', il_output / 'MonoMod.RuntimeDetour.dll', '--ref-dir', release, '--ref-dir', flat_libs],
        'lower-on-hooks')
    for filename, entry in modules.items():
        exact, _ = extract_tmod_member(packages[entry['owner']]['staged'], entry['member'])
        if exact != entry['path'].read_bytes():
            raise ValueError('Final mod/package bytes drifted before AOT: ' + filename)
        shutil.copy2(entry['path'], inputs / filename)
    config.update(mod=args.primary, additional_mods=[name for name in packages if name != args.primary],
                  mod_libraries=libraries, label=args.label)
    for key in ('extra_aot', 'llvm_modules', 'compile_fna'):
        config.pop(key, None)
    if args.interpret_hooks:
        config['aot_replacements'] = [name for name in config['aot_replacements'] if name != 'TerrariaHooks.dll']
    replacements = config['replacements']
    replacements['MonoMod.RuntimeDetour.dll'] = sorted(
        str(path.relative_to(stock_romfs)) for path in stock_romfs.rglob('MonoMod.RuntimeDetour.dll'))
    if not replacements['MonoMod.RuntimeDetour.dll']:
        raise ValueError('No runtime detour payload copies in the baseline')
    for filename in replacements:
        # The patched System.Reflection.Metadata comes from tmod-runtime; the others are lowered here.
        source = combined / filename if filename != 'System.Reflection.Metadata.dll' else args.tmod_runtime / filename
        shutil.copy2(source, inputs / filename)
    shutil.copy2(combined / 'tModLoader.dll', inputs / 'tModLoader.dll')
    (inputs / 'modset.json').write_text(json.dumps(config, indent=2) + '\n')
    receipt = {'variant': out.name, 'base': str(args.base_dir.resolve()), 'config': str(args.config.resolve()),
               'script_sha256': sha(__file__),
               'interpret_hooks': args.interpret_hooks,
               'game': {'path': str(game), 'sha256': sha(game)},
               'hooks': {'path': str(hooks), 'sha256': sha(hooks)},
               'detour': {'path': str(detour), 'sha256': sha(detour)},
               'inventory': str(inventory), 'tuned_mod_assemblies': tuned, 'materialized_mod_assemblies': modified,
               'packages': {name: {'original': str(package['source']), 'original_sha256': package['source_sha256'],
                                   'staged': str(package['staged']), 'staged_sha256': sha(package['staged'])}
                            for name, package in packages.items()},
               'mod_assemblies': {filename: {'owner': entry['owner'], 'member': entry['member'],
                                             'original_sha256': entry['original_sha256'],
                                             'final_sha256': sha(entry['path'])}
                                  for filename, entry in modules.items()},
               'tools': {name: str(path) for name, path in tools.items()}, 'tool_digests': tool_digests,
               'commands': commands}
    (out / 'preparation.json').write_text(json.dumps(receipt, indent=2) + '\n')
    print(f'PASS prepared {out.name}: {len(packages)} packages, {len(modules)} exact mod/library assemblies', flush=True)


if __name__ == '__main__':
    main()
