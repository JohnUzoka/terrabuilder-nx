#!/usr/bin/env python3
from __future__ import annotations

import argparse
import calendar
import contextlib
import fcntl
import fnmatch
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import time
import zipfile
from pathlib import Path, PurePosixPath

from terrabuilder_pkg import toolchain as tclib
from terrabuilder_pkg.containers import (BUILD_IMAGE, BUILD_IMAGE_TAG, BUILD_WORKDIR, engine, engine_status,
                                         root_is_invoking_user, user_args)

ROOT = Path(__file__).resolve().parents[1]
# Source compatibility patches applied to curated mod checkouts (catalog "patches").
RECORDED_PATCHES = Path(__file__).with_name("patches")
DEFAULT_WORKDIR = Path(os.environ.get("TERRABUILDER_WORKDIR", Path.home() / ".cache/terrabuilder"))
# Finished builds: <out>/<Terraria|tModLoader>[-profile]/sdcard mirrors the SD card root.
DEFAULT_OUT = ROOT / "Output"
# The user's Terraria install: vanilla builds use its game, tModLoader builds its Content/.
DEFAULT_TERRARIA = ROOT / "Terraria"
# Per user rather than per workdir: it keeps concurrent builds from exhausting memory.
HEAVY_LOCK = Path.home() / ".cache/terrabuilder/.heavy.lock"
# The prebuilt toolchain, a release asset written by `toolchain pack`. `toolchain --bundle`
# downloads it, checks this SHA-256 and unpacks it as <workdir>/toolchain.
TOOLCHAIN_BUNDLE = {
    "url": "https://github.com/JohnUzoka/terrabuilder-nx/releases/download/toolchain-2026-11-01/"
           "terrabuilder-toolchain-2026-11-01.tar.xz",
    "sha256": "952ef74b0e346e635c10bf58dde64ab66235eeb78ce1dc670d06dfc749802a87",
    "bytes": 287313340,
}
TMOD_MESA_LIB = f"{tclib.MOUNT}/mesa/lib"
# The tModLoader release the port is built from (the GitHub zip, 941 files).
TMOD_RELEASE = {
    "version": "2026.7.3.0",
    "url": "https://github.com/tModLoader/tModLoader/releases/download/v2026.07.3.0/tModLoader.zip",
    "zip_sha256": "6f51610f4b0f167d1620d0e8c83264ba14e047421e97d9f8053eb40e3d764e1a",
    "dll_sha256": "d530e508b2841e66d880ce279a609624b5ab66ce8093eedfa04f47c3d12d485c",
}
HOST_FNA3D = {
    "repo": "https://github.com/FNA-XNA/FNA3D.git",
    "tag": "26.07",
    "commit": "1ac4231ed9f0cfa1211e46b27dc8fb4ef3830eb8",
    "mojoshader_commit": "abdc80360c1d4560ab8f356035dcd53ae6e9b87f",
}
# The FNA the tModLoader patch chain (scripts/tmod/base_dlls.py) makes from the pinned release.
TMOD_SHARED_AUDIO_FNA_SHA256 = "a24a7545293b5b2d351544ab4a7fa3f5f739e00df02714b3c5a3c352b0767678"
# Terraria releases whose Content/ tModLoader builds accept (tModLoader 2026.7 targets 1.4.4.9;
# 1.4.5 Content loads with it too).
TMOD_CONTENT_VERSIONS = re.compile(r"1\.4\.4\.9|1\.4\.5\.\d+")
# The dotnet_runtime fork commit the toolchain's tmod-runtime libraries were built from
# (scripts/tmod/runtime/README.md).
TMOD_RUNTIME_COMMIT = "1951b2f6811085f8d7fa5977feb67feb4954c0ab"
# Baseline mod-set configuration for build_tmod_modset_nro.py; every tModLoader build starts from it.
TMOD_BASE_MODSET = ROOT / "scripts/tmod/base_modset.json"
# Decoders tModLoader loads mod audio with, and the save encryption: AOT-compiled with every set.
# Interpreted, each player or achievement save stalls 1-2 s in NxCrypto.
TMOD_EXTRA_AOT = ("NxCrypto.dll", "NVorbis.dll", "MP3Sharp.dll")
# MONO_INLINELIMIT for every LLVM module a mod build compiles (Mono's LLVM AOT default is 30).
# In run 41, out-of-line managed callees of 30-59 IL bytes held 4.4% of the game thread.
TMOD_AOT_INLINE_LIMIT = 60

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
- In-world Fargo's Souls ran at a median of about 55 fps on hardware (v16, run 42); with all 11 mods, v17 averaged 33 fps in-world (run 44).
- The D-pad does not navigate the inventory.
- Multiplayer is untested on tModLoader.
- Open-source mods are built locally from pinned upstream source and .tmod files from Mods/ are used as they are; several mods publish no license, and untested ones have not run on a Switch.
- Each NRO runs only the mods it was built with: copy the whole sdcard/ folder of one build, and do not turn on other mods in the Mods menu.
- Text boxes open the Switch keyboard when they take input (not on the mod list, mod browser or achievements filters); mod menus that read the raw mouse need touch.
- tModLoader leaves mod hotkeys unassigned: set them in Settings > Controls (Keyboard or Gamepad tab)."""

# Set by the guided build (installer.py): command output goes to its log file and progress line
# instead of the terminal. It provides run(cmd, cwd, env), say(text) and phase(title).
OUTPUT = None


def say(text: str) -> None:
    if OUTPUT is None:
        print(text, flush=True)
    else:
        OUTPUT.say(text)


def phase(title: str) -> None:
    if OUTPUT is None:
        print(f"== {title}", flush=True)
    else:
        OUTPUT.phase(title)


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


def run(cmd: list[str], *, cwd: Path = ROOT, env: dict[str, str] | None = None, heavy: bool = False) -> None:
    actual = [str(part) for part in cmd]
    if heavy:
        HEAVY_LOCK.parent.mkdir(parents=True, exist_ok=True)
        actual = ["flock", str(HEAVY_LOCK), *actual]
    if OUTPUT is not None:
        OUTPUT.run(actual, cwd=cwd, env=env)
        return
    print("+", " ".join(actual), flush=True)
    subprocess.run(actual, cwd=cwd, env=env, check=True)


def capture(cmd: list[str]) -> str:
    return subprocess.check_output(cmd, text=True, stderr=subprocess.STDOUT).strip()


def load_toolchain(workdir: Path) -> tclib.Toolchain:
    root = workdir / "toolchain"
    try:
        return tclib.load(root)
    except tclib.ToolchainError as error:
        raise SystemExit(
            f"{error}\nBuilds need a toolchain at {root}. Download the prebuilt one with "
            "`toolchain --bundle`, or build it from source with `toolchain --from-source --replace` "
            "(hours; see docs/BUILDING.md)."
        ) from error


def image_id(image: str) -> str:
    eng = engine()
    if not eng:
        raise SystemExit("podman or docker is required")
    try:
        return capture([eng, "image", "inspect", "--format", "{{.Id}}", image])
    except subprocess.CalledProcessError as error:
        problem = engine_status(eng)[0]
        raise SystemExit(f"the container image can't be used: {problem}" if problem else
                         f"container image {image} is missing: `./terrabuilder toolchain --bundle` downloads it") from error


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


def machine_checks(workdir: Path) -> list[dict]:
    """Build prerequisites as {name, ok, detail, required}; a failed required check blocks every build."""
    checks = []
    eng = engine()
    problem, report = engine_status(eng) if eng else (
        "missing: install Docker (on Windows: Docker Desktop with WSL integration) or Podman", "")
    # `report` is the engine's own `info` output: the guided build saves it in its check log.
    checks.append({"name": "container engine", "ok": not problem, "detail": problem or eng, "required": True,
                   "report": report})
    present = not problem and subprocess.run(
        [eng, "image", "inspect", BUILD_IMAGE], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL
    ).returncode == 0
    detail = ("not checked: the container engine isn't working" if problem else
              "ok" if present else "missing: `./terrabuilder toolchain --bundle` downloads it")
    checks.append({"name": f"image {BUILD_IMAGE_TAG}", "ok": present, "required": True, "detail": detail})
    tc = None
    try:
        tc = tclib.load(workdir / "toolchain")
        checks.append({"name": "toolchain", "ok": True, "required": True,
                       "detail": f"{tc.root} ({tc.manifest.get('origin')}, {tc.manifest.get('created_at')})"})
    except tclib.ToolchainError as error:
        checks.append({"name": "toolchain", "ok": False, "required": True,
                       "detail": f"{error}; `./terrabuilder toolchain --bundle` downloads it"})
    probe = workdir
    while not probe.exists():
        probe = probe.parent
    usage = shutil.disk_usage(probe)
    checks.append({"name": "disk free", "ok": usage.free > 15 * 1024**3,
                   "detail": f"{usage.free // (1024**3)} GiB (15 GiB needed)", "required": True})
    mem_kb = 0
    try:
        for line in Path("/proc/meminfo").read_text().splitlines():
            if line.startswith("MemTotal:"):
                mem_kb = int(line.split()[1])
                break
    except OSError:
        pass
    checks.append({"name": "RAM", "ok": True, "detail": f"{mem_kb // (1024**2)} GiB", "required": False})
    tmod_runtime = tc is not None and tc.has("tmod-runtime")
    checks.append({"name": "tModLoader runtime", "ok": tmod_runtime, "required": False,
                   "detail": "toolchain component tmod-runtime" if tmod_runtime else
                             "missing: `./terrabuilder toolchain --bundle --replace` installs the prebuilt "
                             "toolchain, which has it"})
    return checks


def doctor(args: argparse.Namespace) -> int:
    checks = machine_checks(args.workdir.expanduser().resolve())
    for check in checks:
        print(f"{check['name']}: {check['detail']}")
    return 0 if all(check["ok"] for check in checks if check["required"]) else 1


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


def swap_toolchain(new: Path, live: Path) -> None:
    """Move `new` into place as `live`, keeping the previous toolchain (if any) as <live>.bak."""
    if live.exists() or live.is_symlink():
        backup = live.with_name(live.name + ".bak")
        if backup.is_symlink() or backup.is_file():
            backup.unlink()
        elif backup.exists():
            shutil.rmtree(backup)
        live.rename(backup)
        say(f"previous toolchain kept at {backup}")
    new.rename(live)


def pack_toolchain(root: Path, out_dir: Path) -> Path:
    """Write the verified toolchain at `root` as the release asset `toolchain --bundle` installs: an
    xz-compressed tar of toolchain/ holding exactly the files and empty directories its manifest records."""
    problems = tclib.verify(root)
    if problems:
        raise SystemExit(f"{root} does not verify ({len(problems)} problem(s), first: {problems[0]}); "
                         "see `toolchain verify`")
    manifest = json.loads((root / tclib.MANIFEST).read_text())
    files = {tclib.MANIFEST: {"exec": False}, **manifest["files"]}
    dirs = set(manifest["empty_dirs"])
    for rel in [*files, *manifest["empty_dirs"]]:
        dirs.update(parent.as_posix() for parent in PurePosixPath(rel).parents)
    dirs.discard(".")
    mtime = calendar.timegm(time.strptime(manifest["created_at"], "%Y-%m-%dT%H:%M:%SZ"))
    out_dir.mkdir(parents=True, exist_ok=True)
    out = out_dir / f"terrabuilder-toolchain-{manifest['created_at'][:10]}.tar.xz"
    partial = out.with_name(out.name + ".partial")
    with partial.open("wb") as sink:
        xz = subprocess.Popen(["xz", "-T0", "-6", "-c"], stdin=subprocess.PIPE, stdout=sink)
        with tarfile.open(fileobj=xz.stdin, mode="w|", format=tarfile.GNU_FORMAT) as tar:
            for rel in sorted(dirs.union(files)):
                info = tarfile.TarInfo(f"toolchain/{rel}")
                info.mtime = mtime
                entry = files.get(rel)
                if entry is None:
                    info.type, info.mode = tarfile.DIRTYPE, 0o755
                    tar.addfile(info)
                elif "symlink" in entry:
                    info.type, info.mode, info.linkname = tarfile.SYMTYPE, 0o777, entry["symlink"]
                    tar.addfile(info)
                else:
                    info.mode = 0o755 if entry["exec"] else 0o644
                    with (root / rel).open("rb") as stream:
                        info.size = os.fstat(stream.fileno()).st_size
                        tar.addfile(info, stream)
        xz.stdin.close()
        if xz.wait():
            raise SystemExit(f"xz failed while writing {partial}")
    partial.replace(out)
    return out


def install_toolchain_bundle(workdir: Path, archive: Path | None = None, *, replace: bool = False) -> None:
    """Make <workdir>/toolchain the pinned prebuilt toolchain, unpacked from `archive` (a local copy of
    the release asset) or from a download. A usable toolchain stays in place unless `replace`."""
    live = workdir / "toolchain"
    if not replace:
        try:
            tclib.load(live)
        except tclib.ToolchainError:
            pass
        else:
            say(f"keeping the toolchain at {live}; add --replace to swap in the prebuilt one")
            return
    workdir.mkdir(parents=True, exist_ok=True)
    name = TOOLCHAIN_BUNDLE["url"].rsplit("/", 1)[1]
    if archive is None:
        phase(f"Downloading the toolchain ({TOOLCHAIN_BUNDLE['bytes'] / 2**20:.0f} MiB)")
        archive = download_pinned(TOOLCHAIN_BUNDLE["url"], TOOLCHAIN_BUNDLE["sha256"], workdir / "downloads" / name)
    elif sha256(archive) != TOOLCHAIN_BUNDLE["sha256"]:
        raise SystemExit(f"{archive} is not the pinned toolchain bundle {name}: its SHA-256 differs")
    phase("Unpacking the toolchain")
    staging = workdir / "toolchain.bundle"
    shutil.rmtree(staging, ignore_errors=True)
    with tarfile.open(archive, "r:xz") as bundle:
        bundle.extractall(staging, filter="data")
    problems = tclib.verify(staging / "toolchain")
    if problems:
        raise SystemExit(f"the unpacked toolchain in {staging} does not verify: " + "; ".join(problems[:3]))
    swap_toolchain(staging / "toolchain", live)
    staging.rmdir()
    say(f"toolchain ready at {live}")


def set_up_from_bundle(workdir: Path, archive: Path | None = None, *, replace: bool = False) -> None:
    """The prebuilt route to a machine that can build: the toolchain bundle plus devkitPro's build image."""
    install_toolchain_bundle(workdir, archive, replace=replace)
    phase("Downloading the build image")
    eng = engine()
    problem = engine_status(eng)[0] if eng else "Docker or Podman is not installed"
    if problem:
        raise SystemExit(f"the build image can't be downloaded: {problem}")
    if subprocess.run([eng, "image", "inspect", BUILD_IMAGE], stdout=subprocess.DEVNULL,
                      stderr=subprocess.DEVNULL).returncode == 0:
        say(f"build image {BUILD_IMAGE_TAG} already present")
        return
    run([eng, "pull", BUILD_IMAGE])


