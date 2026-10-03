#!/usr/bin/env python3
"""Verify real old/new ARM64 pools and run the source-derived host allocator.

No ARM64 trampoline executes on this host. This proves bounded source allocation
and actual object code/data capacity, not Switch startup or eventual pool demand.
"""
import argparse
import json
from pathlib import Path
import resource
import shutil
import signal
import struct
import subprocess
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
from rebuild_corelib_aot import Elf, compare_pools, inspect_pools, require, sha256, source_declarations, write_json


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime-source", type=Path, default=Path("/build/runtime-source"))
    parser.add_argument("--old-object", type=Path, default=Path("/build/aot/System.Private.CoreLib.dll.o"))
    parser.add_argument("--new-object", type=Path, default=Path("/build/aot38/System.Private.CoreLib.dll.o"))
    parser.add_argument("--abi", type=Path, default=Path("/build/aot38/abi/layout.json"))
    parser.add_argument("--work-dir", type=Path, default=Path("/build/aot38/verification"))
    parser.add_argument("--cc", default="cc")
    args = parser.parse_args()
    args.work_dir.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix="run-", dir=args.work_dir.resolve()))
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
    abi = json.loads(args.abi.read_text())
    for source, digest in abi["sources"].items():
        actual = args.runtime_source / "src/mono/mono/mini" / Path(source).name
        require(sha256(actual) == digest, "ABI/compiler source changed since object validation")
    reports = [inspect_pools(path, abi) for path in (args.old_object, args.new_object)]
    delta = compare_pools(*reports)
    log = []

    def run(command, success=True):
        result = subprocess.run([str(part) for part in command], cwd=work, capture_output=True, text=True)
        log.append("$ " + " ".join(str(part) for part in command))
        log.append(f"exit={result.returncode}\n{result.stdout}{result.stderr}")
        (work / "results.log").write_text("\n".join(log))
        if success:
            require(result.returncode == 0, result.stderr)
        return result

    source = (args.runtime_source / "src/mono/mono/mini/aot-runtime.c").read_text()
    start = source.index("static gpointer\nget_numerous_trampoline (")
    end = source.index("\n}\n", start) + 3
    allocator = source[start:end]
    require("get_numerous_trampoline (MONO_AOT_TRAMP_SPECIFIC, 2," in source,
            "runtime caller no longer reserves two GOT slots")
    (work / "allocator.inc").write_text(f'#line {source[:start].count(chr(10)) + 1} "aot-runtime.c"\n' + allocator)
    layout = source_declarations(args.runtime_source)
    layout += f'_Static_assert(sizeof(MonoAotFileInfo) == {abi["struct_size"]}, "host/target ABI size");\n'
    for field, offset in abi["offsets"].items():
        layout += f'_Static_assert(offsetof(MonoAotFileInfo, {field}) == {offset}, "host/target {field}");\n'
    (work / "aot_layout.inc").write_text(layout)
    executable = work / "allocator-boundary"
    run([args.cc, "-std=c11", "-O2", "-Wall", "-Wextra", "-Werror", "-pthread", "-I", work,
         Path(__file__).with_suffix(".c"), "-o", executable])
    scenarios = []
    for label, obj, report in zip(("old", "new"), (args.old_object, args.new_object), reports):
        pool = report["pools"][0]
        code_path = work / (label + "-specific-code.bin")
        with obj.open("rb") as stream:
            elf = Elf(stream)
            code_path.write_bytes(elf.data((pool["code_section"], pool["code_start"]), pool["code_bytes"]))
        prefix = [executable, pool["count"], pool["stride"], pool["got_base_slot"], report["got_slots"]]
        successful = [pool["count"]] if label == "old" else [4097, pool["count"]]
        for requests in successful:
            result = run([*prefix, requests, code_path])
            require(f"served={requests}" in result.stdout, "missing allocation success evidence")
            print(result.stdout, end="")
            scenarios.append({"bank": label, "requests": requests, "status": "served"})
        result = run([*prefix, pool["count"] + 1, code_path], success=False)
        require(result.returncode == -signal.SIGABRT and "Ran out of trampolines of type 0" in result.stderr and
                f'(limit {pool["count"]})' in result.stderr, "expected source allocator exhaustion abort")
        require(f'served={pool["count"]}; requesting={pool["count"] + 1}' in result.stdout,
                "allocator failed before boundary")
        print(f'PASS: {label} request {pool["count"] + 1} fails at real source guard')
        scenarios.append({"bank": label, "requests": pool["count"] + 1, "status": "expected_SIGABRT"})
    # Adversarial real-object copies: no forged fixture is used as the fix.
    rejected = []
    mutations = [
        ("limit-only", args.old_object, reports[0]["file_info_file_offset"] + abi["offsets"]["num_trampolines"], 65536,
         "metadata count/stride exceeds emitted stub range"),
        ("got-size-only", args.old_object, reports[0]["file_info_file_offset"] + abi["offsets"]["got_size"], reports[1]["got_bytes"],
         "GOT metadata does not match emitted BSS reservation"),
    ]
    with args.new_object.open("rb") as stream:
        elf = Elf(stream)
        pool = reports[1]["pools"][0]
        code_offset = elf.sections[pool["code_section"]][4] + pool["code_start"]
    # An intact metadata/range header must not mask a corrupted emitted stub.
    mutations.append(("missing-stub", args.new_object, code_offset + 4096 * 28, 0,
                      "invalid ADRP/ADD stub"))
    for name, original, offset, value, expected in mutations:
        candidate = work / (name + ".rejected.o")
        shutil.copyfile(original, candidate)
        with candidate.open("r+b") as stream:
            stream.seek(offset)
            stream.write(struct.pack("<I", value))
        try:
            inspect_pools(candidate, abi)
        except RuntimeError as error:
            require(expected in str(error), f"wrong negative-case failure: {error}")
            rejected.append({"mutation": name, "reason": str(error)})
            print(f"PASS: rejected {name}: {error}")
        else:
            raise RuntimeError("accepted unsafe mutation: " + name)
        candidate.unlink()
    for obj, report in zip((args.old_object, args.new_object), reports):
        require(sha256(obj) == report["object_sha256"], "real object changed during negative cases")
    result_path = work / "results.json"
    write_json(result_path, {"status": "passed", "scenarios": scenarios, "rejected": rejected, "delta": delta,
               "old_sha256": reports[0]["object_sha256"], "new_sha256": reports[1]["object_sha256"],
               "allocator_source_sha256": sha256(work / "allocator.inc"),
               "scope": "Extracted allocator executes on host with actual object-derived capacities and code bytes; ARM64 stubs are decoded, never executed"})
    print("Results:", result_path)


if __name__ == "__main__":
    main()
