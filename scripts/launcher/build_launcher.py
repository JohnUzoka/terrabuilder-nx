#!/usr/bin/env python3
"""Compile the native launcher objects from source.

The script runs the devkitA64 compiler inside ``localhost/monobuild:local`` and
writes only to the requested output directory, which must live under the build
cache mounted as ``/build`` in the container.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shlex
import shutil
import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
CACHE = Path(os.environ.get("TERRABUILDER_CACHE", Path.home() / ".cache/terraria-switch-build")).resolve()
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
    "-DDLSHIM_OPENAL=1",
    "-DDLSHIM_FNA3D=1",
    "-DDLSHIM_FNA=1",
    "-DDLSHIM_STUBS=1",
    "-DDLSHIM_SDL3=1",
    "-DU_DISABLE_RENAMING=1",
]
INCLUDES = [
    "-I/build/aot42-windows",
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
    "main.o",
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
    "dl_shim_openal.o",
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
    "dl_shim_openal.o": "/mono-nx/native/shared/dl_shim_openal/dl_shim_openal.c",
}

REPO_SOURCES = {
    "main.o": "/work/native/interpreter/source/main.c",
    "dl_shim_FAudio.o": "/work/native/shared/dl_shim_FAudio.c",
    "dl_shim_FNA3D.o": "/work/native/shared/dl_shim_FNA3D.c",
    "dl_shim_SDL3.o": "/work/native/shared/dl_shim_SDL3.c",
    "nx_input.o": "/work/native/shared/nx_input.c",
    "nx_audio.o": "/work/native/shared/nx_audio.c",
    "nx_gpu_timing.o": "/work/native/shared/nx_gpu_timing.c",
    "nx_profiler.o": "/work/native/shared/nx_profiler.c",
}

V88_SOURCES = {
    "nx_input.o": "/build/release58/v81-input/nx_input.c",
    "nx_audio.o": "/build/release58/v81-input/nx_audio.c",
    "nx_gpu_timing.o": "/build/release58/v86-swap/nx_gpu_timing.c",
    # The generated shims in the cache include "../shared_mono_nx/..." from the
    # historical Makefile layout. Use the checked-in regenerated equivalents.
}


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def under_cache(path: Path) -> str:
    try:
        return "/build/" + path.resolve().relative_to(CACHE).as_posix()
    except ValueError as exc:
        raise SystemExit(f"output directory must be under {CACHE}") from exc


def q(words: list[str] | tuple[str, ...]) -> str:
    return " ".join(shlex.quote(str(w)) for w in words)


def compile_argv(obj: str, src: str, out: str, *, profiler: bool, v88_sources: bool) -> list[str]:
    if obj == "main.o":
        defines = [
            "-D__SWITCH__",
            "-DMONO_NX_USE_AOT=1",
            "-DMONO_NX_GL_COMPAT=1",
            "-DMONO_NX_USE_ROMFS=1",
            "-DMONO_NX_EMBEDDED_BCL=1",
            "-DMONO_NX_FATAL_DIAG=1",
        ]
        if profiler:
            defines.append("-DMONO_NX_PROFILER=1")
        return [
            GCC,
            *ARCH,
            "-g",
            "-O2",
            "-ffunction-sections",
            *defines,
            *INCLUDES,
            "-I/build/release58/v88/aot-final",
            "-c",
            src,
            "-o",
            out,
        ]

    flags = [GCC, *ARCH, "-g", "-O2", "-ffunction-sections", "-Wall", *BASE_DEFINES, *INCLUDES]
    if obj in {"nx_input.o", "nx_audio.o", "nx_gpu_timing.o"}:
        flags.insert(flags.index("-Wall") + 1, "-Wextra")
        flags += ["-DMONO_NX_PHASE_TIMING=1", "-DMONO_NX_GPU_TIMING=1"]
        if v88_sources:
            flags.insert(flags.index("-I/work/native/shared"), "-I/build/release58/v81-input")
            flags.insert(flags.index("-I/work/native/shared"), "-I/build/release58/v81-input/inc")
    if obj == "nx_profiler.o":
        # v82-prof's recovered flags did not include -Wall/-Wextra or the shim defines.
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
    ap.add_argument("--out", type=Path, default=None, help="output directory under TERRABUILDER_CACHE")
    ap.add_argument("--profiler", action="store_true", help="build v88-style profiler main.o and nx_profiler.o")
    ap.add_argument("--v88-sources", action="store_true", help="use retained v88 sources for nx_input/audio/gpu timing")
    ap.add_argument("--force", action="store_true", help="remove and recreate the output directory")
    args = ap.parse_args()

    profile = "profiler" if args.profiler else "release"
    flavor = "v88-sources" if args.v88_sources else "repo-head"
    out_dir = (args.out or CACHE / "launcher-src" / f"{profile}-{flavor}").resolve()
    if out_dir.exists():
        if not args.force:
            raise SystemExit(f"{out_dir} already exists (pass --force to replace)")
        shutil.rmtree(out_dir)
    objects_dir = out_dir / "objects"
    objects_dir.mkdir(parents=True)

    out_container = under_cache(out_dir)
    obj_container = f"{out_container}/objects"
    objects: dict[str, dict[str, object]] = {}
    commands: list[list[str]] = []

    source_for = {**MONO_NX_SOURCES, **REPO_SOURCES}
    if args.v88_sources:
        source_for.update(V88_SOURCES)
    object_names = list(LINK_ORDER) + ["nx_audio.o", "nx_gpu_timing.o"]
    if args.profiler:
        object_names.append("nx_profiler.o")

    for obj in object_names:
        src = source_for[obj]
        dst = f"{obj_container}/{obj}"
        argv = compile_argv(obj, src, dst, profiler=args.profiler, v88_sources=args.v88_sources)
        commands.append(argv)
        objects[obj] = {"source": src, "argv": argv, "path": dst}

    script = out_dir / "compile-commands.sh"
    script.write_text(
        "#!/bin/sh\nset -eu\n"
        + "\n".join(f"echo CC {shlex.quote(Path(c[-1]).name)}; {q(c)}" for c in commands)
        + "\n"
    )
    script.chmod(0o755)

    mounts = [
        "-v",
        f"{CACHE}:/build",
        "-v",
        f"{ROOT}:/work:ro",
        "-v",
        f"{CACHE / 'recovery46/sdk-pristine'}:/mono-nx:ro",
        "-v",
        f"{CACHE / 'release58/mono-nx/native'}:/mono-nx/native:ro",
        "-v",
        f"{CACHE / 'recovery46/native-deps/install'}:/fna-install:ro",
    ]
    cmd = ["podman", "run", "--rm", "--userns=keep-id", "--entrypoint", "sh", *mounts, IMAGE, f"{out_container}/compile-commands.sh"]
    subprocess.run(cmd, check=True)

    for obj in object_names:
        host = objects_dir / obj
        objects[obj]["sha256"] = sha256(host)
        objects[obj]["bytes"] = host.stat().st_size

    manifest = {
        "profile": profile,
        "sourceFlavor": flavor,
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
