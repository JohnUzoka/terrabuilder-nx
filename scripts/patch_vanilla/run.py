#!/usr/bin/env python3
"""Patch clean GOG Terraria 1.4.5.8 managed assemblies for the Switch build.

Builds NxCrypto, NxInputDiag and the Cecil patcher with the toolchain's .NET SDK
(no network), then writes the patched assemblies to --out under --workdir.
"""
import argparse, hashlib, shutil, subprocess, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
IMAGE = 'localhost/monobuild:local'
CECIL = '/toolchain/cecil/Mono.Cecil.dll'
DOTNET = '/toolchain/dotnet/dotnet'

def run(cmd, **kw):
    print('+', ' '.join(map(str, cmd)))
    subprocess.run(cmd, check=True, **kw)

def pod(script, workdir, toolchain, mounts, network=False):
    cmd=['podman','run','--rm','--userns=keep-id','--entrypoint','sh',
         '-e','HOME=/build/patch-vanilla-work/home',
         '-e','DOTNET_CLI_HOME=/build/patch-vanilla-work/home',
         '-e','NUGET_PACKAGES=/build/nuget-packages',
         '-e','DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1',
         '-v',f'{ROOT}:/work:ro','-v',f'{workdir}:/build','-v',f'{toolchain}:/toolchain:ro', *mounts]
    if not network: cmd.insert(3,'--network=none')
    cmd += [IMAGE,'-lc',script]
    run(cmd)

def sha(p):
    h=hashlib.sha256();
    with open(p,'rb') as f:
        for b in iter(lambda:f.read(1024*1024), b''): h.update(b)
    return h.hexdigest()

def main():
    ap=argparse.ArgumentParser(description=__doc__)
    ap.add_argument('game_dir')
    ap.add_argument('--out', required=True, help='output directory under --workdir')
    ap.add_argument('--workdir', required=True, help='work directory, mounted as /build')
    ap.add_argument('--toolchain', required=True, help='toolchain directory, mounted as /toolchain')
    args=ap.parse_args()
    workdir=Path(args.workdir).resolve(); toolchain=Path(args.toolchain).resolve()
    game=Path(args.game_dir).resolve(); out=Path(args.out).resolve(); build=workdir/'patch-vanilla-work'/'build'
    if not out.is_relative_to(workdir): sys.exit(f'--out must be under {workdir}')
    if not game.exists(): sys.exit(f'missing game dir: {game}')
    build.mkdir(parents=True, exist_ok=True); out.parent.mkdir(parents=True, exist_ok=True)
    # Keep final output but refresh build intermediates.
    if build.exists(): shutil.rmtree(build)
    build.mkdir(parents=True)
    mounts=['-v', f'{game}:/game:ro']
    fna_container = '/game/FNA.dll'
    # No source-control queries: SourceLink would otherwise stamp the repository's
    # HEAD commit into NxCrypto/NxInputDiag, so every commit would change the RomFS.
    common='-p:Deterministic=true -p:ContinuousIntegrationBuild=true -p:PathMap=/work=/_/repo -p:UseSharedCompilation=false -p:EnableSourceControlManagerQueries=false'
    pod(f'{DOTNET} build /work/managed/nx_crypto/NxCrypto.csproj -c Release -o /build/patch-vanilla-work/build/nxcrypto -p:BaseIntermediateOutputPath=/build/patch-vanilla-work/obj/nxcrypto/ {common} >/dev/null && {DOTNET} build /work/managed/nx_input_diag/NxInputDiag.csproj -c Release -o /build/patch-vanilla-work/build/nxinput -p:BaseIntermediateOutputPath=/build/patch-vanilla-work/obj/nxinput/ -p:FNAPath="{fna_container}" {common} >/dev/null && {DOTNET} build /work/scripts/patch_vanilla/PatchVanilla.csproj -c Release -o /build/patch-vanilla-work/build/tool -p:BaseIntermediateOutputPath=/build/patch-vanilla-work/obj/tool/ -p:CecilPath={CECIL} {common} >/dev/null', workdir, toolchain, mounts)
    if out.exists(): shutil.rmtree(out)
    out.mkdir(parents=True)
    out_container = '/build/' + str(out.relative_to(workdir))
    pod(f'{DOTNET} /build/patch-vanilla-work/build/tool/PatchVanilla.dll "/game" "{out_container}" /build/patch-vanilla-work/build/nxcrypto/NxCrypto.dll /build/patch-vanilla-work/build/nxinput/NxInputDiag.dll', workdir, toolchain, mounts)
    print('Output: '+str(out))
    for name in ['Terraria.exe','ReLogic.dll','FNA.dll','NxCrypto.dll','NxInputDiag.dll']:
        print(name, sha(out/name))
if __name__=='__main__': main()
