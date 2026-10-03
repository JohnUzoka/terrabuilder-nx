#!/usr/bin/env python3
"""Rebuild only CoreLib's specific trampoline bank; run in the mono-nx container.

All generated files stay below the new output directory. The existing AOT bank,
SDK DLLs, runtime archive and six game/helper objects are read-only inputs.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import shutil
import struct
import sys

from compile_terraria_aot import (
    inspect_object, method_counts, read_elf_sections,
    run_logged, sha256, validate_runtime_metadata, write_json,
)

CORELIB = "System.Private.CoreLib.dll"
POOL_NAMES = ("specific", "static_rgctx", "imt", "gsharedvt_arg", "ftnptr_arg", "unbox_arbitrary")
POOL_SLOTS = (2, 2, 1, 2, 2, 1)
ABI_FIELDS = ("version", "jit_got", "assembly_guid", "flags", "opts", "got_size", "nmethods",
              "num_rgctx_fetch_trampolines", "tramp_page_size", "call_table_entry_size", "plt_got_offset_base", "plt_size",
              "num_trampolines", "trampoline_got_offset_base", "trampoline_size",
              "tramp_page_code_offsets", *(name + "_trampolines" for name in POOL_NAMES))


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


class Elf:
    """Small bounded view reusing the existing ELF64/AArch64 section reader."""
    def __init__(self, stream):
        self.path = Path(stream.name)
        self.sections, self.names, self.read_at = read_elf_sections(stream, stream.read(64), self.path)
        tabs = [s for s in self.sections if s[1] == 2]
        require(len(tabs) == 1 and tabs[0][9] == 24, "unsupported symbol table")
        tab = tabs[0]
        strings_section = self.sections[tab[6]]
        strings = self.read_at(strings_section[4], strings_section[5])
        self.symbols = list(struct.iter_unpack("<IBBHQQ", self.read_at(tab[4], tab[5])))
        self.by_name = {}
        for symbol in self.symbols:
            name = strings[symbol[0]:strings.index(0, symbol[0])].decode()
            self.by_name.setdefault(name, []).append(symbol)

    def symbol(self, name):
        found = self.by_name.get(name, [])
        require(len(found) == 1, f"expected one symbol {name}: {self.path}")
        return found[0]

    def location(self, symbol):
        require(0 < symbol[3] < len(self.sections), "undefined/absolute symbol")
        return symbol[3], symbol[4]

    def data(self, location, size):
        section, offset = location
        s = self.sections[section]
        require(s[1] == 1 and 0 <= offset <= s[5] and size <= s[5] - offset,
                f"data exceeds emitted section: {self.path}")
        return self.read_at(s[4] + offset, size)

    def relocations(self, section, start=0, end=None):
        for s in self.sections:
            if s[1] in (4, 9) and s[7] == section:
                require(s[1] == 4 and s[9] == 24, "expected ELF64 RELA")
                for offset, info, addend in struct.iter_unpack("<QQq", self.read_at(s[4], s[5])):
                    if offset >= start and (end is None or offset < end):
                        symbol = self.symbols[info >> 32]
                        target_section, target_offset = self.location(symbol)
                        yield offset, info & 0xffffffff, (target_section, target_offset + addend)

    def pointer(self, location):
        section, offset = location
        matches = list(self.relocations(section, offset, offset + 8))
        require(len(matches) == 1 and matches[0][:2] == (offset, 257), "expected R_AARCH64_ABS64 pointer")
        require(self.data(location, 8) == bytes(8), "unexpected pointer addend")
        return matches[0][2]


def source_declarations(runtime_source):
    header = runtime_source / "src/mono/mono/mini/aot-runtime.h"
    text = header.read_text()
    declarations = []
    for name in ("MonoAotTrampoline", "MonoAotFileTable"):
        end = text.index("} " + name + ";") + len("} " + name + ";")
        start = text.rindex("typedef enum", 0, end)
        declarations.append(text[start:end])
    start = text.index("typedef struct MonoAotFileInfo")
    end = text.index("} MonoAotFileInfo;", start) + len("} MonoAotFileInfo;")
    declarations.append(text[start:end])
    return ("#include <stddef.h>\n#include <stdint.h>\n"
            "typedef void *gpointer;\ntypedef uint8_t guint8;\n"
            "typedef uint32_t guint32;\ntypedef int32_t gint32;\n" + "\n".join(declarations) + "\n")


def derive_abi(runtime_source, output, tool_prefix):
    """Compile the actual source declaration for AArch64, never guess offsets."""
    work = output / "abi"
    work.mkdir()
    source = work / "layout.c"
    values = ["sizeof(MonoAotFileInfo)", "sizeof(gpointer)", "MONO_AOT_TRAMP_NUM"]
    values += [f"offsetof(MonoAotFileInfo, {field})" for field in ABI_FIELDS]
    source.write_text(source_declarations(runtime_source) + "const uint32_t corelib_pool_abi[] = {\n" + ",\n".join(values) + "\n};\n")
    obj = work / "layout.o"
    command = [tool_prefix + "gcc", "-std=c11", "-c", str(source), "-o", str(obj)]
    run_logged(command, work / "layout.log", os.environ.copy())
    with obj.open("rb") as stream:
        elf = Elf(stream)
        data = elf.data(elf.location(elf.symbol("corelib_pool_abi")), len(values) * 4)
    decoded = struct.unpack("<" + "I" * len(values), data)
    require(decoded[1:3] == (8, 6), "unexpected target pointer width or pool count")
    abi = {"struct_size": decoded[0], "pointer_size": decoded[1], "pool_count": decoded[2],
           "offsets": dict(zip(ABI_FIELDS, decoded[3:])), "command": command,
           "sources": {str(runtime_source / "src/mono/mono/mini" / name):
                       sha256(runtime_source / "src/mono/mono/mini" / name)
                       for name in ("aot-runtime.h", "aot-runtime.c", "aot-compiler.c")}}
    write_json(work / "layout.json", abi)
    return abi


def inspect_pools(path, abi):
    with path.open("rb") as stream:
        elf = Elf(stream)
        file_info = elf.location(elf.symbol("mono_aot_file_info"))
        data = elf.data(file_info, abi["struct_size"])
        offsets = abi["offsets"]

        def scalar(name):
            return struct.unpack_from("<I", data, offsets[name])[0]

        def array(name):
            return struct.unpack_from("<6I", data, offsets[name])

        def pointer(name):
            return elf.pointer((file_info[0], file_info[1] + offsets[name]))

        exported = [name for name in elf.by_name if re.fullmatch(r"mono_aot_module_\w+_info", name)]
        require(len(exported) == 1 and elf.pointer(elf.location(elf.symbol(exported[0]))) == file_info,
                "exported registration pointer does not identify MonoAotFileInfo")
        got = pointer("jit_got")
        got_section = elf.sections[got[0]]
        got_bytes = scalar("got_size")
        # emit_got reserves exactly this many bytes in .bss, not in the object file.
        require(got_section[1] == 8 and got_section[2] & 3 == 3 and got[1] % 8 == 0,
                "GOT must be aligned writable allocated NOBITS data")
        require(got_bytes % 8 == 0 and got_section[5] - got[1] == got_bytes,
                "GOT metadata does not match emitted BSS reservation")
        require(scalar("flags") & (2 | 256) == (2 | 256), "CoreLib is not full,interp")
        require(scalar("call_table_entry_size") == 4, "unexpected method table stride")
        pools = []
        for index, (name, slots) in enumerate(zip(POOL_NAMES, POOL_SLOTS)):
            start = elf.location(elf.symbol(name + "_trampolines"))
            end = elf.location(elf.symbol(name + "_trampolines_e"))
            count, stride, base = array("num_trampolines")[index], array("trampoline_size")[index], array("trampoline_got_offset_base")[index]
            require(pointer(name + "_trampolines") == start, f"{name}: file-info code pointer mismatch")
            require(start[0] == end[0] and end[1] - start[1] == count * stride,
                    f"{name}: metadata count/stride exceeds emitted stub range")
            require((not count or stride > 0) and elf.sections[start[0]][2] & 6 == 6,
                    f"{name}: invalid executable pool")
            require(elf.data(end, 4) == bytes(4), f"{name}: missing emitted pool terminator")
            require((base + count * slots) * 8 <= got_bytes, f"{name}: GOT capacity exceeded")
            if pools:
                require(base == pools[-1]["got_end_slot"], f"{name}: noncontiguous/overlapping GOT banks")
            pools.append({"type": index, "name": name, "count": count, "stride": stride,
                          "code_section": start[0], "code_start": start[1], "code_end": end[1],
                          "code_bytes": count * stride, "slots_per_stub": slots,
                          "got_base_slot": base, "got_end_slot": base + count * slots,
                          "got_bytes": count * slots * 8})
        # emit_got_info appends PLT slots after emit_trampolines reserves its banks.
        require(pools[-1]["got_end_slot"] == scalar("plt_got_offset_base"), "pool bank overlaps PLT GOT")
        require((scalar("plt_got_offset_base") + scalar("plt_size")) * 8 == got_bytes,
                "PLT and pool reservations do not account for emitted GOT")
        specific = pools[0]
        require(specific["stride"] == 28, "unexpected ARM64 specific trampoline stride")
        code = elf.data((specific["code_section"], specific["code_start"]), specific["code_bytes"])
        relocs = {}
        for offset, kind, target in elf.relocations(specific["code_section"], specific["code_start"], specific["code_end"]):
            require(offset not in relocs, "duplicate specific stub relocation")
            relocs[offset] = (kind, target)
        require(len(relocs) == specific["count"] * 4, "missing/extra specific stub relocations")
        # arm64_emit_load_got_slot: ADRP x16; ADD x16; LDR destination.
        # Decode every emitted stub and its actual two unique GOT slot references.
        for index in range(specific["count"]):
            pos = index * 28
            words = struct.unpack_from("<7I", code, pos)
            base = specific["got_base_slot"] + index * 2
            for insn, register, slot in ((0, 17, base + 1), (3, 16, base)):
                require(words[insn] == 0x90000010 and words[insn + 1] == 0x91000210,
                        f"specific {index}: invalid ADRP/ADD stub")
                require(words[insn + 2] == 0xf9400200 | register | (((slot * 8) & 0xfff) // 8 << 10),
                        f"specific {index}: wrong GOT load instruction")
                address = specific["code_start"] + pos + insn * 4
                require(relocs.get(address) == (275, (got[0], got[1] + ((slot * 8) & ~0xfff))) and
                        relocs.get(address + 4) == (277, got),
                        f"specific {index}: incorrect GOT relocation pair")
            require(words[6] == 0xd61f0200, f"specific {index}: expected BR x16")
        guid_location = pointer("assembly_guid")
        guid = elf.data(guid_location, 37).rstrip(b"\0").decode().lower()
        allocated = sum(s[5] for s in elf.sections if s[2] & 2)
        return {"object": str(path), "object_sha256": sha256(path), "object_bytes": path.stat().st_size,
                "allocated_section_bytes": allocated, "assembly_mvid": guid,
                "module_symbol": exported[0], "file_info_section": file_info[0], "file_info_offset": file_info[1],
                "file_info_file_offset": elf.sections[file_info[0]][4] + file_info[1],
                "version": scalar("version"), "flags": scalar("flags"), "opts": scalar("opts"),
                "nmethods": scalar("nmethods"), "num_rgctx_fetch_trampolines": scalar("num_rgctx_fetch_trampolines"),
                "tramp_page_size": scalar("tramp_page_size"), "tramp_page_code_offsets": list(array("tramp_page_code_offsets")),
                "plt_got_offset_base": scalar("plt_got_offset_base"), "plt_size": scalar("plt_size"),
                "got_bytes": got_bytes, "got_slots": got_bytes // 8, "got_section": got[0], "got_section_offset": got[1],
                "specific_stubs_instruction_checked": specific["count"], "pools": pools}


def compare_pools(old, new):
    require(old["pools"][0]["count"] == 4096 and new["pools"][0]["count"] == 65536,
            "expected original4096 and rebuilt65536 specific pools")
    for field in ("assembly_mvid", "module_symbol", "version", "flags", "opts", "nmethods",
                  "num_rgctx_fetch_trampolines", "tramp_page_size", "tramp_page_code_offsets", "plt_size"):
        require(old[field] == new[field], f"unexpected change to {field}")
    for before, after in zip(old["pools"], new["pools"]):
        require(before["stride"] == after["stride"], "trampoline stride changed")
        if before["type"]:
            require(before["count"] == after["count"], f"unexpected count change: {before['name']}")
    extra = 65536 - 4096
    require(new["got_bytes"] - old["got_bytes"] == extra * 16, "unexpected GOT growth")
    require(new["pools"][0]["got_base_slot"] == old["pools"][0]["got_base_slot"], "specific GOT base changed")
    return {"specific_code_bytes": extra * 28, "got_bss_bytes": extra * 16,
            "allocated_section_bytes": new["allocated_section_bytes"] - old["allocated_section_bytes"],
            "object_file_bytes": new["object_bytes"] - old["object_bytes"],
            "other_pool_counts_unchanged": True}


def main():
    runtime = "/mono-nx/dotnet_runtime/artifacts/bin"
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-dir", type=Path, default=Path("/build/aot"))
    parser.add_argument("--output-dir", type=Path, default=Path("/build/aot38"))
    parser.add_argument("--runtime-source", type=Path, default=Path("/build/runtime-source"))
    parser.add_argument("--corelib-dir", type=Path, default=Path(runtime + "/mono/libnx.arm64.Debug"))
    parser.add_argument("--runtime-dir", type=Path, default=Path(runtime + "/runtime/net9.0-libnx-Debug-arm64"))
    parser.add_argument("--cross-compiler", default=runtime + "/mono/linux.x64.Debug/cross/linux-x64/libnx-arm64/mono-aot-cross")
    parser.add_argument("--tool-prefix", default="aarch64-none-elf-")
    args = parser.parse_args()
    base, output = args.base_dir.resolve(), args.output_dir.resolve()
    require(output != base and base not in output.parents and output not in base.parents,
            "output must be separate from preserved base")
    require(not output.exists(), "output already exists; refusing to overwrite artifacts")
    previous = json.loads((base / "build-manifest.json").read_text())
    require(previous["status"] == "complete" and len(previous["modules"]) == 6, "expected complete seven-object base")
    corelib = args.corelib_dir / CORELIB
    identity = previous["corelib"]["assembly"]
    require(sha256(corelib) == identity["sha256"], "CoreLib DLL differs from original input")
    preserved = {str(path): sha256(path) for path in [base / "build-manifest.json", base / "mono_aot_modules.h",
                 *(base / Path(item["object"]).name for item in [previous["corelib"], *previous["modules"]])]}
    for item in [previous["corelib"], *previous["modules"]]:
        require(preserved[str(base / Path(item["object"]).name)] == item["object_sha256"], "base object hash changed")
    output.mkdir(parents=True)
    (output / "logs").mkdir()
    (output / "tmp").mkdir()
    abi = derive_abi(args.runtime_source, output, args.tool_prefix)
    old = inspect_pools(base / (CORELIB + ".o"), abi)
    require(old["assembly_mvid"] == identity["mvid"].lower(), "original CoreLib MVID mismatch")
    pending = output / (CORELIB + ".o.pending")
    command = [args.cross_compiler, "--path=" + str(args.corelib_dir), "--path=" + str(args.runtime_dir),
               "--aot=full,interp,static,ntrampolines=65536,outfile=" + str(pending) + ",tool-prefix=" + args.tool_prefix,
               str(corelib)]
    write_json(output / "reproduce-command.json", {"command": command, "corelib": identity,
               "compiler_sha256": sha256(Path(args.cross_compiler)), "preserved_inputs": preserved})
    environment = os.environ.copy()
    environment["TMPDIR"] = str(output / "tmp")
    log = output / "logs" / (CORELIB + ".log")
    print("Compiling exact CoreLib with specific bank 65536", flush=True)
    run_logged(command, log, environment)
    core_result = {"assembly": identity, "status": "compiled", "command": command, "log": str(log),
                   "optimizations": [], **method_counts(log),
                   **inspect_object(pending, args.tool_prefix + "nm", output / "logs" / (CORELIB + ".nm.log"))}
    new = inspect_pools(pending, abi)
    delta = compare_pools(old, new)
    require(core_result["method_table"] == previous["corelib"]["method_table"], "CoreLib method table guards changed")
    require(sha256(corelib) == identity["sha256"], "CoreLib input changed during compile")
    target = output / (CORELIB + ".o")
    pending.rename(target)
    core_result["object"] = new["object"] = str(target)
    modules = []
    for item in previous["modules"]:
        source = base / Path(item["object"]).name
        target = output / source.name
        shutil.copyfile(source, target)
        checked = inspect_object(target, args.tool_prefix + "nm", output / "logs" / (source.name + ".nm.log"))
        require(checked["object_sha256"] == item["object_sha256"], "copied object differs")
        modules.append({**item, **checked, "status": "copied_unchanged", "source_object": str(source)})
    preparation = json.loads((base / "runtime-preparation.json").read_text())
    metadata = validate_runtime_metadata(preparation, [core_result, *modules], output)
    for path, digest in preserved.items():
        require(sha256(Path(path)) == digest, "preserved input changed: " + path)
    symbols = [item["symbol"] for item in [core_result, *modules]]
    require(len(set(symbols)) == 7, "incorrect registration module set")
    require(set(output.glob("*.o")) == {Path(item["object"]) for item in [core_result, *modules]}, "extra link objects")
    report = {"schema_version": 1, "status": "complete", "corelib": core_result, "modules": modules,
              "aot_options": ["full", "interp", "static", "ntrampolines=65536"],
              "base_manifest": str(base / "build-manifest.json"), "preserved_inputs": preserved,
              "inputs_verified_unchanged": True, "runtime_metadata_manifest": str(metadata),
              "runtime_romfs": str(base / "runtime-romfs"), "trampoline_validation": str(output / "trampoline-validation.json"),
              "header": str(output / "mono_aot_modules.h")}
    write_json(output / "trampoline-validation.json", {"abi": abi, "old": old, "new": new, "delta": delta,
               "scope": "Real ARM64 code, relocations, BSS and source ABI checked; no Switch execution or lifetime exhaustion guarantee"})
    write_json(output / "build-manifest.json", report)
    header = "/* Generated only after all AArch64 objects passed verification. */\n"
    header += "".join(f"REGISTER_AOT_MODULE({symbol});\n" for symbol in symbols)
    (output / "mono_aot_modules.h").write_text(header)
    print(json.dumps(delta), flush=True)
    print("Complete: seven checked objects; " + str(output / "build-manifest.json"), flush=True)


if __name__ == "__main__":
    try:
        main()
    except (OSError, RuntimeError) as error:
        print(str(error), file=sys.stderr)
        raise SystemExit(1)
