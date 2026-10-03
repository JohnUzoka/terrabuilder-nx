"""Build tmod03: tmod02's verified ELF (same launcher, assemblies, AOT) repackaged with
Terraria 1.4.4.9 Content (the version tModLoader 1.4.4 targets) instead of 1.4.5.8.

RomFS = tmod02 RomFS minus Content/ + the user's GOG 1.4.4.9 Content/ + tModLoader's own
Content/ overlay (the same overlay order tModLoader uses on desktop). Only the NRO changes.
Run inside localhost/monobuild:local with /build = ~/.cache/terraria-switch-build and the
1.4.4.9 game mounted at /terraria144 (read-only).
"""
import hashlib, json, shutil, struct, subprocess
from pathlib import Path

ROOT = Path('/build/tmod')
SRC = ROOT / 'tmod02'
OUT = ROOT / 'tmod03'
VANILLA = Path('/terraria144/Content')
OVERLAY = ROOT / 'release/Content'


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def content_files(root):
    return {p.relative_to(root): p for p in root.rglob('*') if p.is_file() and not p.name.endswith('Zone.Identifier')}


assert not OUT.exists(), 'fresh tmod03 directory required'
manifest02 = json.loads((SRC / 'manifest.json').read_text())
elf = SRC / 'native/candidate/tmodloader.elf'
assert sha(elf) == manifest02['final_deliverables']['candidate_elf']['sha256'], 'tmod02 ELF changed'
assert (VANILLA.parent / 'Terraria.exe').is_file()

romfs = OUT / 'romfs'
shutil.copytree(SRC / 'romfs', romfs, ignore=lambda d, names: ['Content'] if Path(d) == SRC / 'romfs' else [])
vanilla, overlay = content_files(VANILLA), content_files(OVERLAY)
for rel, path in vanilla.items():
    (romfs / 'Content' / rel).parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(path, romfs / 'Content' / rel)
for rel, path in overlay.items():
    (romfs / 'Content' / rel).parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(path, romfs / 'Content' / rel)
print(f'PASS stage content: {len(vanilla)} vanilla 1.4.4.9 + {len(overlay)} tModLoader overlay', flush=True)

native = OUT / 'native/candidate'
native.mkdir(parents=True)
shutil.copy2(elf, native / 'tmodloader.elf')
nacp = SRC / 'native/candidate/tmodloader.nacp'
data = bytearray(nacp.read_bytes())
title = b'tModLoader NX 1.4.4.9 (Zero-Mod)'
for language in range(16):
    start = language * 0x300
    data[start:start + 0x200] = title.ljust(0x200, b'\0')
(native / 'tmodloader.nacp').write_bytes(data)
subprocess.run(['/opt/devkitpro/tools/bin/elf2nro', native / 'tmodloader.elf', native / 'tmodloader.nro',
                '--nacp=' + str(native / 'tmodloader.nacp'), '--romfsdir=' + str(romfs),
                '--icon=' + str(SRC / 'native/icon.jpg')], check=True, capture_output=True)
nro = native / 'tmodloader.nro'
manifest = dict(manifest02, candidate='tmod03', content={'vanilla': 'GOG Terraria 1.4.4.9', 'vanilla_files': len(vanilla), 'overlay_files': len(overlay)})
manifest['final_deliverables'] = {
    'candidate_nro': {'path': str(nro), 'bytes': nro.stat().st_size, 'sha256': sha(nro)},
    'candidate_elf': {'path': str(native / 'tmodloader.elf'), 'sha256': sha(native / 'tmodloader.elf')}}
for binding in manifest['provenance_and_binding'].values():
    if 'romfs_payload_dll' in binding:
        binding['romfs_payload_dll'] = binding['romfs_payload_dll'].replace('/tmod02/', '/tmod03/')
(OUT / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
print('PASS package tmod03:', manifest['final_deliverables']['candidate_nro'], flush=True)
