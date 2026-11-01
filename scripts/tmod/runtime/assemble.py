"""Assemble the toolchain's optional `tmod-runtime` component from the engineering cache.

Maintainer tool: it reads the build cache the tModLoader runtime was validated in
(~/.cache/terraria-switch-build, "LEGACY") and writes the component directory that goes into a
toolchain as <toolchain>/tmod-runtime. How LEGACY's files were built is in README.md here.
Every copied file is checked against the hash the validated builds recorded; the output is the
same bytes for the same inputs.

    python3 -m scripts.tmod.runtime.assemble --legacy ~/.cache/terraria-switch-build \
        --toolchain ~/.cache/terrabuilder/toolchain --out <dir>/tmod-runtime --work <scratch> \
        --record scripts/tmod/runtime/component-files.json
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]

RUNTIME_RELEASE_LIB = "runtime-source/artifacts/obj/mono/libnx.arm64.Release/out/lib"
FRAMEWORK = "runtime-source/artifacts/bin/runtime/net9.0-libnx-Release-arm64"
FRAMEWORK_FILES = 443
LLVM_CROSS = "runtime-llvm/artifacts/bin/mono/linux.x64.Debug/cross/linux-x64/libnx-arm64"
CORELIB_AOT = "tmod/corelib/pools-llvm-v72"
FRAMEWORK_AOT = "tmod/tmodcli-ddcb095edfa5"

# Component path -> (LEGACY path, sha256 the validated tModLoader builds recorded or checked).
COPIES = {
    "lib/libmonosgen-2.0.a": (
        "runtime-release/libmonosgen-2.0-release.a",
        "5c0ecd2997a0e6a8e9a147ee7752907a34810b1634692f5513c1bc39a955180a"),
    "lib/libmono-component-debugger-static.a": (
        f"{RUNTIME_RELEASE_LIB}/libmono-component-debugger-static.a",
        "a12bca9c82c08a3d02185d9e018856d77f19effe705db3b7bde806cc3c005eba"),
    "lib/libmono-component-diagnostics_tracing-stub-static.a": (
        f"{RUNTIME_RELEASE_LIB}/libmono-component-diagnostics_tracing-stub-static.a",
        "6d3a7e03a6e60b534814a8d8f96266adc4aff2411067412af6a613eca8a7b83a"),
    "lib/libmono-component-hot_reload-stub-static.a": (
        f"{RUNTIME_RELEASE_LIB}/libmono-component-hot_reload-stub-static.a",
        "cb11bdc4922f0547f7806f2e1edd87a0f15ac02929ad55c5169cc06e7cef6b80"),
    "lib/libmono-component-marshal-ilgen-static.a": (
        f"{RUNTIME_RELEASE_LIB}/libmono-component-marshal-ilgen-static.a",
        "9b1f3bc12e5734cb1e0880960d8a04e8fdcc10c90db9261d48648982819554fa"),
    "lib/libSystem.Native.a": (
        "tmod/save-exit/files/libSystem.Native.a",
        "4eb483f56cedbd0d01257eb751a593829feb4e29c3957eab9a4c4fdeb034f97b"),
    "corelib/System.Private.CoreLib.dll": (
        "runtime-source/artifacts/bin/mono/libnx.arm64.Release/System.Private.CoreLib.dll",
        "c29bc7f8d4ee3d7620dfbaa61a7ecfd19e8dcba572b41d8f95c28b3ee9009df8"),
    "aot-compiler/mono-aot-cross": (
        f"{LLVM_CROSS}/mono-aot-cross",
        "f5044b24580d3f138d9a43146d6cbb241ee5472c1e4901413543b9d5e1f9154e"),
    "corelib-aot/System.Private.CoreLib.dll.o": (
        f"{CORELIB_AOT}/System.Private.CoreLib.dll.o",
        "f13677723ce8225946ec8e2c03adf886bd4702db407a9cae779392ac4fab796d"),
    "corelib-aot/System.Private.CoreLib.dll-llvm.o": (
        f"{CORELIB_AOT}/System.Private.CoreLib.dll-llvm.o",
        "f5c41c6aa831e3bb1f92b6242ba28954ee1db5605b88f0ef98e5c17918244e02"),
    "licenses/dotnet-runtime-THIRD-PARTY-NOTICES.TXT": ("runtime-source/THIRD-PARTY-NOTICES.TXT", None),
}
# The tModLoader mono-aot-cross runs the toolchain's LLVM tools, which are byte-identical to the
# ones it was built with (runtime-llvm); its RUNPATH is empty, so callers set LD_LIBRARY_PATH.
LLVM_LINKS = ("opt", "llc", "libc++.so.1", "libc++abi.so.1")
# Framework modules whose AOT objects tModLoader builds reuse (cli.py reuse_modules).
FRAMEWORK_AOT_MODULES = ("System.Linq", "System.Text.RegularExpressions", "System.Collections.Concurrent")
# scripts/tmod/metadata_io applied to framework/System.Reflection.Metadata.dll; equals
# LEGACY tmod/tmod27/input/System.Reflection.Metadata.dll.
PATCHED_SRM_SHA256 = "82762d0817e7868bc9a6d1ba5d43f944d0ae6532493c7afd9659770d9d05aeb9"


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def write_json(path: Path, value) -> None:
    path.write_text(json.dumps(value, indent=1, sort_keys=True) + "\n")


def copy(source: Path, target: Path, expected: str | None, executable: bool = False) -> None:
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, target)
    target.chmod(0o755 if executable else 0o644)
    if expected and sha256(target) != expected:
        raise SystemExit(f"{source}: sha256 {sha256(target)}, expected {expected}")


def patched_metadata(toolchain: Path, work: Path, original: Path, output: Path) -> None:
    dotnet = toolchain / "dotnet/dotnet"
    env = dict(os.environ, DOTNET_ROOT=str(dotnet.parent), DOTNET_CLI_HOME=str(work / "dotnet-home"),
               DOTNET_SYSTEM_GLOBALIZATION_INVARIANT="1", DOTNET_CLI_TELEMETRY_OPTOUT="1",
               DOTNET_NOLOGO="1", DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1")
    source = work / "metadata_io"
    shutil.rmtree(source, ignore_errors=True)
    shutil.copytree(REPO / "scripts/tmod/metadata_io", source, ignore=shutil.ignore_patterns("bin", "obj"))
    tool = work / "metadata_io-bin"
    subprocess.run([str(dotnet), "msbuild", str(source / "MetadataIo.csproj"), "-restore", "-t:Build",
                    "-p:Configuration=Release", f"-p:OutDir={tool}/",
                    f"-p:CecilPath={toolchain / 'cecil/Mono.Cecil.dll'}", "-v:q", "-nologo"],
                   env=env, check=True)
    subprocess.run([str(dotnet), str(tool / "MetadataIo.dll"), str(original), str(output)], env=env, check=True,
                   stdout=subprocess.DEVNULL)
    output.chmod(0o644)
    if sha256(output) != PATCHED_SRM_SHA256:
        raise SystemExit(f"{output}: sha256 {sha256(output)}, expected {PATCHED_SRM_SHA256}")


def assemble(legacy: Path, toolchain: Path, out: Path, work: Path) -> dict[str, str]:
    """Write the component to `out`; return {component path: source description}."""
    if out.exists():
        raise SystemExit(f"{out} exists; the component is assembled into a fresh directory")
    sources: dict[str, str] = {}
    for rel, (source, expected) in COPIES.items():
        copy(legacy / source, out / rel, expected, executable=rel == "aot-compiler/mono-aot-cross")
        sources[rel] = "LEGACY/" + source

    framework = sorted(p for p in (legacy / FRAMEWORK).iterdir())
    if len(framework) != FRAMEWORK_FILES or not all(p.is_file() for p in framework):
        raise SystemExit(f"{legacy / FRAMEWORK}: expected {FRAMEWORK_FILES} files")
    for path in framework:
        copy(path, out / "framework" / path.name, None)
        sources["framework/" + path.name] = f"LEGACY/{FRAMEWORK}/{path.name}"

    for name in LLVM_LINKS:
        if sha256(legacy / LLVM_CROSS / name) != sha256(toolchain / "aot-compiler" / name):
            raise SystemExit(f"aot-compiler/{name} differs from the one the tModLoader compiler was built with")
        (out / "aot-compiler" / name).symlink_to("../../aot-compiler/" + name)
        sources["aot-compiler/" + name] = f"symlink; target equals LEGACY/{LLVM_CROSS}/{name}"

    for name in ("dotnet-runtime-LICENSE.txt", "LLVM-LICENSE.txt"):
        copy(REPO / "licenses" / name, out / "licenses" / name, None)
        sources["licenses/" + name] = "repo licenses/" + name
    copy(HERE / "THIRD_PARTY_NOTICES.md", out / "THIRD_PARTY_NOTICES.md", None)
    sources["THIRD_PARTY_NOTICES.md"] = "repo scripts/tmod/runtime/THIRD_PARTY_NOTICES.md"

    patched_metadata(toolchain, work, out / "framework/System.Reflection.Metadata.dll",
                     out / "System.Reflection.Metadata.dll")
    sources["System.Reflection.Metadata.dll"] = (
        "scripts/tmod/metadata_io over framework/System.Reflection.Metadata.dll; "
        "equals LEGACY/tmod/tmod27/input/System.Reflection.Metadata.dll")

    corelib = json.loads((legacy / CORELIB_AOT / "manifest.json").read_text())
    if corelib["input_sha256"] != sha256(out / "corelib/System.Private.CoreLib.dll"):
        raise SystemExit("corelib-aot was compiled from another CoreLib")
    write_json(out / "corelib-aot/manifest.json", {
        "schema": "terrabuilder-tmod-corelib-aot/1",
        "assembly": "tmod-runtime/corelib/System.Private.CoreLib.dll",
        "input_sha256": corelib["input_sha256"],
        "llvm": corelib["llvm"],
        "pools": corelib["pools"],
        "compiled": corelib["compiled"],
        "aot_object": "System.Private.CoreLib.dll.o",
        "aot_object_sha256": corelib["aot_object_sha256"],
        "llvm_object": "System.Private.CoreLib.dll-llvm.o",
        "llvm_object_sha256": corelib["llvm_object_sha256"],
        "log": "aot.log",
        "legacy_command": corelib["command"],
    })
    sources["corelib-aot/manifest.json"] = f"generated from LEGACY/{CORELIB_AOT}/manifest.json"

    bindings = json.loads((legacy / FRAMEWORK_AOT / "manifest.json").read_text())["provenance_and_binding"]
    modules = {}
    for name in FRAMEWORK_AOT_MODULES:
        record = bindings[name]
        dll = name + ".dll"
        if record["compiler_input_sha256"] != sha256(out / "framework" / dll):
            raise SystemExit(f"framework-aot {name} was compiled from another assembly")
        if record.get("aot_inline_limit") is not None:
            raise SystemExit(f"framework-aot {name} was compiled with an inline limit")
        module = {"assembly": f"tmod-runtime/framework/{dll}", "compiler_input_sha256": record["compiler_input_sha256"],
                  "aot_object": dll + ".o", "aot_object_sha256": record["aot_object_sha256"]}
        copy(legacy / FRAMEWORK_AOT / "aot" / module["aot_object"], out / "framework-aot" / module["aot_object"],
             record["aot_object_sha256"])
        sources["framework-aot/" + module["aot_object"]] = f"LEGACY/{FRAMEWORK_AOT}/aot/{module['aot_object']}"
        if "llvm_object" in record:
            module.update(llvm_object=dll + "-llvm.o", llvm_object_sha256=record["llvm_object_sha256"])
            copy(legacy / FRAMEWORK_AOT / "aot" / module["llvm_object"], out / "framework-aot" / module["llvm_object"],
                 record["llvm_object_sha256"])
            sources["framework-aot/" + module["llvm_object"]] = f"LEGACY/{FRAMEWORK_AOT}/aot/{module['llvm_object']}"
        log = legacy / FRAMEWORK_AOT / f"aot-{dll}.log"
        if log.is_file():
            module["log"] = f"logs/aot-{dll}.log"
            copy(log, out / "framework-aot" / module["log"], None)
            sources["framework-aot/" + module["log"]] = f"LEGACY/{FRAMEWORK_AOT}/aot-{dll}.log"
        modules[name] = module
    write_json(out / "framework-aot/framework-aot.json", {
        "schema": "terrabuilder-tmod-framework-aot/1",
        "note": ("Framework AOT objects tModLoader builds reuse instead of recompiling. Objects are relative "
                 "to this file. LLVM objects come from aot-compiler/mono-aot-cross --llvm; System.Linq.dll.o "
                 "is a non-LLVM object carried forward from an earlier engineering build."),
        "modules": modules,
    })
    sources["framework-aot/framework-aot.json"] = f"generated from LEGACY/{FRAMEWORK_AOT}/manifest.json"
    return sources


def record(out: Path, sources: dict[str, str]) -> dict:
    files = {}
    for path in sorted(out.rglob("*")):
        rel = path.relative_to(out).as_posix()
        if path.is_symlink():
            files[rel] = {"symlink": os.readlink(path), "source": sources[rel]}
        elif path.is_file():
            files[rel] = {"bytes": path.stat().st_size, "exec": bool(path.stat().st_mode & 0o111),
                          "sha256": sha256(path), "source": sources[rel]}
    if files.keys() != sources.keys():
        raise SystemExit("component files and recorded sources differ")
    return {"component": "tmod-runtime", "files": files,
            "bytes": sum(entry.get("bytes", 0) for entry in files.values())}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--legacy", type=Path, required=True)
    parser.add_argument("--toolchain", type=Path, required=True, help="toolchain providing dotnet, cecil, aot-compiler")
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True, help="scratch directory for the MetadataIo build")
    parser.add_argument("--record", type=Path, help="write the per-file record (component-files.json) here")
    args = parser.parse_args()
    args.work.mkdir(parents=True, exist_ok=True)
    sources = assemble(args.legacy.resolve(), args.toolchain.resolve(), args.out.resolve(), args.work.resolve())
    if args.record:
        write_json(args.record, record(args.out, sources))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
