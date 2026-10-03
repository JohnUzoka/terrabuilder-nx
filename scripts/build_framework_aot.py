#!/usr/bin/env python3
"""Build the toolchain's framework-aot component from source; run inside monobuild-llvm.

CoreLib, System.Text.RegularExpressions and System.Collections.Concurrent are AOT-compiled
once (LLVM) and linked by every vanilla build instead of being recompiled per build (see
docs/BUILDING.md 3.6). This reproduces that component from a from-source runtime build:
same mono-aot-cross invocation shape as compile_terraria_aot.py's LLVM path (so the output
satisfies its --framework-aot consumption contract, the terrabuilder-framework-aot/1 schema
checked by framework_modules()), adapted from the original scripts/release_bcl/build_v72.py
experiment that produced the reference artifact (confirms CORELIB_AOT_EXTRA and the
CoreLib-specific "corlib" jit_code_start symbol prefix below).
"""
from __future__ import annotations

import argparse
import os
import re
import shutil
import struct
import subprocess
import sys
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from compile_terraria_aot import inspect_object, method_counts, run_logged, sha256, write_json

# CoreLib is the root assembly; its trampoline pools must be sized for the whole
# game+framework's eventual call graph, not just its own (docs/BUILDING.md 3.6).
CORELIB_AOT_EXTRA = ("full,interp,static,ntrampolines=65536,nimt-trampolines=8192,"
                     "ngsharedvt-trampolines=4096,nunbox-arbitrary-trampolines=2048,")
# (module name, source subdirectory under --corelib-dir/--framework-dir, extra --path dirs)
MODULES = (
    ("System.Private.CoreLib", "corelib", ()),
    ("System.Text.RegularExpressions", "framework", ("framework",)),
    ("System.Collections.Concurrent", "framework", ("framework",)),
)


def pe_mvid(path: Path) -> str:
    """Read the ECMA-335 Module table's Mvid directly from a managed PE; no dotnet/mono
    needed. Ported from scripts/tmod/verify_aot_dependencies.py's pe_mvid (self-contained,
    already used elsewhere in this repo to verify AOT dependency MVIDs)."""
    data = path.read_bytes()

    def u16(o: int) -> int: return struct.unpack_from("<H", data, o)[0]
    def u32(o: int) -> int: return struct.unpack_from("<I", data, o)[0]
    def u64(o: int) -> int: return struct.unpack_from("<Q", data, o)[0]

    pe_offset = u32(0x3c)
    if data[pe_offset:pe_offset + 4] != b"PE\0\0":
        raise ValueError(f"not a PE file: {path}")
    section_count = u16(pe_offset + 6)
    optional_size = u16(pe_offset + 20)
    optional = pe_offset + 24
    magic = u16(optional)
    data_directories = optional + (112 if magic == 0x20b else 96)
    cli_rva = u32(data_directories + 14 * 8)
    if cli_rva == 0:
        raise ValueError(f"not a managed PE file: {path}")
    section_offset = optional + optional_size
    sections = []
    for index in range(section_count):
        section = section_offset + index * 40
        sections.append((u32(section + 12), u32(section + 16), u32(section + 20), u32(section + 8)))

    def rva_to_offset(rva: int) -> int:
        for virtual_address, raw_size, raw_pointer, virtual_size in sections:
            if virtual_address <= rva < virtual_address + max(raw_size, virtual_size):
                return raw_pointer + (rva - virtual_address)
        raise ValueError(f"RVA 0x{rva:x} not mapped: {path}")

    cli = rva_to_offset(cli_rva)
    metadata = rva_to_offset(u32(cli + 8))
    if data[metadata:metadata + 4] != b"BSJB":
        raise ValueError(f"no metadata root: {path}")
    version_length = u32(metadata + 12)
    cursor = metadata + 16 + ((version_length + 3) & ~3) + 2
    stream_count = u16(cursor)
    cursor += 2
    streams = {}
    for _ in range(stream_count):
        offset, size = u32(cursor), u32(cursor + 4)
        cursor += 8
        end = data.index(b"\0", cursor)
        streams[data[cursor:end].decode("utf-8")] = (metadata + offset, size)
        cursor = (end + 1 + 3) & ~3
    tables_offset, _ = streams.get("#~") or streams.get("#-")
    guid_offset, _ = streams["#GUID"]
    heap_sizes = data[tables_offset + 6]
    string_index_size = 4 if heap_sizes & 1 else 2
    guid_index_size = 4 if heap_sizes & 2 else 2
    valid = u64(tables_offset + 8)
    cursor = tables_offset + 24
    if not valid & 1:
        raise ValueError(f"no Module table: {path}")
    # Rows[] lists a 4-byte row count for every present table (ascending table ID), as one
    # contiguous array, before any table's actual row data follows. Module is table 0, so
    # its data is the first table block -- but cursor must still pass every row-count slot
    # for every other present table first, not just Module's own.
    for table in range(64):
        if valid >> table & 1:
            cursor += 4
    module = cursor
    mvid_index_offset = module + 2 + string_index_size
    mvid_index = u32(mvid_index_offset) if guid_index_size == 4 else u16(mvid_index_offset)
    raw = data[guid_offset + (mvid_index - 1) * 16:guid_offset + mvid_index * 16]
    return str(uuid.UUID(bytes_le=raw)).lower()


