"""Guided build: what ./terrabuilder runs without a command.

Folders next to the script hold the user's files and the results: Terraria/ (the game),
tModLoader/ (optional), Mods/ (.tmod files, plus git-mods.json for mods added from GitHub) and
Output/. Menus pick what to build and which mods; one build then runs with a progress view and
leaves the SD-card files in Output/<target>/sdcard.
"""
from __future__ import annotations

import datetime
import hashlib
import json
import re
import shutil
import subprocess
import sys
from collections.abc import Callable
from pathlib import Path
from typing import Any

from scripts.tmod.base_dlls import build_tool, dotnet_env
from scripts.tmod.base_romfs import flat_lib_sources
from scripts.tmod.tmod_file import extract_tmod_member, loader_version_problem, read_tmod_info
from terrabuilder_pkg import cli, ui
from terrabuilder_pkg import toolchain as tclib

ROOT = cli.ROOT
GAME_DIR = ROOT / "Terraria"
TMOD_DIR = ROOT / "tModLoader"
MODS_DIR = ROOT / "Mods"
OUT_DIR = cli.DEFAULT_OUT
GIT_MODS = MODS_DIR / "git-mods.json"
PROFILES = {"release": "Release", "debug": "Debug (extra logging)", "profiler": "Profiler (logging and sampling profiler)"}
KIND_ORDER = ("catalog", "git", "file")


def shown(path: Path) -> str:
    return path.relative_to(ROOT).as_posix() if path.is_relative_to(ROOT) else str(path)


def windows_path(path: Path) -> str | None:
    """The Explorer path of `path` under WSL, else None."""
    if not shutil.which("wslpath"):
        return None
    result = subprocess.run(["wslpath", "-w", str(path)], capture_output=True, text=True)
    return result.stdout.strip() if result.returncode == 0 else None


def run_logged(heading: str, log_name: str, failure: str,
               work: Callable[[ui.Progress], Any]) -> tuple[Any, float, Path] | None:
    """Run `work` with a progress line per phase and command output in Output/logs/<stamp>-<log_name>.log.
    Returns (work's result, seconds taken, log path); on failure shows the error and the log's last
    lines and returns None."""
    log = OUT_DIR / "logs" / f"{datetime.datetime.now().strftime('%Y-%m-%d_%H%M%S')}-{log_name}.log"
    ui.write("\n" + ui.bold(heading) + ui.dim(f"   log: {shown(log)}") + "\n")
    progress = ui.Progress(log)
    cli.OUTPUT = progress
    ok, error, result = False, "", None
    try:
        result = work(progress)
        ok = True
    except subprocess.CalledProcessError as failed:
        error = f"{Path(failed.cmd[0]).name} exited with code {failed.returncode}"
    except SystemExit as failed:
        error = str(failed.code)
    finally:
        took = progress.close(ok)
        cli.OUTPUT = None
    if not ok:
        ui.write("\n" + ui.red(ui.bold(f"{failure}: ")) + error + "\n\n" + ui.dim("  Last lines of the log:") + "\n")
        ui.write("".join(ui.dim("    " + ui.clip(line, ui.size()[0] - 6)) + "\n" for line in progress.tail(15)))
        ui.write(f"\n  Full log: {log}\n")
        return None
    return result, took, log


def version_key(text: str) -> tuple[int, ...]:
    return tuple(int(part) if part.isdigit() else 0 for part in re.split(r"[.+-]", text))


# ---- Terraria/ and tModLoader/ ----------------------------------------------------------------

def find_game() -> dict:
    exes = cli.search(GAME_DIR, "Terraria.exe")
    if not exes:
        return {"ready": False, "summary": "Terraria/ is empty",
                "detail": "Vanilla builds need GOG Terraria 1.4.5.8 for Linux: put its game folder "
                          "(Terraria.exe, FNA.dll, Content/) or the whole GOG install in Terraria/."}
    for exe in exes:
        game = exe.parent
        fna = game / "FNA.dll"
        if (fna.is_file() and (game / "Content").is_dir() and cli.sha256(exe) == cli.GOG_1458["Terraria.exe"]
                and cli.sha256(fna) == cli.GOG_1458["FNA.dll"]):
            return {"ready": True, "path": game, "summary": "GOG Terraria 1.4.5.8", "detail": f"Using {shown(game)}."}
    game = exes[0].parent
    version = cli.game_version(game)
    if not (game / "FNA.dll").is_file():
        return {"ready": False, "path": game, "summary": f"Terraria {version} for Windows",
                "detail": f"{shown(game)} is the Windows (XNA) version, such as Steam's. Vanilla builds need "
                          "GOG's Linux version 1.4.5.8, which uses FNA."}
    return {"ready": False, "path": game, "summary": f"Terraria {version}",
            "detail": f"{shown(game)} is not a clean GOG Terraria 1.4.5.8 for Linux, the only version the "
                      "game patcher accepts."}


