#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LEGACY = Path.home() / ".cache/terraria-switch-build"
DEFAULT_WORKDIR = Path(os.environ.get("TERRABUILDER_WORKDIR", Path.home() / ".cache/terrabuilder"))
HEAVY_LOCK = Path.home() / ".cache/terraria-switch-build/.heavy.lock"
MONO_IMAGE = "localhost/monobuild:local"
LLVM_IMAGE = "localhost/monobuild-llvm:local"
MESA_IMAGE = "localhost/mesabuild-r28:local"

V88_WRAPS = [
    "SDL_PollEvent", "FNA3D_SwapBuffers", "SDL_StartTextInput", "SDL_StopTextInput",
    "SDL_SetThreadPriority", "SDL_OpenAudioDevice", "audrenWaitFrame", "SDL_PauseAudioDevice",
    "SDL_CloseAudioDevice", "nwindowDequeueBuffer", "nwindowQueueBuffer", "nwindowSetSwapInterval",
    "nwindowConfigureBuffer",
]
GAME_DLLS = ["Terraria.exe", "FNA.dll", "ReLogic.dll", "Newtonsoft.Json.dll", "NxCrypto.dll", "NxInputDiag.dll"]


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    pending = path.with_name(path.name + ".pending")
    pending.write_text(json.dumps(value, indent=2) + "\n")
    pending.replace(path)


def run(cmd: list[str], *, cwd: Path = ROOT, env: dict[str, str] | None = None, heavy: bool = False) -> None:
    actual = cmd
    if heavy:
        HEAVY_LOCK.parent.mkdir(parents=True, exist_ok=True)
        actual = ["flock", str(HEAVY_LOCK), *cmd]
    print("+", " ".join(map(str, actual)), flush=True)
    subprocess.run(actual, cwd=cwd, env=env, check=True)


def capture(cmd: list[str]) -> str:
    return subprocess.check_output(cmd, text=True, stderr=subprocess.STDOUT).strip()


def engine() -> str | None:
    for name in ("podman", "docker"):
        if shutil.which(name):
            return name
    return None


def rel_to_mount(path: Path, root: Path, mount: str) -> str:
    return mount + "/" + path.absolute().relative_to(root.absolute()).as_posix()


def ensure_compat_layout(workdir: Path) -> None:
    workdir.mkdir(parents=True, exist_ok=True)
    # Compatibility paths consumed by the historical release scripts. Symlinks point
    # at the read-only legacy cache; new variants are created only under workdir.
    links = {
        "runtime-source": LEGACY / "runtime-source",
        "runtime-llvm": LEGACY / "runtime-llvm",
        "runtime-release": LEGACY / "runtime-release",
        "recovery46": LEGACY / "recovery46",
        "nochroma42": LEGACY / "nochroma42",
    }
    for name, target in links.items():
        link = workdir / name
        if not link.exists():
            link.symlink_to(target, target_is_directory=True)
    (workdir / "gfx").mkdir(exist_ok=True)
    gfx = workdir / "gfx" / "v88-lib"
    if not gfx.exists():
        gfx.symlink_to(LEGACY / "gfx" / "v88-lib", target_is_directory=True)
    (workdir / "release58").mkdir(exist_ok=True)
    mono = workdir / "release58" / "mono-nx"
    if not mono.exists():
        mono.symlink_to(LEGACY / "release58" / "mono-nx", target_is_directory=True)
    for src in (ROOT / "scripts" / "release_bcl").glob("*"):
        if src.is_file():
            dst = workdir / "release58" / src.name
            if dst.is_symlink() or not dst.exists() or sha256(dst) != sha256(src):
                if dst.exists() or dst.is_symlink():
                    dst.unlink()
                shutil.copy2(src, dst)


