#!/usr/bin/env python3
from __future__ import annotations

import argparse
import contextlib
import fcntl
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
HOST_FNA3D = {
    "repo": "https://github.com/FNA-XNA/FNA3D.git",
    "tag": "26.07",
    "commit": "1ac4231ed9f0cfa1211e46b27dc8fb4ef3830eb8",
    "mojoshader_commit": "abdc80360c1d4560ab8f356035dcd53ae6e9b87f",
}
TMOD_SHARED_AUDIO_FNA_SHA256 = "a24a7545293b5b2d351544ab4a7fa3f5f739e00df02714b3c5a3c352b0767678"

DIAGNOSTIC_WRAPS = ["SDL_PollEvent", "FNA3D_SwapBuffers"]
BEHAVIOR_WRAPS = [
    "SDL_StartTextInput", "SDL_StopTextInput",
    "SDL_SetThreadPriority", "SDL_OpenAudioDevice", "audrenWaitFrame", "SDL_PauseAudioDevice",
    "SDL_CloseAudioDevice", "nwindowDequeueBuffer", "nwindowQueueBuffer", "nwindowSetSwapInterval",
    "nwindowConfigureBuffer",
]
GAME_DLLS = ["Terraria.exe", "FNA.dll", "ReLogic.dll", "Newtonsoft.Json.dll", "NxCrypto.dll", "NxInputDiag.dll"]
TMOD_WARNING = """\
tModLoader (experimental) build notes:
- In-world Fargo's Souls performance is very slow (about 15-20 fps measured on an earlier build).
- The D-pad does not navigate the inventory; the Plus+Minus FPS toggle is unconfirmed.
- Multiplayer is untested on tModLoader.
- The mod list is limited to tested open-source mods built from pinned source."""


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


@contextlib.contextmanager
def tmod_variant_lock(variant: str):
    lock_path = LEGACY / "tmod" / f".{variant}.lock"
    lock_path.parent.mkdir(parents=True, exist_ok=True)
    with lock_path.open("a") as lock_file:
        fcntl.flock(lock_file, fcntl.LOCK_EX)
        try:
            yield
        finally:
            fcntl.flock(lock_file, fcntl.LOCK_UN)


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
    notice_files: list[Path] = []
    for name in ("THIRD_PARTY_NOTICES.md", "CREDITS.md"):
        src = ROOT / name
        if src.exists():
            shutil.copy2(src, tc / name)
            notice_files.append(tc / name)
    licenses_src = ROOT / "licenses"
    licenses_dst = tc / "licenses"
    if licenses_dst.exists():
        shutil.rmtree(licenses_dst)
    if licenses_src.exists():
        shutil.copytree(licenses_src, licenses_dst)
        notice_files.extend(sorted(p for p in licenses_dst.rglob("*") if p.is_file()))
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
        "licenses": {
            "note": "Bundle includes open-source/toolchain notices only; game-derived Terraria/tModLoader files are built locally and are not distributed.",
            "files": file_manifest(notice_files, tc),
        },
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
    if args.from_source:
        raise SystemExit(
            "toolchain --from-source is not available yet: the clean-checkout "
            "runtime/BCL/LLVM/Mesa pipeline is incomplete. See docs/BUILDING.md."
        )
    if args.bundle:
        raise SystemExit(
            "toolchain --bundle is not available yet: bundle import into a "
            "clean workdir is incomplete. See docs/BUILDING.md."
        )
    if args.toolcmd == "pack" or not args.toolcmd:
        toolchain_pack(args.workdir.expanduser())
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


def validate_tmodloader(game: Path) -> dict:
    required = ["tModLoader.dll", "tModLoader.deps.json", "tMLMod.targets", "Content"]
    missing = [n for n in required if not (game / n).exists()]
    if missing:
        raise SystemExit("tModLoader directory missing: " + ", ".join(missing))
    deps = json.loads((game / "tModLoader.deps.json").read_text())
    version = None
    for target in deps.get("targets", {}):
        for package in deps["targets"][target]:
            if package.startswith("tModLoader/"):
                version = package.split("/", 1)[1]
                break
        if version:
            break
    if not version or not version.startswith("1.4.4."):
        raise SystemExit(f"unsupported tModLoader version {version or 'unknown'}; expected 1.4.4.x")
    return {"tmodloader_sha256": sha256(game / "tModLoader.dll"), "version": version}


def curated_mod_catalog() -> dict[str, dict]:
    data = json.loads((Path(__file__).with_name("curated_tmod_mods.json")).read_text())
    return {entry["id"]: entry for entry in data["mods"]}


