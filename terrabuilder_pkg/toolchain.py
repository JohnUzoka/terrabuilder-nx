"""The toolchain every build consumes: <workdir>/toolchain, mounted read-only at /toolchain.

It holds only open-source runtime/compiler/library artifacts; nothing in it is derived
from game files. manifest.json records every file so builds can key their stages on
component digests and `terrabuilder toolchain verify` can detect any change.
"""
from __future__ import annotations

import hashlib
import json
import os
import time
from pathlib import Path

SCHEMA = "terrabuilder-toolchain/1"
MOUNT = "/toolchain"
MANIFEST = "manifest.json"
RUNTIME_COMPONENT_LIBS = ("debugger", "diagnostics_tracing-stub", "hot_reload-stub", "marshal-ilgen")

# Component directory -> files a build cannot start without.
COMPONENTS: dict[str, tuple[str, ...]] = {
    "sdk": (
        "dotnet_runtime/artifacts/bin/mono/linux.x64.Debug/cross/linux-x64/libnx-arm64/mono-aot-cross",
        "dotnet_runtime/artifacts/bin/native/net9.0-libnx-Debug-arm64/libSystem.Native.a",
        "icu/libnx/lib/libicuuc.a",
        "icu/libnx/share/icu/77.1/icudt77l.dat",
    ),
    "mono-nx-native": ("shared/core.c",),
    "native-deps": ("lib/libFNA3D.a", "lib/libFAudio.a", "lib/libmojoshader.a"),
    "mesa": ("lib/libEGL.a", "lib/libglapi.a"),
    "runtime": (
        "lib/libmonosgen-2.0.a",
        *(f"lib/libmono-component-{name}-static.a" for name in RUNTIME_COMPONENT_LIBS),
        "corelib/System.Private.CoreLib.dll",
        "framework/System.Runtime.dll",
    ),
    "aot-compiler": ("mono-aot-cross", "opt", "llc", "libc++.so.1", "libc++abi.so.1"),
    "framework-aot": ("framework-aot.json",),
    "facades": ("mscorlib.dll", "System.IO.Packaging.dll", "System.Security.Permissions.dll"),
    "cecil": ("Mono.Cecil.dll",),
    "dotnet": ("dotnet",),
    "notices": ("THIRD_PARTY_NOTICES.md",),
}

# Container paths the launcher sources, link line and scripts are written against.
ALIASES = {"sdk": "/mono-nx", "mono-nx-native": "/mono-nx/native", "native-deps": "/fna-install"}

# Relative to the toolchain root; read from the SD card before RomFS is mounted.
ICU_DATA = "sdk/icu/libnx/share/icu/77.1/icudt77l.dat"


class ToolchainError(Exception):
    def __init__(self, root: Path, problems: list[str]):
        self.root = root
        self.problems = problems
        super().__init__(f"toolchain at {root} is not usable: " + "; ".join(problems[:5])
                         + (f" (+{len(problems) - 5} more)" if len(problems) > 5 else ""))


def _sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def scan(root: Path, component: str, *, hashes: bool = True) -> tuple[dict[str, dict], list[str]]:
    """Return ({rel: entry}, empty_dirs) for one component; rel paths include the component."""
    files: dict[str, dict] = {}
    empty: list[str] = []
    base = root / component
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames.sort()
        here = Path(dirpath)
        names = sorted(filenames) + [d for d in dirnames if (here / d).is_symlink()]
        if not names and not dirnames:
            empty.append(here.relative_to(root).as_posix())
        for name in names:
            path = here / name
            rel = path.relative_to(root).as_posix()
            if path.is_symlink():
                files[rel] = {"symlink": os.readlink(path)}
                continue
            info = path.stat()
            entry = {"bytes": info.st_size, "exec": bool(info.st_mode & 0o111)}
            if hashes:
                entry["sha256"] = _sha256(path)
            files[rel] = entry
    return files, empty


def component_digest(component: str, files: dict[str, dict], empty_dirs: list[str]) -> str:
    prefix = component + "/"
    h = hashlib.sha256()
    lines = []
    for rel, entry in files.items():
        if not rel.startswith(prefix):
            continue
        inner = rel[len(prefix):]
        if "symlink" in entry:
            lines.append(f"{inner}\0symlink\0{entry['symlink']}\n")
        else:
            lines.append(f"{inner}\0file\0{entry['sha256']}\0{int(entry['exec'])}\n")
    for rel in empty_dirs:
        if rel.startswith(prefix):
            lines.append(f"{rel[len(prefix):]}\0dir\n")
    for line in sorted(lines):
        h.update(line.encode())
    return h.hexdigest()