def toolchain_command(args: argparse.Namespace) -> int:
    workdir = args.workdir.expanduser().resolve()
    live = workdir / "toolchain"
    if args.from_source:
        script = ROOT / "scripts/toolchain/build_from_source.py"
        cmd = [sys.executable, str(script), "--workdir", str(workdir)]
        # build_from_source.py takes its own ~/.cache/terrabuilder/.heavy.lock per heavy step;
        # don't also wrap the whole multi-hour call in run(..., heavy=True), which would
        # deadlock (the outer flock would never release for the inner ones to acquire).
        run(cmd)
        final = workdir / "toolchain.fromsource"
        if args.replace:
            swap_toolchain(final, live)
            print(f"replaced {live}")
        else:
            print(f"built and verified at {final}; rerun with --replace to swap it into {live}")
        return 0
    if args.bundle is not None:
        set_up_from_bundle(workdir, Path(args.bundle).expanduser().resolve() if args.bundle else None,
                           replace=args.replace)
        return 0
    if args.toolcmd == "pack":
        out = pack_toolchain(live, args.out.expanduser().resolve())
        print(f"{out}\n  bytes  {out.stat().st_size}\n  sha256 {sha256(out)}")
        return 0
    if args.toolcmd == "verify":
        try:
            problems = tclib.verify(live)
        except tclib.ToolchainError as error:
            problems = error.problems
        for problem in problems:
            print(problem)
        print(f"{live}: " + (f"{len(problems)} problem(s)" if problems else "ok"))
        return 1 if problems else 0
    tc = load_toolchain(workdir)
    print(tc.root)
    print(f"origin {tc.manifest.get('origin')}, created {tc.manifest.get('created_at')}")
    for name, entry in tc.manifest["components"].items():
        print(f"  {name:15} {entry['files']:6} files {entry['bytes'] / 2**20:9.1f} MiB  {entry['digest'][:16]}")
    return 0