def dependency_closed_mods(selected: list[str], catalog: dict[str, dict]) -> list[str]:
    ordered: list[str] = []
    seen: set[str] = set()

    def add(name: str) -> None:
        if name in seen:
            return
        if name not in catalog:
            raise SystemExit(f"unknown curated mod: {name}")
        for dep in catalog[name].get("dependencies", []):
            add(dep)
        seen.add(name)
        ordered.append(name)

    for item in selected:
        add(item)
    return ordered


def ask_tmod_mods(catalog: dict[str, dict]) -> list[str]:
    choices = [m for m in catalog.values() if m["id"] in ("Luminance", "Fargowiltas", "FargowiltasSouls")]
    print("Select tested open-source mods to build from source:")
    print("  0) no mods")
    for i, mod in enumerate(choices, 1):
        deps = mod.get("dependencies") or []
        suffix = f" (auto-selects: {', '.join(deps)})" if deps else ""
        print(f"  {i}) {mod['display_name']} {mod['version']} [{mod['spdx_license']}]" + suffix)
    raw = ask("Mods (comma-separated numbers)", "0")
    if raw.strip() in ("", "0", "none", "no mods"):
        return []
    selected = []
    for token in re.split(r"[, ]+", raw.strip()):
        if not token:
            continue
        if not token.isdigit() or not (1 <= int(token) <= len(choices)):
            raise SystemExit(f"invalid mod selection: {token}")
        selected.append(choices[int(token) - 1]["id"])
    return dependency_closed_mods(selected, catalog)


def ask(prompt: str, default: str | None = None) -> str:
    suffix = f" [{default}]" if default else ""
    value = input(prompt + suffix + ": ").strip()
    return value or (default or "")


def parse_mod_flags(value: str | None, catalog: dict[str, dict]) -> list[str]:
    if value is None:
        return []
    if value.strip().lower() in ("", "none", "no-mods", "no mods"):
        return []
    requested = [part.strip() for part in value.split(",") if part.strip()]
    by_lower = {key.lower(): key for key in catalog}
    selected = []
    for item in requested:
        key = by_lower.get(item.lower())
        if not key:
            raise SystemExit(f"unknown mod {item}; available: " + ", ".join(sorted(catalog)))
        selected.append(key)
    return dependency_closed_mods(selected, catalog)


def tmod_variant_key(tmod_dir: Path, mods: list[str], catalog: dict[str, dict], profile: str) -> str:
    h = hashlib.sha256()
    h.update(b"openal-free-quiet-launcher-v2-shared-audio-fna")
    h.update(sha256(tmod_dir / "tModLoader.dll").encode())
    h.update(TMOD_SHARED_AUDIO_FNA_SHA256.encode())
    h.update(profile.encode())
    for mod in mods:
        h.update(mod.encode() + b"\0" + catalog[mod]["commit"].encode() + b"\0")
    return "tmodcli-" + h.hexdigest()[:12]


def apply_recorded_patch(checkout: Path, patch: Path) -> None:
    marker = ".terrabuilder-" + patch.name + ".applied"
    target = None
    add_file = False
    old: list[str] = []
    new: list[str] = []
    for line in patch.read_text().splitlines():
        if line.startswith("*** Update File: "):
            target = checkout / line.split(": ", 1)[1]
            add_file = False
        elif line.startswith("*** Add File: "):
            target = checkout / line.split(": ", 1)[1]
            add_file = True
        elif line.startswith("-") and not line.startswith("---"):
            old.append(line[1:])
        elif line.startswith("+") and not line.startswith("+++"):
            new.append(line[1:])
    if target is None:
        raise SystemExit(f"patch missing target: {patch}")
    if add_file:
        data = "\n".join(new) + "\n"
        if target.exists() and target.read_text() != data:
            raise SystemExit(f"failed to apply recorded patch {patch}: {target.name} already exists")
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(data)
    else:
        data = target.read_text()
        for idx, before in enumerate(old):
            after = new[idx] if idx < len(new) else ""
            if before in data:
                data = data.replace(before, after, 1)
            elif after not in data:
                raise SystemExit(f"failed to apply recorded patch {patch}: context not found")
        target.write_text(data)
    (checkout / marker).write_text(sha256(patch) + "\n")


