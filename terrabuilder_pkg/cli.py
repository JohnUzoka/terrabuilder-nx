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
from pathlib import Path

from terrabuilder_pkg import toolchain as tclib

ROOT = Path(__file__).resolve().parents[1]
# Pre-toolchain build cache. Only the experimental tModLoader path still reads it.
LEGACY = Path.home() / ".cache/terraria-switch-build"
DEFAULT_WORKDIR = Path(os.environ.get("TERRABUILDER_WORKDIR", Path.home() / ".cache/terrabuilder"))
# Per user rather than per workdir: it keeps concurrent builds from exhausting memory.
HEAVY_LOCK = Path.home() / ".cache/terrabuilder/.heavy.lock"
MONO_IMAGE = "localhost/monobuild:local"
LLVM_IMAGE = "localhost/monobuild-llvm:local"
TMOD_LEGACY_INPUTS = (
    "tmod/tmod27/input/modset.json",
    "tmod/tmod27-nxfix-mp/tModLoader_patched_nxfix.dll",
    "release58/compile_main.sh",
)
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


def publish_file(src: Path, dst: Path) -> None:
    # Rename into place so a copy hardlinked to dst (e.g. staged with cp -l) keeps its contents.
    dst.parent.mkdir(parents=True, exist_ok=True)
    pending = dst.with_name(dst.name + ".pending")
    pending.unlink(missing_ok=True)
    shutil.copy2(src, pending)
    pending.replace(dst)


@contextlib.contextmanager
def file_lock(path: Path):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a") as lock_file:
        fcntl.flock(lock_file, fcntl.LOCK_EX)
        try:
            yield
        finally:
            fcntl.flock(lock_file, fcntl.LOCK_UN)


def tmod_variant_lock(variant: str):
    return file_lock(LEGACY / "tmod" / f".{variant}.lock")


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


def load_toolchain(workdir: Path) -> tclib.Toolchain:
    root = workdir / "toolchain"
    try:
        return tclib.load(root)
    except tclib.ToolchainError as error:
        raise SystemExit(
            f"{error}\nBuilds need a toolchain at {root}. Building one from source "
            "(toolchain --from-source) and importing a release bundle (toolchain --bundle) "
            "are not available yet; see docs/BUILDING.md."
        ) from error


def image_id(image: str) -> str:
    eng = engine()
    if not eng:
        raise SystemExit("podman or docker is required")
    try:
        return capture([eng, "image", "inspect", "--format", "{{.Id}}", image])
    except subprocess.CalledProcessError as error:
        raise SystemExit(f"container image {image} is missing; see docs/BUILDING.md") from error


def toolchain_summary(tc: tclib.Toolchain) -> dict:
    return {
        "root": str(tc.root),
        "origin": tc.manifest.get("origin"),
        "created_at": tc.manifest.get("created_at"),
        "manifest_sha256": sha256(tc.root / tclib.MANIFEST),
        "components": {name: entry["digest"] for name, entry in tc.manifest["components"].items()},
    }


def source_shas(*paths: Path) -> dict[str, str]:
    return {p.relative_to(ROOT).as_posix(): sha256(p) for p in paths}


def doctor(args: argparse.Namespace) -> int:
    workdir = args.workdir.expanduser().resolve()
    eng = engine()
    ok = bool(eng)
    print(f"container engine: {eng or 'missing'}")
    for image in (MONO_IMAGE, LLVM_IMAGE):
        present = bool(eng) and subprocess.run(
            [eng, "image", "inspect", image], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL
        ).returncode == 0
        print(f"image {image}: {'ok' if present else 'missing'}")
        ok &= present
    try:
        tc = tclib.load(workdir / "toolchain")
        print(f"toolchain {tc.root}: ok ({tc.manifest.get('origin')}, {tc.manifest.get('created_at')})")
    except tclib.ToolchainError as error:
        print(f"toolchain: {error}")
        ok = False
    probe = workdir
    while not probe.exists():
        probe = probe.parent
    usage = shutil.disk_usage(probe)
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
    missing = [rel for rel in TMOD_LEGACY_INPUTS if not (LEGACY / rel).exists()]
    print(f"tModLoader (experimental) inputs in {LEGACY}: "
          + ("ok" if not missing else "missing " + ", ".join(missing)))
    return 0 if ok else 1


def write_sd_runtime(sd_root: Path, tc: tclib.Toolchain) -> list[dict]:
    from scripts.pack_terraria_romfs import write_external_config
    config = write_external_config(sd_root)
    # Read from the SD card before RomFS is mounted, so every NRO needs it next to config.ini.
    icu_data = tc.path(tclib.ICU_DATA)
    icu = sd_root / "mono/etc" / icu_data.name
    icu.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(icu_data, icu)
    return file_manifest([config, icu], sd_root)


