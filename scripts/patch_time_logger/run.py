#!/usr/bin/env python3
"""Build and exercise the guarded patcher inside the mono-nx build container."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import sys


def sha(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, default=Path("/build/aot/runtime-romfs/Terraria.exe"))
    parser.add_argument("--fna", type=Path, default=Path("/build/aot/runtime-romfs/FNA.dll"))
    parser.add_argument("--output", type=Path, default=Path("/build/timelogger-opt/repro"))
    parser.add_argument("--dotnet", default="/root/.dotnet/dotnet")
    parser.add_argument("--cecil", default="/mono-nx/dotnet_runtime/artifacts/bin/Mono.Linker/Debug/net9.0/Mono.Cecil.dll")
    args = parser.parse_args()
    source, fna, root = args.input.resolve(), args.fna.resolve(), args.output.resolve()
    if source.is_relative_to(root) or fna.is_relative_to(root):
        raise RuntimeError("output must not contain either original input")
    root.mkdir(parents=True, exist_ok=True)
    originals = {str(path): sha(path) for path in (source, fna)}
    environment = os.environ.copy()
    environment.update(DOTNET_TieredCompilation="0", DOTNET_NOLOGO="1", DOTNET_CLI_TELEMETRY_OPTOUT="1")
    invocations = []

    def run(command: list[str], label: str, expected: int = 0) -> str:
        log = root / (label + ".log")
        with log.open("w") as stream:
            result = subprocess.run(command, env=environment, stdout=stream, stderr=subprocess.STDOUT)
        invocations.append({"argv": command, "returncode": result.returncode, "log": str(log)})
        text = log.read_text()
        if result.returncode != expected:
            raise RuntimeError(f"{label} exited {result.returncode}, expected {expected}; {log}\n{text}")
        print(f"PASS {label}: {log}", flush=True)
        return text

    project = Path(__file__).resolve().with_name("PatchTimeLogger.csproj")
    run([args.dotnet, "build", str(project), "-c", "Release", "-o", str(root / "tool"),
         f"-p:BaseIntermediateOutputPath={root}/obj/", f"-p:CecilPath={args.cecil}"], "build")
    tool = [args.dotnet, str(root / "tool/PatchTimeLogger.dll"), "accept"]
    for name in ("first", "repeat"):
        run(tool + [str(source), str(fna), str(root / name)], name)
    for filename in ("Terraria.exe", "TimeLoggerProbe.dll"):
        if sha(root / "first" / filename) != sha(root / "repeat" / filename):
            raise RuntimeError(f"nondeterministic {filename}")
    fixture = root / "fixtures"
    fixture.mkdir(exist_ok=True)
    bad_source = fixture / "Terraria-modified.exe"
    bad_source.write_bytes(source.read_bytes() + b"\x00")
    bad_fna = fixture / "FNA-modified.dll"
    bad_fna.write_bytes(fna.read_bytes() + b"\x00")
    cases = [
        ("reject-input", bad_source, fna, "input SHA differs"),
        ("reject-fna", source, bad_fna, "FNA SHA differs"),
        ("reject-already-patched", root / "first/Terraria.exe", fna, "input SHA differs"),
    ]
    for name, game_input, framework_input, expected_error in cases:
        text = run(tool + [str(game_input), str(framework_input), str(root / name)], name, expected=1)
        if expected_error not in text or (root / name / "Terraria.exe").exists():
            raise RuntimeError(f"guard failed: {name}")
    if originals != {str(path): sha(path) for path in (source, fna)}:
        raise RuntimeError("original input changed")
    cpu_model = next((line.partition(":")[2].strip() for line in Path("/proc/cpuinfo").read_text().splitlines()
                      if line.startswith("model name")), "not reported")
    result = {"passed": True, "originalsUnchanged": originals, "reproducible": {
        filename: sha(root / "first" / filename) for filename in ("Terraria.exe", "TimeLoggerProbe.dll")},
        "negativeGuards": [case[0] for case in cases], "host": {"platform": platform.platform(), "cpu": cpu_model},
        "invocations": invocations}
    (root / "runner-results.json").write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