def ensure_host_fna3d(workdir: Path, tmod_dir: Path) -> Path:
    out = workdir / "native-host" / f"fna3d-{HOST_FNA3D['tag']}"
    receipt = out / "receipt.json"
    if receipt.exists() and (out / "libFNA3D.so.0").exists():
        return out
    src = workdir / "native-host" / f"fna3d-{HOST_FNA3D['tag']}-src"
    build = workdir / "native-host" / f"fna3d-{HOST_FNA3D['tag']}-build"
    eng = engine() or "podman"
    if not src.exists():
        run(["git", "clone", "--recursive", "--depth", "1", "--branch", HOST_FNA3D["tag"], HOST_FNA3D["repo"], str(src)])
    else:
        run(["git", "-C", str(src), "fetch", "--depth", "1", "origin", HOST_FNA3D["commit"]])
    run(["git", "-C", str(src), "checkout", "--detach", HOST_FNA3D["commit"]])
    run(["git", "-C", str(src), "submodule", "update", "--init", "--recursive"])
    if not (build / "libFNA3D.so.0").exists():
        cmd = [eng, "run", "--rm", "--entrypoint", "/bin/bash",
               "-v", f"{workdir}:/tb", MONO_IMAGE, "-lc",
               "apt-get update -qq && "
               "apt-get install -y --no-install-recommends libsdl2-dev >/tmp/apt-terrabuilder-fna3d.log && "
               f"cmake -S /tb/native-host/fna3d-{HOST_FNA3D['tag']}-src "
               f"-B /tb/native-host/fna3d-{HOST_FNA3D['tag']}-build "
               "-DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON -DBUILD_SDL3=OFF && "
               f"cmake --build /tb/native-host/fna3d-{HOST_FNA3D['tag']}-build --parallel 4"]
        run(cmd)
    out.mkdir(parents=True, exist_ok=True)
    for path in build.glob("libFNA3D.so*"):
        target = out / path.name
        if target.exists() or target.is_symlink():
            target.unlink()
        if path.is_symlink():
            target.symlink_to(os.readlink(path))
        else:
            shutil.copy2(path, target)
    native_linux = tmod_dir / "Libraries/Native/Linux"
    for name in ("libSDL2-2.0.so.0", "libFAudio.so.0"):
        if (native_linux / name).exists():
            shutil.copy2(native_linux / name, out / name)
    write_json(receipt, {
        "source": HOST_FNA3D,
        "libFNA3D_sha256": sha256(out / "libFNA3D.so.0.26.07"),
        "note": "Host-side tModLoader ModCompile dependency only; never copied to Switch payload.",
    })
    return out


def ensure_dotnet8(workdir: Path) -> Path:
    dotnet = workdir / "dotnet8" / "dotnet"
    if dotnet.exists():
        return dotnet
    install_dir = workdir / "dotnet8"
    install_dir.mkdir(parents=True, exist_ok=True)
    script = install_dir / "dotnet-install.sh"
    if not script.exists():
        run(["curl", "-fsSL", "https://dot.net/v1/dotnet-install.sh", "-o", str(script)])
    run(["bash", str(script), "--channel", "8.0", "--install-dir", str(install_dir), "--runtime", "dotnet", "--no-path"])
    run(["bash", str(script), "--channel", "8.0", "--install-dir", str(install_dir), "--no-path"])
    return dotnet