# Clean GOG Terraria 1.4.5.8, the only input scripts/patch_vanilla accepts.
GOG_1458 = {
    "Terraria.exe": "6bba49a535bc9dc2ade62d6b6f0bc0c755223bed60796af51f5024a3c87c43ce",
    "FNA.dll": "46d201f59ce6c3827414b2c2c5141c218acabda605b02e564d73ca2efb2a3171",
}


def validate_game(game: Path) -> dict:
    required = ["Terraria.exe", "FNA.dll", "Content"]
    missing = [n for n in required if not (game / n).exists()]
    if missing:
        if missing == ["FNA.dll"]:
            raise SystemExit(f"{game} has no FNA.dll: it is the Windows (XNA) version of Terraria, such as Steam's. "
                             "Vanilla builds need GOG Terraria 1.4.5.8 for Linux; tModLoader builds can use this "
                             "folder (build --target tmodloader --terraria DIR).")
        raise SystemExit("game directory missing: " + ", ".join(missing))
    terraria_sha = sha256(game / "Terraria.exe")
    fna_sha = sha256(game / "FNA.dll")
    if terraria_sha != GOG_1458["Terraria.exe"] or fna_sha != GOG_1458["FNA.dll"]:
        raise SystemExit(f"{game} is not clean GOG Terraria 1.4.5.8 "
                         f"(Terraria.exe {terraria_sha[:16]}, FNA.dll {fna_sha[:16]})")
    return {"terraria_sha256": terraria_sha, "fna_sha256": fna_sha, "version": "1.4.5.8"}


def search(folder: Path, pattern: str, depth: int = 3) -> list[Path]:
    """Files matching `pattern` at most `depth` path parts below `folder`. Symlinked folders are
    followed, so a linked game or Workshop folder works like a copied one."""
    found: list[Path] = []
    if not folder.is_dir():
        return found
    for current, folders, files in os.walk(folder, followlinks=True):
        here = Path(current)
        if len(here.relative_to(folder).parts) >= depth - 1:
            folders.clear()
        found += [here / name for name in files if fnmatch.fnmatch(name, pattern)]
    return sorted(found)


def game_version(game: Path) -> str:
    changelog = game / "changelog.txt"
    if changelog.is_file():
        match = re.search(r"Version (1\.\d+\.\d+(?:\.\d+)?)", changelog.read_text(errors="replace")[:4000])
        if match:
            return match.group(1)
    gameinfo = game.parent / "gameinfo"
    if gameinfo.is_file():
        match = re.search(r"v(1\.\d+\.\d+(?:\.\d+)?)", gameinfo.read_text(errors="replace"))
        if match:
            return match.group(1)
    return "of unknown version"


def find_tmod_content(folder: Path) -> dict | None:
    """The Terraria install under `folder` (searched like the guided build's game folder) whose
    Content/ tModLoader builds use: GOG's Linux game folder or a copy of Steam's Terraria folder,
    version 1.4.4.9 or 1.4.5.x. Returns {"game", "content", "version"} or None."""
    for exe in search(folder, "Terraria.exe"):
        game = exe.parent
        version = game_version(game)
        if (game / "Content").is_dir() and TMOD_CONTENT_VERSIONS.fullmatch(version):
            return {"game": game, "content": game / "Content", "version": version}
    return None


def tmod_content_source(folder: Path) -> dict:
    found = find_tmod_content(folder)
    if found is None:
        raise SystemExit(f"no Terraria 1.4.4.9 or 1.4.5.x install with a Content folder under {folder}: tModLoader "
                         "builds use its Content/ (GOG's Linux game folder or a copy of Steam's Terraria folder)")
    return found


def tmodloader_release_version(dll: Path) -> str | None:
    """The release in tModLoader.dll's informational version (1.4.4.9+2026.08.3.0 -> 2026.8.3.0)."""
    match = re.search(rb"1\.4\.4\.\d+\+(\d{4})\.(\d{2})\.(\d+)\.(\d+)", dll.read_bytes())
    return ".".join(str(int(part)) for part in match.groups()) if match else None


def validate_tmodloader(tmod_dir: Path) -> dict:
    required = ["tModLoader.dll", "tModLoader.deps.json", "tMLMod.targets", "Content"]
    missing = [n for n in required if not (tmod_dir / n).exists()]
    if missing:
        raise SystemExit("tModLoader directory missing: " + ", ".join(missing))
    dll_sha = sha256(tmod_dir / "tModLoader.dll")
    if dll_sha != TMOD_RELEASE["dll_sha256"]:
        found = tmodloader_release_version(tmod_dir / "tModLoader.dll") or "an unknown release"
        raise SystemExit(f"{tmod_dir} has tModLoader {found}; Switch builds use tModLoader "
                         f"{TMOD_RELEASE['version']}. Leave out --game-dir to download it.")
    return {"tmodloader_sha256": dll_sha, "version": TMOD_RELEASE["version"]}


def download_pinned(url: str, expected_sha256: str, dest: Path) -> Path:
    """`dest`, downloaded from `url` unless a copy matching `expected_sha256` is already there."""
    if dest.is_file() and sha256(dest) == expected_sha256:
        return dest
    dest.parent.mkdir(parents=True, exist_ok=True)
    partial = dest.with_name(dest.name + ".partial")
    run(["curl", "-fL", "--retry", "3", "-o", str(partial), url])
    if sha256(partial) != expected_sha256:
        partial.unlink()
        raise SystemExit(f"the download of {url} does not match its pinned SHA-256")
    partial.replace(dest)
    return dest