def file_manifest(paths: list[Path], base: Path) -> list[dict]:
    rows = []
    for p in paths:
        if p.is_file():
            rows.append({"path": p.relative_to(base).as_posix() if p.is_relative_to(base) else str(p), "bytes": p.stat().st_size, "sha256": sha256(p)})
    return rows


def toolchain_command(args: argparse.Namespace) -> int:
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
    workdir = args.workdir.expanduser().resolve()
    if args.toolcmd == "verify":
        root = workdir / "toolchain"
        try:
            problems = tclib.verify(root)
        except tclib.ToolchainError as error:
            problems = error.problems
        for problem in problems:
            print(problem)
        print(f"{root}: " + (f"{len(problems)} problem(s)" if problems else "ok"))
        return 1 if problems else 0
    tc = load_toolchain(workdir)
    print(tc.root)
    print(f"origin {tc.manifest.get('origin')}, created {tc.manifest.get('created_at')}")
    for name, entry in tc.manifest["components"].items():
        print(f"  {name:15} {entry['files']:6} files {entry['bytes'] / 2**20:9.1f} MiB  {entry['digest'][:16]}")
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


def build_curated_mod_sources(tmod_dir: Path, mods: list[str], workdir: Path, catalog: dict[str, dict], tc: tclib.Toolchain) -> Path:
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
               "-v", f"{tc.path('sdk')}:/mono-nx:ro",
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


def run_tmod_pipeline(variant: str, workdir: Path, tc: tclib.Toolchain) -> None:
    out = LEGACY / "tmod" / variant
    if (out / "native/candidate/tmodloader.nro").exists() and (out / "manifest.json").exists():
        print("tModLoader native build: cached")
        return
    eng = engine() or "podman"
    cmd = [eng, "run", "--rm", "--userns=keep-id", "--entrypoint", "python3",
           "-e", f"TMOD_VARIANT={variant}", "-e", f"TMPDIR=/build/tmod/{variant}/container-tmp",
           "-v", f"{LEGACY}:/build",
           "-v", f"{ROOT}:/work:ro",
           *tc.mounts(),
           LLVM_IMAGE, "/work/scripts/tmod/build_tmod_modset_nro.py"]
    run(cmd, heavy=True)
    verify = [sys.executable, str(ROOT / "scripts/tmod/verify_aot_dependencies.py"),
              "--cache-root", str(LEGACY), "--variant", variant, "--json-out", str(out / "aot-mvid-check.json")]
    run(verify)