def build_curated_mod_sources(tmod_dir: Path, mods: list[str], workdir: Path, catalog: dict[str, dict]) -> Path:
    cache_key = hashlib.sha256(
        (sha256(tmod_dir / "tModLoader.dll") + "\n" +
         "\n".join(
             f"{m}:{catalog[m]['commit']}:{','.join(catalog[m].get('patches', []))}"
             for m in mods
         )).encode()
    ).hexdigest()[:12]
    packages = workdir / "tmod" / "packages" / cache_key
    done = packages / ".complete.json"
    if done.exists() and all((packages / (m + ".tmod")).exists() for m in mods if m != "StructureHelper"):
        print("tModLoader mod source build: cached")
        return packages
    packages.mkdir(parents=True, exist_ok=True)
    src_root = workdir / "tmod" / "sources"
    src_root.mkdir(parents=True, exist_ok=True)
    (workdir / "tmod-home").mkdir(parents=True, exist_ok=True)
    shutil.copy2(tmod_dir / "tMLMod.targets", src_root / "tModLoader.targets")
    host_native = ensure_host_fna3d(workdir, tmod_dir)
    dotnet8 = ensure_dotnet8(workdir)
    built_dlls: dict[str, Path] = {}
    built_packages: dict[str, Path] = {}
    build_order = [m for m in mods if m != "FargowiltasSouls"] + ([m for m in mods if m == "FargowiltasSouls"])
    eng = engine() or "podman"
    for name in build_order:
        meta = catalog[name]
        checkout = src_root / name
        if not checkout.exists():
            run(["git", "clone", "--filter=blob:none", meta["repo"], str(checkout)])
        run(["git", "-C", str(checkout), "fetch", "--depth", "1", "origin", meta["commit"]])
        run(["git", "-C", str(checkout), "checkout", "--detach", meta["commit"]])
        run(["git", "-C", str(checkout), "reset", "--hard", meta["commit"]])
        run(["git", "-C", str(checkout), "clean", "-fdx"])
        for patch_name in meta.get("patches", []):
            apply_recorded_patch(checkout, Path(__file__).with_name("patches") / patch_name)
        for dep_name, dep_dll in built_dlls.items():
            if dep_name in meta.get("dependencies", []):
                shutil.copy2(dep_dll, checkout / (dep_name + ".dll"))
        before = {p.resolve() for p in checkout.rglob("*.tmod")}
        cmd = [eng, "run", "--rm", "--entrypoint", "/tb/dotnet8/dotnet",
               "-e", "DOTNET_CLI_HOME=/tb/dotnet-home",
               "-e", "DOTNET_ROOT=/tb/dotnet8",
               "-e", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1",
               "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1",
               "-e", "HOME=/tb/tmod-home",
               "-e", "PATH=/tb/dotnet8:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
               "-e", f"LD_LIBRARY_PATH=/tb/{host_native.relative_to(workdir).as_posix()}:/tml/Libraries/Native/Linux",
               "-e", "SDL_VIDEODRIVER=dummy",
               "-e", "SDL_AUDIODRIVER=dummy",
               "-v", f"{workdir}:/tb",
               "-v", f"{tmod_dir}:/tml:ro",
               "-v", f"{LEGACY / 'recovery46/sdk-pristine'}:/mono-nx:ro",
               "-w", f"/tb/tmod/sources/{name}",
               MONO_IMAGE,
               "build", f"{name}.csproj", "-c", "Debug", f"-p:tMLSteamPath=/tml/",
               "-p:TreatWarningsAsErrors=false", "-p:NoWarn=0619",
               "-p:DebugType=none", "-p:DebugSymbols=false",
               "-p:GenerateTargetFrameworkAttribute=false"]
        try:
            run(cmd, heavy=True)
        except subprocess.CalledProcessError as exc:
            raise SystemExit(
                f"failed to build {name} from source at {meta['commit']}. "
                "See the build output above for the tML ModCompile or C# compiler error."
            ) from exc
        dlls = sorted(checkout.rglob(f"{name}.dll"), key=lambda p: p.stat().st_mtime, reverse=True)
        if dlls:
            built_dlls[name] = dlls[0]
        after = [p for p in checkout.rglob("*.tmod") if p.resolve() not in before]
        if not after:
            candidates = list(checkout.rglob("*.tmod")) + list((workdir / "tmod-home").rglob("*.tmod"))
            after = sorted([p for p in candidates if p.name == name + ".tmod"],
                           key=lambda p: p.stat().st_mtime, reverse=True)
        if name != "StructureHelper":
            if not after:
                raise SystemExit(f"built {name} but no .tmod package was produced")
            dest = packages / (name + ".tmod")
            shutil.copy2(after[0], dest)
            built_packages[name] = dest
    write_json(done, {"mods": mods, "packages": {k: str(v) for k, v in built_packages.items()}})
    return packages


def profile_wraps(profile: str) -> list[str]:
    return [*(DIAGNOSTIC_WRAPS if profile in ("debug", "profiler") else []), *BEHAVIOR_WRAPS]


def launcher_config(profile: str, launcher: Path) -> dict:
    extra = ["nx_audio.o", "nx_gpu_timing.o"]
    if profile == "profiler":
        extra.append("nx_profiler.o")
    manifest = launcher / "launcher-build.json"
    if manifest.is_relative_to(LEGACY):
        manifest_path = "/build/" + manifest.relative_to(LEGACY).as_posix()
    else:
        manifest_path = str(manifest)
    return {"manifest": manifest_path, "extra_objects": extra}


