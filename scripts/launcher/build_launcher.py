#!/usr/bin/env python3
"""Compile the native launcher objects from source.

The script runs the devkitA64 compiler inside ``localhost/monobuild:local`` with the
toolchain mounted read-only, and writes only to the requested output directory, which
must live under the work directory mounted as ``/build`` in the container. main.o is
built by scripts/native/link_nro.py against each build's AOT registration header.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import shlex
import shutil
import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from terrabuilder_pkg import toolchain  # noqa: E402

IMAGE = "localhost/monobuild:local"

ARCH = ["-march=armv8-a+crc+crypto", "-mtune=cortex-a57", "-mtp=soft", "-fPIE"]
GCC = "/opt/devkitpro/devkitA64/bin/aarch64-none-elf-gcc"
MONO_INC = "/mono-nx/dotnet_runtime/artifacts/bin/mono/libnx.arm64.Debug/include/mono-2.0"
BASE_DEFINES = [
    "-D__SWITCH__",
    "-DMONO_NX_USE_AOT=1",
    "-DMONO_NX_GL_COMPAT=1",
    "-DMONO_NX_USE_ROMFS=1",
    "-DDLSHIM_SDL2=1",
    "-DDLSHIM_SDL2_IMAGE=1",
    "-DDLSHIM_OPENGL=1",
    "-DDLSHIM_FNA3D=1",
    "-DDLSHIM_FNA=1",
    "-DDLSHIM_STUBS=1",
    "-DDLSHIM_SDL3=1",
    "-DU_DISABLE_RENAMING=1",
]
INCLUDES = [
    "-I/work/native/shared",
    "-I/mono-nx/native/shared",
    "-I/mono-nx/native/shared/third_party/ini",
    f"-I{MONO_INC}",
    "-I/mono-nx/icu/libnx/include",
    "-I/opt/devkitpro/libnx/include",
    "-I/opt/devkitpro/portlibs/switch/include",
    "-I/opt/devkitpro/portlibs/switch/include/SDL2",
    "-I/fna-install/include",
]

LINK_ORDER = [
    "core.o",
    "dl_shim.o",
    "dl_shim_dotnet.o",
    "dl_shim_libnx.o",
    "dl_shim_stubs.o",
    "heap.o",
    "io_shims.o",
    "io_util.o",
    "ini.o",
    "dl_shim_FAudio.o",
    "dl_shim_FNA3D.o",
    "dl_shim_SDL3.o",
    "nx_input.o",
    "dl_shim_SDL2.o",
    "dl_shim_SDL2_image.o",
    "dl_shim_opengl.o",
]

MONO_NX_SOURCES = {
    "core.o": "/mono-nx/native/shared/core.c",
    "dl_shim.o": "/mono-nx/native/shared/dl_shim.c",
    "dl_shim_dotnet.o": "/mono-nx/native/shared/dl_shim_dotnet.c",
    "dl_shim_libnx.o": "/mono-nx/native/shared/dl_shim_libnx.c",
    "dl_shim_stubs.o": "/mono-nx/native/shared/dl_shim_stubs.c",
    "heap.o": "/mono-nx/native/shared/heap.c",
    "io_shims.o": "/mono-nx/native/shared/io_shims.c",
    "io_util.o": "/mono-nx/native/shared/io_util.c",
    "ini.o": "/mono-nx/native/shared/third_party/ini/ini.c",
    "dl_shim_SDL2.o": "/mono-nx/native/shared/dl_shim_sdl2/dl_shim_SDL2.c",
    "dl_shim_SDL2_image.o": "/mono-nx/native/shared/dl_shim_sdl2_image/dl_shim_SDL2_image.c",
    "dl_shim_opengl.o": "/mono-nx/native/shared/dl_shim_opengl/dl_shim_opengl.c",
}

REPO_SOURCES = {
    "dl_shim_FAudio.o": "/work/native/shared/dl_shim_FAudio.c",
    "dl_shim_FNA3D.o": "/work/native/shared/dl_shim_FNA3D.c",
    "dl_shim_SDL3.o": "/work/native/shared/dl_shim_SDL3.c",
    "nx_input.o": "/work/native/shared/nx_input.c",
    "nx_audio.o": "/work/native/shared/nx_audio.c",
    "nx_gpu_timing.o": "/work/native/shared/nx_gpu_timing.c",
    "nx_profiler.o": "/work/native/shared/nx_profiler.c",
}


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def under_workdir(path: Path, workdir: Path) -> str:
    try:
        return "/build/" + path.resolve().relative_to(workdir).as_posix()
    except ValueError as exc:
        raise SystemExit(f"output directory must be under {workdir}") from exc


def q(words: list[str] | tuple[str, ...]) -> str:
    return " ".join(shlex.quote(str(w)) for w in words)


def compile_argv(obj: str, src: str, out: str, *, diagnostics: bool) -> list[str]:
    flags = [GCC, *ARCH, "-g", "-O2", "-ffunction-sections", "-Wall", *BASE_DEFINES, *INCLUDES]
    if obj in {"nx_input.o", "nx_audio.o", "nx_gpu_timing.o"}:
        flags.insert(flags.index("-Wall") + 1, "-Wextra")
        if obj in {"nx_input.o", "nx_gpu_timing.o"} and diagnostics:
            flags += ["-DMONO_NX_PHASE_TIMING=1", "-DMONO_NX_GPU_TIMING=1"]
        if obj == "nx_audio.o" and diagnostics:
            flags.append("-DMONO_NX_AUDIO_DIAG=1")
    if obj == "nx_profiler.o":
        # nx_profiler.o keeps its original flags: no -Wall/-Wextra or shim defines.
        flags = [
            GCC,
            *ARCH,
            "-g",
            "-O2",
            "-ffunction-sections",
            "-D__SWITCH__",
            "-I/work/native/shared",
            "-I/mono-nx/native/shared",
            "-I/opt/devkitpro/libnx/include",
        ]
    return [*flags, "-c", src, "-o", out]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--workdir", type=Path, required=True, help="work directory, mounted as /build")
    ap.add_argument("--toolchain", type=Path, required=True, help="toolchain directory (see terrabuilder toolchain)")
    ap.add_argument("--out", type=Path, required=True, help="output directory under --workdir")
    ap.add_argument("--profiler", action="store_true", help="also build nx_profiler.o")
    ap.add_argument("--debug-diagnostics", action="store_true", help="enable NX_PHASE/NX_GPU/NX_AUDIO diagnostics")
    ap.add_argument("--force", action="store_true", help="remove and recreate the output directory")
    args = ap.parse_args()

    diagnostics = args.debug_diagnostics or args.profiler
    profile = "profiler" if args.profiler else "debug" if diagnostics else "release"
    workdir = args.workdir.resolve()
    try:
        tc = toolchain.load(args.toolchain.resolve())
    except toolchain.ToolchainError as error:
        raise SystemExit(str(error)) from error
    out_dir = args.out.resolve()
    if out_dir.exists():
        if not args.force:
            raise SystemExit(f"{out_dir} already exists (pass --force to replace)")
        shutil.rmtree(out_dir)
    objects_dir = out_dir / "objects"
    objects_dir.mkdir(parents=True)

    out_container = under_workdir(out_dir, workdir)
    obj_container = f"{out_container}/objects"
    objects: dict[str, dict[str, object]] = {}
    commands: list[list[str]] = []

    source_for = {**MONO_NX_SOURCES, **REPO_SOURCES}
    object_names = list(LINK_ORDER) + ["nx_audio.o", "nx_gpu_timing.o"]
    if args.profiler:
        object_names.append("nx_profiler.o")

    for obj in object_names:
        src = source_for[obj]
        dst = f"{obj_container}/{obj}"
        argv = compile_argv(obj, src, dst, diagnostics=diagnostics)
        commands.append(argv)
        objects[obj] = {"source": src, "argv": argv, "path": dst}

    script = out_dir / "compile-commands.sh"
    script.write_text(
        "#!/bin/sh\nset -eu\n"
        + "\n".join(f"echo CC {shlex.quote(Path(c[-1]).name)}; {q(c)}" for c in commands)
        + "\n"
    )
    script.chmod(0o755)

    mounts = ["-v", f"{workdir}:/build", "-v", f"{ROOT}:/work:ro", *tc.mounts()]
    cmd = ["podman", "run", "--rm", "--userns=keep-id", "--entrypoint", "sh", *mounts, IMAGE, f"{out_container}/compile-commands.sh"]
    subprocess.run(cmd, check=True)

    for obj in object_names:
        host = objects_dir / obj
        objects[obj]["sha256"] = sha256(host)
        objects[obj]["bytes"] = host.stat().st_size

    manifest = {
        "profile": profile,
        "diagnostics": diagnostics,
        "monoNxCommit": "8be547c (github.com/JohnUzoka/mono-nx fna-support)",
        "objects": objects,
        "linkOrder": LINK_ORDER,
        "extraObjects": ["nx_audio.o", "nx_gpu_timing.o"] + (["nx_profiler.o"] if args.profiler else []),
    }
    (out_dir / "launcher-build.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(out_dir)
    return 0


if __name__ == "__main__":
    sys.exit(main())
