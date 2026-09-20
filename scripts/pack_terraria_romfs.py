#!/usr/bin/env python3
"""Pack a user-owned Terraria GOG install into one NRO's RomFS.

This does not distribute Terraria data. The caller supplies a local game
installation; the output NRO contains that caller's files in its embedded
RomFS. Runtime, config, logs, and ICU remain outside the NRO on the SD card.

Run inside the mono-nx devkitA64 build container from the fna-nx-test tree.
"""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys

# Mono/.NET Framework payloads supplied by mono-nx must not be copied into the
# game RomFS. They conflict with System.Private.CoreLib and the net9 BCL.
BCL_NAMES = {
    "mscorlib.dll",
    "System.dll",
    "System.Core.dll",
    "System.Configuration.dll",
    "System.Data.dll",
    "System.Drawing.dll",
    "System.Numerics.dll",
    "System.Runtime.Serialization.dll",
    "System.Security.dll",
    "System.Windows.Forms.dll",
    "System.Xml.dll",
    "System.Xml.Linq.dll",
    "WindowsBase.dll",
}

# These are not needed by the vanilla GOG executable by default. They remain
# opt-in because a future build or mod may reference one explicitly.
OPTIONAL_GAME_DLLS = (
    "I18N.dll",
    "I18N.West.dll",
    "Mono.Posix.dll",
    "Mono.Security.dll",
)

# Terraria's .NET Framework build references these concrete Mono assemblies;
# net9/libnx does not provide WinForms. They intentionally override the small
# runtime facade copies after the facade closure is staged.
LEGACY_COMPAT_DLLS = (
    "System.Windows.Forms.dll",
    "System.Drawing.dll",
)


def fail(message: str) -> "NoReturn":
    print(f"error: {message}", file=sys.stderr)
    raise SystemExit(2)


def copy_file(source: Path, destination: Path) -> None:
    if not source.is_file():
        fail(f"required file not found: {source}")
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, destination)


def ignore_download_metadata(_directory: str, names: list[str]) -> set[str]:
    """Drop Windows Zone.Identifier sidecars from an uploaded install."""
    return {name for name in names if name.endswith(":Zone.Identifier")}


def copy_runtime_facades(
    runtime_dir: Path | None, romfs_dir: Path, copied: list[str]
) -> None:
    """Embed mono-nx's complete managed facade closure at RomFS root."""
    if runtime_dir is None:
        return
    sources = [
        source
        for source in sorted(runtime_dir.glob("*.dll"))
        if source.name != "System.Private.CoreLib.dll"
    ]
    # System.Private.CoreLib is the actual loaded corelib and remains in
    # SD:/mono/lib_net9.0; all other runtime DLLs are managed facades.
    for source in sources:
        copy_file(source, romfs_dir / source.name)
    copied.append(f"runtime facades/ ({len(sources)} files)")

def copy_game_payload(
    game_dir: Path,
    romfs_dir: Path,
    include_legacy_dlls: tuple[str, ...],
    runtime_facades_dir: Path | None = None,
    patched_mscorlib: Path | None = None,
) -> list[str]:
    required = ("Terraria.exe", "FNA.dll")
    for name in required:
        if not (game_dir / name).is_file():
            fail(f"GOG install is missing {name}: {game_dir}")

    if not (game_dir / "Content").is_dir():
        fail(f"GOG install is missing Content/: {game_dir}")

    romfs_dir.mkdir(parents=True, exist_ok=True)
    copied: list[str] = []

    for name in required + ("FNA.dll.config",):
        source = game_dir / name
        if source.is_file():
            copy_file(source, romfs_dir / name)
            copied.append(name)

    for name in include_legacy_dlls:
        if name in BCL_NAMES:
            fail(f"refusing to embed framework assembly {name}")
        source = game_dir / name
        if not source.is_file():
            fail(f"requested optional assembly not found: {source}")
        copy_file(source, romfs_dir / name)
        copied.append(name)
    copy_runtime_facades(runtime_facades_dir, romfs_dir, copied)
    for name in LEGACY_COMPAT_DLLS:
        source = game_dir / name
        if source.is_file():
            copy_file(source, romfs_dir / name)
            copied.append(f"legacy compatibility: {name}")
    if patched_mscorlib is not None:
        copy_file(patched_mscorlib, romfs_dir / "mscorlib.dll")
        copied.append("patched runtime facade: mscorlib.dll")

    destination_content = romfs_dir / "Content"
    if destination_content.exists():
        shutil.rmtree(destination_content)
    shutil.copytree(
        game_dir / "Content",
        destination_content,
        ignore=ignore_download_metadata,
    )
    content_files = sum(1 for path in destination_content.rglob("*") if path.is_file())
    copied.append(f"Content/ ({content_files} files)")
    return copied