def compile_module(name: str, source: Path, path_dirs: list[Path], llvm_dir: Path,
                    tool_prefix: str, output: Path, logs: Path, temporary: Path,
                    environment: dict) -> dict:
    assembly = {"name": name, "full_name": name, "mvid": pe_mvid(source), "sha256": sha256(source),
                "size": source.stat().st_size, "path": "runtime/" + ("corelib" if name == "System.Private.CoreLib" else "framework") + "/" + source.name}
    target, sidecar = output / (source.name + ".o"), output / (source.name + "-llvm.o")
    pending, llvm_pending = target.with_name(target.name + ".pending"), sidecar.with_name(sidecar.name + ".pending")
    module_temp = temporary / ("llvm-" + source.name)
    module_temp.mkdir(parents=True, exist_ok=True)
    aot_extra = CORELIB_AOT_EXTRA if name == "System.Private.CoreLib" else "full,interp,static,"
    path_flags = [f"--path={p}" for p in path_dirs]
    command = [
        "stdbuf", "-oL", "-eL", str(llvm_dir / "mono-aot-cross"), "--llvm", *path_flags,
        "--aot=" + aot_extra + "outfile=" + str(pending)
        + ",llvm-path=" + str(llvm_dir) + "/,llvm-outfile=" + str(llvm_pending)
        + ",temp-path=" + str(module_temp) + ",tool-prefix=" + tool_prefix, str(source),
    ]
    log = logs / (source.name + ".log")
    print(f"{name}: compiling...", flush=True)
    run_logged(command, log, environment)
    with llvm_pending.open("rb") as stream:
        header = stream.read(20)
    if header[:6] != b"\x7fELF\x02\x01" or struct.unpack_from("<HH", header, 16) != (1, 183):
        raise RuntimeError(f"LLVM sidecar is not an AArch64 relocatable object: {llvm_pending}")
    counts = method_counts(log)
    inspected = inspect_object(pending, tool_prefix + "nm", logs / (source.stem + ".nm.log"), llvm_pending)
    # CoreLib's own AOT module is registered under the special "corlib" prefix, not one
    # derived from its name (confirmed by scripts/release_bcl/build_v72.py, the original
    # script that produced the hand-built reference framework-aot component).
    prefix = "corlib" if name == "System.Private.CoreLib" else inspected["symbol"][len("mono_aot_module_"):-len("_info")]
    own = subprocess.run([tool_prefix + "nm", "-g", "--defined-only", str(pending)], capture_output=True, text=True)
    if own.returncode or not re.search(rf"\sT mono_aot_{re.escape(prefix)}jit_code_start$", own.stdout, re.M):
        raise RuntimeError(f"LLVM main object does not define mono_aot_{prefix}jit_code_start: {pending}")
    llvm_pending.replace(sidecar)
    pending.replace(target)
    print(f"{name}: {counts['compiled_methods']}/{counts['total_methods']} -> {target}", flush=True)
    return {
        "assembly": assembly, "object": target.name, "object_sha256": sha256(target),
        "object_size": target.stat().st_size, "llvm_object": sidecar.name, "llvm_object_sha256": sha256(sidecar),
        "symbol": inspected["symbol"], "symbol_type": inspected["symbol_type"],
        "compiled_methods": counts["compiled_methods"], "total_methods": counts["total_methods"],
        "optimizations": ["--llvm"], "log": "logs/" + log.name, "nm_log": "logs/" + (source.stem + ".nm.log"),
        "command": command,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--corelib-dir", type=Path, required=True)
    parser.add_argument("--framework-dir", type=Path, required=True)
    parser.add_argument("--llvm-compiler-dir", type=Path, required=True)
    parser.add_argument("--tool-prefix", default="aarch64-none-elf-")
    parser.add_argument("--output-dir", type=Path, required=True)
    args = parser.parse_args()

    corelib_dir, framework_dir = args.corelib_dir.resolve(), args.framework_dir.resolve()
    llvm_dir = args.llvm_compiler_dir.resolve()
    output = args.output_dir.resolve()
    output.mkdir(parents=True, exist_ok=True)
    logs, temporary = output / "logs", output / "tmp"
    logs.mkdir(exist_ok=True)
    temporary.mkdir(exist_ok=True)

    # Set on the process's own environ (not just a local copy) so the nm invocations in
    # compile_module()/inspect_object() -- which run via subprocess.run() without an explicit
    # env= override -- also resolve the devkitA64 tool-prefix binaries, matching the convention
    # cli.py uses for compile_terraria_aot.py's container-level -e PATH=... (scripts/build_framework_aot.py
    # instead fixes it at the process level since it has no equivalent container-launch call of its own).
    os.environ["LD_LIBRARY_PATH"] = str(llvm_dir)
    os.environ["PATH"] = "/opt/devkitpro/devkitA64/bin:/opt/devkitpro/tools/bin:" + os.environ.get("PATH", "")
    environment = os.environ.copy()

    dirs = {"corelib": corelib_dir, "framework": framework_dir}
    modules = []
    for name, subdir, extra_dirs in MODULES:
        source = dirs[subdir] / (name + ".dll")
        if not source.is_file():
            raise RuntimeError(f"missing source assembly: {source}")
        path_dirs = [corelib_dir, *(dirs[d] for d in extra_dirs)]
        modules.append(compile_module(name, source, path_dirs, llvm_dir, args.tool_prefix,
                                       output, logs, temporary, environment))

    symbols = [m["symbol"] for m in modules]
    if len(set(symbols)) != len(symbols):
        raise RuntimeError("duplicate exported AOT registration symbols")
    record = {
        "schema": "terrabuilder-framework-aot/1", "aot_options": ["full", "interp", "static"],
        "note": "Framework assemblies compiled once with the LLVM AOT compiler; linked by every "
                "vanilla build. Built from source by scripts/build_framework_aot.py.",
        "modules": modules,
    }
    write_json(output / "framework-aot.json", record)
    shutil.rmtree(temporary, ignore_errors=True)
    print(f"Complete: {len(modules)} framework AOT modules; {output / 'framework-aot.json'}", flush=True)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, KeyError, ValueError) as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(1)
