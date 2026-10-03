#!/usr/bin/env python3
"""Compile main.o and link the launcher, AOT objects and toolchain libraries into an NRO.

Runs inside the devkitA64 build image with /work (repository, ro), /build (work
directory), /toolchain (ro) and the /mono-nx, /mono-nx/native and /fna-install
toolchain aliases mounted. Every input is checked against the sha256 recorded by the
stage that produced it, and the RomFS assemblies must be the ones the AOT objects
were compiled from.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
from pathlib import Path

GCC = "/opt/devkitpro/devkitA64/bin/aarch64-none-elf-gcc"
ELF2NRO = "/opt/devkitpro/tools/bin/elf2nro"
WORK = Path("/work")
HEADER_TEXT = "/* Generated only after all AArch64 objects passed verification. */\n"
CORELIB_IN_ROMFS = "mono/lib_net9.0/System.Private.CoreLib.dll"
# Mono's LLVM output keeps absolute pointers in .rodata. A PIE NRO must relocate them at
# load time, so that .rodata goes into the writable data segment.
LLVM_SECTIONS = (
    "\n/* Mono LLVM AOT sidecars: absolute pointers in .rodata need load-time relocation. */\n"
    "SECTIONS\n{\n    .mono_llvm_rodata : ALIGN(16)\n    {\n"
    "        /* A writable input first makes ld mark the output section SHF_WRITE. */\n"
    "        *-llvm.o(.data .data.*)\n        *-llvm.o(.rodata .rodata.*)\n"
    "    } :data\n}\nINSERT BEFORE .data.rel.ro;\n"
)


def sha256(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def checked(path: Path, expected: str, what: str) -> Path:
    if not path.is_file() or sha256(path) != expected:
        raise SystemExit(f"{what} is missing or changed since it was built: {path}")
    return path


def aot_inputs(manifest: dict, romfs: Path) -> tuple[list[str], Path]:
    if manifest.get("status") != "complete":
        raise SystemExit("AOT build manifest is not complete")
    modules = [manifest["corelib"], *manifest["modules"]]
    objects: list[Path] = []
    sidecars: dict[Path, Path] = {}
    for module in modules:
        name = module["assembly"]["name"]
        obj = checked(Path(module["object"]), module["object_sha256"], f"{name} AOT object")
        objects.append(obj)
        if module.get("llvm_object"):
            sidecars[obj] = checked(Path(module["llvm_object"]), module["llvm_object_sha256"], f"{name} LLVM AOT object")
        assembly = Path(module["assembly"]["path"])
        loaded = romfs / CORELIB_IN_ROMFS if module is manifest["corelib"] else assembly
        if (module is manifest["corelib"] or assembly.is_relative_to(romfs)) and (
                not loaded.is_file() or sha256(loaded) != module["assembly"]["sha256"]):
            raise SystemExit(f"RomFS {loaded} is not the {name} assembly its AOT object was compiled from")
    if len({p.name for p in objects}) != len(objects):
        raise SystemExit("duplicate AOT object file names")
    header = Path(manifest["header"])
    expected = HEADER_TEXT + "".join(f"REGISTER_AOT_MODULE({m['symbol']});\n" for m in modules)
    if not header.is_file() or header.read_text() != expected:
        raise SystemExit(f"AOT registration header does not match the build manifest: {header}")
    # The native verifier maps linked method tables to modules by object name order;
    # each LLVM sidecar directly follows its module object.
    ordered: list[str] = []
    for obj in sorted(objects, key=lambda p: p.name):
        ordered.append(str(obj))
        if obj in sidecars:
            ordered.append(str(sidecars[obj]))
    return ordered, header.parent


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--aot-manifest", type=Path, required=True, help="complete build-manifest.json from compile_terraria_aot.py")
    ap.add_argument("--romfs", type=Path, required=True)
    ap.add_argument("--launcher", type=Path, required=True, help="directory holding launcher-build.json")
    ap.add_argument("--out", type=Path, required=True, help="fresh output directory")
    ap.add_argument("--main-source", type=Path, default=WORK / "native/interpreter/source/main.c")
    ap.add_argument("--main-define", action="append", default=[], metavar="NAME[=VALUE]")
    ap.add_argument("--wrap", action="append", default=[], metavar="SYMBOL")
    ap.add_argument("--title", default="Terraria")
    ap.add_argument("--author", default="terrabuilder-nx")
    ap.add_argument("--version", default="1.4.5.8")
    ap.add_argument("--link-arguments", type=Path, default=WORK / "native/interpreter/link-arguments.json")
    args = ap.parse_args()
    out = args.out
    if out.exists():
        raise SystemExit(f"fresh output directory required: {out}")

    aot, header_dir = aot_inputs(json.loads(args.aot_manifest.read_text()), args.romfs.resolve())
    launcher_json = args.launcher / "launcher-build.json"
    launcher = json.loads(launcher_json.read_text())

    def launcher_object(name: str) -> str:
        entry = launcher["objects"].get(name)
        if entry is None:
            raise SystemExit(f"launcher build has no {name}")
        return str(checked(Path(entry["path"]), entry["sha256"], f"launcher object {name}"))

    link = json.loads(args.link_arguments.read_text())
    extra = [launcher_object(n) for n in link["extra_objects"] if n in launcher["objects"]]
    objects = [launcher_object(n) for n in link["launcher_objects"]]
    out.mkdir(parents=True)
    commands: list[dict] = []
    env = dict(os.environ, TOPDIR=str(out))

    def run(argv: list, label: str) -> None:
        argv = list(map(str, argv))
        result = subprocess.run(argv, env=env, cwd=out, capture_output=True, text=True)
        (out / f"{label}.log").write_text(result.stdout + result.stderr)
        commands.append({"argv": argv, "exit": result.returncode, "log": f"{label}.log"})
        if result.returncode:
            raise SystemExit(f"{label} failed:\n{result.stdout}{result.stderr}")
        print("PASS " + label, flush=True)

    main_obj = out / "main.o"
    run([WORK / "scripts/native/compile_main.sh", args.main_source, main_obj,
         "-DMONO_NX_EMBEDDED_BCL=1", "-DMONO_NX_FATAL_DIAG=1", *("-D" + d for d in args.main_define),
         "-I" + str(header_dir)], "compile-main")

    # aot-method-tables.specs loads $TOPDIR/aot-method-tables.ld.
    script = out / "aot-method-tables.ld"
    shutil.copy2(WORK / "native/interpreter/aot-method-tables.ld", script)
    if any(p.endswith("-llvm.o") for p in aot):
        with script.open("a") as stream:
            stream.write(LLVM_SECTIONS)

    libraries: list[str] = []
    for value in link["libraries"]:
        if value == "@AOT@":
            libraries += aot
            continue
        if value.startswith("/") and not Path(value).is_file():
            raise SystemExit(f"missing link input {value}")
        libraries.append(value)
    elf = out / "mono_nx_fna.elf"  # .nx-module-name is the ELF file name without extension
    ldflags = [f.replace("@MAP@", str(out / "link.map")) for f in link["ldflags"]]
    run([GCC, *ldflags, *extra, *("-Wl,--wrap=" + w for w in args.wrap), main_obj, *objects,
         *link["library_dirs"], *libraries, "-o", elf], "link")

    nacp = bytearray(0x4000)
    title, author = args.title.encode()[:0x1ff], args.author.encode()[:0xff]
    for language in range(16):
        start = language * 0x300
        nacp[start:start + 0x200] = title.ljust(0x200, b"\0")
        nacp[start + 0x200:start + 0x300] = author.ljust(0x100, b"\0")
    nacp[0x3060:0x3070] = args.version.encode()[:0xf].ljust(0x10, b"\0")
    nacp_path = out / "mono_nx_fna.nacp"
    nacp_path.write_bytes(nacp)
    nro = out / "mono_nx_fna.nro"
    run([ELF2NRO, elf, nro, f"--nacp={nacp_path}", f"--romfsdir={args.romfs}"], "package")

    report = {
        "schema": "terrabuilder-native/1",
        "title": args.title, "author": args.author, "version": args.version,
        "aot_manifest": str(args.aot_manifest), "aot_objects": aot,
        "launcher_manifest_sha256": sha256(launcher_json),
        "link_arguments_sha256": sha256(args.link_arguments),
        "commands": commands,
        "main_object_sha256": sha256(main_obj),
        "elf_sha256": sha256(elf),
        "nro_sha256": sha256(nro), "nro_bytes": nro.stat().st_size,
    }
    (out / "native-build.json").write_text(json.dumps(report, indent=2) + "\n")
    print(f"NRO {nro} {report['nro_sha256']}", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