def doctor(args: argparse.Namespace) -> int:
    eng = engine()
    ok = True
    print(f"container engine: {eng or 'missing'}")
    ok &= bool(eng)
    for image in (MONO_IMAGE, LLVM_IMAGE, MESA_IMAGE, "devkitpro/devkita64:20250728"):
        present = False
        if eng:
            try:
                capture([eng, "image", "exists", image])
                present = True
            except subprocess.CalledProcessError:
                try:
                    out = capture([eng, "images", "--format", "{{.Repository}}:{{.Tag}}"])
                    present = image in out.splitlines()
                except Exception:
                    present = False
        print(f"image {image}: {'ok' if present else 'missing'}")
        ok &= present
    usage = shutil.disk_usage(args.workdir.expanduser().parent)
    print(f"disk free: {usage.free // (1024**3)} GiB")
    ok &= usage.free > 15 * 1024**3
    mem_kb = 0
    try:
        for line in Path("/proc/meminfo").read_text().splitlines():
            if line.startswith("MemTotal:"):
                mem_kb = int(line.split()[1])
                break
    except OSError:
        pass
    print(f"RAM: {mem_kb // (1024**2)} GiB")
    for path in required_legacy_paths():
        exists = path.exists()
        print(f"legacy {path.relative_to(LEGACY) if str(path).startswith(str(LEGACY)) else path}: {'ok' if exists else 'missing'}")
        ok &= exists
    return 0 if ok else 1


def required_legacy_paths() -> list[Path]:
    return [
        LEGACY / "runtime-release/libmonosgen-2.0-release.a",
        LEGACY / "runtime-source/artifacts/bin/runtime/net9.0-libnx-Release-arm64",
        LEGACY / "runtime-source/artifacts/bin/mono/libnx.arm64.Release/System.Private.CoreLib.dll",
        LEGACY / "runtime-llvm/artifacts/bin/mono/linux.x64.Debug/cross/linux-x64/libnx-arm64/mono-aot-cross",
        LEGACY / "gfx/v88-lib/libEGL.a",
        LEGACY / "gfx/v88-lib/libglapi.a",
        LEGACY / "recovery46/native-deps/install/lib/libFNA3D.a",
        LEGACY / "release58/v88/aot-final/System.Private.CoreLib.dll.o",
        LEGACY / "release58/v88/aot-final/System.Text.RegularExpressions.dll.o",
        LEGACY / "release58/v88/aot-final/System.Collections.Concurrent.dll.o",
    ]


def file_manifest(paths: list[Path], base: Path) -> list[dict]:
    rows = []
    for p in paths:
        if p.is_file():
            rows.append({"path": p.relative_to(base).as_posix() if p.is_relative_to(base) else str(p), "bytes": p.stat().st_size, "sha256": sha256(p)})
    return rows