def write_external_config(output_dir: Path) -> Path:
    mono_dir = output_dir / "mono"
    mono_dir.mkdir(parents=True, exist_ok=True)
    config = mono_dir / "config.ini"
    config.write_text(
        """[mono]
logging = true
runtime_logging = false
icu = /mono/etc/icudt77l.dat
assembly_dir = "/mono/lib_net9.0;/mono/framework_net9.0;/"
config_dir = /mono/etc
default_assembly = Terraria.exe

[nx]
file_io_redirect = /mono/log.txt
exit_process_on_end = true
force_full_application = true
""",
        encoding="utf-8",
    )
    return config


def run_make(interpreter_dir: Path) -> None:
    env = os.environ.copy()
    env.setdefault("MONO_NX_DIR", str(interpreter_dir.parents[2]))
    env.setdefault("FNA_NX_INSTALL_DIR", str(interpreter_dir.parent / "install"))

    mono_shared = Path(env["MONO_NX_DIR"]) / "native" / "shared"
    project_shared_link = interpreter_dir.parent / "shared_mono_nx"
    if not mono_shared.is_dir():
        fail(f"mono-nx shared source directory not found: {mono_shared}")
    if project_shared_link.exists() and not project_shared_link.is_symlink():
        fail(f"refusing to replace non-symlink: {project_shared_link}")
    if not project_shared_link.exists():
        project_shared_link.symlink_to(mono_shared, target_is_directory=True)

    project_dir = interpreter_dir.parents[1]
    generator = project_dir / "scripts" / "gen_dl_shim.sh"
    if generator.is_file():
        subprocess.run([str(generator)], cwd=project_dir, env=env, check=True)

    subprocess.run(["make", "clean"], cwd=interpreter_dir, env=env, check=True)
    subprocess.run(
        ["make", "-j", str(os.cpu_count() or 1), "MONO_NX_USE_ROMFS=1"],
        cwd=interpreter_dir,
        env=env,
        check=True,
    )


def main() -> int:
    project_default = Path(__file__).resolve().parents[1]
    game_default = Path.home() / "FNA-Game" / "Terraria" / "game"
    parser = argparse.ArgumentParser(
        description="Embed a user-owned Terraria GOG install into one NRO RomFS"
    )
    parser.add_argument(
        "--game-dir",
        type=Path,
        default=game_default,
        help=f"GOG game directory (default: {game_default})",
    )
    parser.add_argument(
        "--project-dir",
        type=Path,
        default=project_default,
        help="fna-nx-test project directory",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=project_default / "dist" / "terraria",
        help="output directory containing the NRO and external config",
    )
    parser.add_argument(
        "--include-legacy-dll",
        action="append",
        default=[],
        choices=OPTIONAL_GAME_DLLS,
        help="optional non-framework assembly; repeat for multiple DLLs",
    )
    parser.add_argument(
        "--no-build",
        action="store_true",
        help="stage RomFS/config only; do not invoke make",
    )
    args = parser.parse_args()

    game_dir = args.game_dir.expanduser().resolve()
    project_dir = args.project_dir.expanduser().resolve()
    output_dir = args.output.expanduser().resolve()
    interpreter_dir = project_dir / "native" / "interpreter"
    romfs_dir = interpreter_dir / "romfs"
    patched_mscorlib = project_dir / "native" / "patched-mscorlib.dll"
    if not patched_mscorlib.is_file():
        patched_mscorlib = None
    runtime_facades_dir = None
    if os.environ.get("MONO_NX_ROOT"):
        candidate = (
            Path(os.environ["MONO_NX_ROOT"])
            / "artifacts"
            / "bin"
            / "runtime"
            / "net9.0-libnx-Debug-arm64"
        )
        if candidate.is_dir():
            runtime_facades_dir = candidate

    if not game_dir.is_dir():
        fail(f"game directory does not exist: {game_dir}")
    if not interpreter_dir.is_dir():
        fail(f"interpreter directory does not exist: {interpreter_dir}")

    if romfs_dir.exists():
        shutil.rmtree(romfs_dir)
    copied = copy_game_payload(
        game_dir,
        romfs_dir,
        tuple(args.include_legacy_dll),
        runtime_facades_dir,
        patched_mscorlib,
    )
    config = write_external_config(output_dir)

    if not args.no_build:
        run_make(interpreter_dir)

    nro = interpreter_dir / "mono_nx_fna.nro"
    if not nro.is_file() and not args.no_build:
        fail(f"build completed without NRO: {nro}")

    if nro.is_file():
        output_nro = output_dir / "mono" / "mono_nx_fna_terraria.nro"
        output_nro.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(nro, output_nro)
    else:
        output_nro = output_dir / "mono" / "mono_nx_fna_terraria.nro"

    print(f"staged: {', '.join(copied)}")
    print(f"RomFS source: {romfs_dir}")
    print(f"NRO: {output_nro}")
    print(f"External config: {config}")
    print("Runtime DLLs and logs remain external under SD:/mono/")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