def find_content() -> dict:
    """The Terraria install in Terraria/ whose Content/ tModLoader builds use."""
    found = cli.find_tmod_content(GAME_DIR)
    if found is None:
        return {"ready": False, "summary": "no Terraria Content in Terraria/",
                "detail": "tModLoader builds use the Content/ folder of Terraria 1.4.4.9 or 1.4.5.x: put GOG's "
                          "Linux game folder or a copy of Steam's Terraria folder in Terraria/."}
    return {"ready": True, **found, "summary": f"Terraria {found['version']} Content",
            "detail": f"Game content comes from Terraria {found['version']} in {shown(found['content'])}."}


def find_tmodloader(workdir: Path) -> dict:
    pinned = cli.TMOD_RELEASE["version"]
    dlls = cli.search(TMOD_DIR, "tModLoader.dll")
    for dll in dlls:
        if (dll.parent / "tMLMod.targets").is_file() and cli.sha256(dll) == cli.TMOD_RELEASE["dll_sha256"]:
            return {"path": dll.parent, "summary": f"tModLoader {pinned} from tModLoader/",
                    "detail": f"Using {shown(dll.parent)}."}
    note = ""
    if dlls:
        other = cli.tmodloader_release_version(dlls[0]) or "of an unknown version"
        note = f"tModLoader/ has tModLoader {other}; Switch builds use {pinned}. "
    downloaded = workdir / "tmodloader" / pinned
    if (downloaded / "tModLoader.dll").is_file() and cli.sha256(downloaded / "tModLoader.dll") == cli.TMOD_RELEASE["dll_sha256"]:
        return {"path": downloaded, "summary": f"tModLoader {pinned} (downloaded)", "detail": note + "The downloaded copy is used."}
    return {"path": None, "summary": f"tModLoader {pinned} (downloaded from GitHub, 61 MB)",
            "detail": note + f"The build downloads tModLoader {pinned} from GitHub."}


# ---- Mods/: .tmod files ---------------------------------------------------------------------

_package_cache: dict[tuple[str, int, int], tuple[dict, str]] = {}


def package_info(path: Path) -> tuple[dict, str]:
    stat = path.stat()
    key = (str(path), stat.st_size, stat.st_mtime_ns)
    if key not in _package_cache:
        _package_cache[key] = (read_tmod_info(path), cli.sha256(path))
    return _package_cache[key]


def find_mod_files() -> tuple[list[dict], list[str]]:
    """One source per mod name found in Mods/ (any depth, e.g. copied Workshop folders that hold a
    package per tModLoader version): the newest package this port can load, else the newest."""
    by_name: dict[str, list[tuple[Path, dict, str]]] = {}
    unreadable = []
    for path in cli.search(MODS_DIR, "*.tmod", depth=5):
        try:
            info, digest = package_info(path)
        except (OSError, ValueError) as error:
            unreadable.append(f"{shown(path)}: not a readable .tmod ({error})")
            continue
        by_name.setdefault(info["name"], []).append((path, info, digest))
    sources = []
    for name, packages in sorted(by_name.items()):
        packages.sort(key=lambda p: (loader_version_problem(p[1]["loader_version"]) is None,
                                     version_key(p[1]["loader_version"]), version_key(p[1]["version"])), reverse=True)
        path, info, digest = packages[0]
        sources.append({
            "id": name, "kind": "file", "path": str(path), "sha256": digest,
            "display_name": info.get("displayName") or name, "version": info["version"],
            "loader_version": info["loader_version"], "author": info.get("author", ""),
            "dependencies": [ref.split("@", 1)[0].strip() for ref in info["modReferences"]],
            "requirements": info["modReferences"], "dll_references": info["dllReferences"],
            "license": "unknown (your .tmod file)",
        })
    return sources, unreadable


