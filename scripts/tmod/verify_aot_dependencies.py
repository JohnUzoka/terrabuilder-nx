#!/usr/bin/env python3
"""Verify Mono AOT image_table dependency MVIDs against a tmod build tree."""
import argparse
import json
import struct
import sys
import uuid
from pathlib import Path

from aot_dependencies import image_dependencies


def u16(data, offset):
    return struct.unpack_from("<H", data, offset)[0]


def u32(data, offset):
    return struct.unpack_from("<I", data, offset)[0]


def u64(data, offset):
    return struct.unpack_from("<Q", data, offset)[0]


def rva_to_offset(sections, rva):
    for virtual_address, raw_size, raw_pointer, virtual_size in sections:
        size = max(raw_size, virtual_size)
        if virtual_address <= rva < virtual_address + size:
            return raw_pointer + (rva - virtual_address)
    raise ValueError(f"RVA 0x{rva:x} not mapped")


def pe_mvid(path):
    data = Path(path).read_bytes()
    pe_offset = u32(data, 0x3c)
    if data[pe_offset:pe_offset + 4] != b"PE\0\0":
        raise ValueError(f"not a PE file: {path}")
    section_count = u16(data, pe_offset + 6)
    optional_size = u16(data, pe_offset + 20)
    optional = pe_offset + 24
    magic = u16(data, optional)
    data_directories = optional + (112 if magic == 0x20b else 96)
    cli_rva = u32(data, data_directories + 14 * 8)
    if cli_rva == 0:
        raise ValueError(f"not a managed PE file: {path}")
    section_offset = optional + optional_size
    sections = []
    for index in range(section_count):
        section = section_offset + index * 40
        virtual_size = u32(data, section + 8)
        virtual_address = u32(data, section + 12)
        raw_size = u32(data, section + 16)
        raw_pointer = u32(data, section + 20)
        sections.append((virtual_address, raw_size, raw_pointer, virtual_size))

    cli = rva_to_offset(sections, cli_rva)
    metadata = rva_to_offset(sections, u32(data, cli + 8))
    if data[metadata:metadata + 4] != b"BSJB":
        raise ValueError(f"no metadata root: {path}")

    version_length = u32(data, metadata + 12)
    cursor = metadata + 16 + ((version_length + 3) & ~3) + 2
    stream_count = u16(data, cursor)
    cursor += 2
    streams = {}
    for _ in range(stream_count):
        offset = u32(data, cursor)
        size = u32(data, cursor + 4)
        cursor += 8
        end = data.index(b"\0", cursor)
        name = data[cursor:end].decode("utf-8")
        cursor = (end + 1 + 3) & ~3
        streams[name] = (metadata + offset, size)

    tables_offset, _ = streams.get("#~") or streams.get("#-")
    guid_offset, _ = streams["#GUID"]
    heap_sizes = data[tables_offset + 6]
    string_index_size = 4 if heap_sizes & 1 else 2
    guid_index_size = 4 if heap_sizes & 2 else 2
    valid = u64(data, tables_offset + 8)
    cursor = tables_offset + 24
    rows = {}
    for table in range(64):
        if valid >> table & 1:
            rows[table] = u32(data, cursor)
            cursor += 4
    if 0 not in rows:
        raise ValueError(f"no Module table: {path}")

    module = cursor
    mvid_index_offset = module + 2 + string_index_size
    mvid_index = u32(data, mvid_index_offset) if guid_index_size == 4 else u16(data, mvid_index_offset)
    raw = data[guid_offset + (mvid_index - 1) * 16:guid_offset + mvid_index * 16]
    return str(uuid.UUID(bytes_le=raw)).lower()


def add_provider(providers, path):
    path = Path(path)
    if not path.exists():
        return
    try:
        mvid = pe_mvid(path)
    except Exception:
        return
    providers.setdefault(path.stem.casefold(), {"name": path.stem, "path": str(path), "mvid": mvid})


def candidate_object(cache_root, variant, module_name, entry):
    object_path = Path(entry.get("llvm_object") or entry.get("aot_object", ""))
    if str(object_path).startswith("/build"):
        object_path = Path(str(object_path).replace("/build", str(cache_root), 1))
    if object_path.exists():
        return object_path

    dll = module_name + ".dll"
    for candidate in [
        cache_root / "tmod" / variant / "aot" / (dll + "-llvm.o"),
        cache_root / "tmod" / variant / "aot" / (dll + ".o"),
        cache_root / "tmod" / "corelib" / "pools-llvm-v72" / (dll + "-llvm.o"),
        cache_root / "tmod" / "corelib" / "pools-llvm-v72" / (dll + ".o"),
    ]:
        if candidate.exists():
            return candidate
    return None


def verify(cache_root, variant):
    build = cache_root / "tmod" / variant
    manifest = json.loads((build / "manifest.json").read_text())
    providers = {}
    for path in sorted((build / "input").glob("*.dll")):
        add_provider(providers, path)
    add_provider(providers, cache_root / "tmod/corelib/pools-llvm-v72/System.Private.CoreLib.dll")
    for directory in [
        cache_root / "runtime-source/artifacts/bin/runtime/net9.0-libnx-Release-arm64",
        cache_root / "runtime-source/artifacts/bin/mono/libnx.arm64.Release",
        cache_root / "tmod/flat_libs",
    ]:
        if directory.exists():
            for path in sorted(directory.glob("*.dll")):
                add_provider(providers, path)

    errors = []
    checks = []
    for module_name, entry in manifest["provenance_and_binding"].items():
        obj = candidate_object(cache_root, variant, module_name, entry)
        if obj is None:
            errors.append(f"{module_name}: missing AOT object")
            continue
        dependencies = []
        for dependency in image_dependencies(obj):
            provider = providers.get(dependency["name"].casefold())
            ok = bool(provider and provider["mvid"] == dependency["mvid"])
            if not ok:
                errors.append(
                    f"{module_name}:{obj.name}: {dependency['name']} expected {dependency['mvid']} "
                    f"got {provider and provider['mvid']}"
                )
            dependencies.append({
                "dependency": dependency["name"],
                "expected": dependency["mvid"],
                "actual": provider and provider["mvid"],
                "provider": provider and provider["path"],
                "ok": ok,
            })
        checks.append({"module": module_name, "object": str(obj), "dependencies": dependencies})
    return {"variant": variant, "providers": providers, "checks": checks,
            "errors": errors, "status": "PASS" if not errors else "FAIL"}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--cache-root", type=Path, default=Path("/build"))
    parser.add_argument("--variant", required=True)
    parser.add_argument("--json-out", type=Path)
    args = parser.parse_args()

    report = verify(args.cache_root, args.variant)
    if args.json_out:
        args.json_out.write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps({"variant": args.variant, "modules": len(report["checks"]),
                      "errors": len(report["errors"]), "status": report["status"]}, indent=2))
    if report["errors"]:
        for error in report["errors"][:80]:
            print("ERROR", error)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
