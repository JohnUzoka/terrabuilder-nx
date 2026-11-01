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

# Optional component directory -> files it cannot be used without. A toolchain may lack an
# optional component; when the directory exists, the manifest records and verifies it like a
# required one, and builds that need it check `Toolchain.has`.
OPTIONAL_COMPONENTS: dict[str, tuple[str, ...]] = {
    # The runtime tModLoader builds were validated on (scripts/tmod/runtime/README.md). Its
    # aot-compiler/ holds the tModLoader mono-aot-cross; opt, llc and the libc++ libraries in it
    # are symlinks to the identical files in the aot-compiler component.
    "tmod-runtime": (
        "lib/libmonosgen-2.0.a",
        *(f"lib/libmono-component-{name}-static.a" for name in RUNTIME_COMPONENT_LIBS),
        "lib/libSystem.Native.a",
        "framework/System.Runtime.dll",
        "corelib/System.Private.CoreLib.dll",
        "aot-compiler/mono-aot-cross",
        "aot-compiler/opt",
        "aot-compiler/llc",
        "aot-compiler/libc++.so.1",
        "aot-compiler/libc++abi.so.1",
        "corelib-aot/manifest.json",
        "framework-aot/framework-aot.json",
        "System.Reflection.Metadata.dll",
        "THIRD_PARTY_NOTICES.md",
    ),
}

# Container paths the launcher sources, link line and scripts are written against.
ALIASES = {"sdk": "/mono-nx", "mono-nx-native": "/mono-nx/native", "native-deps": "/fna-install"}
# A component mounted inside another one's mount (mono-nx-native at /mono-nx/native) makes the
# container runtime create that mount point in the host directory (sdk/native). It belongs to no
# component, so scans skip it while it is empty.
MOUNT_POINTS = {f"{outer}{inner_path[len(outer_path):]}"
                for outer, outer_path in ALIASES.items()
                for inner, inner_path in ALIASES.items()
                if inner != outer and inner_path.startswith(outer_path + "/")}

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
            rel_dir = here.relative_to(root).as_posix()
            if rel_dir not in MOUNT_POINTS:
                empty.append(rel_dir)
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
    """Record every required component and each optional component whose directory exists."""
    files: dict[str, dict] = {}
    empty: list[str] = []
    components = {}
    present = {**COMPONENTS, **{name: required for name, required in OPTIONAL_COMPONENTS.items()
                                if (root / name).is_dir()}}
    missing = [f"{name}/{rel}" for name, required in present.items() for rel in required
               if not (root / name / rel).is_file()]
    if missing:
        raise ToolchainError(root, ["missing " + rel for rel in missing])
    for name in present:
        part, part_empty = scan(root, name)
        files.update(part)
        empty.extend(part_empty)
        regular = [e for e in part.values() if "symlink" not in e]
        components[name] = {"files": len(part), "bytes": sum(e["bytes"] for e in regular),
                            "digest": component_digest(name, part, part_empty)}
    extra = sorted(p.name for p in root.iterdir() if p.name != MANIFEST and p.name not in present)
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

    def has(self, component: str) -> bool:
        """Whether the manifest records `component` (always true for required components)."""
        return component in self.manifest["components"]

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


def _recorded_components(root: Path, manifest: dict) -> tuple[list[str], list[str]]:
    """(components to check, problems): every required component plus each recorded optional one.
    An optional component directory the manifest does not record is unexpected."""
    recorded = manifest.get("components", {})
    problems = [f"component {name} missing from manifest" for name in COMPONENTS if name not in recorded]
    problems += [f"unknown component {name} in manifest" for name in recorded
                 if name not in COMPONENTS and name not in OPTIONAL_COMPONENTS]
    problems += [f"unexpected top-level entry {name} (optional component not in manifest)"
                 for name in OPTIONAL_COMPONENTS if name not in recorded and (root / name).exists()]
    names = [name for name in COMPONENTS if name in recorded]
    names += [name for name in OPTIONAL_COMPONENTS if name in recorded]
    return names, problems


def load(root: Path) -> Toolchain:
    """Quick check (presence, type, size) for every recorded file; `verify` rehashes."""
    manifest = _read_manifest(root)
    _, problems = _recorded_components(root, manifest)
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
    names, problems = _recorded_components(root, manifest)
    for name in names:
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
