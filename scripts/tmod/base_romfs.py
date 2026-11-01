"""Stage the tModLoader base inputs that come from the user's machine: flat_libs and the stock RomFS.

stage_flat_libs ports the cache-only clean_flat_libs.sh: every assembly under the release's
Libraries/ flattened into one folder, first match wins in the script's glob order (the script's
`cp -u` only copies when the destination is missing, because each copy is newer than the zip's
files), `ref/` assemblies skipped, then runtimes/linux/lib/*/ implementations overwrite.

stage_romfs builds the stock RomFS that every tModLoader NRO packages (the layout of the cache's
tmod/tmod09/romfs):

    <root>/*                         flat_libs assemblies (release Libraries/, flattened)
    <root>/*                         the runtime framework dir (overrides 4 flat_libs names:
                                     System.Collections.Immutable, System.IO.Pipelines,
                                     System.Text.Encodings.Web, System.Text.Json)
    <root>/tModLoader.deps.json      release
    <root>/{tModLoader,FNA,NxCrypto,TerrariaHooks}.dll   `dlls` (console-patched builds)
    Libraries/**                     release Libraries/, verbatim
    mono/lib_net9.0/System.Private.CoreLib.dll           `corelib`
    Content/**                       the user's Content/ (wrong-case vanilla assets renamed the
                                     way tModLoader's TMLContentManager.TryFixFileCasings does
                                     on desktop, since the Switch RomFS is read-only), then the
                                     release's Content/ overlay; *Zone.Identifier skipped.
"""
import hashlib
import re
import shutil
from pathlib import Path

# TMLContentManager.TryFixFileCasings (tModLoader v2026.07.3.0,
# patches/tModLoader/Terraria/ModLoader/Engine/TMLContentManager.cs).
PROBLEMATIC_ASSETS = (
    'Images/NPC_517.xnb',
    'Images/Gore_240.xnb',
    'Images/Projectile_179.xnb',
    'Images/Projectile_189.xnb',
    'Images/Projectile_618.xnb',
    'Images/Tiles_650.xnb',
    'Images/Item_2648.xnb',
)
CONSOLE_DLLS = ('tModLoader.dll', 'FNA.dll', 'NxCrypto.dll', 'TerrariaHooks.dll')
CORELIB = 'System.Private.CoreLib.dll'
CORELIB_DEST = 'mono/lib_net9.0/' + CORELIB


def _sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def _files(root, skip_zone=False):
    """Regular files under root as {posix relative path: path}, sorted."""
    root = Path(root)
    found = {}
    for path in root.rglob('*'):
        if path.is_file() and not (skip_zone and path.name.endswith('Zone.Identifier')):
            found[path.relative_to(root).as_posix()] = path
    return dict(sorted(found.items()))


def _tree_hash(files):
    """sha256 over sha256sum-style lines ('<sha256>  <relative path>\\n'), sorted by path."""
    digest = hashlib.sha256()
    for rel, path in sorted(files.items()):
        digest.update(f'{_sha(path)}  {rel}\n'.encode())
    return digest.hexdigest()


def _fresh(out):
    out = Path(out)
    if out.exists() and any(out.iterdir()):
        raise FileExistsError(f'{out} must be absent or empty')
    out.mkdir(parents=True, exist_ok=True)
    return out


def _copy(source, target):
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, target)


def flat_lib_sources(release):
    """{file name: source path} that clean_flat_libs.sh puts in flat_libs."""
    libraries = Path(release) / 'Libraries'
    chosen = {}
    # Bash expands each glob sorted (C locale); `cp -u` then keeps the first copy of each name.
    for pattern in ('*/*/*/*/*.dll', '*/*/*/*.dll', '*/*/*.dll'):
        for path in sorted(libraries.glob(pattern), key=lambda p: p.as_posix().encode()):
            if '/ref/' in path.as_posix() or not path.is_file():
                continue
            chosen.setdefault(path.name, path)
    # find -path "*/runtimes/linux/lib/*/*.dll": `*` also matches '/', so any depth below lib/.
    for path in sorted(libraries.rglob('*.dll'), key=lambda p: p.as_posix().encode()):
        parts = path.relative_to(libraries).parts
        for index in range(len(parts) - 3):
            if parts[index:index + 3] == ('runtimes', 'linux', 'lib') and path.is_file():
                chosen[path.name] = path
                break
    return dict(sorted(chosen.items()))


def stage_flat_libs(release: Path, out: Path, fna: Path | None = None) -> None:
    """Write clean_flat_libs.sh's output for `release` into `out`.

    `fna` optionally replaces FNA.dll afterwards: the cache's tmod/flat_libs/FNA.dll was
    overwritten after the script ran with the romfs:/-patched FNA (tmod02/input/FNA.dll)."""
    out = _fresh(out)
    for name, source in flat_lib_sources(release).items():
        _copy(source, out / name)
    if fna is not None:
        _copy(fna, out / 'FNA.dll')