def build_tmodloader(args: argparse.Namespace, tmod_dir: Path, mods: list[str], catalog: dict[str, dict]) -> int:
    workdir = args.workdir.expanduser().resolve()
    out_root = Path(args.out).expanduser().resolve() if args.out else workdir / "out"
    tmeta = validate_tmodloader(tmod_dir)
    tc = load_toolchain(workdir)
    variant = tmod_variant_key(tmod_dir, mods, catalog, args.profile)
    stamps = workdir / "stamps" / variant
    launcher_dir = launcher_stage(LEGACY, LEGACY / "launcher-src" / args.profile, tc, args.profile, stamps / "launcher.json")
    packages = build_curated_mod_sources(tmod_dir, mods, workdir, catalog, tc) if mods else None
    with tmod_variant_lock(variant):
        seed_tmod_input(variant, mods, packages, catalog, args.profile, launcher_dir)
        run_tmod_pipeline(variant, workdir, tc)
        src_root = LEGACY / "tmod" / variant
        out_dir = out_root / ("tmodloader-" + ("no-mods" if not mods else "-".join(mods).lower()))
        out_dir.mkdir(parents=True, exist_ok=True)
        nro = out_dir / "tmodloader.nro"
        publish_file(src_root / "native/candidate/tmodloader.nro", nro)
        sd_out = out_dir / "sdcard"
        if sd_out.exists():
            shutil.rmtree(sd_out)
        shutil.copytree(src_root / "sdcard", sd_out)
        sd_runtime = write_sd_runtime(sd_out, tc)
        manifest = json.loads((src_root / "manifest.json").read_text())
        mvid = json.loads((src_root / "aot-mvid-check.json").read_text())
        receipt = {"target": "tmodloader", "experimental": True, "variant": variant, "input": tmeta,
                   "mods": [catalog[m] for m in mods], "nro": {"path": str(nro), "sha256": sha256(nro), "bytes": nro.stat().st_size},
                   "sd_payload": str(sd_out), "sd_runtime": sd_runtime, "aot_mvid_check": mvid, "manifest": manifest,
                   "toolchain": toolchain_summary(tc)}
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
    tc = load_toolchain(workdir)
    meta = validate_game(game_dir)
    suffix = "" if profile == "release" else f"-{profile}"
    receipt = out_root / f"Terraria{suffix}.receipt.json"
    final_nro = out_root / f"Terraria{suffix}.nro"
    stamps = workdir / "stamps" / "vanilla"
    info: dict[str, object] = {"input": meta, "stages": {}}
    with file_lock(workdir / ".vanilla.lock"):
        patched = patch_stage(game_dir, workdir, tc, stamps, info)
        romfs_hash = romfs_stage(game_dir, patched, workdir, tc, stamps, info)
        aot = aot_stage(romfs_hash, workdir, tc, stamps, info)
        launcher = launcher_stage(workdir, workdir / "vanilla" / f"launcher-{profile}", tc, profile,
                                  stamps / f"launcher-{profile}.json", info)
        native = native_stage(workdir, romfs_hash, aot, launcher, tc, profile, stamps, info)
        out_root.mkdir(parents=True, exist_ok=True)
        publish_file(native / "mono_nx_fna.nro", final_nro)
        sd_out = out_root / "sdcard"
        if sd_out.exists():
            shutil.rmtree(sd_out)
        info["nro"] = {"path": str(final_nro), "sha256": sha256(final_nro), "bytes": final_nro.stat().st_size}
        info["sd_payload"] = str(sd_out)
        info["sd_runtime"] = write_sd_runtime(sd_out, tc)
        info["toolchain"] = toolchain_summary(tc)
        write_json(receipt, info)
    print(f"Built {final_nro} {info['nro']['sha256']}")
    print(f"Copy {final_nro} to sd:/switch/")
    print(f"Copy contents of {sd_out}/ to the SD card root, overwriting existing files")
    return 0


def stage_cached(name: str, stamp: Path, key: dict, outputs: list[Path]) -> bool:
    """True when the stamp records exactly this key and the outputs exist.

    Otherwise the stamp is removed before the stage rebuilds, so an interrupted
    rebuild is never mistaken for a finished one.
    """
    try:
        recorded = json.loads(stamp.read_text())
    except (OSError, ValueError):
        recorded = None
    if recorded == key and all(p.exists() for p in outputs):
        print(f"{name}: cached")
        return True
    if recorded == key:
        reason = "outputs missing"
    elif isinstance(recorded, dict):
        reason = "changed: " + ", ".join(sorted(k for k in key.keys() | recorded.keys() if key.get(k) != recorded.get(k)))
    else:
        reason = "no previous build"
    print(f"{name}: building ({reason})")
    stamp.unlink(missing_ok=True)
    return False


def write_stamp(stamp: Path, key: dict) -> None:
    write_json(stamp, key)


def patch_stage(game_dir: Path, workdir: Path, tc: tclib.Toolchain, stamps: Path, info: dict) -> Path:
    sources = [
        *sorted((ROOT / "scripts/patch_vanilla").glob("*.cs")),
        ROOT / "scripts/patch_vanilla/PatchVanilla.csproj",
        ROOT / "scripts/patch_vanilla/run.py",
        *sorted((ROOT / "managed/nx_crypto").glob("*.cs*")),
        *sorted((ROOT / "managed/nx_input_diag").glob("*.cs*")),
    ]
    key = {
        "Terraria.exe": sha256(game_dir / "Terraria.exe"),
        "FNA.dll": sha256(game_dir / "FNA.dll"),
        "sources": source_shas(*sources),
        "toolchain": tc.digests("cecil", "dotnet"),
        "image": image_id(MONO_IMAGE),
    }
    out = workdir / "patch" / hashlib.sha256(json.dumps(key, sort_keys=True).encode()).hexdigest()[:16]
    stamp = stamps / "patch.json"
    if not stage_cached("patch managed", stamp, key, [out / "Terraria.exe"]):
        run([sys.executable, str(ROOT / "scripts/patch_vanilla/run.py"), str(game_dir), "--out", str(out),
             "--workdir", str(workdir), "--toolchain", str(tc.root)], heavy=True)
        write_stamp(stamp, key)
    info["stages"]["patch_managed"] = {p.name: sha256(p) for p in sorted(out.glob("*.dll"))}
    info["stages"]["patch_managed"]["Terraria.exe"] = sha256(out / "Terraria.exe")
    return out