def toolchain_pack(workdir: Path) -> Path:
    ensure_compat_layout(workdir)
    tc = workdir / "toolchain"
    tc.mkdir(parents=True, exist_ok=True)
    artifacts = [
        LEGACY / "runtime-release/libmonosgen-2.0-release.a",
        *(LEGACY / "runtime-source/artifacts/bin/mono/libnx.arm64.Release/out/lib").glob("libmono-component-*-static.a"),
        LEGACY / "runtime-llvm/artifacts/bin/mono/linux.x64.Debug/cross/linux-x64/libnx-arm64/mono-aot-cross",
        LEGACY / "runtime-llvm/artifacts/bin/mono/linux.x64.Debug/cross/linux-x64/libnx-arm64/opt",
        LEGACY / "runtime-llvm/artifacts/bin/mono/linux.x64.Debug/cross/linux-x64/libnx-arm64/llc",
        LEGACY / "gfx/v88-lib/libEGL.a", LEGACY / "gfx/v88-lib/libglapi.a",
        LEGACY / "recovery46/native-deps/install/lib/libFNA3D.a",
        LEGACY / "recovery46/native-deps/install/lib/libFAudio.a",
        LEGACY / "recovery46/native-deps/install/lib/libmojoshader.a",
        LEGACY / "release58/v88/aot-final/System.Private.CoreLib.dll.o",
        LEGACY / "release58/v88/aot-final/System.Private.CoreLib.dll-llvm.o",
        LEGACY / "release58/v88/aot-final/System.Text.RegularExpressions.dll.o",
        LEGACY / "release58/v88/aot-final/System.Text.RegularExpressions.dll-llvm.o",
        LEGACY / "release58/v88/aot-final/System.Collections.Concurrent.dll.o",
        LEGACY / "release58/v88/aot-final/System.Collections.Concurrent.dll-llvm.o",
    ]
    for src in artifacts:
        if src.exists():
            dst = tc / "artifacts" / src.relative_to(LEGACY)
            dst.parent.mkdir(parents=True, exist_ok=True)
            if not dst.exists():
                try:
                    os.link(src, dst)
                except OSError:
                    shutil.copy2(src, dst)
    manifest = {
        "schema_version": 1,
        "created_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "legal_model": "Contains open-source/runtime/toolchain artifacts only. Game-derived assemblies, RomFS, icon, and game AOT objects are built locally from the user's install and are not part of this toolchain.",
        "sources": [
            {"name": "dotnet_runtime", "repo": "https://github.com/JohnUzoka/dotnet_runtime", "branch": "terrabuilder-nx", "commit_observed": git_head(LEGACY / "runtime-source")},
            {"name": "dotnet_runtime_llvm", "repo": "https://github.com/JohnUzoka/dotnet_runtime", "branch": "terrabuilder-nx", "commit_observed": git_head(LEGACY / "runtime-llvm")},
            {"name": "mono-nx", "repo": "https://github.com/JohnUzoka/mono-nx", "branch": "fna-support", "commit": "8be547c"},
            {"name": "Mesa", "version": "20.1.0-rc3 + devkitPro switch patches + terrabuilder patches", "script": "scripts/mesa/build_mesa.sh"},
            {"name": "FNA3D/FAudio/MojoShader", "script": "scripts/build_native_deps.sh", "license": "zlib/libpng-style upstream notices; include upstream license files before publishing a bundle asset"},
        ],
        "licenses": "TODO for release asset: copy full upstream notices for dotnet/runtime, mono-nx, Mesa, FNA3D, FAudio, MojoShader, LLVM/Clang, devkitPro/libnx/portlibs.",
        "artifacts": file_manifest(list((tc / "artifacts").rglob("*")), tc),
        "build_scripts": ["scripts/release_bcl/build_release_managed.sh", "scripts/release_bcl/build_runtime_release.sh", "scripts/release_bcl/build_llvm_cross.sh", "scripts/mesa/build_mesa.sh", "scripts/build_native_deps.sh"],
    }
    write_json(tc / "manifest.json", manifest)
    print(tc)
    return tc


def git_head(path: Path) -> str | None:
    try:
        return subprocess.check_output(["git", "-C", str(path), "rev-parse", "HEAD"], text=True, stderr=subprocess.DEVNULL).strip()
    except Exception:
        return None


def toolchain(args: argparse.Namespace) -> int:
    if args.toolcmd == "pack" or not args.toolcmd:
        toolchain_pack(args.workdir.expanduser())
        return 0
    if args.from_source:
        ensure_compat_layout(args.workdir.expanduser())
        # Long path: delegate to existing scripts under a heavy-build lock.
        run(["bash", "-lc", f"cd {ROOT} && scripts/mesa/build_mesa.sh {args.workdir.expanduser()/'gfx'}"], heavy=True)
        print("from-source runtime/BCL/LLVM builds are documented in docs/BUILDING.md and use scripts/release_bcl/*")
        return 0
    if args.bundle:
        src = Path(args.bundle).expanduser().resolve()
        dst = args.workdir.expanduser() / "toolchain"
        if dst.exists():
            shutil.rmtree(dst)
        shutil.copytree(src, dst, symlinks=True)
        ensure_compat_layout(args.workdir.expanduser())
        print(dst)
        return 0
    return 0