def normalize_vanilla_case(files):
    """Apply TryFixFileCasings to a {relative path: path} map of vanilla Content.

    Returns (files with wrong-case names renamed, renames, collisions). For a missing expected
    name, tModLoader renames the first entry of Images/ whose name equals it ignoring case; when
    several exist that order is filesystem-dependent, so the first in sorted order is taken and
    all candidates are reported as a collision."""
    files = dict(files)
    renames, collisions = [], []
    for expected in PROBLEMATIC_ASSETS:
        if expected in files:
            continue
        directory, name = expected.rsplit('/', 1)
        candidates = sorted(rel for rel in files
                            if rel.rsplit('/', 1)[0] == directory
                            and rel.rsplit('/', 1)[-1].casefold() == name.casefold())
        if not candidates:
            continue
        if len(candidates) > 1:
            collisions.append({'expected': expected, 'candidates': candidates})
        files[expected] = files.pop(candidates[0])
        renames.append({'from': candidates[0], 'to': expected})
    return dict(sorted(files.items())), renames, collisions


def content_files(content):
    """The Content/ files stage_romfs reads, as {relative path: path}."""
    return _files(content, skip_zone=True)


def content_tree_hash(content):
    """The `tree_sha256` stage_romfs records for `content`."""
    return _tree_hash(content_files(content))


def content_version(content):
    """The Terraria version in changelog.txt next to `content`, or None."""
    changelog = Path(content).parent / 'changelog.txt'
    if changelog.is_file():
        with changelog.open(encoding='utf-8', errors='replace') as stream:
            match = re.match(r'\s*Version\s+(\S+)', stream.readline())
        if match:
            return match.group(1)
    return None


def stage_romfs(release: Path, content: Path, framework: Path, corelib: Path, dlls: dict, out: Path) -> dict:
    """Build the stock tModLoader RomFS into `out` (absent or empty). `dlls` maps each of
    CONSOLE_DLLS to the file to ship under that name.

    Returns the base-manifest fields build_tmod_modset_nro.py reads from the base: `content`, and
    the RomFS side of `provenance_and_binding` (tModLoader, FNA and the CoreLib payloads, paths
    relative to `out`). The AOT objects for those bindings and `runtime_provenance` describe the
    runtime and AOT outputs, not the RomFS; the caller adds them."""
    release, content, framework, corelib = map(Path, (release, content, framework, corelib))
    dlls = {name: Path(dlls[name]) for name in CONSOLE_DLLS}
    for path in dlls.values():
        if not path.is_file():
            raise FileNotFoundError(path)
    if not (corelib / CORELIB).is_file():
        raise FileNotFoundError(corelib / CORELIB)
    out = _fresh(out)

    root = dict(flat_lib_sources(release))
    root.update((path.name, path) for path in sorted(framework.iterdir()) if path.is_file())
    root['tModLoader.deps.json'] = release / 'tModLoader.deps.json'
    root.update(dlls)
    staged = dict(root)
    staged.update(('Libraries/' + rel, path) for rel, path in _files(release / 'Libraries').items())
    staged[CORELIB_DEST] = corelib / CORELIB

    vanilla_source = content_files(content)
    vanilla, renames, collisions = normalize_vanilla_case(vanilla_source)
    overlay = _files(release / 'Content', skip_zone=True)
    for rel, path in vanilla.items():
        staged['Content/' + rel] = path
    for rel, path in overlay.items():
        staged['Content/' + rel] = path

    folded = {}
    for rel in staged:
        folded.setdefault(rel.casefold(), []).append(rel)
    case_collisions = sorted(names for names in folded.values() if len(names) > 1)

    for rel, source in sorted(staged.items()):
        _copy(source, out / rel)

    def payload(rel):
        return {'romfs_payload_dll': rel, 'romfs_payload_sha256': _sha(out / rel)}

    return {
        'provenance_and_binding': {
            'tModLoader': payload('tModLoader.dll'),
            'FNA': payload('FNA.dll'),
            'System.Private.CoreLib': payload(CORELIB_DEST),
        },
        'content': {
            'vanilla': f'Terraria {content_version(content) or "unknown version"} Content',
            'vanilla_files': len(vanilla_source),
            'overlay_files': len(overlay),
            'source': str(content),
            'version': content_version(content),
            'tree_sha256': _tree_hash(vanilla_source),
            'case_renames': renames,
            'case_rename_collisions': collisions,
            'romfs_case_collisions': case_collisions,
            'romfs_files': len(staged),
        },
    }