def ensure_tmodloader_release(workdir: Path) -> Path:
    """The pinned tModLoader release from GitHub, verified and unpacked under <workdir>/tmodloader."""
    target = workdir / "tmodloader" / TMOD_RELEASE["version"]
    dll = target / "tModLoader.dll"
    if dll.is_file() and sha256(dll) == TMOD_RELEASE["dll_sha256"]:
        return target
    archive = download_pinned(TMOD_RELEASE["url"], TMOD_RELEASE["zip_sha256"],
                              workdir / "downloads" / f"tModLoader-v{TMOD_RELEASE['version']}.zip")
    staging = target.with_name(target.name + ".partial")
    shutil.rmtree(staging, ignore_errors=True)
    with zipfile.ZipFile(archive) as release:
        release.extractall(staging)
    if sha256(staging / "tModLoader.dll") != TMOD_RELEASE["dll_sha256"]:
        raise SystemExit(f"{archive} does not contain the pinned tModLoader.dll")
    shutil.rmtree(target, ignore_errors=True)
    staging.rename(target)
    return target


# A mod source is a dict with "id", "kind" and "dependencies" (ids). Kinds: "catalog" (curated,
# pinned source plus recorded patches), "git" (a repository commit the user added in the guided
# build), "file" (a .tmod package from Mods/, used as is: "path", "sha256"). "library_only"
# sources are built only for their dependents' compile and never packaged (StructureHelper ships
# inside Fargo's Souls as lib/StructureHelper.dll).
def curated_mod_catalog() -> dict[str, dict]:
    data = json.loads((Path(__file__).with_name("curated_tmod_mods.json")).read_text())
    return {entry["id"]: {**entry, "kind": "catalog"} for entry in data["mods"]}


def dependency_closed_mods(selected: list[str], sources: dict[str, dict]) -> list[str]:
    """`selected` plus their dependencies, every dependency before its dependents."""
    ordered: list[str] = []
    seen: set[str] = set()

    def add(name: str) -> None:
        if name in seen:
            return
        if name not in sources:
            raise SystemExit(f"unknown mod: {name}")
        seen.add(name)
        for dep in sources[name].get("dependencies", []):
            add(dep)
        ordered.append(name)

    for item in selected:
        add(item)
    return ordered


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


def source_identity(source: dict) -> str:
    """What a mod's package bytes depend on, for variant and package cache keys."""
    if source["kind"] == "file":
        return "file:" + source["sha256"]
    patches = ",".join(p + "=" + sha256(RECORDED_PATCHES / p) for p in source.get("patches", []))
    return f"{source['kind']}:{source['repo']}:{source['commit']}:{source.get('subdir', '')}:{patches}"


def tmod_variant_key(base: Path, mods: list[str], sources: dict[str, dict], profile: str, tc: tclib.Toolchain) -> str:
    h = hashlib.sha256()
    h.update(b"tmod-variant-v20-workdir-base")
    # The base stage's key covers the tModLoader release, its patched assemblies, the user's
    # Content and tmod-runtime.
    h.update(base.name.encode())
    # Launcher, input and profiler sources (the launcher stage keys on the same tree).
    h.update(tree_hash(ROOT / "native").encode())
    for source in (
        ROOT / "scripts/tmod/launcher/main_tmod.c",
        ROOT / "scripts/tmod/launcher/link-arguments.txt",
        ROOT / "scripts/tmod/launcher/frame_stats.c",
        ROOT / "scripts/tmod/launcher/heap_split.c",
        ROOT / "scripts/tmod/launcher/mesa_guard.c",
        ROOT / "scripts/native/compile_main.sh",
        ROOT / "scripts/tmod/build_tmod_modset_nro.py",
        ROOT / "scripts/tmod/prepare_modset.py",
        ROOT / "scripts/tmod/tmod_file.py",
        ROOT / "scripts/tmod/base_dlls.py",
        TMOD_BASE_MODSET,
    ):
        h.update(sha256(source).encode())
    # The offline hook scanner, IL lowering, hook patcher and mod tuner shape every mod assembly
    # and the combined tModLoader.dll; prepare_modset.py builds them without bin/obj.
    for tool in ("hook_scan", "il_lowering", "offline_hooks", "mod_tuning"):
        for path in sorted((ROOT / "scripts/tmod" / tool).rglob("*")):
            rel = path.relative_to(ROOT)
            if path.is_file() and not {"bin", "obj", "__pycache__"} & set(rel.parts):
                h.update(rel.as_posix().encode() + b"\0" + sha256(path).encode())
    h.update(profile.encode())
    for mod in mods:
        h.update(mod.encode() + b"\0" + source_identity(sources[mod]).encode() + b"\0")
    # Matches the toolchain components the stages key on, so a toolchain rebuild (e.g. swapping
    # in a from-source toolchain) invalidates cached variants instead of silently reusing a
    # candidate AOT-compiled against the old one.
    h.update(tc.digests("sdk", "runtime", "aot-compiler", "framework-aot", "cecil", "dotnet",
                         "mono-nx-native", "mesa", "native-deps", "tmod-runtime").encode())
    return "tmodcli-" + h.hexdigest()[:12]


def apply_recorded_patch(checkout: Path, patch: Path) -> None:
    marker = ".terrabuilder-" + patch.name + ".applied"
    target = None
    files_to_patch: list[tuple[Path, bool, list[str], list[str]]] = []
    curr_target = None
    curr_add = False
    curr_old: list[str] = []
    curr_new: list[str] = []

    def flush_curr():
        nonlocal curr_target, curr_add, curr_old, curr_new
        if curr_target is not None:
            files_to_patch.append((curr_target, curr_add, curr_old, curr_new))
            curr_target = None
            curr_add = False
            curr_old = []
            curr_new = []

    for line in patch.read_text().splitlines():
        if line.startswith("*** Update File: "):
            flush_curr()
            curr_target = checkout / line.split(": ", 1)[1]
            curr_add = False
        elif line.startswith("*** Add File: "):
            flush_curr()
            curr_target = checkout / line.split(": ", 1)[1]
            curr_add = True
        elif line.startswith("-") and not line.startswith("---"):
            curr_old.append(line[1:])
        elif line.startswith("+") and not line.startswith("+++"):
            curr_new.append(line[1:])
    flush_curr()

    if not files_to_patch:
        raise SystemExit(f"patch missing target: {patch}")

    for target, add_file, old, new in files_to_patch:
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
                    raise SystemExit(f"failed to apply recorded patch {patch}: context not found in {target.name}: {before!r}")
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
        script = ("apt-get update -qq && "
                  "apt-get install -y --no-install-recommends libsdl2-dev >/tmp/apt-terrabuilder-fna3d.log && "
                  f"cmake -S /tb/native-host/fna3d-{HOST_FNA3D['tag']}-src "
                  f"-B /tb/native-host/fna3d-{HOST_FNA3D['tag']}-build "
                  "-DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON -DBUILD_SDL3=OFF && "
                  f"cmake --build /tb/native-host/fna3d-{HOST_FNA3D['tag']}-build --parallel 4")
        if not root_is_invoking_user(eng):
            # apt-get needs container root, which is real root under rootful Docker.
            script = f"trap 'chown -R {os.getuid()}:{os.getgid()} /tb/native-host' EXIT; " + script
        cmd = [eng, "run", "--rm", "--entrypoint", "/bin/bash",
               "-v", f"{workdir}:/tb", BUILD_IMAGE, "-lc", script]
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