def check_mod_file(mod: dict, files: dict[str, dict], dlls: Path, release: Path, workdir: Path,
                   tc: tclib.Toolchain) -> None:
    """Set mod["problem"] when the build cannot use the package, and mod["report"] with what the
    hook scanner found: hook counts and members that tModLoader 2026.7.3.0 no longer has. `dlls`
    is the tModLoader patch stage (cli.tmod_dlls_stage) of `release`: the scan reads its
    TerrariaHooks.dll and NxFix'd tModLoader.dll, the assemblies the build lowers hooks against."""
    problem = loader_version_problem(mod["loader_version"])
    if problem:
        mod["problem"] = problem
        return
    deps, todo = [], list(mod["dependencies"])
    while todo:
        dep = todo.pop()
        if dep in files and files[dep] not in deps and files[dep] is not mod:
            deps.append(files[dep])
            todo.extend(files[dep]["dependencies"])
    dotnet = tc.path("dotnet") / "dotnet"
    scanner = build_tool("hook_scan", dotnet=dotnet, cecil=tc.path("cecil") / "Mono.Cecil.dll", tools=workdir / "tools")
    hooks, game = dlls / "TerrariaHooks.dll", dlls / "tModLoader.dll"
    key = hashlib.sha256("\n".join([scanner.parent.name, cli.sha256(game), cli.sha256(hooks), mod["sha256"],
                                    *sorted(d["sha256"] for d in deps)]).encode()).hexdigest()[:16]
    record = workdir / "precheck" / f"{key}.json"
    if record.is_file():
        result = json.loads(record.read_text())
    else:
        folder = workdir / "precheck" / key
        shutil.rmtree(folder, ignore_errors=True)
        folder.mkdir(parents=True)
        assemblies = []
        for package in (mod, *deps):
            for member in (package["id"] + ".dll", *(f"lib/{lib}.dll" for lib in package["dll_references"])):
                data, _ = extract_tmod_member(Path(package["path"]), member)
                (folder / Path(member).name).write_bytes(data)
                assemblies.append(str(folder / Path(member).name))
        # ReLogic and the other release libraries resolve from the scanned assemblies' folder, as
        # they do from the stock RomFS in the build.
        for name, path in flat_lib_sources(release).items():
            if not (folder / name).exists():
                (folder / name).symlink_to(path)
        inventory_path = folder / "inventory.json"
        scan = subprocess.run([str(dotnet), str(scanner), str(hooks), str(game), *assemblies, "-o", str(inventory_path)],
                              env=dotnet_env(dotnet, workdir / "tools"), capture_output=True, text=True)
        own = {mod["id"], *mod["dll_references"]}
        if scan.returncode:
            error = next((line for line in scan.stdout.splitlines() + scan.stderr.splitlines() if "Exception" in line),
                         f"exit code {scan.returncode}")
            result = {"error": error.strip()[:300]}
        else:
            inventory = json.loads(inventory_path.read_text())
            def mine(entries):
                return sum(1 for e in entries if any(r.get("mod") in own for r in e.get("registrations", [])))
            result = {"on_hooks": mine(inventory["on_hooks"]), "il_hooks": mine(inventory["il_hooks"]),
                      "detours": mine(inventory["runtime_detours"]),
                      "unsupported": [u for u in inventory["unsupported"] if u["mod"] in own],
                      "missing": [m for m in inventory.get("missing_members", []) if m["mod"] in own]}
        shutil.rmtree(folder, ignore_errors=True)
        record.write_text(json.dumps(result, indent=2) + "\n")
    if "error" in result:
        mod["problem"] = "the hook check failed: " + result["error"]
        return
    lines = [f"{result['on_hooks']} On hooks, {result['il_hooks']} IL hooks, {result['detours']} runtime detours."]
    if result["unsupported"]:
        codes = sorted({u["reason_code"] for u in result["unsupported"]})
        mod["problem"] = f"{len(result['unsupported'])} hook registrations can't be converted ({', '.join(codes)})"
        lines += [f"Can't convert: {u['caller']} ({u['reason_code']}: {u['detail']})" for u in result["unsupported"][:3]]
    if result["missing"]:
        lines.append(f"{len(result['missing'])} members it uses are missing from tModLoader "
                     f"{cli.TMOD_RELEASE['version']}; code using them fails when it runs, e.g. {result['missing'][0]['member']}.")
    else:
        lines.append(f"Every tModLoader member it uses exists in {cli.TMOD_RELEASE['version']}.")
    mod["report"] = " ".join(lines)


# ---- Mods/git-mods.json: mods added from GitHub -------------------------------------------------

def load_git_mods() -> list[dict]:
    if not GIT_MODS.is_file():
        return []
    return [{**entry, "kind": "git"} for entry in json.loads(GIT_MODS.read_text())]


def save_git_mods(entries: list[dict]) -> None:
    MODS_DIR.mkdir(exist_ok=True)
    GIT_MODS.write_text(json.dumps([{k: v for k, v in e.items() if k != "kind"} for e in entries], indent=2) + "\n")


def parse_build_txt(path: Path) -> dict[str, str]:
    values = {}
    for line in path.read_text(errors="replace").splitlines():
        if "=" in line and not line.lstrip().startswith("#"):
            key, value = line.split("=", 1)
            values[key.strip()] = value.strip()
    return values