def romfs_stage(game_dir: Path, patched: Path, workdir: Path, tc: tclib.Toolchain, stamps: Path, info: dict) -> str:
    """Assemble <workdir>/vanilla/romfs and return its tree hash."""
    romfs = workdir / "vanilla" / "romfs"
    controller_db = ROOT / "native/gamecontrollerdb.txt"
    from scripts.pack_terraria_romfs import assemble_cli_romfs, game_payload_files
    key = {
        "patched": tree_hash(patched),
        "game": tree_hash(game_dir, game_payload_files(game_dir)),
        "sources": source_shas(ROOT / "scripts/pack_terraria_romfs.py", controller_db),
        "toolchain": tc.digests("runtime", "facades"),
    }
    stamp = stamps / "romfs.json"
    if not stage_cached("romfs", stamp, key, [romfs / "Terraria.exe"]):
        assemble_cli_romfs(game_dir, patched, romfs, tc.path("runtime/framework"),
                           [tc.path("facades") / name for name in tclib.COMPONENTS["facades"]],
                           tc.path("runtime/corelib/System.Private.CoreLib.dll"), controller_db)
        write_stamp(stamp, key)
    romfs_hash = tree_hash(romfs)
    info["stages"]["romfs"] = {"file_count": sum(1 for p in romfs.rglob("*") if p.is_file()), "hash": romfs_hash}
    return romfs_hash


def tree_hash(root: Path, files: list[Path] | None = None) -> str:
    h = hashlib.sha256()
    for p in sorted(x for x in root.rglob("*") if x.is_file()) if files is None else files:
        h.update(p.relative_to(root).as_posix().encode()+b"\0")
        h.update(sha256(p).encode()+b"\0")
    return h.hexdigest()


def container_mounts(workdir: Path, tc: tclib.Toolchain) -> list[str]:
    return ["-v", f"{workdir}:/build", "-v", f"{ROOT}:/work:ro", *tc.mounts()]


def aot_stage(romfs_hash: str, workdir: Path, tc: tclib.Toolchain, stamps: Path, info: dict) -> Path:
    aot = workdir / "vanilla" / "aot"
    key = {
        "romfs": romfs_hash,
        "sources": source_shas(ROOT / "scripts/compile_terraria_aot.py", ROOT / "scripts/prepare_aot/PrepareAot.cs",
                               ROOT / "scripts/prepare_aot/PrepareAot.csproj"),
        "toolchain": tc.digests("sdk", "runtime", "aot-compiler", "framework-aot", "cecil", "dotnet"),
        "image": image_id(LLVM_IMAGE),
        "llvm_modules": ["Terraria", "FNA"],
    }
    stamp = stamps / "aot.json"
    if not stage_cached("aot", stamp, key, [aot / "build-manifest.json"]):
        if aot.exists():
            shutil.rmtree(aot)
        aot.mkdir(parents=True)
        cmd = [engine() or "podman", "run", "--rm", "--userns=keep-id", "--entrypoint", "/usr/bin/python3",
               "-e", "PATH=/opt/devkitpro/devkitA64/bin:/opt/devkitpro/tools/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
               *container_mounts(workdir, tc), LLVM_IMAGE,
               "/work/scripts/compile_terraria_aot.py", "--game-dir", "/build/vanilla/romfs", "--output-dir", "/build/vanilla/aot",
               "--corelib-dir", "/toolchain/runtime/corelib", "--runtime-dir", "/toolchain/runtime/framework",
               "--cross-compiler", "/mono-nx/dotnet_runtime/artifacts/bin/mono/linux.x64.Debug/cross/linux-x64/libnx-arm64/mono-aot-cross",
               "--llvm-compiler-dir", "/toolchain/aot-compiler",
               "--cecil-path", "/toolchain/cecil/Mono.Cecil.dll", "--dotnet", "/toolchain/dotnet/dotnet",
               "--framework-aot", "/toolchain/framework-aot",
               *(arg for module in key["llvm_modules"] for arg in ("--llvm-module", module)), "--jobs", "3"]
        run(cmd, heavy=True)
        write_stamp(stamp, key)
    manifest = json.loads((aot / "build-manifest.json").read_text())
    info["stages"]["aot"] = {"modules": {m["assembly"]["name"]: [m.get("compiled_methods"), m.get("total_methods")] for m in [manifest["corelib"], *manifest["modules"]]}}
    return aot