def write_manifest(root: Path, origin: str, provenance: dict, lock_sha256: str | None = None) -> dict:
    files: dict[str, dict] = {}
    empty: list[str] = []
    components = {}
    missing = [f"{name}/{rel}" for name, required in COMPONENTS.items() for rel in required
               if not (root / name / rel).is_file()]
    if missing:
        raise ToolchainError(root, ["missing " + rel for rel in missing])
    for name in COMPONENTS:
        part, part_empty = scan(root, name)
        files.update(part)
        empty.extend(part_empty)
        regular = [e for e in part.values() if "symlink" not in e]
        components[name] = {"files": len(part), "bytes": sum(e["bytes"] for e in regular),
                            "digest": component_digest(name, part, part_empty)}
    extra = sorted(p.name for p in root.iterdir() if p.name != MANIFEST and p.name not in COMPONENTS)
    if extra:
        raise ToolchainError(root, ["unexpected top-level entry " + name for name in extra])
    manifest = {
        "schema": SCHEMA,
        "origin": origin,
        "created_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "lock_sha256": lock_sha256,
        "provenance": provenance,
        "components": components,
        "files": dict(sorted(files.items())),
        "empty_dirs": sorted(empty),
    }
    pending = root / (MANIFEST + ".pending")
    pending.write_text(json.dumps(manifest, indent=1) + "\n")
    pending.replace(root / MANIFEST)
    return manifest


class Toolchain:
    def __init__(self, root: Path, manifest: dict):
        self.root = root
        self.manifest = manifest

    def digest(self, component: str) -> str:
        return self.manifest["components"][component]["digest"]

    def digests(self, *components: str) -> str:
        return "".join(f"{name}={self.digest(name)};" for name in components)

    def path(self, rel: str) -> Path:
        return self.root / rel

    def mounts(self) -> list[str]:
        args = ["-v", f"{self.root}:{MOUNT}:ro"]
        for component, target in ALIASES.items():
            args += ["-v", f"{self.root / component}:{target}:ro"]
        return args


def _read_manifest(root: Path) -> dict:
    path = root / MANIFEST
    if not path.is_file():
        raise ToolchainError(root, ["manifest.json not found"])
    try:
        manifest = json.loads(path.read_text())
    except ValueError as error:
        raise ToolchainError(root, [f"manifest.json unreadable: {error}"]) from error
    if manifest.get("schema") != SCHEMA:
        raise ToolchainError(root, [f"manifest schema {manifest.get('schema')!r}, expected {SCHEMA}"])
    return manifest


def load(root: Path) -> Toolchain:
    """Quick check (presence, type, size) for every recorded file; `verify` rehashes."""
    manifest = _read_manifest(root)
    problems = [f"component {name} missing from manifest" for name in COMPONENTS
                if name not in manifest.get("components", {})]
    for rel, entry in manifest.get("files", {}).items():
        path = root / rel
        if "symlink" in entry:
            if not path.is_symlink() or os.readlink(path) != entry["symlink"]:
                problems.append("changed symlink " + rel)
            continue
        try:
            info = path.lstat()
        except FileNotFoundError:
            problems.append("missing " + rel)
            continue
        if not path.is_file() or path.is_symlink() or info.st_size != entry["bytes"]:
            problems.append("changed " + rel)
    for rel in manifest.get("empty_dirs", []):
        if not (root / rel).is_dir():
            problems.append("missing directory " + rel)
    if problems:
        raise ToolchainError(root, problems)
    return Toolchain(root, manifest)


def verify(root: Path) -> list[str]:
    """Rehash everything; return a list of problems (empty when the toolchain is intact)."""
    manifest = _read_manifest(root)
    recorded = manifest.get("files", {})
    problems: list[str] = []
    for name in COMPONENTS:
        if name not in manifest.get("components", {}):
            problems.append(f"component {name} missing from manifest")
            continue
        files, empty = scan(root, name)
        prefix = name + "/"
        expected = {rel: entry for rel, entry in recorded.items() if rel.startswith(prefix)}
        for rel in sorted(expected.keys() - files.keys()):
            problems.append("missing " + rel)
        for rel in sorted(files.keys() - expected.keys()):
            problems.append("unexpected " + rel)
        for rel in sorted(files.keys() & expected.keys()):
            if files[rel] != expected[rel]:
                problems.append("changed " + rel)
        recorded_empty = {rel for rel in manifest.get("empty_dirs", []) if rel.startswith(prefix)}
        for rel in sorted(recorded_empty ^ set(empty)):
            problems.append("directory layout changed: " + rel)
        if not problems and component_digest(name, files, empty) != manifest["components"][name]["digest"]:
            problems.append(f"component {name} digest mismatch")
    return problems