def build_mod_sources(tmod_dir: Path, mods: list[str], workdir: Path, sources: dict[str, dict], tc: tclib.Toolchain) -> Path | None:
    """Build the catalog and git mods in `mods` from source; return the folder with their packages."""
    from scripts.tmod.tmod_file import extract_tmod_member
    built_mods = [m for m in mods if sources[m]["kind"] != "file"]
    if not built_mods:
        return None
    cache_key = hashlib.sha256(
        (sha256(tmod_dir / "tModLoader.dll") + "\n"
         + "\n".join(f"{m}:{source_identity(sources[m])}" for m in built_mods)).encode()
    ).hexdigest()[:12]
    packages = workdir / "tmod" / "packages" / cache_key
    done = packages / ".complete.json"
    if done.exists() and all((packages / (m + ".tmod")).exists() for m in built_mods
                             if not sources[m].get("library_only")):
        say("mod source build: cached")
        return packages
    packages.mkdir(parents=True, exist_ok=True)
    src_root = workdir / "tmod" / "sources"
    src_root.mkdir(parents=True, exist_ok=True)
    (workdir / "tmod-home").mkdir(parents=True, exist_ok=True)
    # tModLoader mod projects import ../tModLoader.targets.
    shutil.copy2(tmod_dir / "tMLMod.targets", src_root / "tModLoader.targets")
    host_native = ensure_host_fna3d(workdir, tmod_dir)
    ensure_dotnet8(workdir)
    built_dlls: dict[str, Path] = {}
    built_packages: dict[str, Path] = {}
    eng = engine() or "podman"
    # dependency_closed_mods lists every dependency before its dependents.
    for name in built_mods:
        meta = sources[name]
        subdir = meta.get("subdir", "")
        checkout = src_root / name
        mod_root = checkout / subdir if subdir else checkout
        if not checkout.exists():
            run(["git", "clone", "--filter=blob:none", meta["repo"], str(checkout)])
        run(["git", "-C", str(checkout), "fetch", "--depth", "1", "origin", meta["commit"]])
        run(["git", "-C", str(checkout), "checkout", "--detach", meta["commit"]])
        run(["git", "-C", str(checkout), "reset", "--hard", meta["commit"]])
        run(["git", "-C", str(checkout), "clean", "-fdx"])
        for patch_name in meta.get("patches", []):
            apply_recorded_patch(checkout, RECORDED_PATCHES / patch_name)
        if subdir:
            shutil.copy2(tmod_dir / "tMLMod.targets", mod_root.parent / "tModLoader.targets")
        for dep_name in meta.get("dependencies", []):
            if dep_name in built_dlls:
                shutil.copy2(built_dlls[dep_name], mod_root / (dep_name + ".dll"))
            elif sources[dep_name]["kind"] == "file":
                data, _ = extract_tmod_member(Path(sources[dep_name]["path"]), dep_name + ".dll")
                (mod_root / (dep_name + ".dll")).write_bytes(data)
        before = {p.resolve() for p in checkout.rglob("*.tmod")}
        cmd = [eng, "run", "--rm", *user_args(eng, keep_id=False), "--entrypoint", "/tb/dotnet8/dotnet",
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
               "-w", "/tb/" + mod_root.relative_to(workdir).as_posix(),
               BUILD_IMAGE,
               "build", f"{name}.csproj", "-c", "Debug", f"-p:tMLSteamPath=/tml/",
               "-p:TreatWarningsAsErrors=false", "-p:NoWarn=0619",
               "-p:DebugType=none", "-p:DebugSymbols=false",
               "-p:GenerateTargetFrameworkAttribute=false"]
        try:
            run(cmd, heavy=True)
        except subprocess.CalledProcessError as exc:
            raise SystemExit(
                f"failed to build {name} from source at {meta['commit']}. "
                "The build output has the tModLoader ModCompile or C# compiler error."
            ) from exc
        # The compiler output, not obj/**/ref/: reference assemblies omit the private members a
        # dependent may publicize (Magic Storage publicizes SerousCommonLib).
        built = mod_root / "bin/Debug/net8.0" / f"{name}.dll"
        if built.is_file():
            built_dlls[name] = built
        after = [p for p in checkout.rglob("*.tmod") if p.resolve() not in before]
        if not after:
            candidates = list(checkout.rglob("*.tmod")) + list((workdir / "tmod-home").rglob("*.tmod"))
            after = sorted([p for p in candidates if p.name == name + ".tmod"],
                           key=lambda p: p.stat().st_mtime, reverse=True)
        if not meta.get("library_only"):
            if not after:
                raise SystemExit(f"built {name} but no .tmod package was produced")
            dest = packages / (name + ".tmod")
            shutil.copy2(after[0], dest)
            built_packages[name] = dest
    write_json(done, {"mods": built_mods, "packages": {k: str(v) for k, v in built_packages.items()}})
    return packages


def stage_mod_packages(workdir: Path, variant: str, mods: list[str], sources: dict[str, dict], built: Path | None) -> Path:
    """One folder with every package of the set: source builds plus .tmod files used as they are."""
    staged = workdir / "tmod" / "staged" / variant
    shutil.rmtree(staged, ignore_errors=True)
    staged.mkdir(parents=True)
    for name in mods:
        source = sources[name]
        if source.get("library_only"):
            continue
        package = Path(source["path"]) if source["kind"] == "file" else built / (name + ".tmod")
        if source["kind"] == "file" and sha256(package) != source["sha256"]:
            raise SystemExit(f"{package} changed after it was checked; start the build again")
        shutil.copy2(package, staged / (name + ".tmod"))
    return staged


def profile_wraps(profile: str) -> list[str]:
    wraps = [*(DIAGNOSTIC_WRAPS if profile in ("debug", "profiler") else []), *BEHAVIOR_WRAPS]
    if profile == "profiler":
        # Mono creates tModLoader's large-stack game thread through pthread_create. The
        # profiler must register that thread; envGetMainThreadHandle() is only the launcher
        # thread, which waits on it.
        wraps.append("pthread_create")
    return wraps


def launcher_config(profile: str, launcher: Path, workdir: Path) -> dict:
    extra = ["nx_audio.o", "nx_gpu_timing.o"]
    if profile == "profiler":
        extra.append("nx_profiler.o")
    manifest = "/build/" + (launcher / "launcher-build.json").relative_to(workdir).as_posix()
    return {"manifest": manifest, "extra_objects": extra}


def profile_suffix(profile: str) -> str:
    return "" if profile == "release" else f"-{profile}"


def stage_key(key: dict) -> str:
    return hashlib.sha256(json.dumps(key, sort_keys=True).encode()).hexdigest()[:16]


def tmod_release_hash(release: Path) -> str:
    """Hash of the release files the tModLoader stages read."""
    files = [release / "tModLoader.dll", release / "tModLoader.deps.json",
             *sorted(p for folder in ("Libraries", "Content") for p in (release / folder).rglob("*") if p.is_file())]
    return tree_hash(release, files)


def tmod_dlls_dir(release_hash: str, workdir: Path, tc: tclib.Toolchain) -> Path:
    """Where tmod_dlls_stage puts the patched assemblies of this release, tools and toolchain."""
    from scripts.tmod.base_dlls import CHAIN_TOOLS, tool_digest
    key = {"release": release_hash, "tools": {name: tool_digest(name) for name in CHAIN_TOOLS},
           "sources": source_shas(ROOT / "scripts/tmod/base_dlls.py"), "toolchain": tc.digests("dotnet", "cecil")}
    return workdir / "tmod" / f"dlls-{stage_key(key)}"


