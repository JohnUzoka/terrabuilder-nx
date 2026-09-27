"""Build58 artifact verification (host side).

Checks: every native AOT target/fallback sentinel in the linked ELF (no RWX,
read-only method tables) for control52 and candidate58; RomFS payload differs
from build52 ONLY by (a) the 166 Debug->Release framework swaps at root and
(b) the new romfs:/mono/{lib,framework}_net9.0 embedded BCL; game/staged files,
Content, icon and non-title NACP bytes unchanged; embedded BCL bytes equal the
Release build outputs.
"""
import hashlib, importlib.util, json, os, struct
from pathlib import Path

ROOT = Path(os.environ.get('TERRABUILDER_CACHE', Path.home() / '.cache/terraria-switch-build'))
OUT = ROOT / 'release58' / os.environ.get('R58_VARIANT', '')
BASE = ROOT / 'hint52/aot-final'
CONTROL = ROOT / 'hint52/native/candidate'
AOT = OUT / 'aot-final'
NATIVE = OUT / 'native'
REL_CORE = ROOT / 'runtime-source/artifacts/bin/mono/libnx.arm64.Release/System.Private.CoreLib.dll'
REL_FW = ROOT / 'runtime-source/artifacts/bin/runtime/net9.0-libnx-Release-arm64'
DBG_FW = ROOT / 'recovery46/sdk-pristine/dotnet_runtime/artifacts/bin/runtime/net9.0-libnx-Debug-arm64'
spec = importlib.util.spec_from_file_location('verify_pair', ROOT / 'ab46/verify_pair.py')
verify = importlib.util.module_from_spec(spec); spec.loader.exec_module(verify)


def assets(path):
    with path.open('rb') as s:
        header = s.read(128); image_size = struct.unpack_from('<I', header, 24)[0]
        s.seek(image_size); a = s.read(56); assert a[:4] == b'ASET'
        io, isz, no, nsz = struct.unpack_from('<4Q', a, 8)
        s.seek(image_size + io); icon = s.read(isz)
        s.seek(image_size + no); nacp = s.read(nsz)
    return dict(icon=icon, nacp=nacp)


base_manifest = json.loads((BASE / 'build-manifest.json').read_text())
cand_manifest = json.loads((AOT / 'build-manifest.json').read_text())
assert base_manifest['status'] == cand_manifest['status'] == 'complete'
assert json.loads((AOT / 'runtime-metadata-manifest.json').read_text())['status'] == 'complete'
assert json.loads((NATIVE / 'native-build.json').read_text())['controlAllocatedSectionsIdentical']
control_nro, cand_nro = CONTROL / 'mono_nx_fna.nro', NATIVE / 'candidate/mono_nx_fna.nro'
assert verify.sha(control_nro) == '6a34580f9c2f09c030f9687ed4d9f572388b226d539b2a53e8088871bb8b84de'

has_llvm = any(m.get('llvm_object') for m in cand_manifest['modules'])
if has_llvm:
    sys_path_entry = str(ROOT / 'release58')
    import sys; sys.path.insert(0, sys_path_entry)
    from verify_native_llvm import verify_native_llvm as candidate_verifier
else:
    candidate_verifier = verify.verify_native
native = {'control': verify.verify_native(CONTROL / 'mono_nx_fna.elf', BASE, base_manifest),
          'candidate': candidate_verifier(NATIVE / 'candidate/mono_nx_fna.elf', AOT, cand_manifest)}
for name, rec in native.items():
    (OUT / f'{name}-native-verification.json').write_text(json.dumps(rec, indent=2) + '\n')
    print(name, rec['total_methods_verified'], 'native targets,', rec['total_fallbacks_verified'], 'fallback sentinels; rwx', rec['has_rwx_segment'], flush=True)
assert not native['candidate']['has_rwx_segment']

payload = {'control': verify.inspect_romfs(control_nro), 'candidate': verify.inspect_romfs(cand_nro)}
for name, rec in payload.items():
    (OUT / f'{name}-payload-verification.json').write_text(json.dumps(rec, indent=2) + '\n')
    print(name, rec['embedded_files'], 'embedded files;', rec['sha256'], flush=True)
old, new = payload['control']['files'], payload['candidate']['files']
dbg_names = {p.name for p in DBG_FW.glob('*.dll')}
changed = sorted(n for n in old if n in new and old[n] != new[n])
removed = sorted(set(old) - set(new))
added = sorted(set(new) - set(old))
assert not removed, removed
assert set(changed) <= dbg_names and len(changed) == 166, (len(changed), [c for c in changed if c not in dbg_names])
for n in changed:
    assert new[n]['sha256'] == verify.sha(REL_FW / n), n
expected_added = {'mono/lib_net9.0/System.Private.CoreLib.dll'}
assert set(added) == expected_added, sorted(set(added) ^ expected_added)[:10]
assert new['mono/lib_net9.0/System.Private.CoreLib.dll']['sha256'] == verify.sha(REL_CORE)
# No same-name assembly may exist under a searched directory ahead of "/" with different bytes.
for n in new:
    if n.startswith('mono/') and n != 'mono/lib_net9.0/System.Private.CoreLib.dll':
        raise AssertionError('unexpected shadowing candidate: ' + n)
unchanged = [n for n in old if old[n] == new.get(n)]
for key in ('Terraria.exe', 'ReLogic.dll', 'FNA.dll', 'mscorlib.dll', 'System.Drawing.dll', 'NxCrypto.dll', 'System.IO.Packaging.dll'):
    assert key in unchanged, key
assert sum(1 for n in unchanged if n.startswith('Content/')) == sum(1 for n in old if n.startswith('Content/'))

oa, na = assets(control_nro), assets(cand_nro)
assert oa['icon'] == na['icon'] and len(oa['nacp']) == len(na['nacp']) == 0x4000
allowed = {i for lang in range(16) for i in range(lang * 0x300, lang * 0x300 + 0x200)}
assert all(a == b or i in allowed for i, (a, b) in enumerate(zip(oa['nacp'], na['nacp'])))
assert na['nacp'][:0x200].split(b'\0', 1)[0] == os.environ.get('R58_TITLE', 'Terraria 58 Release BCL').encode()

result = dict(passed=True, hardwarePending=True, performanceAdopted=False, activePerformanceBaseline=52,
              change='Release CoreLib/framework + MONO_NX_EMBEDDED_BCL launcher; game/native runtime unchanged',
              changedRootFrameworkFiles=len(changed), addedEmbeddedBclFiles=len(added), unchangedFiles=len(unchanged),
              native={k: {kk: v[kk] for kk in ('total_methods_verified', 'total_fallbacks_verified', 'has_rwx_segment')} for k, v in native.items()},
              candidateNroSha256=payload['candidate']['sha256'], candidateNroBytes=payload['candidate']['size'])
(OUT / 'artifact-verification.json').write_text(json.dumps(result, indent=2) + '\n')
print('PASS RELEASE58 artifact:', json.dumps({k: result[k] for k in ('changedRootFrameworkFiles', 'addedEmbeddedBclFiles', 'unchangedFiles', 'native')}), flush=True)