LICENSES = (("Permission is hereby granted, free of charge", "MIT"), ("MIT License", "MIT"),
            ("GNU LESSER GENERAL PUBLIC LICENSE", "LGPL"), ("GNU AFFERO GENERAL PUBLIC LICENSE", "AGPL-3.0"),
            ("GNU GENERAL PUBLIC LICENSE", "GPL"), ("Apache License", "Apache-2.0"),
            ("Mozilla Public License", "MPL-2.0"), ("This is free and unencumbered software", "Unlicense"),
            ("Creative Commons", "Creative Commons"), ("Redistribution and use in source and binary forms", "BSD"))


def detect_license(*folders: Path) -> str:
    for folder in folders:
        for path in sorted(folder.glob("*")):
            if path.is_file() and re.match(r"(?i)(licen[cs]e|copying)", path.name):
                text = path.read_text(errors="replace")[:4000]
                for marker, name in LICENSES:
                    if marker.lower() in text.lower():
                        if name == "GPL":
                            return "GPL-3.0" if "Version 3" in text else "GPL-2.0" if "Version 2" in text else "GPL"
                        return name
                return f"custom ({path.name})"
    return "no license file"


def git(*args: str, cwd: Path | None = None) -> str:
    result = subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True)
    if result.returncode:
        raise SystemExit((result.stderr or result.stdout).strip().splitlines()[-1] if (result.stderr or result.stdout).strip()
                         else f"git {args[0]} failed")
    return result.stdout.strip()


def add_git_mod(workdir: Path, taken: set[str]) -> str:
    """Ask for a repository, pin its commit and save it to Mods/git-mods.json; returns a message."""
    ui.write("\n" + ui.bold("Add a tModLoader mod from a Git repository") + "\n")
    printed = 2
    probe = workdir / "tmod" / "probe"
    try:
        url = ui.ask_text("Repository URL (e.g. https://github.com/owner/repo)")
        printed += 1
        if not url:
            return "Nothing added."
        ref = ui.ask_text("Branch, tag or commit (Enter for the default branch)")
        printed += 1
        shutil.rmtree(probe, ignore_errors=True)
        probe.parent.mkdir(parents=True, exist_ok=True)
        with ui.Spinner(f"Cloning {url}"):
            git("clone", "--filter=blob:none", url, str(probe))
            if ref:
                git("checkout", "--detach", ref, cwd=probe)
            commit = git("rev-parse", "HEAD", cwd=probe)
    except SystemExit as error:
        return f"Could not clone {url}: {error}"
    finally:
        ui.write(f"\x1b[{printed}A\r\x1b[J")
    roots = sorted({p.parent for p in probe.rglob("build.txt")
                    if len(p.relative_to(probe).parts) <= 3 and any(p.parent.glob("*.csproj"))})
    if not roots:
        return f"{url} has no tModLoader mod (a folder with build.txt and a .csproj)."
    root = roots[0]
    projects = sorted(root.glob("*.csproj"))
    project = next((p for p in projects if p.stem == root.name), projects[0])
    name = project.stem
    if name in taken:
        return f"{name} is already in the list."
    build = parse_build_txt(root / "build.txt")
    requirements = [r.strip() for r in build.get("modReferences", "").split(",") if r.strip()]
    entry = {"id": name, "kind": "git", "repo": url, "ref": ref or git("rev-parse", "--abbrev-ref", "HEAD", cwd=probe),
             "commit": commit, "subdir": root.relative_to(probe).as_posix() if root != probe else "",
             "display_name": build.get("displayName", name), "version": build.get("version", "?"),
             "license": detect_license(root, probe), "dependencies": [r.split("@", 1)[0].strip() for r in requirements],
             "requirements": requirements}
    target = workdir / "tmod" / "sources" / name
    if not target.exists():
        probe.rename(target)
    save_git_mods([*load_git_mods(), entry])
    return f"Added {entry['display_name']} {entry['version']} at {commit[:10]} (license: {entry['license']})."


# ---- mod selection --------------------------------------------------------------------------

