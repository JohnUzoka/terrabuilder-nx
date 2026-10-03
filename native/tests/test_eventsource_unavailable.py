#!/usr/bin/env python3
"""Exercise real Mono/CoreLib provider initialization, not a modeled EventSource.

Requires an existing same-fork Linux x64 Mono CMake build configured with
DISABLE_EVENTPIPE=ON, ENABLE_PERFTRACING=OFF, STATIC_COMPONENTS=ON and
AOT_COMPONENTS=ON. Builds only this probe, never the runtime or game. All generated
files remain under --work-dir. The host includes the production capability header.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import resource
import shlex
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime-source", required=True, type=Path)
    parser.add_argument("--native-build", required=True, type=Path)
    parser.add_argument("--corelib", required=True, type=Path)
    parser.add_argument("--bcl", required=True, type=Path)
    parser.add_argument("--dotnet", required=True, type=Path)
    parser.add_argument("--native-support", required=True, type=Path)
    parser.add_argument("--work-dir", required=True, type=Path)
    parser.add_argument("--cc", default="clang")
    parser.add_argument("--gdb", default="gdb")
    args = parser.parse_args()
    args.work_dir.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix="run-", dir=args.work_dir.resolve()))
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
    env = os.environ.copy()
    env.update(REPRO_INTERP="1", LD_LIBRARY_PATH=str(args.native_support), TMPDIR=str(work))
    source = Path(__file__).resolve()
    native_build = args.native_build.resolve()
    manifest = {"work_dir": str(work), "commands": [], "cases": []}

    def run(command, name, *, cwd=work, timeout=60):
        command = [str(value) for value in command]
        log_path = work / (name + ".log")
        with log_path.open("w") as log:
            log.write("COMMAND: " + repr(command) + "\n")
            log.flush()
            try:
                result = subprocess.run(command, cwd=cwd, env=env, stdout=log,
                                        stderr=subprocess.STDOUT, timeout=timeout)
                code = result.returncode
            except subprocess.TimeoutExpired:
                code = "timeout"
        manifest["commands"].append({"command": command, "exit_code": code, "log": str(log_path)})
        (work / "results.json").write_text(json.dumps(manifest, indent=2) + "\n")
        text = log_path.read_text()
        assert code == 0, (name, code, log_path)
        return text

    corelib = args.corelib.resolve()
    references = [corelib] + sorted(path for path in args.bcl.glob("*.dll") if path.name != corelib.name)
    sdk_version = run([args.dotnet, "--version"], "sdk-version").splitlines()[-1].strip()
    csc = args.dotnet.parent / "sdk" / sdk_version / "Roslyn/bincore/csc.dll"
    response = work / "probe.rsp"
    response.write_text("\n".join(["-nostdlib+", "-target:library", "-debug:portable", "-optimize-",
                                   "-out:" + str(work / "Probe.dll")]
                                  + ["-r:" + str(path) for path in references]
                                  + [str(source.with_suffix(".cs"))]) + "\n")
    run([args.dotnet, csc, "-noconfig", "@" + str(response)], "compile-managed")
    run([args.cc, "-g", "-O0", "-Wall", "-Werror", "-I" + str(args.runtime_source / "src/native/public"),
         "-c", source.with_suffix(".c"), "-o", work / "host.o"], "compile-host")
    commands = run(["ninja", "-C", native_build, "-t", "commands", "mono-sgen"], "native-link-inputs")
    link = shlex.split(commands.splitlines()[-1])
    assert link[:2] == [":", "&&"], "Unexpected source-generated linker command"
    link = link[2:link.index("&&", 2)]
    main_object = "mono/mini/CMakeFiles/mono-sgen.dir/main.c.o"
    assert link.count(main_object) == 1, "Unexpected Mono executable entrypoint"
    link[link.index(main_object)] = str(work / "host.o")
    executable = work / "eventsource-host"
    link[link.index("-o") + 1] = str(executable)
    run(link, "link-host", cwd=native_build)
    search_path = str(corelib.parent) + ":" + str(args.bcl)
    debugger = work / "provider-count.gdb"
    debugger.write_text('''set pagination off
set confirm off
set $provider_calls = 0
handle SIGSEGV stop print pass
handle SIGXCPU nostop noprint pass
handle SIGPWR nostop noprint pass
break ves_icall_System_Diagnostics_Tracing_EventPipeInternal_CreateProvider
commands
silent
set $provider_calls = $provider_calls + 1
printf "NATIVE_CREATE_PROVIDER_CALL=%d\\n", $provider_calls
bt 5
continue
end
run
printf "TOTAL_NATIVE_CREATE_PROVIDER_CALLS=%d\\n", $provider_calls
thread apply all bt 16
quit
''')
    for mode in ["default", "false"]:
        command = [executable, mode, search_path, work / "Probe.dll"]
        text = run(command, mode)
        required = ["NATIVE_EXIT=0", "NATIVE_CORELIB=" + str(corelib),
                    "PASS allocation regex exception BinaryReader"]
        required += [f"MANAGED_STAGE={stage}\n" for stage in range(1, 6)]
        if mode == "default":
            required += ["MANAGED_STAGE=100", "MANAGED_STAGE=201", "AppContext=<missing>",
                         "ArrayPool.ConstructionException=System.NullReferenceException",
                         "System.Diagnostics.Tracing.EventPipeInternal.CreateProvider",
                         "System.SR.InternalGetResourceString", "System.IO.BinaryReader.ReadInt32"]
        else:
            required += ["MANAGED_STAGE=110", "MANAGED_STAGE=200", "AppContext=false",
                         "ArrayPool.ConstructionException=<null>"]
        assert all(value in text for value in required), (mode, work / (mode + ".log"))
        debug_text = run([args.gdb, "-q", "-batch", "-x", debugger, "--args", *command], "debug-" + mode)
        count = int(re.search(r"TOTAL_NATIVE_CREATE_PROVIDER_CALLS=(\d+)", debug_text).group(1))
        assert "NATIVE_EXIT=0" in debug_text, work / ("debug-" + mode + ".log")
        assert count > 0 if mode == "default" else count == 0, (mode, count)
        manifest["cases"].append({"mode": mode, "exit_code": 0, "native_provider_calls": count})
        print(f"PASS {mode}: native provider calls={count}; ArrayPool/Regex/exception/BinaryReader passed", flush=True)

    inputs = [corelib, source.with_suffix(".cs"), source.with_suffix(".c"),
              source.parent.parent / "shared/nx_runtime_config.h", native_build / "config.h"]
    manifest["sha256"] = {str(path): hashlib.sha256(path.read_bytes()).hexdigest() for path in inputs}
    manifest["native_support"] = str(args.native_support)
    manifest["execution"] = "Same-fork Linux x64 Mono interpreter; existing libnx ARM64 CoreLib IL/BCL; no AOT objects"
    manifest["limitation"] = "Default reproduces the reentrant NRE as EventSource.ConstructionException, but not the fatal Switch ARM64 crash; the x64 interpreter catches it and continues."
    (work / "results.json").write_text(json.dumps(manifest, indent=2) + "\n")
    (args.work_dir / "latest.json").write_text(json.dumps({"work_dir": str(work)}, indent=2) + "\n")
    print("RESULTS:", work, flush=True)


if __name__ == "__main__":
    main()
