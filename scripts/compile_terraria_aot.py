#!/usr/bin/env python3
"""Compile exact staged Terraria assemblies; run inside the mono-nx build container.

The output directory is private/BYO build data, not runtime RomFS. CoreLib and the
other framework modules are not compiled here: they come prebuilt from the toolchain
(--framework-aot) and are checked against the exact assemblies this build loads.
"""

from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import struct
import subprocess
import sys


COUNTS = re.compile(r"Compiled\s*:?\s*(\d+)\s*/\s*(\d+)")
MODULE_SYMBOL = re.compile(
    r"^[0-9a-fA-F]+\s+([BDR])\s+"
    r"(mono_aot_module_[A-Za-z0-9_]+_info)$", re.MULTILINE
)


def sha256(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write_json(path: Path, value: object) -> None:
    temporary = path.with_name(path.name + ".pending")
    temporary.write_text(json.dumps(value, indent=2) + "\n")
    temporary.replace(path)


def run_logged(command: list[str], log: Path, environment: dict[str, str]) -> None:
    with log.open("w") as stream:
        stream.write("$ " + shlex.join(command) + "\n")
        stream.flush()
        result = subprocess.run(command, stdout=stream, stderr=subprocess.STDOUT, env=environment)
    if result.returncode:
        raise RuntimeError(f"command exited {result.returncode}; inspect {log}: {shlex.join(command)}")


def read_elf_sections(stream, header: bytes, path: Path):
    """Read bounded ELF64 metadata without loading the object code into memory."""
    if len(header) != 64 or header[:6] != b"\x7fELF\x02\x01" or struct.unpack_from("<HH", header, 16) != (1, 183):
        raise RuntimeError(f"not an ELF64 little-endian AArch64 relocatable object: {path}")
    file_size = os.fstat(stream.fileno()).st_size

    def read_at(offset: int, size: int) -> bytes:
        if offset < 0 or size < 0 or offset + size > file_size:
            raise RuntimeError(f"ELF section outside object bounds: {path}")
        stream.seek(offset)
        return stream.read(size)

    section_offset = struct.unpack_from("<Q", header, 40)[0]
    section_size, section_count, names_index = struct.unpack_from("<HHH", header, 58)
    if section_size != 64 or not section_offset:
        raise RuntimeError(f"unsupported ELF section header layout: {path}")
    # ELF extended numbering (LLVM AOT sidecars have >65279 sections): e_shnum==0 and
    # e_shstrndx==SHN_XINDEX mean the real values sit in section 0's sh_size / sh_link.
    if section_count == 0 or names_index == 0xffff:
        first = struct.unpack("<IIQQQQIIQQ", read_at(section_offset, section_size))
        if section_count == 0:
            section_count = first[5]
        if names_index == 0xffff:
            names_index = first[6]
    if not 0 < names_index < section_count:
        raise RuntimeError(f"unsupported ELF section header layout: {path}")
    sections = list(struct.iter_unpack("<IIQQQQIIQQ", read_at(section_offset, section_size * section_count)))
    names_section = sections[names_index]
    names = read_at(names_section[4], names_section[5])
    return sections, names, read_at


def inspect_method_table(stream, header: bytes, path: Path) -> dict:
    """Validate the only AOT input section the native linker moves to read-only memory."""
    sections, names, read_at = read_elf_sections(stream, header, path)
    tables = [index for index, section in enumerate(sections)
              if names[section[0]:].split(b"\0", 1)[0] == b".data.rel.ro"]
    if len(tables) != 1:
        raise RuntimeError(f"expected exactly one .data.rel.ro AOT method table: {path}")
    table_index = tables[0]
    table = sections[table_index]
    table_size = table[5]
    if table[1] != 1 or not table[2] & 2 or table[2] & 4 or table[8] < 8 or not table_size or table_size % 4:
        raise RuntimeError(f"unsupported .data.rel.ro AOT method table shape: {path}")
    relocations = [section for section in sections if section[1] in (4, 9) and section[7] == table_index]
    if len(relocations) != 1 or relocations[0][1] != 4 or relocations[0][9] != 24:
        raise RuntimeError(f"expected one ELF64 RELA section for the AOT method table: {path}")
    relocation_section = relocations[0]
    entry_count = table_size // 4
    if relocation_section[5] % 24 or relocation_section[5] > entry_count * 24:
        raise RuntimeError(f"unsupported AOT method table relocation count: {path}")
    instructions = read_at(table[4], table_size)
    relocation_bytes = read_at(relocation_section[4], relocation_section[5])
    relocated = bytearray(entry_count)
    for index, (offset, info, _addend) in enumerate(struct.iter_unpack("<QQq", relocation_bytes)):
        if offset >= table_size or offset % 4 or info & 0xffffffff != 283:
            raise RuntimeError(f"unsupported AOT table relocation {index}: offset={offset}, type={info & 0xffffffff}; expected R_AARCH64_CALL26 (283): {path}")
        if relocated[offset // 4]:
            raise RuntimeError(f"duplicate AOT method table relocation at offset {offset}: {path}")
        relocated[offset // 4] = 1
    for index, (instruction,) in enumerate(struct.iter_unpack("<I", instructions)):
        if instruction & 0xfc000000 != 0x94000000:
            raise RuntimeError(f"AOT method table entry {index} is not an ARM64 BL encoding: {path}")
        if not relocated[index]:
            # Missing methods branch to the table start; the assembler resolves
            # these same-section sentinels without emitting a relocation.
            displacement = ((instruction & 0x03ffffff) ^ 0x02000000) - 0x02000000
            if index * 4 + displacement * 4 != 0:
                raise RuntimeError(f"unsupported unrelocated AOT table entry {index}: {path}")
    return {"section": ".data.rel.ro", "bytes": table_size, "entries": entry_count,
            "relocation_count": relocation_section[5] // 24, "relocation_type": "R_AARCH64_CALL26",
            "relocation_type_id": 283, "resolved_sentinels": entry_count - sum(relocated),
            "read_only_encoded_addresses": True}


def inspect_object(path: Path, nm: str, log: Path, symbol_object: Path | None = None) -> dict:
    """symbol_object: where the module's exported info symbol lives (the -llvm.o sidecar
    in LLVM mode, which also holds image_table/mono_aot_file_info); default: path."""
    with path.open("rb") as stream:
        header = stream.read(64)
        if len(header) != 64 or header[:6] != b"\x7fELF\x02\x01" or struct.unpack_from("<HH", header, 16) != (1, 183):
            raise RuntimeError(f"not an ELF64 little-endian AArch64 relocatable object: {path}")
        method_table = inspect_method_table(stream, header, path)
    symbol_source = symbol_object or path
    result = subprocess.run([nm, "-g", "--defined-only", str(symbol_source)], capture_output=True, text=True)
    log.write_text(result.stdout + result.stderr)
    if result.returncode:
        raise RuntimeError(f"nm exited {result.returncode}: {log}")
    matches = MODULE_SYMBOL.findall(result.stdout)
    if len(matches) != 1:
        raise RuntimeError(f"expected one exported AOT module data symbol, found {matches}: {symbol_source}")
    result = {"object": str(path), "object_sha256": sha256(path), "object_size": path.stat().st_size,
              "architecture": "AArch64", "symbol": matches[0][1], "symbol_type": matches[0][0],
              "symbol_registration": "value, not address", "nm_log": str(log), "method_table": method_table}
    # symbol_object is recorded (as the final path) by the caller only for LLVM sidecars.
    return result


def read_aot_dependencies(path: Path) -> list[dict]:
    """Decode emit_image_table/load_aot_module's exact name/MVID hard bindings."""
    with path.open("rb") as stream:
        sections, names, read_at = read_elf_sections(stream, stream.read(64), path)
        symtabs = [section for section in sections if section[1] == 2]
        if len(symtabs) != 1 or symtabs[0][9] != 24 or symtabs[0][6] >= len(sections):
            raise RuntimeError(f"unsupported AOT symbol table: {path}")
        symtab = symtabs[0]
        strtab = sections[symtab[6]]
        strings = read_at(strtab[4], strtab[5])
        symbols = [symbol for symbol in struct.iter_unpack("<IBBHQQ", read_at(symtab[4], symtab[5]))
                   if strings.startswith(b"image_table\0", symbol[0])]
        if len(symbols) != 1 or not 0 < symbols[0][3] < len(sections):
            raise RuntimeError(f"expected one embedded AOT image_table symbol: {path}")
        symbol = symbols[0]
        section = sections[symbol[3]]
        if names[section[0]:].split(b"\0", 1)[0] != b".rodata" or symbol[4] % 8:
            raise RuntimeError(f"unsupported AOT image_table placement: {path}")
        base = section[4] + symbol[4]
        available = section[5] - symbol[4]
        count = struct.unpack("<I", read_at(base, 4))[0]
        if not 0 < count <= available // 24:
            raise RuntimeError(f"invalid AOT image_table length: {path}")
        cursor = 4
        dependencies = []
        for _ in range(count):
            values = []
            for _ in range(4):
                stream.seek(base + cursor)
                value = bytearray()
                while cursor < available:
                    byte = stream.read(1)
                    if not byte:
                        raise RuntimeError(f"truncated AOT image_table string: {path}")
                    cursor += 1
                    if byte == b"\0":
                        break
                    value.extend(byte)
                else:
                    raise RuntimeError(f"unterminated AOT image_table string: {path}")
                values.append(value.decode("utf-8"))
            cursor = (cursor + 7) & ~7
            if cursor + 20 > available:
                raise RuntimeError(f"truncated AOT image_table version: {path}")
            flags, *version = struct.unpack("<IIIII", read_at(base + cursor, 20))
            cursor += 20
            name, mvid, culture, token = values
            if not name or not re.fullmatch(r"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}", mvid):
                raise RuntimeError(f"invalid AOT image_table identity: {path}")
            dependencies.append({"name": name, "mvid": mvid.lower(), "culture": culture,
                                 "public_key_token": token, "version": ".".join(map(str, version)), "flags": flags})
        return dependencies


def validate_runtime_metadata(manifest: dict, modules: list[dict], output: Path) -> Path:
    providers = {}
    for source, assemblies in [("corelib", [manifest["corelib"]]), ("staged", manifest["staged_assemblies"]),
                               ("sdk", manifest["runtime_assemblies"]), ("runtime_metadata", manifest["runtime_metadata_assemblies"])]:
        for assembly in assemblies:
            providers.setdefault(assembly["name"].casefold(), (source, assembly))
    errors, checks = [], []
    checked_paths = set()
    for assembly in manifest["runtime_metadata_assemblies"]:
        if sha256(Path(assembly["path"])) != assembly["sha256"]:
            errors.append("runtime metadata bytes changed: " + assembly["path"])
    for module in modules:
        path = Path(module["object"])
        if sha256(path) != module["object_sha256"]:
            errors.append("compiled object changed: " + str(path))
        own_provider = providers.get(module["assembly"]["name"].casefold())
        if not own_provider or own_provider[1]["sha256"] != module["assembly"]["sha256"]:
            errors.append("compiled assembly bytes no longer supplied: " + module["assembly"]["name"])
        # LLVM mode: image_table lives in the -llvm.o sidecar with the module info
        dependencies = read_aot_dependencies(Path(module.get("symbol_object") or path))
        for dependency in dependencies:
            provider = providers.get(dependency["name"].casefold())
            if provider is None:
                errors.append(f"{path.name}: missing early AOT dependency {dependency['name']}")
                continue
            source, assembly = provider
            if assembly["path"] not in checked_paths:
                if sha256(Path(assembly["path"])) != assembly["sha256"]:
                    errors.append("early metadata provider bytes changed: " + assembly["path"])
                checked_paths.add(assembly["path"])
            if assembly["mvid"].lower() != dependency["mvid"]:
                errors.append(f"{path.name}: MVID mismatch for {dependency['name']}: expected {dependency['mvid']}, got {assembly['mvid']}")
            dependency.update(provider_source=source, provider_path=assembly["path"], provider_sha256=assembly["sha256"])
        checks.append({"object": str(path), "object_sha256": module["object_sha256"], "dependencies": dependencies})
    if not checks:
        errors.append("no compiled AOT modules supplied for early metadata validation")
    report_path = output / "runtime-metadata-manifest.json"
    report = {"schema_version": 1, "status": "incomplete" if errors else "complete",
              "runtime_metadata_directory": manifest["runtime_metadata_directory"],
              "assemblies": manifest["runtime_metadata_assemblies"], "excluded": manifest["runtime_metadata_excluded"],
              "aot_modules": checks, "errors": errors,
              "validation_scope": "Exact image_table name/MVID availability in supplied inventories; native startup probe still required"}
    write_json(report_path, report)
    if errors:
        raise RuntimeError("early AOT metadata validation failed: " + "; ".join(errors))
    print(f"Verified early metadata for {len(checks)} AOT modules: {report_path}", flush=True)
    return report_path


def method_counts(log: Path) -> dict:
    counts = COUNTS.findall(log.read_text(errors="replace"))
    if len(counts) != 1:
        raise RuntimeError(f"expected exactly one compiler method-count summary: {log}")
    compiled, total = map(int, counts[0])
    if not 0 < compiled <= total:
        raise RuntimeError(f"invalid compiler method counts {compiled}/{total}: {log}")
    return {"compiled_methods": compiled, "total_methods": total}


def load_framework_record(directory: Path) -> dict:
    path = directory / "framework-aot.json"
    record = json.loads(path.read_text())
    if record.get("schema") != "terrabuilder-framework-aot/1" or record.get("aot_options") != ["full", "interp", "static"]:
        raise RuntimeError(f"unsupported framework AOT record: {path}")
    return record


def framework_modules(directory: Path, record: dict, manifest: dict, staged: dict, nm: str, logs: Path) -> list[dict]:
    """Prebuilt framework objects; each must match the assembly this build stages or loads."""
    results = []
    for entry in record["modules"]:
        expected = entry["assembly"]
        name = expected["name"]
        assembly = manifest["corelib"] if name == manifest["corelib"]["name"] else staged.get(Path(expected["path"]).name)
        if assembly is None:
            raise RuntimeError(f"framework AOT module {name} has no staged RomFS assembly")
        if (assembly["sha256"], assembly["mvid"].lower()) != (expected["sha256"], expected["mvid"].lower()):
            raise RuntimeError(f"framework AOT object for {name} was compiled from MVID {expected['mvid']}, "
                               f"but this build loads {assembly['mvid']} ({assembly['path']})")
        obj, llvm = directory / entry["object"], directory / entry["llvm_object"]
        for path, digest in ((obj, entry["object_sha256"]), (llvm, entry["llvm_object_sha256"])):
            if sha256(path) != digest:
                raise RuntimeError(f"framework AOT object changed: {path}")
        result = {"assembly": assembly, "status": "toolchain", "command": entry["command"],
                  "log": str(directory / entry["log"]), "optimizations": entry["optimizations"]}
        result.update(method_counts(directory / entry["log"]))
        result.update(inspect_object(obj, nm, logs / (obj.stem + ".nm.log"), llvm))
        if result["symbol"] != entry["symbol"]:
            raise RuntimeError(f"{obj} exports {result['symbol']}, expected {entry['symbol']}")
        result.update(llvm_object=str(llvm), llvm_object_sha256=entry["llvm_object_sha256"], symbol_object=str(llvm))
        results.append(result)
    if [r["assembly"]["name"] for r in results].count(manifest["corelib"]["name"]) != 1:
        raise RuntimeError("framework AOT record must contain exactly one CoreLib module")
    return results


def main() -> int:
    runtime_root = "/mono-nx/dotnet_runtime/artifacts/bin"
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-dir", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, default=Path("/build/aot"))
    parser.add_argument("--corelib-dir", type=Path, default=Path(runtime_root + "/mono/libnx.arm64.Debug"))
    parser.add_argument("--runtime-dir", type=Path, default=Path(runtime_root + "/runtime/net9.0-libnx-Debug-arm64"))
    parser.add_argument("--cross-compiler", default=runtime_root + "/mono/linux.x64.Debug/cross/linux-x64/libnx-arm64/mono-aot-cross")
    parser.add_argument("--cecil-path", type=Path, default=Path(runtime_root + "/Mono.Linker/Debug/net9.0/Mono.Cecil.dll"))
    parser.add_argument("--dotnet", default="/root/.dotnet/dotnet")
    parser.add_argument("--stdbuf", default="stdbuf", help="line-buffer compiler output, including crashes")
    parser.add_argument("--tool-prefix", default="aarch64-none-elf-")
    parser.add_argument("--reference-dir", type=Path, action="append", default=[])
    parser.add_argument("--framework-aot", type=Path, required=True,
                        help="toolchain directory with framework-aot.json and the prebuilt CoreLib/framework objects")
    parser.add_argument("--no-inline-assembly", action="append", default=[], metavar="NAME",
                        help="disable method inlining only for named modules; works around the Terraria SetDisplayMode compiler crash")
    parser.add_argument("--extra-module", action="append", default=[], metavar="FILE",
                        help="also AOT-compile this staged RomFS assembly (e.g. System.Linq.dll); it must exist at the RomFS root")
    parser.add_argument("--llvm-module", action="append", default=[], metavar="NAME",
                        help="compile this assembly with Mono's LLVM backend (needs --llvm-compiler-dir); "
                             "emits NAME's object plus a <file>-llvm.o sidecar that must be linked with it")
    parser.add_argument("--llvm-compiler-dir", type=Path,
                        help="directory holding an LLVM-enabled mono-aot-cross with its llc/opt/libc++")
    parser.add_argument("--llvm-aot-extra", default="",
                        help="extra comma-separated --aot options for --llvm-module assemblies only, "
                             "e.g. 'llvmopts=-mtriple=aarch64-none-elf -mcpu=cortex-a57,llvmllc=-mcpu=cortex-a57'")
    parser.add_argument("--runtime-metadata-only", action="store_true",
                        help="prepare/validate early runtime metadata using existing objects; do not rebuild or rewrite the header")
    parser.add_argument("--jobs", type=int, default=3)
    args = parser.parse_args()
    if args.jobs < 1:
        parser.error("--jobs must be positive")
    framework_dir = args.framework_aot.resolve()
    framework_record = load_framework_record(framework_dir)
    framework_files = {Path(m["assembly"]["path"]).name for m in framework_record["modules"]}
    game, output = args.game_dir.resolve(), args.output_dir.resolve()
    if output == game or game in output.parents:
        parser.error("--output-dir must be outside staged RomFS")
    output.mkdir(parents=True, exist_ok=True)
    header = output / "mono_aot_modules.h"
    if not args.runtime_metadata_only:
        header.unlink(missing_ok=True)  # A failed build must never leave a stale success header.
    (output / "runtime-metadata-manifest.json").unlink(missing_ok=True)
    logs = output / "logs"
    logs.mkdir(exist_ok=True)
    temporary = output / "tmp"
    temporary.mkdir(exist_ok=True)
    environment = os.environ.copy()
    environment["TMPDIR"] = str(temporary)
    environment["DOTNET_CLI_HOME"] = str(output / "dotnet-home")
    environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
    corelib = args.corelib_dir.resolve() / "System.Private.CoreLib.dll"
    dependencies = output / ("dependencies-" + sha256(game / "Terraria.exe"))
    preparation = output / ("runtime-preparation.json" if args.runtime_metadata_only else "preparation.json")
    runtime_metadata = output / "runtime-metadata"
    project = Path(__file__).resolve().parent / "prepare_aot" / "PrepareAot.csproj"
    build_command = [args.dotnet, "build", str(project), "--configuration", "Release",
                     "--output", str(output / "prepare-tool"),
                     "-p:BaseIntermediateOutputPath=" + str(output / "prepare-obj") + "/",
                     "-p:CecilPath=" + str(args.cecil_path.resolve())]
    run_logged(build_command, logs / "prepare-build.log", environment)
    prepare_command = [args.dotnet, str(output / "prepare-tool" / "PrepareAot.dll"),
                       str(game), str(dependencies), str(preparation), str(corelib),
                       str(args.runtime_dir.resolve()), str(runtime_metadata)]
    run_logged(prepare_command, logs / "prepare.log", environment)
    manifest = json.loads(preparation.read_text())
    staged = {Path(a["path"]).name: a for a in manifest["staged_assemblies"]}
    embedded = {a["name"]: a for a in manifest["embedded_assemblies"]}
    modules = [staged[name] for name in ("Terraria.exe", "FNA.dll", "NxCrypto.dll", "NxInputDiag.dll")]
    # A staged copy is what Mono loads at runtime (staged precedes embedded as a
    # metadata provider), e.g. the patched ReLogic.dll at the RomFS root.
    for name in ("ReLogic", "Newtonsoft.Json"):
        if name + ".dll" in staged:
            modules.append(staged[name + ".dll"])
        elif name in embedded:
            modules.append(embedded[name])
    for filename in args.extra_module:
        if filename not in staged:
            parser.error(f"--extra-module {filename} is not a staged RomFS assembly")
        if filename in framework_files:
            parser.error(f"--extra-module {filename} is prebuilt in --framework-aot")
        modules.append(staged[filename])
    if args.runtime_metadata_only:
        previous = json.loads((output / "build-manifest.json").read_text())
        if previous["status"] != "complete":
            raise RuntimeError("runtime metadata verification requires a complete existing build manifest")
        existing = [previous["corelib"], *previous["modules"]]
        expected = {assembly["name"] for assembly in [manifest["corelib"], *modules]}
        expected |= {m["assembly"]["name"] for m in framework_record["modules"]}
        if {module["assembly"]["name"] for module in existing} != expected:
            raise RuntimeError("existing AOT module set does not match requested assemblies")
        header_before = sha256(header)
        validate_runtime_metadata(manifest, existing, output)
        if sha256(header) != header_before:
            raise RuntimeError("registration header changed during runtime metadata preparation")
        print("Runtime metadata prepared; existing objects and registration header unchanged", flush=True)
        return 0
    no_inline = set(args.no_inline_assembly)
    unknown = no_inline - {assembly["name"] for assembly in modules}
    if unknown:
        parser.error("--no-inline-assembly names not requested for compilation: " + ", ".join(sorted(unknown)))
    # The staged mscorlib facade is patched and must win over the SDK copy.
    references = [args.corelib_dir.resolve(), game, args.runtime_dir.resolve(), dependencies]
    references += [path.resolve() for path in args.reference_dir]
    for path in references:
        if not path.is_dir():
            raise RuntimeError(f"missing reference directory: {path}")
    # --path takes ONE directory. A colon-separated string makes corlib resolution abort.
    path_flags = ["--path=" + str(path) for path in references]
    nm = args.tool_prefix + "nm"

    llvm_modules = set(args.llvm_module)
    unknown = llvm_modules - {assembly["name"] for assembly in modules}
    if unknown:
        parser.error("--llvm-module names not requested for compilation: " + ", ".join(sorted(unknown)))
    if llvm_modules and not args.llvm_compiler_dir:
        parser.error("--llvm-module requires --llvm-compiler-dir")

    def compile_module(assembly: dict) -> dict:
        source = Path(assembly["path"])
        target = output / (source.name + ".o")
        pending = target.with_name(target.name + ".pending")
        log = logs / (source.name + ".log")
        optimizations = ["--optimize=-inline"] if assembly["name"] in no_inline else []
        llvm = assembly["name"] in llvm_modules
        compiler, module_env, aot_extra, llvm_target = args.cross_compiler, environment, "", None
        if llvm:
            llvm_dir = args.llvm_compiler_dir.resolve()
            compiler = str(llvm_dir / "mono-aot-cross")
            module_env = dict(environment, LD_LIBRARY_PATH=str(llvm_dir))
            llvm_target = output / (source.name + "-llvm.o")
            # LLVM mode writes fixed names (temp.s/.bc/.opt.bc/.o) into temp-path, so each LLVM
            # module needs its own directory: parallel LLVM compiles sharing one clobbered each
            # other's main object.
            module_temp = temporary / ("llvm-" + source.name)
            module_temp.mkdir(exist_ok=True)
            aot_extra = f",llvm-path={llvm_dir}/,llvm-outfile={llvm_target}.pending,temp-path={module_temp}"
            if args.llvm_aot_extra:
                aot_extra += "," + args.llvm_aot_extra
            optimizations = ["--llvm", *optimizations]
        command = [args.stdbuf, "-oL", "-eL", compiler, *optimizations, *path_flags,
                   "--aot=full,interp,static,outfile=" + str(pending) + aot_extra + ",tool-prefix=" + args.tool_prefix,
                   str(source)]
        result = {"assembly": assembly, "command": command, "log": str(log), "optimizations": optimizations}
        try:
            if sha256(source) != assembly["sha256"]:
                raise RuntimeError(f"input changed before compilation: {source}")
            run_logged(command, log, module_env)
            result.update(method_counts(log))
            llvm_pending = None
            if llvm:
                llvm_pending = Path(str(llvm_target) + ".pending")
                with llvm_pending.open("rb") as stream:
                    head = stream.read(20)
                if head[:6] != b"\x7fELF\x02\x01" or struct.unpack_from("<HH", head, 16) != (1, 183):
                    raise RuntimeError(f"LLVM sidecar is not an AArch64 relocatable object: {llvm_pending}")
            result.update(inspect_object(pending, nm, logs / (source.name + ".nm.log"), llvm_pending))
            if llvm:
                # The main object must define this module's own code-range symbol, not a sibling's.
                prefix = result["symbol"][len("mono_aot_module_"):-len("_info")]
                own = subprocess.run([nm, "-g", "--defined-only", str(pending)], capture_output=True, text=True)
                if own.returncode or not re.search(rf"\sT mono_aot_{re.escape(prefix)}jit_code_start$", own.stdout, re.M):
                    raise RuntimeError(f"LLVM main object does not define mono_aot_{prefix}jit_code_start: {pending}")
            if llvm:
                llvm_pending.replace(llvm_target)
                result.update(llvm_object=str(llvm_target), llvm_object_sha256=sha256(llvm_target),
                              symbol_object=str(llvm_target))
            if sha256(source) != assembly["sha256"]:
                raise RuntimeError(f"input changed during compilation: {source}")
            pending.replace(target)
            result["object"] = str(target)
            result["status"] = "compiled"
            print(f"{assembly['name']}: {result['compiled_methods']}/{result['total_methods']} -> {target}", flush=True)
        except (OSError, RuntimeError) as error:
            pending.unlink(missing_ok=True)
            result.update(status="failed", error=str(error))
            print(f"{assembly['name']}: {error}", file=sys.stderr, flush=True)
        return result

    with ThreadPoolExecutor(max_workers=args.jobs) as executor:
        results = list(executor.map(compile_module, modules))
    report = {"schema_version": 1, "status": "incomplete", "preparation_manifest": str(preparation),
              "aot_options": ["full", "interp", "static"], "reference_directories": list(map(str, references)),
              "build_command": build_command, "prepare_command": prepare_command, "modules": results}
    report_path = output / "build-manifest.json"
    write_json(report_path, report)
    try:
        framework = framework_modules(framework_dir, framework_record, manifest, staged, nm, logs)
    except (OSError, RuntimeError, KeyError, ValueError) as error:
        report["framework_aot_error"] = str(error)
        write_json(report_path, report)
        raise RuntimeError(f"toolchain framework AOT objects are unusable: {error}") from error
    core_result = next(r for r in framework if r["assembly"]["name"] == manifest["corelib"]["name"])
    framework_extra = [r for r in framework if r is not core_result]
    report["framework_aot"] = {"directory": str(framework_dir), "record_sha256": sha256(framework_dir / "framework-aot.json")}
    report["corelib"] = core_result
    report["inputs_verified_unchanged"] = True
    for assembly in [*manifest["staged_assemblies"], *manifest["embedded_assemblies"], manifest["corelib"]]:
        if sha256(Path(assembly["path"])) != assembly["sha256"]:
            report["inputs_verified_unchanged"] = False
            report["input_error"] = "input changed after preparation: " + assembly["path"]
    write_json(report_path, report)
    if any(result["status"] != "compiled" for result in results) or not report["inputs_verified_unchanged"]:
        raise RuntimeError(f"AOT build incomplete; no registration header emitted. See {report_path} and per-module logs")
    # Registration order: CoreLib, game modules, then the other framework modules.
    report["modules"] = [*results, *framework_extra]
    all_results = [core_result, *results, *framework_extra]
    report["runtime_metadata_manifest"] = str(validate_runtime_metadata(manifest, all_results, output))
    symbols = [result["symbol"] for result in all_results]
    if len(set(symbols)) != len(symbols):
        raise RuntimeError("duplicate exported AOT registration symbols")
    expected_objects = {Path(result["object"]) for result in all_results}
    expected_objects |= {Path(result["llvm_object"]) for result in all_results if result.get("llvm_object")}
    unexpected = set(output.glob("*.o")) - expected_objects
    if unexpected:
        raise RuntimeError("unexpected objects in link directory; move diagnostics elsewhere: " + ", ".join(map(str, sorted(unexpected))))
    # No include guard: the native driver may include this once for extern declarations
    # and again for registrations. REGISTER_AOT_MODULE must register the pointer VALUE.
    text = "/* Generated only after all AArch64 objects passed verification. */\n"
    text += "".join(f"REGISTER_AOT_MODULE({symbol});\n" for symbol in symbols)
    report.update(status="complete", corelib=core_result, header=str(header), inputs_verified_unchanged=True)
    write_json(report_path, report)
    header_pending = header.with_name(header.name + ".pending")
    header_pending.write_text(text)
    header_pending.replace(header)
    print(f"Complete: {len(symbols)} AArch64 objects; {header}; {report_path}", flush=True)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, KeyError, ValueError) as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(1)