def seed_tmod_input(variant: str, mods: list[str], packages: Path | None, catalog: dict[str, dict], profile: str, launcher: Path) -> Path:
    out = LEGACY / "tmod" / variant
    if out.exists() and (out / "manifest.json").exists():
        return out
    if out.exists():
        shutil.rmtree(out)
    selected_packages = [m for m in mods if m != "StructureHelper"]
    if selected_packages:
        primary = "FargowiltasSouls" if "FargowiltasSouls" in mods else selected_packages[0]
        label = "+".join(selected_packages)
        run([sys.executable, str(ROOT / "scripts/tmod/prepare_modset.py"),
             "--cache-root", str(LEGACY), "--mods-dir", str(packages),
             "--variant", variant, "--primary", primary, "--label", label,
             "--base", "tmod27", "--game", str(LEGACY / "tmod/tmod27-nxfix-mp/tModLoader_patched_nxfix.dll"),
             "--interpret-hooks"])
        config_path = out / "input/modset.json"
        config = json.loads(config_path.read_text())
        baseline_config = json.loads((LEGACY / "tmod/tmod27/input/modset.json").read_text())
        llvm_modules = set(baseline_config.get("llvm_modules", []))
        llvm_modules.discard("tModLoader.dll")
        for mod_name in selected_packages:
            llvm_modules.add(mod_name + ".dll")
        if "FargowiltasSouls" in mods and "StructureHelper" in mods:
            llvm_modules.add("StructureHelper.dll")
        config["llvm_modules"] = sorted(llvm_modules)
        config["extra_aot"] = baseline_config.get("extra_aot", [])
        config["reuse_base"] = "tmod27"
        config["reuse_modules"] = ["ReLogic.dll", "System.Linq.dll"]
        config["aot_workers"] = 1
        config["launcher_objects"] = launcher_config(profile, launcher)
        config["extra_ldflags"] = ["-Wl," + ",".join("--wrap=" + w for w in profile_wraps(profile))]
        native = dict(config.get("native", {}))
        native["gc_stats"] = profile in ("debug", "profiler")
        native["frame_stats"] = profile in ("debug", "profiler")
        native["debug_diagnostics"] = profile in ("debug", "profiler")
        native["profiler"] = profile == "profiler"
        config["native"] = native
        write_json(config_path, config)
        patched_fna = LEGACY / "tmod/tmod27/input/FNA.dll"
        if not patched_fna.is_file() or sha256(patched_fna) != TMOD_SHARED_AUDIO_FNA_SHA256:
            raise SystemExit("validated tModLoader shared-audio FNA is missing or has an unexpected hash")
        shutil.copy2(patched_fna, out / "input/FNA.dll")
        nxcrypto = LEGACY / "hint52/aot-final/runtime-romfs/NxCrypto.dll"
        if nxcrypto.exists():
            shutil.copy2(nxcrypto, out / "input/NxCrypto.dll")
        write_json(out / "source-mods.json", {"mods": mods, "catalog": [catalog[m] for m in mods]})
        return out
    src = LEGACY / "tmod/tmod27/input"
    if not src.exists():
        raise SystemExit("validated tmod27 input cache is missing")
    inputs = out / "input"
    inputs.mkdir(parents=True)
    for name in ("tModLoader.dll", "FNA.dll", "System.Reflection.Metadata.dll", "TerrariaHooks.dll", "MonoMod.RuntimeDetour.dll"):
        if (src / name).exists():
            shutil.copy2(src / name, inputs / name)
    config = json.loads((src / "modset.json").read_text())
    primary = "FargowiltasSouls" if "FargowiltasSouls" in mods else (selected_packages[0] if selected_packages else "")
    config["mod"] = primary
    config["additional_mods"] = [m for m in selected_packages if m != primary]
    config["mod_libraries"] = ([{"mod": "FargowiltasSouls", "member": "lib/StructureHelper.dll"}]
                               if "FargowiltasSouls" in mods else [])
    config["label"] = "no mods" if not selected_packages else "+".join(selected_packages)
    config["reuse_base"] = "tmod27"
    config["reuse_modules"] = [
        "tModLoader.dll", "FNA.dll", "ReLogic.dll", "System.Linq.dll",
        "System.Text.RegularExpressions.dll", "System.Collections.Concurrent.dll",
    ]
    config["launcher_objects"] = launcher_config(profile, launcher)
    config["extra_ldflags"] = ["-Wl," + ",".join("--wrap=" + w for w in profile_wraps(profile))]
    native = dict(config.get("native", {}))
    native["gc_stats"] = profile in ("debug", "profiler")
    native["frame_stats"] = profile in ("debug", "profiler")
    native["debug_diagnostics"] = profile in ("debug", "profiler")
    native["profiler"] = profile == "profiler"
    config["native"] = native
    write_json(inputs / "modset.json", config)
    write_json(out / "source-mods.json", {"mods": mods, "catalog": [catalog[m] for m in mods]})
    return out