def validate_game(game: Path) -> dict:
    required = ["Terraria.exe", "FNA.dll", "Content"]
    missing = [n for n in required if not (game / n).exists()]
    if missing:
        raise SystemExit("game directory missing: " + ", ".join(missing))
    terraria_sha = sha256(game / "Terraria.exe")
    fna_sha = sha256(game / "FNA.dll")
    # Known clean GOG 1.4.5.8 hashes from the patch_vanilla verification report.
    known = {"6bba49a535bc9dc2ade62d6b6f0bc0c755223bed60796af51f5024a3c87c43ce": "1.4.5.8"}
    version = known.get(terraria_sha, "1.4.5.x-unverified")
    if not version.startswith("1.4.5"):
        raise SystemExit(f"unsupported Terraria.exe hash {terraria_sha}; expected clean GOG 1.4.5.x")
    return {"terraria_sha256": terraria_sha, "fna_sha256": fna_sha, "version": version}


def ask(prompt: str, default: str | None = None) -> str:
    suffix = f" [{default}]" if default else ""
    value = input(prompt + suffix + ": ").strip()
    return value or (default or "")


def build(args: argparse.Namespace) -> int:
    target = args.target
    game_dir = Path(args.game_dir).expanduser() if args.game_dir else None
    if not args.yes and (not target or not game_dir):
        target = target or ask("Target (vanilla | tmodloader)", "vanilla")
        game_dir = game_dir or Path(ask("Path to game files"))
    target = target or "vanilla"
    if target == "tmodloader":
        print("tModLoader support coming in this release; not yet wired")
        return 0
    if target != "vanilla":
        raise SystemExit("--target must be vanilla or tmodloader")
    if not game_dir:
        raise SystemExit("--game-dir is required in non-interactive mode")
    game_dir = game_dir.resolve()
    profile = args.profile
    workdir = args.workdir.expanduser().resolve()
    out_root = Path(args.out).expanduser().resolve() if args.out else workdir / "out"
    ensure_compat_layout(workdir)
    tc = toolchain_pack(workdir)
    meta = validate_game(game_dir)
    variant = "cli-" + profile
    vdir = workdir / "release58" / variant
    aot = vdir / "aot-final"
    receipt = out_root / ("Terraria-profiler.receipt.json" if profile == "profiler" else "Terraria.receipt.json")
    final_nro = out_root / ("Terraria-profiler.nro" if profile == "profiler" else "Terraria.nro")
    stamps = workdir / "stamps" / variant
    stamps.mkdir(parents=True, exist_ok=True)
    stage_info: dict[str, object] = {"input": meta, "stages": {}}

    patch_dir = run_patch_stage(game_dir, workdir, stamps, stage_info)
    romfs_stage(game_dir, patch_dir, aot / "runtime-romfs", workdir, stamps, stage_info)
    # AOT/RomFS are profile-independent; reuse the sibling profile's completed tree.
    other = "cli-profiler" if profile == "release" else "cli-release"
    other_aot = workdir / "release58" / other / "aot-final"
    if not (aot / "build-manifest.json").exists() and (other_aot / "build-manifest.json").exists():
        if json.loads((other_aot / "build-manifest.json").read_text()).get("status") == "complete":
            if aot.exists():
                shutil.rmtree(aot)
            shutil.copytree(other_aot, aot, copy_function=os.link, symlinks=True)

    aot_stage(aot, workdir, stamps, stage_info)
    augment_toolchain_aot(aot, stage_info)
    compat_header = workdir / "release58/v88/aot-final/mono_aot_modules.h"
    compat_header.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(aot / "mono_aot_modules.h", compat_header)
    launcher_dir = launcher_stage(workdir, profile, stamps, stage_info)
    native_stage(workdir, variant, aot, launcher_dir, profile, stamps, stage_info, final_nro)
    out_root.mkdir(parents=True, exist_ok=True)
    built = vdir / "native/candidate/mono_nx_fna.nro"
    shutil.copy2(built, final_nro)
    stage_info["nro"] = {"path": str(final_nro), "sha256": sha256(final_nro), "bytes": final_nro.stat().st_size}
    stage_info["toolchain_manifest_sha256"] = sha256(tc / "manifest.json")
    write_json(receipt, stage_info)
    print(f"Built {final_nro} {stage_info['nro']['sha256']}")
    return 0


def stamp_ok(stamp: Path, key: str) -> bool:
    return stamp.exists() and stamp.read_text().strip() == key


