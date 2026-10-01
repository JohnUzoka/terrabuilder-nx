#!/usr/bin/env python3
import argparse, hashlib, json, os, shutil, subprocess, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CACHE = Path.home()/'.cache/terraria-switch-build'
WORK = CACHE/'patch-vanilla-work'
IMAGE = 'localhost/monobuild:local'
CECIL = '/build/runtime-fix/nuget-packages/microsoft.net.illink.tasks/9.0.1/tools/net9.0/Mono.Cecil.dll'
DOTNET = '/build/runtime-source/.dotnet/dotnet'

def run(cmd, **kw):
    print('+', ' '.join(map(str, cmd)))
    subprocess.run(cmd, check=True, **kw)

def pod(script, network=False):
    cmd=['podman','run','--rm','--userns=keep-id','--entrypoint','sh','-e','HOME=/build/patch-vanilla-work/home','-e','DOTNET_CLI_HOME=/build/patch-vanilla-work/home','-e','NUGET_PACKAGES=/build/runtime-fix/nuget-packages','-e','DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1','-v',f'{ROOT}:/work','-v',f'{CACHE}:/build','-v',f'{Path.home()/"GOG Games"}:/gog:ro']
    if not network: cmd.insert(3,'--network=none')
    cmd += [IMAGE,'-lc',script]
    run(cmd)

def sha(p):
    h=hashlib.sha256();
    with open(p,'rb') as f:
        for b in iter(lambda:f.read(1024*1024), b''): h.update(b)
    return h.hexdigest()

def main():
    ap=argparse.ArgumentParser(description='Patch clean GOG Terraria 1.4.5.8 managed assemblies for the Switch build')
    ap.add_argument('game_dir', nargs='?', default=str(Path.home()/'GOG Games/Terraria1_4_5_8/game'))
    ap.add_argument('--out', default=str(WORK/'verified-output'))
    args=ap.parse_args()
    game=Path(args.game_dir).resolve(); out=Path(args.out).resolve(); build=WORK/'build'
    if not game.exists(): sys.exit(f'missing game dir: {game}')
    build.mkdir(parents=True, exist_ok=True); out.parent.mkdir(parents=True, exist_ok=True)
    # Keep final output but refresh build intermediates.
    if build.exists(): shutil.rmtree(build)
    build.mkdir(parents=True)
    fna_container = '/gog/' + str(game.relative_to(Path.home()/'GOG Games')) + '/FNA.dll' if str(game).startswith(str(Path.home()/'GOG Games')) else None
    if fna_container is None: sys.exit('game_dir must be under $HOME/GOG Games so the container can read it')
    pod(f'{DOTNET} build /work/managed/nx_crypto/NxCrypto.csproj -c Release -o /build/patch-vanilla-work/build/nxcrypto >/dev/null && {DOTNET} build /work/managed/nx_input_diag/NxInputDiag.csproj -c Release -o /build/patch-vanilla-work/build/nxinput -p:FNAPath="{fna_container}" >/dev/null && {DOTNET} build /work/scripts/patch_vanilla/PatchVanilla.csproj -c Release -o /build/patch-vanilla-work/build/tool -p:CecilPath={CECIL} >/dev/null')
    if out.exists(): shutil.rmtree(out)
    out.mkdir(parents=True)
    game_container = '/gog/' + str(game.relative_to(Path.home()/'GOG Games'))
    out_container = '/build/' + str(out.relative_to(CACHE))
    pod(f'{DOTNET} /build/patch-vanilla-work/build/tool/PatchVanilla.dll "{game_container}" "{out_container}" /build/patch-vanilla-work/build/nxcrypto/NxCrypto.dll /build/patch-vanilla-work/build/nxinput/NxInputDiag.dll')
    print('Output: '+str(out))
    for name in ['Terraria.exe','ReLogic.dll','FNA.dll','NxCrypto.dll','NxInputDiag.dll']:
        print(name, sha(out/name))
if __name__=='__main__': main()