def run_tmod_pipeline(variant: str, workdir: Path) -> None:
    out = LEGACY / "tmod" / variant
    if (out / "native/candidate/tmodloader.nro").exists() and (out / "manifest.json").exists():
        print("tModLoader native build: cached")
        return
    eng = engine() or "podman"
    cmd = [eng, "run", "--rm", "--userns=keep-id", "--entrypoint", "python3",
           "-e", f"TMOD_VARIANT={variant}", "-e", f"TMPDIR=/build/tmod/{variant}/container-tmp",
           "-v", f"{LEGACY}:/build",
           "-v", f"{ROOT}:/work:ro",
           "-v", f"{LEGACY / 'recovery46/sdk-pristine'}:/mono-nx:ro",
           "-v", f"{LEGACY / 'release58/mono-nx/native'}:/mono-nx/native:ro",
           "-v", f"{LEGACY / 'recovery46/native-deps/install'}:/fna-install:ro",
           LLVM_IMAGE, "/work/scripts/tmod/build_tmod_modset_nro.py"]
    run(cmd, heavy=True)
    verify = [sys.executable, str(ROOT / "scripts/tmod/verify_aot_dependencies.py"),
              "--cache-root", str(LEGACY), "--variant", variant, "--json-out", str(out / "aot-mvid-check.json")]
    run(verify)


def build_tmodloader(args: argparse.Namespace, tmod_dir: Path, mods: list[str], catalog: dict[str, dict]) -> int:
    workdir = args.workdir.expanduser().resolve()
    out_root = Path(args.out).expanduser().resolve() if args.out else workdir / "out"
    tmeta = validate_tmodloader(tmod_dir)
    variant = tmod_variant_key(tmod_dir, mods, catalog, args.profile)
    stamps = workdir / "stamps" / variant
    stamps.mkdir(parents=True, exist_ok=True)
    launcher_dir = launcher_stage(LEGACY, args.profile, stamps)
    packages = build_curated_mod_sources(tmod_dir, mods, workdir, catalog) if mods else None
    with tmod_variant_lock(variant):
        seed_tmod_input(variant, mods, packages, catalog, args.profile, launcher_dir)
        run_tmod_pipeline(variant, workdir)
        src_root = LEGACY / "tmod" / variant
        out_dir = out_root / ("tmodloader-" + ("no-mods" if not mods else "-".join(mods).lower()))
        out_dir.mkdir(parents=True, exist_ok=True)
        nro = out_dir / "tmodloader.nro"
        shutil.copy2(src_root / "native/candidate/tmodloader.nro", nro)
        sd_out = out_dir / "sdcard"
        if sd_out.exists():
            shutil.rmtree(sd_out)
        shutil.copytree(src_root / "sdcard", sd_out)
        manifest = json.loads((src_root / "manifest.json").read_text())
        mvid = json.loads((src_root / "aot-mvid-check.json").read_text())
        receipt = {"target": "tmodloader", "experimental": True, "variant": variant, "input": tmeta,
                   "mods": [catalog[m] for m in mods], "nro": {"path": str(nro), "sha256": sha256(nro), "bytes": nro.stat().st_size},
                   "sd_payload": str(sd_out), "aot_mvid_check": mvid, "manifest": manifest}
        write_json(out_dir / "receipt.json", receipt)
        print(f"Built {nro} {receipt['nro']['sha256']}")
        print(f"Copy {nro} to sd:/switch/tmodloader.nro")
        print(f"Copy contents of {sd_out}/ to the SD card root, overwriting existing files")
        if mods:
            print("This NRO only runs with these exact .tmod files. Workshop downloads and earlier builds can "
                  "share mod names and versions but not code; mismatches abort at mod load with "
                  "\"Failed to load AOT module ... doesn't match assembly\".")
        print(TMOD_WARNING)
        return 0