def write_stamp(stamp: Path, key: str) -> None:
    stamp.parent.mkdir(parents=True, exist_ok=True)
    stamp.write_text(key + "\n")


def run_patch_stage(game_dir: Path, workdir: Path, stamps: Path, info: dict) -> Path:
    key = sha256(game_dir / "Terraria.exe") + sha256(game_dir / "FNA.dll")
    out = workdir / "patch" / key[:16]
    stamp = stamps / "patch-managed.stamp"
    if stamp_ok(stamp, key) and out.exists():
        print("patch managed: cached")
    else:
        if out.exists():
            shutil.rmtree(out)
        env = os.environ.copy(); env["TERRABUILDER_CACHE"] = str(workdir); env["TERRABUILDER_LEGACY_CACHE"] = str(LEGACY)
        run([sys.executable, str(ROOT / "scripts/patch_vanilla/run.py"), str(game_dir), "--out", str(out), "--workdir", str(workdir)], env=env, heavy=True)
        write_stamp(stamp, key)
    info["stages"]["patch_managed"] = {n: sha256(out / n) for n in ["Terraria.exe", "FNA.dll", "ReLogic.dll", "NxCrypto.dll", "NxInputDiag.dll"]}
    return out


def romfs_stage(game_dir: Path, patched: Path, romfs: Path, workdir: Path, stamps: Path, info: dict) -> None:
    key = sha256(patched / "Terraria.exe") + sha256(patched / "FNA.dll") + sha256(game_dir / "Terraria.exe")
    stamp = stamps / "romfs.stamp"
    if stamp_ok(stamp, key) and romfs.exists():
        print("romfs: cached")
    else:
        if romfs.exists():
            shutil.rmtree(romfs)
        from scripts.pack_terraria_romfs import copy_game_payload
        rel_fw = workdir / "runtime-source/artifacts/bin/runtime/net9.0-libnx-Release-arm64"
        copy_game_payload(game_dir, romfs, tuple(), runtime_facades_dir=rel_fw)
        for name in ["Terraria.exe", "FNA.dll", "ReLogic.dll", "NxCrypto.dll", "NxInputDiag.dll"]:
            shutil.copy2(patched / name, romfs / name)
        core_dir = workdir / "runtime-source/artifacts/bin/mono/libnx.arm64.Release"
        (romfs / "mono/lib_net9.0").mkdir(parents=True, exist_ok=True)
        shutil.copy2(core_dir / "System.Private.CoreLib.dll", romfs / "mono/lib_net9.0/System.Private.CoreLib.dll")
        if (ROOT / "native/gamecontrollerdb.txt").exists():
            shutil.copy2(ROOT / "native/gamecontrollerdb.txt", romfs / "gamecontrollerdb.txt")
        write_stamp(stamp, key)
    files = sorted(p for p in romfs.rglob("*") if p.is_file())
    info["stages"]["romfs"] = {"file_count": len(files), "hash": tree_hash(romfs)}


def tree_hash(root: Path) -> str:
    h = hashlib.sha256()
    for p in sorted(x for x in root.rglob("*") if x.is_file()):
        h.update(p.relative_to(root).as_posix().encode()+b"\0")
        h.update(sha256(p).encode()+b"\0")
    return h.hexdigest()


def container_mounts(workdir: Path) -> list[str]:
    return ["-v", f"{workdir}:/build", "-v", f"{LEGACY}:/legacy:ro", "-v", f"{LEGACY}:{LEGACY}:ro", "-v", f"{ROOT}:/work:ro", "-v", f"{workdir / 'recovery46/sdk-pristine'}:/mono-nx:ro", "-v", f"{workdir / 'release58/mono-nx/native'}:/mono-nx/native:ro", "-v", f"{workdir / 'recovery46/native-deps/install'}:/fna-install:ro"]




def host_build_path(path: str | Path, workdir: Path | None = None) -> Path:
    p = Path(path)
    if str(p).startswith('/build/'):
        return (workdir or DEFAULT_WORKDIR).expanduser().resolve() / str(p)[7:]
    return p