def tmod_dlls_stage(release: Path, release_hash: str, workdir: Path, tc: tclib.Toolchain) -> Path:
    """The release's tModLoader.dll, FNA.dll and TerrariaHooks.dll patched for the Switch, plus
    NxCrypto.dll (scripts/tmod/base_dlls.py), in <workdir>/tmod/dlls-<key>."""
    from scripts.tmod.base_dlls import build_base_dlls
    out = tmod_dlls_dir(release_hash, workdir, tc)
    if (out / "dlls.json").is_file():
        say("tModLoader patches: cached")
        return out
    say("tModLoader patches: building")
    partial = out.with_name(out.name + ".partial")
    shutil.rmtree(partial, ignore_errors=True)
    hashes = build_base_dlls(release, partial, dotnet=tc.path("dotnet") / "dotnet",
                             cecil=tc.path("cecil") / "Mono.Cecil.dll", tools=workdir / "tools")
    if hashes["FNA.dll"] != TMOD_SHARED_AUDIO_FNA_SHA256:
        raise SystemExit(f"the patched FNA.dll is {hashes['FNA.dll'][:16]}, not the validated shared-audio "
                         f"build {TMOD_SHARED_AUDIO_FNA_SHA256[:16]}")
    write_json(partial / "dlls.json", {"release": TMOD_RELEASE["version"], "release_hash": release_hash, "sha256": hashes})
    shutil.rmtree(out, ignore_errors=True)
    partial.rename(out)
    return out


def tmod_base_stage(release: Path, release_hash: str, dlls: Path, content: dict, workdir: Path,
                    tc: tclib.Toolchain) -> Path:
    """<workdir>/tmod/base-<key>: what every tModLoader build of this release, Content and
    tmod-runtime shares: flat_libs/, the stock romfs/, native/{icon.jpg,tmodloader.nacp} and
    manifest.json (build_tmod_modset_nro.py's base record)."""
    from scripts.tmod import base_romfs
    from scripts.tmod.launcher import native_assets
    runtime = tc.path("tmod-runtime")
    content_hash = base_romfs.content_tree_hash(content["content"])
    key = {"release": release_hash, "dlls": dlls.name, "content": content_hash,
           "tmod-runtime": tc.digest("tmod-runtime"),
           "sources": source_shas(ROOT / "scripts/tmod/base_romfs.py", ROOT / "scripts/tmod/launcher/native_assets.py")}
    out = workdir / "tmod" / f"base-{stage_key(key)}"
    if (out / "manifest.json").is_file():
        say("tModLoader base: cached")
        return out
    say(f"tModLoader base: building from Terraria {content['version']} Content")
    partial = out.with_name(out.name + ".partial")
    shutil.rmtree(partial, ignore_errors=True)
    base_romfs.stage_flat_libs(release, partial / "flat_libs", fna=dlls / "console/FNA.dll")
    # The RomFS ships the romfs:/-patched FNA; builds replace it with the compiled shared-audio one.
    record = base_romfs.stage_romfs(release, content["content"], runtime / "framework", runtime / "corelib",
                                    {"tModLoader.dll": dlls / "tModLoader.dll", "FNA.dll": dlls / "console/FNA.dll",
                                     "NxCrypto.dll": dlls / "NxCrypto.dll", "TerrariaHooks.dll": dlls / "TerrariaHooks.dll"},
                                    partial / "romfs")
    if record["content"]["tree_sha256"] != content_hash:
        raise SystemExit(f"{content['content']} changed while it was copied; start the build again")
    native = partial / "native"
    native.mkdir()
    native_assets.write_icon(release, native / "icon.jpg")
    native_assets.write_nacp(native / "tmodloader.nacp", "tModLoader NX")
    corelib_aot = json.loads((runtime / "corelib-aot/manifest.json").read_text())
    corelib = record["provenance_and_binding"]["System.Private.CoreLib"]
    if corelib_aot["input_sha256"] != corelib["romfs_payload_sha256"]:
        raise SystemExit("tmod-runtime's corelib-aot objects were not compiled from its CoreLib")
    container_runtime = f"{tclib.MOUNT}/tmod-runtime"
    corelib.update(compiler_input_sha256=corelib_aot["input_sha256"],
                   aot_object=f"{container_runtime}/corelib-aot/{corelib_aot['aot_object']}",
                   aot_object_sha256=corelib_aot["aot_object_sha256"],
                   llvm_object=f"{container_runtime}/corelib-aot/{corelib_aot['llvm_object']}",
                   llvm_object_sha256=corelib_aot["llvm_object_sha256"], pools=corelib_aot["pools"])
    libraries = {p.name: sha256(p) for p in sorted((runtime / "lib").glob("*.a"))}
    write_json(partial / "manifest.json", {
        "release": {"version": TMOD_RELEASE["version"], "hash": release_hash},
        "dlls": json.loads((dlls / "dlls.json").read_text()),
        "provenance_and_binding": record["provenance_and_binding"],
        "runtime_provenance": {"git_commit": TMOD_RUNTIME_COMMIT, "libmonosgen_sha256": libraries["libmonosgen-2.0.a"],
                               "libraries": libraries},
        "content": record["content"],
        "toolchain": {"tmod-runtime": tc.digest("tmod-runtime")},
    })
    shutil.rmtree(out, ignore_errors=True)
    partial.rename(out)
    return out


def seed_tmod_input(out: Path, mods: list[str], staged: Path | None, sources: dict[str, dict], profile: str,
                    launcher: Path, release: Path, dlls: Path, base: Path, workdir: Path, tc: tclib.Toolchain) -> None:
    """Write <variant>/input: the assemblies, packages and modset.json build_tmod_modset_nro.py builds."""
    from scripts.tmod.tmod_file import read_tmod_info
    if (out / "manifest.json").exists():
        return
    if out.exists():
        shutil.rmtree(out)
    runtime = tc.path("tmod-runtime")
    base_config = json.loads(TMOD_BASE_MODSET.read_text())
    inputs = out / "input"
    packaged = [m for m in mods if not sources[m].get("library_only")]
    llvm_modules = set(base_config["llvm_modules"]) | set(TMOD_EXTRA_AOT)
    if packaged:
        # The primary only orders enabled.json. Dependencies come first, so the last mod is the one
        # that pulled the others in.
        run([sys.executable, str(ROOT / "scripts/tmod/prepare_modset.py"),
             "--out", str(out), "--mods-dir", str(staged), "--primary", packaged[-1], "--label", "+".join(packaged),
             "--base-dir", str(base), "--release", str(release), "--config", str(TMOD_BASE_MODSET),
             "--tmod-runtime", str(runtime), "--dotnet", str(tc.path("dotnet") / "dotnet"),
             "--cecil", str(tc.path("cecil") / "Mono.Cecil.dll"), "--tools", str(workdir / "tools"),
             "--game", str(dlls / "tModLoader.dll"), "--interpret-hooks"])
        config = json.loads((inputs / "modset.json").read_text())
        for mod_name in packaged:
            llvm_modules.add(mod_name + ".dll")
            # Bundled libraries (lib/<name>.dll, e.g. Fargo's Souls' StructureHelper) are mod code too.
            for library in read_tmod_info(staged / (mod_name + ".tmod"))["dllReferences"]:
                llvm_modules.add(library + ".dll")
    else:
        inputs.mkdir(parents=True)
        shutil.copy2(dlls / "tModLoader.dll", inputs / "tModLoader.dll")
        shutil.copy2(runtime / "System.Reflection.Metadata.dll", inputs / "System.Reflection.Metadata.dll")
        srm = "System.Reflection.Metadata.dll"
        config = {**base_config, "mod": "", "additional_mods": [], "mod_libraries": [], "label": "no mods",
                  "replacements": {srm: base_config["replacements"][srm]}}
    shutil.copy2(dlls / "FNA.dll", inputs / "FNA.dll")
    config["extra_aot"] = [*base_config["extra_aot"], *TMOD_EXTRA_AOT]
    config["llvm_modules"] = sorted(llvm_modules)
    config["aot_workers"] = 1
    # Framework assemblies keep Mono's default limit, so tmod-runtime's framework-aot objects can be reused.
    framework_aot = json.loads((runtime / "framework-aot/framework-aot.json").read_text())["modules"]
    config["aot_inline_limits"] = {name: TMOD_AOT_INLINE_LIMIT for name in config["llvm_modules"]
                                   if name.removesuffix(".dll") not in framework_aot}
    config["launcher_objects"] = launcher_config(profile, launcher, workdir)
    config["extra_ldflags"] = ["-Wl," + ",".join("--wrap=" + w for w in profile_wraps(profile))]
    native = dict(base_config["native"])
    native["gc_stats"] = profile in ("debug", "profiler")
    native["frame_stats"] = profile in ("debug", "profiler")
    native["debug_diagnostics"] = profile in ("debug", "profiler")
    native["profiler"] = profile == "profiler"
    config["native"] = native
    config["native_library_paths"] = [TMOD_MESA_LIB]
    write_json(inputs / "modset.json", config)
    write_json(out / "source-mods.json", {"mods": mods, "sources": [sources[m] for m in mods]})