class ModChoice:
    """The mods menu's state. Keys are "<kind>:<id>"; the same mod id can come from several kinds
    (e.g. the open-source list and a .tmod file), and only one of them is used."""

    def __init__(self, entries: dict[str, dict]) -> None:
        self.entries = entries
        self.by_id: dict[str, list[str]] = {}
        for key, source in entries.items():
            self.by_id.setdefault(source["id"], []).append(key)
        self.chosen: list[str] = []
        self.blocked = {key: source["problem"] for key, source in entries.items() if source.get("problem")}
        changed = True
        while changed:
            changed = False
            for key, source in entries.items():
                if key in self.blocked:
                    continue
                for dep in source.get("dependencies", []):
                    if self.provider(dep, set()) is None:
                        self.blocked[key] = (f"needs {dep}, which can't be used" if dep in self.by_id
                                             else f"needs {dep}, which is not available")
                        changed = True
                        break

    def provider(self, mod_id: str, active: set[str]) -> str | None:
        keys = self.by_id.get(mod_id, [])
        for key in keys:
            if key in active:
                return key
        for kind in KIND_ORDER:
            for key in keys:
                if key.startswith(kind + ":") and key not in self.blocked:
                    return key
        return None

    def resolve(self) -> tuple[list[str], dict[str, list[str]]]:
        """Active keys (chosen plus dependencies) and, for each dependency, who needs it."""
        active: list[str] = []
        needed_by: dict[str, list[str]] = {}

        def visit(key: str) -> None:
            if key in active:
                return
            active.append(key)
            for dep in self.entries[key].get("dependencies", []):
                provider = self.provider(dep, set(active) | set(self.chosen))
                needed_by.setdefault(provider, []).append(self.entries[key]["display_name"])
                visit(provider)

        for key in self.chosen:
            visit(key)
        return active, needed_by

    def toggle(self, key: str, on: bool) -> str | None:
        message = None
        if on:
            mod_id = self.entries[key]["id"]
            replaced = [k for k in self.chosen if self.entries[k]["id"] == mod_id and k != key]
            for other in replaced:
                self.chosen.remove(other)
                message = f"Using {describe(self.entries[key])} instead of {describe(self.entries[other])}."
            self.chosen.append(key)
        elif key in self.chosen:
            self.chosen.remove(key)
        return message

    def build_input(self) -> tuple[list[str], dict[str, dict]]:
        active, _ = self.resolve()
        sources = {self.entries[key]["id"]: self.entries[key] for key in active}
        selected = [self.entries[key]["id"] for key in self.chosen]
        return cli.dependency_closed_mods(selected, sources), sources


def describe(source: dict) -> str:
    where = {"catalog": "open-source build", "git": "GitHub build", "file": "your .tmod"}[source["kind"]]
    return f"{source['display_name']} ({where})"


# ---- the guided flow ------------------------------------------------------------------------

