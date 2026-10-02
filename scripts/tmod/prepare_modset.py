"""Stage exact .tmod packages, lower their hooks, and prepare build_tmod_modset_nro.py inputs.

Run on the host with --cache-root or inside monobuild with --cache-root=/build.
The output variant must be fresh. Materialized mod methods are repacked and rescanned
before final hook identities are baked; no assembly is changed after AOT compilation.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess

from tmod_file import extract_tmod_member, read_tmod_info, replace_tmod_member


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--cache-root', type=Path, required=True)
    parser.add_argument('--mods-dir', type=Path, required=True)
    parser.add_argument('--variant', required=True)
    parser.add_argument('--primary', required=True)
    parser.add_argument('--label', required=True)
    parser.add_argument('--base', default='tmod13c')
    parser.add_argument('--game', type=Path, required=True,
                        help='NX/save-patched tModLoader.dll before any offline hook lowering')
    parser.add_argument('--interpret-hooks', action='store_true',
                        help='leave TerrariaHooks.dll interpreted (not AOT). Its ~35 MiB of generated '
                             'accessor/delegate code otherwise pushes large mod sets past the ±128 MiB '
                             'CALL26 reach of the AOT method tables; hardware performance is unmeasured.')
    args = parser.parse_args()
    root = args.cache_root.resolve()
    game = args.game.resolve()
    for variant in (args.variant, args.base):
        if Path(variant).name != variant or not variant.startswith('tmod'):
            parser.error('Expected a tmod variant name, not a path')
    out = root / 'tmod' / args.variant
    if out.exists():
        parser.error(f'Fresh output variant required: {out}')
    baseline = root / 'tmod' / args.base
    stock_romfs = root / 'tmod/tmod09/romfs'
    hooks = stock_romfs / 'TerrariaHooks.dll'
    detour = root / 'tmod/release/Libraries/monomod.runtimedetour/25.3.2/lib/net8.0/MonoMod.RuntimeDetour.dll'
    dotnet = root / 'runtime-source/.dotnet/dotnet'
    tool_root = Path(__file__).resolve().parent
    config = json.loads((baseline / 'input/modset.json').read_text())
    packages = {}
    for path in sorted(args.mods_dir.resolve().glob('*.tmod')):
        info = read_tmod_info(path)
        name = info['name']
        if not name.isidentifier() or info['loader_version'] != '2026.7.3.0':
            raise ValueError(f'Unsupported package identity: {name} ({info["loader_version"]})')
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
    environment = dict(os.environ, DOTNET_ROOT=str(dotnet.parent), TERRARIA_BUILD_CACHE=str(root),
                       DOTNET_CLI_HOME=str(out / 'dotnet-home'),
                       DOTNET_SYSTEM_GLOBALIZATION_INVARIANT='1',
                       DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1')
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

    projects = {'scan': ('hook_scan', 'HookScan.csproj', 'HookScan.dll'),
                'il': ('il_lowering', 'IlLowering.csproj', 'IlLowering.dll'),
                'on': ('offline_hooks', 'Patcher.csproj', 'Patcher.dll')}
    tools = {}
    source_hashes = {}
    for name, (directory, project, assembly) in projects.items():
        source = out / 'tools' / name / 'source'
        shutil.copytree(tool_root / directory, source,
                        ignore=shutil.ignore_patterns('bin', 'obj', '__pycache__'))
        for path in sorted(source.rglob('*')):
            if path.is_file():
                source_hashes[str(path.relative_to(out))] = sha(path)
        cecil = (root / 'tmod/release/Libraries/mono.cecil/0.11.6/lib/netstandard2.0/Mono.Cecil.dll'
                 if name == 'scan' else root / 'release58/ildump/out/Mono.Cecil.dll')
        output = out / 'tools' / name / 'bin'
        run([dotnet, 'build', source / project, '-c', 'Release', '-o', output, '--nologo', '-v', 'q',
             '-p:CacheRoot=' + str(root), '-p:CecilPath=' + str(cecil)], 'build-' + name)
        tools[name] = output / assembly

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
             '--mods-dir', extracted], label)

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

    # Mod-side lowering (UnsafeAccessor bodies, runtime-detour adapters) changes mod MVIDs, and
    # baked IL-hook identities bind those MVIDs. Iterate scan -> IL -> detour adapters until a
    # whole round changes no mod assembly; that final round's outputs are the ones baked.
    modified = set()
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
             '--ref-dir', stock_romfs, '--ref-dir', root / 'tmod/flat_libs'], f'detours-{round_number}')
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
         '--detour-in', il_output / 'MonoMod.RuntimeDetour.dll'], 'lower-on-hooks')
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
        source = combined / filename
        if not source.exists():
            source = baseline / 'input' / filename
        shutil.copy2(source, inputs / filename)
    shutil.copy2(combined / 'tModLoader.dll', inputs / 'tModLoader.dll')
    (inputs / 'modset.json').write_text(json.dumps(config, indent=2) + '\n')
    receipt = {'variant': args.variant, 'base': args.base, 'script_sha256': sha(__file__),
               'interpret_hooks': args.interpret_hooks,
               'game': {'path': str(game), 'sha256': sha(game)},
               'hooks': {'path': str(hooks), 'sha256': sha(hooks)},
               'detour': {'path': str(detour), 'sha256': sha(detour)},
               'inventory': str(inventory), 'materialized_mod_assemblies': modified,
               'packages': {name: {'original': str(package['source']), 'original_sha256': package['source_sha256'],
                                   'staged': str(package['staged']), 'staged_sha256': sha(package['staged'])}
                            for name, package in packages.items()},
               'mod_assemblies': {filename: {'owner': entry['owner'], 'member': entry['member'],
                                             'original_sha256': entry['original_sha256'],
                                             'final_sha256': sha(entry['path'])}
                                  for filename, entry in modules.items()},
               'tool_sources': source_hashes, 'commands': commands}
    (out / 'preparation.json').write_text(json.dumps(receipt, indent=2) + '\n')
    print(f'PASS prepared {args.variant}: {len(packages)} packages, {len(modules)} exact mod/library assemblies', flush=True)
    print(f'Next: TMOD_VARIANT={args.variant} python3 scripts/tmod/build_tmod_modset_nro.py', flush=True)


if __name__ == '__main__':
    main()