def repair_incomplete_aot(aot: Path) -> bool:
    manifest_path = aot / "build-manifest.json"
    if not manifest_path.exists():
        return False
    manifest = json.loads(manifest_path.read_text())
    if manifest.get("status") == "complete":
        return True
    if not manifest.get("modules") or any(m.get("status") != "compiled" for m in manifest["modules"]):
        return False
    v88 = json.loads((LEGACY / "release58/v88/aot-final/build-manifest.json").read_text())
    core = dict(v88["corelib"])
    obj_src = LEGACY / "release58/v88/aot-final/System.Private.CoreLib.dll.o"
    llvm_src = LEGACY / "release58/v88/aot-final/System.Private.CoreLib.dll-llvm.o"
    obj = aot / obj_src.name
    llvm = aot / llvm_src.name
    if not obj.exists():
        os.link(obj_src, obj)
    if not llvm.exists():
        os.link(llvm_src, llvm)
    core.update(object=str(obj), object_sha256=sha256(obj), llvm_object=str(llvm), llvm_object_sha256=sha256(llvm), symbol_object=str(llvm), status="reused_from_toolchain")
    manifest["corelib"] = core
    symbols = [m["symbol"] for m in [manifest["corelib"], *manifest["modules"]]]
    (aot / "mono_aot_modules.h").write_text("/* Generated only after all AArch64 objects passed verification. */\n" + "".join(f"REGISTER_AOT_MODULE({sym});\n" for sym in symbols))
    manifest["status"] = "complete"
    manifest["runtime_metadata_manifest"] = manifest.get("runtime_metadata_manifest", "not regenerated; corelib reused from validated toolchain")
    write_json(manifest_path, manifest)
    return True

def aot_stage(aot: Path, workdir: Path, stamps: Path, info: dict) -> None:
    romfs = aot / "runtime-romfs"
    key = tree_hash(romfs)
    stamp = stamps / "aot.stamp"
    if stamp_ok(stamp, key) and (aot / "build-manifest.json").exists():
        print("aot: cached")
    elif repair_incomplete_aot(aot):
        print("aot: repaired cached objects")
        write_stamp(stamp, key)
    else:
        for p in aot.glob("*.o"):
            p.unlink()
        logs = aot / "logs"; logs.mkdir(exist_ok=True)
        core_obj = workdir / "release58/v88/aot-final/System.Private.CoreLib.dll.o"
        if not core_obj.exists():
            core_obj.parent.mkdir(parents=True, exist_ok=True)
            core_obj.symlink_to(LEGACY / "release58/v88/aot-final/System.Private.CoreLib.dll.o")
        eng = engine() or "podman"
        cmd = [eng, "run", "--rm", "--userns=keep-id", "--entrypoint", "/usr/bin/python3", "-e", "PATH=/opt/devkitpro/devkitA64/bin:/opt/devkitpro/tools/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin", *container_mounts(workdir), LLVM_IMAGE,
               "/work/scripts/compile_terraria_aot.py", "--game-dir", rel_to_mount(romfs, workdir, "/build"), "--output-dir", rel_to_mount(aot, workdir, "/build"),
               "--corelib-dir", "/build/runtime-source/artifacts/bin/mono/libnx.arm64.Release", "--runtime-dir", "/build/runtime-source/artifacts/bin/runtime/net9.0-libnx-Release-arm64",
               "--cross-compiler", "/mono-nx/dotnet_runtime/artifacts/bin/mono/linux.x64.Debug/cross/linux-x64/libnx-arm64/mono-aot-cross",
               "--llvm-compiler-dir", "/build/runtime-llvm/artifacts/bin/mono/linux.x64.Debug/cross/linux-x64/libnx-arm64",
               "--cecil-path", "/build/runtime-source/artifacts/bin/Mono.Linker/Release/net9.0/Mono.Cecil.dll", "--dotnet", "/build/runtime-source/.dotnet/dotnet",
               "--corelib-object", rel_to_mount(core_obj, workdir, "/build"), "--corelib-log", "/legacy/release58/v88/aot-final/logs/System.Private.CoreLib.dll.log",
               "--llvm-module", "Terraria", "--llvm-module", "FNA", "--jobs", "3"]
        run(cmd, heavy=True)
        write_stamp(stamp, key)
    manifest = json.loads((aot / "build-manifest.json").read_text())
    info["stages"]["aot"] = {"modules": {m["assembly"]["name"]: [m.get("compiled_methods"), m.get("total_methods")] for m in [manifest["corelib"], *manifest["modules"]]}}