def build(args: argparse.Namespace) -> int:
    target = args.target
    game_dir = Path(args.game_dir).expanduser() if args.game_dir else None
    if not args.yes and (not target or not game_dir):
        if not target:
            choice = ask("Target: 1) Vanilla Terraria (GOG 1.4.5.x)  2) tModLoader (1.4.4) (experimental)", "1")
            target = "tmodloader" if choice.strip() in ("2", "tmodloader", "tmod") else "vanilla"
        game_dir = game_dir or Path(ask("Path to game files"))
    target = target or "vanilla"
    if target == "tmodloader":
        if not game_dir:
            raise SystemExit("--game-dir is required in non-interactive mode and must point to a tModLoader 1.4.4.x folder")
        catalog = curated_mod_catalog()
        mods = parse_mod_flags(args.mods, catalog) if args.yes or args.mods is not None else ask_tmod_mods(catalog)
        return build_tmodloader(args, game_dir.resolve(), mods, catalog)
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
    suffix = "" if profile == "release" else f"-{profile}"
    receipt = out_root / f"Terraria{suffix}.receipt.json"
    final_nro = out_root / f"Terraria{suffix}.nro"
    stamps = workdir / "stamps" / variant
    stamps.mkdir(parents=True, exist_ok=True)
    stage_info: dict[str, object] = {"input": meta, "stages": {}}

    patch_dir = run_patch_stage(game_dir, workdir, stamps, stage_info)
    romfs_stage(game_dir, patch_dir, aot / "runtime-romfs", workdir, stamps, stage_info)
    # AOT/RomFS are profile-independent; reuse the sibling profile's completed tree.
    other = "cli-profiler" if profile == "release" else "cli-release"
    other_aot = workdir / "release58" / other / "aot-final"
    if other_aot.joinpath("build-manifest.json").exists() and (
        not (aot / "build-manifest.json").exists()
        or tree_hash(aot / "runtime-romfs") == tree_hash(other_aot / "runtime-romfs")
    ):
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
    patch_sources = sorted((ROOT / "scripts/patch_vanilla").glob("*.cs")) + [
        ROOT / "scripts/patch_vanilla/PatchVanilla.csproj",
        ROOT / "managed/nx_crypto/NxCrypto.cs",
        ROOT / "managed/nx_crypto/NxCrypto.csproj",
        ROOT / "managed/nx_input_diag/NxInputDiag.cs",
        ROOT / "managed/nx_input_diag/NxInputDiag.csproj",
    ]
    key = sha256(game_dir / "Terraria.exe") + sha256(game_dir / "FNA.dll") + "".join(sha256(p) for p in patch_sources if p.exists())
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
    info["stages"]["patch_managed"] = {p.name: sha256(p) for p in sorted(out.glob("*.dll"))}
    info["stages"]["patch_managed"]["Terraria.exe"] = sha256(out / "Terraria.exe")
    return out


def romfs_stage(game_dir: Path, patched: Path, romfs: Path, workdir: Path, stamps: Path, info: dict) -> None:
    legacy_facade_names = ("mscorlib.dll", "System.IO.Packaging.dll", "System.Security.Permissions.dll")
    legacy_facades = {name: LEGACY / "hint52/aot-final/runtime-romfs" / name for name in legacy_facade_names}
    key = tree_hash(patched) + sha256(game_dir / "Terraria.exe") + "".join(sha256(p) for p in legacy_facades.values())
    stamp = stamps / "romfs.stamp"
    if stamp_ok(stamp, key) and romfs.exists():
        print("romfs: cached")
    else:
        if romfs.exists():
            shutil.rmtree(romfs)
        from scripts.pack_terraria_romfs import copy_game_payload
        rel_fw = workdir / "runtime-source/artifacts/bin/runtime/net9.0-libnx-Release-arm64"
        copy_game_payload(game_dir, romfs, tuple(), runtime_facades_dir=rel_fw)
        for source in sorted(patched.glob("*.dll")):
            shutil.copy2(source, romfs / source.name)
        shutil.copy2(patched / "Terraria.exe", romfs / "Terraria.exe")
        # These v88/v89s root facades are project/toolchain compatibility
        # overrides, not game code. They must shadow the net9 facade closure copied
        # above or metadata binding and runtime dependency resolution changes.
        for name, source in legacy_facades.items():
            shutil.copy2(source, romfs / name)
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
        return False
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
        for stale in ("runtime-metadata", "prepare-tool", "prepare-obj"):
            path = aot / stale
            if path.exists():
                shutil.rmtree(path)
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