def run_tmod_pipeline(out: Path, base: Path, workdir: Path, tc: tclib.Toolchain) -> None:
    if (out / "native/candidate/tmodloader.nro").exists() and (out / "manifest.json").exists():
        say("tModLoader native build: cached")
        return
    eng = engine() or "podman"
    variant = "/build/" + out.relative_to(workdir).as_posix()
    cmd = [eng, "run", "--rm", *user_args(eng), "--entrypoint", "python3",
           "-e", f"TMOD_VARIANT={out.name}", "-e", f"TMOD_BASE=/build/{base.relative_to(workdir).as_posix()}",
           "-e", f"TMPDIR={variant}/container-tmp",
           *container_mounts(workdir, tc), BUILD_IMAGE, "/work/scripts/tmod/build_tmod_modset_nro.py"]
    run(cmd, heavy=True)
    verify = [sys.executable, str(ROOT / "scripts/tmod/verify_aot_dependencies.py"),
              "--variant-dir", str(out), "--base-dir", str(base), "--tmod-runtime", str(tc.path("tmod-runtime")),
              "--json-out", str(out / "aot-mvid-check.json")]
    run(verify)


def build_tmodloader(tmod_dir: Path, mods: list[str], sources: dict[str, dict], *, workdir: Path, out_root: Path,
                     profile: str, terraria: Path = DEFAULT_TERRARIA) -> Path:
    """Build tModLoader with `mods` (dependency-closed, dependencies first), with the Content/ of the
    Terraria install under `terraria`; return the output folder."""
    tmeta = validate_tmodloader(tmod_dir)
    tc = load_toolchain(workdir)
    if not tc.has("tmod-runtime"):
        raise SystemExit(f"the toolchain at {tc.root} has no tmod-runtime component, which tModLoader builds need; "
                         "`./terrabuilder toolchain --bundle --replace` installs the prebuilt toolchain, which has it")
    content = tmod_content_source(terraria)
    phase("Patching tModLoader")
    release_hash = tmod_release_hash(tmod_dir)
    dlls = tmod_dlls_stage(tmod_dir, release_hash, workdir, tc)
    phase("Packing game files")
    base = tmod_base_stage(tmod_dir, release_hash, dlls, content, workdir, tc)
    variant = tmod_variant_key(base, mods, sources, profile, tc)
    out = workdir / "tmod" / variant
    phase("Building the launcher")
    launcher_dir = launcher_stage(workdir, workdir / "launcher-src" / profile, tc, profile,
                                  workdir / "stamps" / "tmod" / f"launcher-{profile}.json")
    built = None
    if any(sources[m]["kind"] != "file" for m in mods):
        phase("Building mods from source")
        built = build_mod_sources(tmod_dir, mods, workdir, sources, tc)
    with file_lock(workdir / "tmod" / f".{variant}.lock"):
        packaged = [m for m in mods if not sources[m].get("library_only")]
        staged = stage_mod_packages(workdir, variant, mods, sources, built) if packaged else None
        phase("Converting mods for the Switch" if packaged else "Preparing tModLoader")
        seed_tmod_input(out, mods, staged, sources, profile, launcher_dir, tmod_dir, dlls, base, workdir, tc)
        phase("Compiling for the Switch")
        run_tmod_pipeline(out, base, workdir, tc)
        phase("Packaging")
        suffix = profile_suffix(profile)
        out_dir = out_root / f"tModLoader{suffix}"
        sd_out = out_dir / "sdcard"
        if sd_out.exists():
            shutil.rmtree(sd_out)
        shutil.copytree(out / "sdcard", sd_out)
        nro = sd_out / "switch" / f"tmodloader{suffix}.nro"
        publish_file(out / "native/candidate/tmodloader.nro", nro)
        sd_runtime = write_sd_runtime(sd_out, tc)
        manifest = json.loads((out / "manifest.json").read_text())
        mvid = json.loads((out / "aot-mvid-check.json").read_text())
        base_manifest = json.loads((base / "manifest.json").read_text())
        receipt = {"target": "tmodloader", "experimental": True, "variant": variant, "input": tmeta,
                   "content": {"game": str(content["game"]), **base_manifest["content"]},
                   "base": {"path": str(base), "release": base_manifest["release"], "dlls": base_manifest["dlls"],
                            "runtime_provenance": base_manifest["runtime_provenance"]},
                   "mods": [sources[m] for m in mods], "nro": {"path": str(nro), "sha256": sha256(nro), "bytes": nro.stat().st_size},
                   "sd_payload": str(sd_out), "sd_runtime": sd_runtime, "aot_mvid_check": mvid, "manifest": manifest,
                   "toolchain": toolchain_summary(tc)}
        write_json(out_dir / "receipt.json", receipt)
    return out_dir


def build_vanilla(game_dir: Path, *, workdir: Path, out_root: Path, profile: str) -> Path:
    """Build vanilla Terraria from a clean GOG 1.4.5.8 game folder; return the output folder."""
    tc = load_toolchain(workdir)
    meta = validate_game(game_dir)
    suffix = profile_suffix(profile)
    out_dir = out_root / f"Terraria{suffix}"
    sd_out = out_dir / "sdcard"
    final_nro = sd_out / "switch" / f"Terraria{suffix}.nro"
    stamps = workdir / "stamps" / "vanilla"
    info: dict[str, object] = {"input": meta, "stages": {}}
    with file_lock(workdir / ".vanilla.lock"):
        phase("Patching the game")
        patched = patch_stage(game_dir, workdir, tc, stamps, info)
        phase("Packing game files")
        romfs_hash = romfs_stage(game_dir, patched, workdir, tc, stamps, info)
        phase("Compiling for the Switch")
        aot = aot_stage(romfs_hash, workdir, tc, stamps, info)
        phase("Building the launcher")
        launcher = launcher_stage(workdir, workdir / "vanilla" / f"launcher-{profile}", tc, profile,
                                  stamps / f"launcher-{profile}.json", info)
        phase("Linking")
        native = native_stage(workdir, romfs_hash, aot, launcher, tc, profile, stamps, info)
        phase("Packaging")
        if sd_out.exists():
            shutil.rmtree(sd_out)
        publish_file(native / "mono_nx_fna.nro", final_nro)
        info["nro"] = {"path": str(final_nro), "sha256": sha256(final_nro), "bytes": final_nro.stat().st_size}
        info["sd_payload"] = str(sd_out)
        info["sd_runtime"] = write_sd_runtime(sd_out, tc)
        info["toolchain"] = toolchain_summary(tc)
        write_json(out_dir / "receipt.json", info)
    return out_dir