def augment_toolchain_aot(aot: Path, info: dict) -> None:
    manifest_path = aot / "build-manifest.json"
    manifest = json.loads(manifest_path.read_text())
    existing = {m["assembly"]["name"] for m in [manifest["corelib"], *manifest["modules"]]}
    prep = json.loads(host_build_path(manifest["preparation_manifest"], aot.parents[2]).read_text())
    staged = {a["name"]: a for a in prep["staged_assemblies"]}
    added = []
    for name, stem in [("System.Text.RegularExpressions", "System.Text.RegularExpressions.dll"), ("System.Collections.Concurrent", "System.Collections.Concurrent.dll")]:
        if name in existing:
            continue
        src_obj = LEGACY / "release58/v88/aot-final" / f"{stem}.o"
        src_llvm = LEGACY / "release58/v88/aot-final" / f"{stem}-llvm.o"
        dst_obj = aot / src_obj.name; dst_llvm = aot / src_llvm.name
        if not dst_obj.exists(): os.link(src_obj, dst_obj)
        if not dst_llvm.exists(): os.link(src_llvm, dst_llvm)
        # Copy method counts/symbol metadata from the validated v88 manifest, but point at this build's files.
        v88 = json.loads((LEGACY / "release58/v88/aot-final/build-manifest.json").read_text())
        base = next(m for m in [v88["corelib"], *v88["modules"]] if m["assembly"]["name"] == name)
        module = dict(base)
        module["assembly"] = staged.get(name, base["assembly"])
        module.update(object=str(dst_obj), object_sha256=sha256(dst_obj), llvm_object=str(dst_llvm), llvm_object_sha256=sha256(dst_llvm), symbol_object=str(dst_llvm), status="reused_from_toolchain")
        manifest["modules"].append(module)
        added.append(name)
    if added:
        symbols = [m["symbol"] for m in [manifest["corelib"], *manifest["modules"]]]
        (aot / "mono_aot_modules.h").write_text("/* Generated only after all AArch64 objects passed verification. */\n" + "".join(f"REGISTER_AOT_MODULE({s});\n" for s in symbols))
        write_json(manifest_path, manifest)
    info["stages"]["toolchain_aot_reuse"] = added


def launcher_stage(workdir: Path, profile: str, stamps: Path, info: dict) -> Path:
    out = workdir / "launcher-src" / profile
    key = profile + sha256(ROOT / "native/shared/nx_input.c")
    stamp = stamps / "launcher.stamp"
    if stamp_ok(stamp, key) and (out / "launcher-build.json").exists():
        print("launcher: cached")
    else:
        if out.exists(): shutil.rmtree(out)
        env = os.environ.copy(); env["TERRABUILDER_CACHE"] = str(workdir)
        cmd = [sys.executable, str(ROOT / "scripts/launcher/build_launcher.py"), "--out", str(out), "--force"]
        if profile == "profiler": cmd.append("--profiler")
        run(cmd, env=env, heavy=True)
        write_stamp(stamp, key)
    info["stages"]["launcher"] = json.loads((out / "launcher-build.json").read_text())
    return out