def launcher_stage(workdir: Path, out: Path, tc: tclib.Toolchain, profile: str, stamp: Path, info: dict | None = None) -> Path:
    """Build the launcher objects into `out`, which must be under `workdir` (mounted as /build)."""
    key = {
        "profile": profile,
        "sources": source_shas(ROOT / "scripts/launcher/build_launcher.py"),
        "native": tree_hash(ROOT / "native"),
        "toolchain": tc.digests("sdk", "mono-nx-native", "native-deps"),
        "image": image_id(MONO_IMAGE),
    }
    if not stage_cached(f"launcher ({profile})", stamp, key, [out / "launcher-build.json"]):
        cmd = [sys.executable, str(ROOT / "scripts/launcher/build_launcher.py"), "--workdir", str(workdir),
               "--toolchain", str(tc.root), "--out", str(out), "--force"]
        if profile == "debug":
            cmd.append("--debug-diagnostics")
        elif profile == "profiler":
            cmd.append("--profiler")
        run(cmd)
        write_stamp(stamp, key)
    if info is not None:
        info["stages"]["launcher"] = json.loads((out / "launcher-build.json").read_text())
    return out


def native_stage(workdir: Path, romfs_hash: str, aot: Path, launcher: Path, tc: tclib.Toolchain, profile: str, stamps: Path, info: dict) -> Path:
    out = workdir / "vanilla" / f"native-{profile}"
    wraps = profile_wraps(profile)
    defines = ["MONO_NX_PROFILER=1"] if profile == "profiler" else []
    key = {
        "romfs": romfs_hash,
        "aot": {name: sha256(aot / name) for name in ("build-manifest.json", "mono_aot_modules.h")},
        "launcher": sha256(launcher / "launcher-build.json"),
        "native": tree_hash(ROOT / "native"),
        "sources": source_shas(ROOT / "scripts/native/link_nro.py", ROOT / "scripts/native/compile_main.sh"),
        "profile": profile,
        "wraps": wraps,
        "defines": defines,
        "toolchain": tc.digests("sdk", "mono-nx-native", "runtime", "mesa", "native-deps", "framework-aot"),
        "image": image_id(LLVM_IMAGE),
    }
    stamp = stamps / f"native-{profile}.json"
    if not stage_cached(f"native link ({profile})", stamp, key, [out / "mono_nx_fna.nro"]):
        if out.exists():
            shutil.rmtree(out)
        cmd = [engine() or "podman", "run", "--rm", "--userns=keep-id", "--entrypoint", "/usr/bin/python3",
               *container_mounts(workdir, tc), LLVM_IMAGE, "/work/scripts/native/link_nro.py",
               "--aot-manifest", "/build/vanilla/aot/build-manifest.json", "--romfs", "/build/vanilla/romfs",
               "--launcher", "/build/" + launcher.relative_to(workdir).as_posix(),
               "--out", "/build/" + out.relative_to(workdir).as_posix(),
               *(f"--wrap={w}" for w in wraps), *(f"--main-define={d}" for d in defines)]
        run(cmd, heavy=True)
        write_stamp(stamp, key)
    info["stages"]["native"] = json.loads((out / "native-build.json").read_text())
    return out


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
    t = sub.add_parser("toolchain"); t.add_argument("--workdir", type=Path, default=DEFAULT_WORKDIR); t.add_argument("toolcmd", nargs="?", choices=["status", "verify"], default="status"); t.add_argument("--from-source", action="store_true", help="unavailable: clean-checkout pipeline is incomplete"); t.add_argument("--bundle", help="unavailable: clean-workdir bundle import is incomplete"); t.set_defaults(func=toolchain_command)
    b = sub.add_parser("build"); b.add_argument("--workdir", type=Path, default=DEFAULT_WORKDIR); b.add_argument("--target", choices=["vanilla","tmodloader"]); b.add_argument("--game-dir", help="Vanilla game folder for --target vanilla; tModLoader 1.4.4.x folder for --target tmodloader"); b.add_argument("--mods", help="Comma-separated curated tModLoader mods, or 'none'"); b.add_argument("--out"); b.add_argument("--profile", choices=["release","debug","profiler"], default="release"); b.add_argument("--debug-diagnostics", action="store_const", const="debug", dest="profile", help="alias for --profile debug"); b.add_argument("-y", "--yes", action="store_true"); b.set_defaults(func=build)
    c = sub.add_parser("compare"); c.add_argument("--workdir", type=Path, default=DEFAULT_WORKDIR); c.set_defaults(func=compare)
    args = p.parse_args(argv)
    return args.func(args)

if __name__ == "__main__":
    raise SystemExit(main())