def build(args: argparse.Namespace) -> int:
    workdir = args.workdir.expanduser().resolve()
    out_root = Path(args.out).expanduser().resolve() if args.out else DEFAULT_OUT
    if args.target == "vanilla":
        if not args.game_dir:
            raise SystemExit("--target vanilla needs --game-dir: a clean GOG Terraria 1.4.5.8 game folder")
        out_dir = build_vanilla(Path(args.game_dir).expanduser().resolve(), workdir=workdir, out_root=out_root,
                                profile=args.profile)
    else:
        from terrabuilder_pkg.installer import find_mod_files
        files, _ = find_mod_files()
        # A curated mod is built from its pinned source; other names come from .tmod files in Mods/.
        sources = {**{source["id"]: source for source in files}, **curated_mod_catalog()}
        mods = parse_mod_flags(args.mods, sources)
        tmod_dir = Path(args.game_dir).expanduser().resolve() if args.game_dir else ensure_tmodloader_release(workdir)
        terraria = Path(args.terraria).expanduser().resolve() if args.terraria else DEFAULT_TERRARIA
        out_dir = build_tmodloader(tmod_dir, mods, sources, workdir=workdir, out_root=out_root, profile=args.profile,
                                   terraria=terraria)
    receipt = json.loads((out_dir / "receipt.json").read_text())
    print(f"Built {receipt['nro']['path']} {receipt['nro']['sha256']}")
    print(f"Copy everything inside {out_dir / 'sdcard'} to the root of the SD card, replacing existing files.")
    if args.target == "tmodloader":
        print(TMOD_WARNING)
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
        say(f"{name}: cached")
        return True
    if recorded == key:
        reason = "outputs missing"
    elif isinstance(recorded, dict):
        reason = "changed: " + ", ".join(sorted(k for k in key.keys() | recorded.keys() if key.get(k) != recorded.get(k)))
    else:
        reason = "no previous build"
    say(f"{name}: building ({reason})")
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
        "image": image_id(BUILD_IMAGE),
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
    """The mounts every build container gets, and the working directory builds run in."""
    return ["-v", f"{workdir}:/build", "-v", f"{ROOT}:/work:ro", *tc.mounts(), "-w", BUILD_WORKDIR]


def aot_stage(romfs_hash: str, workdir: Path, tc: tclib.Toolchain, stamps: Path, info: dict) -> Path:
    aot = workdir / "vanilla" / "aot"
    key = {
        "romfs": romfs_hash,
        "sources": source_shas(ROOT / "scripts/compile_terraria_aot.py", ROOT / "scripts/prepare_aot/PrepareAot.cs",
                               ROOT / "scripts/prepare_aot/PrepareAot.csproj"),
        "toolchain": tc.digests("sdk", "runtime", "aot-compiler", "framework-aot", "cecil", "dotnet"),
        "image": image_id(BUILD_IMAGE),
        "llvm_modules": ["Terraria", "FNA"],
    }
    stamp = stamps / "aot.json"
    if not stage_cached("aot", stamp, key, [aot / "build-manifest.json"]):
        if aot.exists():
            shutil.rmtree(aot)
        aot.mkdir(parents=True)
        eng = engine() or "podman"
        cmd = [eng, "run", "--rm", *user_args(eng), "--entrypoint", "/usr/bin/python3",
               "-e", "PATH=/opt/devkitpro/devkitA64/bin:/opt/devkitpro/tools/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
               *container_mounts(workdir, tc), BUILD_IMAGE,
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
        "image": image_id(BUILD_IMAGE),
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
        "image": image_id(BUILD_IMAGE),
    }
    stamp = stamps / f"native-{profile}.json"
    if not stage_cached(f"native link ({profile})", stamp, key, [out / "mono_nx_fna.nro"]):
        if out.exists():
            shutil.rmtree(out)
        eng = engine() or "podman"
        cmd = [eng, "run", "--rm", *user_args(eng), "--entrypoint", "/usr/bin/python3",
               *container_mounts(workdir, tc), BUILD_IMAGE, "/work/scripts/native/link_nro.py",
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
    out_root = Path(args.out).expanduser().resolve() if args.out else DEFAULT_OUT
    for profile in ("profiler", "release"):
        suffix = profile_suffix(profile)
        nro = out_root / f"Terraria{suffix}" / "sdcard/switch" / f"Terraria{suffix}.nro"
        if nro.exists():
            print(f"{nro} {nro.stat().st_size} {sha256(nro)}")
    return 0


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(prog="terrabuilder", description="Without a command: the guided build.")
    p.add_argument("--workdir", type=Path, default=DEFAULT_WORKDIR)
    sub = p.add_subparsers(dest="cmd")
    d = sub.add_parser("doctor"); d.add_argument("--workdir", type=Path, default=DEFAULT_WORKDIR); d.set_defaults(func=doctor)
    t = sub.add_parser("toolchain"); t.add_argument("--workdir", type=Path, default=DEFAULT_WORKDIR); t.add_argument("toolcmd", nargs="?", choices=["status", "verify", "pack"], default="status", help="pack: write the toolchain as the release asset --bundle installs"); t.add_argument("--from-source", action="store_true", help="build the toolchain from the pinned public sources in toolchain.lock.json (hours; see docs/BUILDING.md)"); t.add_argument("--bundle", nargs="?", const="", metavar="ARCHIVE", help="install the pinned prebuilt toolchain (downloaded, or ARCHIVE: a local copy of the release asset) and build the container image"); t.add_argument("--replace", action="store_true", help="with --from-source or --bundle, swap the new toolchain into <workdir>/toolchain, backing up the previous one"); t.add_argument("--out", type=Path, default=DEFAULT_OUT, help="pack: output folder (default: Output/)"); t.set_defaults(func=toolchain_command)
    b = sub.add_parser("build", help="non-interactive build for scripts"); b.add_argument("--workdir", type=Path, default=DEFAULT_WORKDIR); b.add_argument("--target", choices=["vanilla", "tmodloader"], required=True); b.add_argument("--game-dir", help="vanilla: a clean GOG Terraria 1.4.5.8 game folder; tmodloader: a tModLoader 2026.7.3.0 folder (default: downloaded from GitHub)"); b.add_argument("--mods", help="comma-separated tModLoader mods: curated ones, or the names of .tmod files in Mods/; 'none' for none"); b.add_argument("--terraria", help="tmodloader: a folder holding a Terraria 1.4.4.9 or 1.4.5.x install, whose Content/ the build uses (default: Terraria/ next to this script)"); b.add_argument("--out", help="output root (default: Output/ next to this script)"); b.add_argument("--profile", choices=["release", "debug", "profiler"], default="release"); b.add_argument("--debug-diagnostics", action="store_const", const="debug", dest="profile", help="alias for --profile debug"); b.set_defaults(func=build)
    c = sub.add_parser("compare"); c.add_argument("--out"); c.set_defaults(func=compare)
    args = p.parse_args(argv)
    if args.cmd is None:
        from terrabuilder_pkg import installer
        return installer.main(args.workdir.expanduser().resolve())
    try:
        return args.func(args)
    except subprocess.CalledProcessError as error:
        # The command printed its own output above; a Python traceback would only bury it.
        print(f"\nerror: {Path(str(error.cmd[0])).name} exited with code {error.returncode}", file=sys.stderr)
        return 1

if __name__ == "__main__":
    raise SystemExit(main())
