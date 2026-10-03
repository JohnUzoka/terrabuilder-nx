#!/usr/bin/env python3
"""Run the source-derived Mono switch-table regression without a runtime build.

Usage: python3 test_mono_switch_table.py --runtime-source /path/to/runtime-source
The checkout may be before or after the adjacent source patch. All generated
sources, binaries and assertion logs stay in the disk-backed build cache.
"""
import argparse
from pathlib import Path
import resource
import signal
import subprocess
import tempfile


def extract_switch(text, name):
    start = text.index("\tcase MONO_PATCH_INFO_SWITCH: {", text.index("mono_resolve_patch_target_ext ("))
    end = text.index("\tcase MONO_PATCH_INFO_METHODCONST:", start)
    return f'#line {text[:start].count(chr(10)) + 1} "{name}"\n' + text[start:end]


def extract_helpers(text):
    # This fork defines both helpers at the end of mono-codeman.c. Keep their
    # actual FULL-only bypass, asserts and flush/translation order unchanged.
    start = text.index("guint8* mono_codeman_enable_write_ex (")
    assert "guint8* mono_codeman_disable_write_ex (" in text[start:]
    return f'#line {text[:start].count(chr(10)) + 1} "mono-codeman.c"\n' + text[start:]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime-source", required=True, type=Path)
    parser.add_argument("--patch", type=Path, default=Path(__file__).resolve().parent.parent / "patches/mono-switch-table-data-memory.patch")
    parser.add_argument("--work-dir", type=Path, default=Path.home() / ".cache/terraria-switch-build/switch-table-regression")
    parser.add_argument("--cc", default="cc")
    args = parser.parse_args()
    args.work_dir.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix="run-", dir=args.work_dir.resolve()))
    log = []
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))

    def run(command, *, cwd=work, success=True):
        result = subprocess.run([str(part) for part in command], cwd=cwd, text=True, capture_output=True)
        log.append("$ " + " ".join(str(part) for part in command))
        log.append(f"exit={result.returncode}\n{result.stdout}{result.stderr}")
        (work / "results.log").write_text("\n".join(log))
        if success and result.returncode:
            raise RuntimeError(f"Command failed: {command}\n{result.stdout}{result.stderr}")
        return result

    relative = Path("src/mono/mono/mini/mini-runtime.c")
    source_text = (args.runtime_source / relative).read_text()
    patch_root = work / "runtime"
    staged_source = patch_root / relative
    staged_source.parent.mkdir(parents=True)
    staged_source.write_text(source_text)
    patch_command = ["patch", "--batch", "--fuzz=0", "-p1", "-i", args.patch.resolve()]
    forward = run([*patch_command, "--forward", "--dry-run"], cwd=patch_root, success=False)
    if forward.returncode == 0:
        old = source_text
        run([*patch_command, "--forward"], cwd=patch_root)
        new = staged_source.read_text()
    else:
        new = source_text
        run([*patch_command, "--reverse"], cwd=patch_root)
        old = staged_source.read_text()
    (work / "switch_old.inc").write_text(extract_switch(old, "mini-runtime.old.c"))
    (work / "switch_new.inc").write_text(extract_switch(new, "mini-runtime.c"))
    helpers = (args.runtime_source / "src/mono/mono/utils/mono-codeman.c").read_text()
    (work / "codeman_helpers.inc").write_text(extract_helpers(helpers))
    harness = work / "test_mono_switch_table.c"
    harness.write_text(Path(__file__).resolve().with_suffix(".c").read_text())
    flags = [args.cc, "-std=c11", "-O2", "-Wall", "-Wextra", "-Werror", "-pedantic", "-I", work, harness]
    executable = work / "test_mono_switch_table"
    no_codeman = work / "test_mono_switch_table_no_codeman"
    for command in ([*flags, "-o", executable], [*flags, "-DMONO_ARCH_NO_CODEMAN", "-o", no_codeman]):
        print("Compile:", " ".join(str(part) for part in command))
        run(command)

    def expect_abort(binary, arguments, message):
        result = run([binary, *arguments], success=False)
        assert result.returncode == -signal.SIGABRT, result.stderr
        assert message in result.stderr, result.stderr
        print("PASS: expected SIGABRT:", " ".join(arguments))
        if arguments[0].startswith("old-aot"):
            assert "jit area not found" in result.stderr
            print(result.stderr.strip())

    for case in ("old-aot-method", "old-aot-no-method"):
        expect_abort(executable, [case], "res != NULL")
    result = run([executable])
    print(result.stdout, end="")
    for operation in ("unknown-write", "unknown-exec"):
        for mode in ("interp", "normal"):
            expect_abort(executable, [operation, mode], "res != NULL")
    for version in ("old", "new"):
        expect_abort(no_codeman, [version], "MONO_ARCH_NO_CODEMAN")
    print("PASS: 8 expected assertion failures; results:", work / "results.log")


if __name__ == "__main__":
    main()