def launcher_stage(cache_root: Path, profile: str, stamps: Path, info: dict | None = None) -> Path:
    out = cache_root / "launcher-src" / profile
    key = profile + sha256(ROOT / "scripts/launcher/build_launcher.py") + sha256(ROOT / "native/shared/nx_input.c")
    stamp = stamps / "launcher.stamp"
    if stamp_ok(stamp, key) and (out / "launcher-build.json").exists():
        print("launcher: cached")
    else:
        if out.exists(): shutil.rmtree(out)
        env = os.environ.copy(); env["TERRABUILDER_CACHE"] = str(cache_root)
        cmd = [sys.executable, str(ROOT / "scripts/launcher/build_launcher.py"), "--out", str(out), "--force"]
        if profile == "debug":
            cmd.append("--debug-diagnostics")
        elif profile == "profiler":
            cmd.append("--profiler")
        run(cmd, env=env)
        write_stamp(stamp, key)
    if info is not None:
        info["stages"]["launcher"] = json.loads((out / "launcher-build.json").read_text())
    return out


def native_stage(workdir: Path, variant: str, aot: Path, launcher: Path, profile: str, stamps: Path, info: dict, final_nro: Path) -> None:
    key = (
        tree_hash(aot / "runtime-romfs")
        + sha256(aot / "mono_aot_modules.h")
        + sha256(ROOT / "scripts/release_bcl/build_native.py")
        + sha256(launcher / "launcher-build.json")
        + profile
        + ",".join(profile_wraps(profile))
        + "openal-free"
    )
    stamp = stamps / "native.stamp"
    vdir = workdir / "release58" / variant
    if stamp_ok(stamp, key) and (vdir / "native/candidate/mono_nx_fna.nro").exists():
        print("native link: cached")
    else:
        native = vdir / "native"
        if native.exists(): shutil.rmtree(native)
        overrides = []
        for name in ["core.o","dl_shim.o","dl_shim_dotnet.o","dl_shim_libnx.o","dl_shim_stubs.o","heap.o","io_shims.o","io_util.o","ini.o","dl_shim_FAudio.o","dl_shim_FNA3D.o","dl_shim_SDL3.o","nx_input.o","dl_shim_SDL2.o","dl_shim_SDL2_image.o","dl_shim_opengl.o"]:
            p = launcher / "objects" / name
            if p.exists(): overrides.append(f"{name}={rel_to_mount(p, workdir, '/build')}")
        extra_objs = [launcher / "objects/nx_gpu_timing.o", launcher / "objects/nx_audio.o"]
        if profile == "profiler": extra_objs.append(launcher / "objects/nx_profiler.o")
        extra_flags = ["-L/build/gfx/v88-lib", *[rel_to_mount(p, workdir, "/build") for p in extra_objs], *["-Wl,--wrap=" + w for w in profile_wraps(profile)]]
        env = os.environ.copy()
        env.update({"R58_VARIANT": variant, "R58_RUNTIME": "release", "R58_SKIP_CONTROL": "1", "R58_OBJECT_OVERRIDES": " ".join(overrides), "R58_EXTRA_LDFLAGS": " ".join(extra_flags), "R58_TITLE": "Terraria", "R58_NACP_VERSION": "1.4.5.8"})
        if profile == "profiler":
            env["R58_MAIN_DEFINES"] = "-DMONO_NX_PROFILER=1"
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
    t = sub.add_parser("toolchain"); t.add_argument("--workdir", type=Path, default=DEFAULT_WORKDIR); t.add_argument("toolcmd", nargs="?", choices=["pack"]); t.add_argument("--from-source", action="store_true", help="unavailable: clean-checkout pipeline is incomplete"); t.add_argument("--bundle", help="unavailable: clean-workdir bundle import is incomplete"); t.set_defaults(func=toolchain)
    b = sub.add_parser("build"); b.add_argument("--workdir", type=Path, default=DEFAULT_WORKDIR); b.add_argument("--target", choices=["vanilla","tmodloader"]); b.add_argument("--game-dir", help="Vanilla game folder for --target vanilla; tModLoader 1.4.4.x folder for --target tmodloader"); b.add_argument("--mods", help="Comma-separated curated tModLoader mods, or 'none'"); b.add_argument("--out"); b.add_argument("--profile", choices=["release","debug","profiler"], default="release"); b.add_argument("--debug-diagnostics", action="store_const", const="debug", dest="profile", help="alias for --profile debug"); b.add_argument("-y", "--yes", action="store_true"); b.set_defaults(func=build)
    c = sub.add_parser("compare"); c.add_argument("--workdir", type=Path, default=DEFAULT_WORKDIR); c.set_defaults(func=compare)
    args = p.parse_args(argv)
    return args.func(args)

if __name__ == "__main__":
    raise SystemExit(main())