def native_stage(workdir: Path, variant: str, aot: Path, launcher: Path, profile: str, stamps: Path, info: dict, final_nro: Path) -> None:
    key = tree_hash(aot / "runtime-romfs") + sha256(aot / "mono_aot_modules.h") + profile
    stamp = stamps / "native.stamp"
    vdir = workdir / "release58" / variant
    if stamp_ok(stamp, key) and (vdir / "native/candidate/mono_nx_fna.nro").exists():
        print("native link: cached")
    else:
        native = vdir / "native"
        if native.exists(): shutil.rmtree(native)
        overrides = []
        for name in ["core.o","dl_shim.o","dl_shim_dotnet.o","dl_shim_libnx.o","dl_shim_stubs.o","heap.o","io_shims.o","io_util.o","ini.o","dl_shim_FAudio.o","dl_shim_FNA3D.o","dl_shim_SDL3.o","nx_input.o","dl_shim_SDL2.o","dl_shim_SDL2_image.o","dl_shim_opengl.o","dl_shim_openal.o"]:
            p = launcher / "objects" / name
            if p.exists(): overrides.append(f"{name}={rel_to_mount(p, workdir, '/build')}")
        extra_objs = [launcher / "objects/nx_gpu_timing.o", launcher / "objects/nx_audio.o"]
        if profile == "profiler": extra_objs.append(launcher / "objects/nx_profiler.o")
        extra_flags = ["-L/build/gfx/v88-lib", *[rel_to_mount(p, workdir, "/build") for p in extra_objs], *["-Wl,--wrap=" + w for w in V88_WRAPS]]
        env = os.environ.copy()
        env.update({"R58_VARIANT": variant, "R58_RUNTIME": "release", "R58_SKIP_CONTROL": "1", "R58_OBJECT_OVERRIDES": " ".join(overrides), "R58_EXTRA_LDFLAGS": " ".join(extra_flags), "R58_TITLE": "Terraria", "R58_NACP_VERSION": "1.4.5.8"})
        if profile == "profiler": env["R58_MAIN_DEFINES"] = "-DMONO_NX_PROFILER=1"
        eng = engine() or "podman"
        # Build env is clearer as repeated -e before image.
        cmd = [eng, "run", "--rm", "--userns=keep-id", "--entrypoint", "/usr/bin/python3"]
        for k in ("R58_VARIANT","R58_RUNTIME","R58_SKIP_CONTROL","R58_OBJECT_OVERRIDES","R58_EXTRA_LDFLAGS","R58_TITLE","R58_NACP_VERSION","R58_MAIN_DEFINES"):
            if k in env: cmd += ["-e", f"{k}={env[k]}"]
        cmd += [*container_mounts(workdir), LLVM_IMAGE, "/build/release58/build_native.py"]
        run(cmd, heavy=True)
        write_stamp(stamp, key)
    nb = json.loads((vdir / "native/native-build.json").read_text())
    info["stages"]["native"] = nb


def compare(args: argparse.Namespace) -> int:
    # Lightweight offline comparison helper used by verification/reporting.
    workdir = args.workdir.expanduser().resolve()
    profiler = workdir / "out/Terraria-profiler.nro"
    release = workdir / "out/Terraria.nro"
    for p in (profiler, release):
        if p.exists(): print(f"{p} {p.stat().st_size} {sha256(p)}")
    return 0


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(prog="terrabuilder")
    p.add_argument("--workdir", type=Path, default=DEFAULT_WORKDIR)
    sub = p.add_subparsers(dest="cmd", required=True)
    d = sub.add_parser("doctor"); d.add_argument("--workdir", type=Path, default=DEFAULT_WORKDIR); d.set_defaults(func=doctor)
    t = sub.add_parser("toolchain"); t.add_argument("--workdir", type=Path, default=DEFAULT_WORKDIR); t.add_argument("toolcmd", nargs="?", choices=["pack"]); t.add_argument("--from-source", action="store_true"); t.add_argument("--bundle"); t.set_defaults(func=toolchain)
    b = sub.add_parser("build"); b.add_argument("--workdir", type=Path, default=DEFAULT_WORKDIR); b.add_argument("--target", choices=["vanilla","tmodloader"]); b.add_argument("--game-dir"); b.add_argument("--out"); b.add_argument("--profile", choices=["release","profiler"], default="release"); b.add_argument("-y", "--yes", action="store_true"); b.set_defaults(func=build)
    c = sub.add_parser("compare"); c.add_argument("--workdir", type=Path, default=DEFAULT_WORKDIR); c.set_defaults(func=compare)
    args = p.parse_args(argv)
    return args.func(args)

if __name__ == "__main__":
    raise SystemExit(main())