class Wizard:
    def __init__(self, workdir: Path) -> None:
        self.workdir = workdir
        self.target = "tmodloader"
        self.profile = "release"
        self.game: dict = {}
        self.content: dict = {}
        self.tmod: dict = {}
        self.tc: tclib.Toolchain | None = None
        self.choice: ModChoice | None = None
        self.unreadable: list[str] = []   # .tmod files in Mods/ that could not be read
        self.flash = ""                    # a one-time message for the next mods menu
        self.mods_focus: str | None = None  # the mods menu reopens on the item it left from

    def run(self) -> int:
        for folder in (GAME_DIR, TMOD_DIR, MODS_DIR, OUT_DIR):
            folder.mkdir(exist_ok=True)
        self.welcome()
        if not self.check_machine():
            return 1
        step = "target"
        while step != "build":
            step = getattr(self, "step_" + step)()
        return self.build()

    def welcome(self) -> None:
        ui.screen("terrabuilder: Terraria for the Nintendo Switch", [
            "",
            "Builds a Switch homebrew app (NRO) from your own copy of Terraria.",
            "Put your files in these folders next to this script:",
            "",
            f"  {ui.bold('Terraria/  ')}  your Terraria: GOG 1.4.5.8 for Linux for a vanilla build; tModLoader",
            f"  {ui.bold('           ')}  builds use the Content/ of Terraria 1.4.4.9 or 1.4.5.x (GOG or Steam)",
            f"  {ui.bold('tModLoader/')}  optional: tModLoader {cli.TMOD_RELEASE['version']} (otherwise downloaded)",
            f"  {ui.bold('Mods/      ')}  .tmod files to add: Steam Workshop downloads or your own builds",
            f"  {ui.bold('Output/    ')}  finished builds, ready to copy to your SD card",
            "",
            ui.dim(f"  {ROOT}"),
        ], {"enter": "continue"})

    def check_machine(self) -> bool:
        set_up = False
        while True:
            with ui.Spinner("Checking this computer"):
                checks = cli.machine_checks(self.workdir)
            failed = [c for c in checks if c["required"] and not c["ok"]]
            if not failed:
                self.tc = tclib.load(self.workdir / "toolchain")
                return True
            lines = ["", *(f"  {ui.OK if c['ok'] else ui.FAIL} {c['name']}: {c['detail']}" for c in checks if c["required"])]
            # The prebuilt toolchain and the container image are the two things set up can provide.
            fixable = all(c["name"] == "toolchain" or c["name"].startswith("image ") for c in failed)
            if set_up or not fixable:
                lines += ["", "  Fix the items marked " + ui.FAIL + "; docs/BUILDING.md has the setup steps."]
                ui.screen("This computer can't build yet", lines, {"enter": "exit"})
                return False
            fixes = []
            if any(c["name"] == "toolchain" for c in failed):
                fixes.append(f"downloads the prebuilt toolchain ({cli.TOOLCHAIN_BUNDLE['bytes'] / 2**20:.0f} MiB)")
            if any(c["name"].startswith("image ") for c in failed):
                fixes.append("builds the container image that builds run in (about 5 GB)")
            lines += ["", "  Set up " + " and ".join(fixes) + "."]
            ui.screen("This computer needs setting up", lines, {"enter": "set up"})
            if run_logged("Setting up", "setup", "Setup failed", lambda _: cli.set_up_from_bundle(self.workdir)) is None:
                return False
            set_up = True

    def refresh_inputs(self) -> None:
        with ui.Spinner("Looking at your folders"):
            self.game = find_game()
            self.content = find_content()
            self.tmod = find_tmodloader(self.workdir)

    def step_target(self) -> str:
        self.refresh_inputs()
        if not self.tc.has("tmod-runtime"):
            tmod_disabled = "needs the toolchain's tmod-runtime component: see below"
            tmod_detail = (f"The toolchain at {self.tc.root} has no tmod-runtime component; "
                           "`./terrabuilder toolchain --bundle --replace` installs the prebuilt toolchain, which has it.")
        else:
            tmod_disabled = "" if self.content["ready"] else self.content["summary"] + ": see below"
            tmod_detail = self.tmod["detail"] + " " + self.content["detail"]
        items = [
            ui.Item("vanilla", "Terraria", note=self.game["summary"], kind="radio", checked=self.target == "vanilla",
                    disabled="" if self.game["ready"] else self.game["summary"] + ": see below",
                    detail=self.game["detail"]),
            ui.Item("tmodloader", "tModLoader with mods", note=self.tmod["summary"] + " · experimental", kind="radio",
                    checked=self.target == "tmodloader",
                    disabled=tmod_disabled, detail=tmod_detail),
        ]
        event, key, _ = ui.menu("Step 1 · What do you want to build?", items, keys={"r": "check folders again"},
                                focus=self.target, enter_label="choose")
        if event == "back":
            raise ui.Quit
        if event == "key":
            return "target"
        self.target = key
        return "mods" if key == "tmodloader" else "summary"

    def load_mods(self) -> None:
        catalog = cli.curated_mod_catalog()
        files, unreadable = find_mod_files()
        file_by_id = {source["id"]: source for source in files}
        if files:
            dlls = self.patched_tmodloader()
            if dlls is None:
                for source in files:
                    source["problem"] = "the hook check could not be prepared (its log is in Output/logs)"
            else:
                with ui.Spinner(f"Checking {len(files)} mod file(s) in Mods/ (the first check builds the checker)"):
                    for source in files:
                        check_mod_file(source, file_by_id, dlls, self.tmod["path"], self.workdir, self.tc)
        entries = {f"catalog:{s['id']}": s for s in catalog.values()}
        entries.update({f"git:{s['id']}": s for s in load_git_mods()})
        entries.update({f"file:{s['id']}": s for s in files})
        previous = self.choice.chosen if self.choice else []
        self.choice = ModChoice(entries)
        self.choice.chosen = [key for key in previous if key in entries and key not in self.choice.blocked]
        self.unreadable = unreadable

    def patched_tmodloader(self) -> Path | None:
        """The tModLoader patch stage the hook check reads (cli.tmod_dlls_stage). A missing stage is
        built, after downloading tModLoader if needed, with a progress line; None if that failed."""
        release = self.tmod["path"]
        if release is not None:
            with ui.Spinner("Looking for the patched tModLoader"):
                dlls = cli.tmod_dlls_dir(cli.tmod_release_hash(release), self.workdir, self.tc)
            if (dlls / "dlls.json").is_file():
                return dlls

        def prepare(progress: ui.Progress) -> Path:
            nonlocal release
            if release is None:
                progress.phase(f"Downloading tModLoader {cli.TMOD_RELEASE['version']}")
                release = cli.ensure_tmodloader_release(self.workdir)
                self.tmod = find_tmodloader(self.workdir)
            progress.phase("Patching tModLoader")
            return cli.tmod_dlls_stage(release, cli.tmod_release_hash(release), self.workdir, self.tc)

        done = run_logged("Preparing the mod check", "mod-check", "Preparing the mod check failed", prepare)
        return None if done is None else done[0]

    def mod_items(self) -> list[ui.Item]:
        active, needed_by = self.choice.resolve()
        items = []
        sections = (("catalog", "Open-source mods (built from pinned source)"),
                    ("git", "From GitHub (built from source)"),
                    ("file", "Your .tmod files (Mods/)"))
        for kind, title in sections:
            keys = [k for k, s in self.choice.entries.items() if s["kind"] == kind and not s.get("library_only")]
            if not keys and kind != "file":
                continue
            items.append(ui.Item(f"header:{kind}", title, kind="header"))
            for key in sorted(keys, key=lambda k: self.choice.entries[k]["display_name"].lower()):
                source = self.choice.entries[key]
                note, detail = self.describe_mod(source)
                needs = needed_by.get(key)
                items.append(ui.Item(key, source["display_name"], note=note, detail=detail, checked=key in active,
                                     locked=f"needed by {', '.join(sorted(set(needs)))}" if needs else "",
                                     disabled=self.choice.blocked.get(key, "")))
            if kind == "file" and not keys:
                items.append(ui.Item("none", "No .tmod files in Mods/ yet", kind="header"))
        items.append(ui.Item("add-git", "Add a mod from GitHub…" if ui.UNICODE else "Add a mod from GitHub...", kind="action",
                             detail="Builds a tModLoader mod from its source repository, pinned to the current commit."))
        items.append(ui.Item("rescan", "Check the Mods folder again", kind="action",
                             detail=f"Reads {shown(MODS_DIR)} again after you add or remove .tmod files."))
        return items

    @staticmethod
    def describe_mod(source: dict) -> tuple[str, str]:
        if source["kind"] == "catalog":
            status = "tested on Switch" if source.get("tested") else "experimental"
            spdx = source.get("spdx_license", "")
            short = "no license" if spdx in ("", "NOASSERTION", "NONE") else spdx
            return (f"{source['version']} · {short} · {status}",
                    f"License: {source['license']}. {source.get('notes', '')} "
                    f"Source: {source['repo']} at {source['commit'][:10]}.")
        if source["kind"] == "git":
            return (f"{source['version']} · {source['license']}",
                    f"Source: {source['repo']} at {source['commit'][:10]}"
                    + (f", folder {source['subdir']}" if source.get("subdir") else "") + ". Press d to remove it.")
        built_for = ".".join(source["loader_version"].split(".")[:2])
        return (f"{source['version']} · built for tModLoader {built_for}",
                f"{shown(Path(source['path']))} by {source.get('author') or 'unknown'}. {source.get('report', '')}")

    def step_mods(self) -> str:
        if self.choice is None:
            self.load_mods()
        intro = [ui.dim("  Pick the mods to build in. Mods they need are added for you."),
                 *(ui.yellow("  " + line) for line in self.unreadable),
                 *([ui.cyan("  " + self.flash)] if self.flash else [])]
        self.flash = ""

        def on_toggle(item: ui.Item) -> str | None:
            message = self.choice.toggle(item.key, item.checked)
            active, needed_by = self.choice.resolve()
            for other in items:
                if other.kind == "check":
                    other.checked = other.key in active
                    needs = needed_by.get(other.key)
                    other.locked = f"needed by {', '.join(sorted(set(needs)))}" if needs else ""
            return message

        items = self.mod_items()
        event, key, self.mods_focus = ui.menu("Step 2 of 3 · Choose mods", items, intro=intro, on_toggle=on_toggle,
                                              keys={"d": "remove GitHub mod"}, focus=self.mods_focus)
        focus = self.mods_focus
        if event == "back":
            return "target"
        if event == "action" and key == "add-git":
            taken = {s["id"] for s in self.choice.entries.values()}
            message = add_git_mod(self.workdir, taken)
            self.load_mods()
            self.flash = message
            return "mods"
        if event == "action" and key == "rescan":
            self.load_mods()
            return "mods"
        if event == "key" and key == "d":
            if focus.startswith("git:"):
                mod_id = focus.split(":", 1)[1]
                save_git_mods([e for e in load_git_mods() if e["id"] != mod_id])
                self.load_mods()
                self.flash = f"Removed {mod_id} from {shown(GIT_MODS)}."
            else:
                self.flash = "Only mods added from GitHub can be removed here."
            return "mods"
        return "summary"

    def step_summary(self) -> str:
        lines = [""]
        if self.target == "vanilla":
            lines += [f"  {ui.bold('Build      ')} Terraria (vanilla) from {shown(self.game['path'])}",
                      f"  {ui.bold('Time       ')} up to an hour the first time; later builds reuse unchanged steps"]
            out = OUT_DIR / f"Terraria{cli.profile_suffix(self.profile)}"
        else:
            mods, sources = self.choice.build_input()
            shown_mods = [describe(sources[m]) for m in mods if not sources[m].get("library_only")]
            lines += [f"  {ui.bold('Build      ')} tModLoader {cli.TMOD_RELEASE['version']} (experimental)",
                      f"  {ui.bold('tModLoader ')} {self.tmod['summary']}",
                      f"  {ui.bold('Content    ')} Terraria {self.content['version']} from {shown(self.content['content'])}",
                      f"  {ui.bold('Mods       ')} " + (shown_mods[0] if shown_mods else "none")]
            lines += [f"               {name}" for name in shown_mods[1:]]
            lines += [f"  {ui.bold('Time       ')} about an hour the first time; later builds reuse unchanged steps"]
            out = OUT_DIR / f"tModLoader{cli.profile_suffix(self.profile)}"
        lines += [f"  {ui.bold('Build type ')} {PROFILES[self.profile]}",
                  f"  {ui.bold('Output     ')} {shown(out / 'sdcard')}  (copy it to your SD card)"]
        step = "Step 3 of 3" if self.target == "tmodloader" else "Step 2 of 2"
        key = ui.screen(f"{step} · Ready to build", lines,
                        {"enter": "build", "t": "change build type", "esc": "back"})
        if key == "esc":
            return "mods" if self.target == "tmodloader" else "target"
        if key == "t":
            order = list(PROFILES)
            self.profile = order[(order.index(self.profile) + 1) % len(order)]
            return "summary"
        return "build"

    def build(self) -> int:
        title = "Terraria" if self.target == "vanilla" else "tModLoader"
        done = run_logged(f"Building {title}", self.target, "Build failed", self.run_build)
        if done is None:
            return 1
        self.finish(*done)
        return 0

    def run_build(self, progress: ui.Progress) -> Path:
        if self.target == "vanilla":
            return cli.build_vanilla(self.game["path"], workdir=self.workdir, out_root=OUT_DIR, profile=self.profile)
        mods, sources = self.choice.build_input()
        tmod_dir = self.tmod["path"]
        if tmod_dir is None:
            progress.phase(f"Downloading tModLoader {cli.TMOD_RELEASE['version']}")
            tmod_dir = cli.ensure_tmodloader_release(self.workdir)
        return cli.build_tmodloader(tmod_dir, mods, sources, workdir=self.workdir, out_root=OUT_DIR,
                                    profile=self.profile, terraria=self.content["game"])

    def finish(self, out_dir: Path, took: float, log: Path) -> None:
        sd = out_dir / "sdcard"
        receipt = json.loads((out_dir / "receipt.json").read_text())
        nro = Path(receipt["nro"]["path"])
        lines = ["", ui.green(ui.bold(f"Done in {ui.elapsed(took)}.")), "",
                 "Copy everything inside this folder to the root of your SD card (replace existing files):",
                 "  " + ui.bold(str(sd))]
        windows = windows_path(sd)
        if windows:
            lines.append("  " + ui.dim(windows))
        contents = [(f"switch/{nro.name}", "the app; start it from the Homebrew Menu")]
        if self.target == "tmodloader":
            contents.append(("switch/tmodloader/", "the mods; they only work with this build"))
        contents.append(("mono/", "runtime files"))
        width = max(len(path) for path, _ in contents) + 3
        lines += ["", "It holds:", *(f"  {path:<{width}}{what}" for path, what in contents)]
        if self.target == "tmodloader":
            lines += ["", ui.dim("Notes:")]
            for note in cli.TMOD_WARNING.splitlines()[1:]:
                wrapped = ui.wrap(note.removeprefix("- "), ui.size()[0] - 6, "    ")
                wrapped[0] = "  - " + wrapped[0].lstrip()
                lines += [ui.dim(line) for line in wrapped]
        lines += ["", ui.dim(f"Build log: {log}"), ""]
        ui.write("\n".join(lines) + "\n")


def main(workdir: Path) -> int:
    if not (sys.stdin.isatty() and sys.stdout.isatty()):
        raise SystemExit("The guided build needs a terminal. Scripts can use: ./terrabuilder build --help")
    try:
        with ui.Session():
            return Wizard(workdir).run()
    except ui.Quit:
        ui.write("\nNothing was built.\n")
        return 0
    except KeyboardInterrupt:
        ui.write("\n\nCancelled.\n")
        return 130
