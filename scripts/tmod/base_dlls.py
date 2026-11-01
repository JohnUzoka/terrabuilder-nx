"""Build the base tModLoader assemblies of a Switch build from the user's tModLoader release.

Inputs: the unzipped pinned release (tModLoader 2026.7.3.0), the host .NET SDK and Mono.Cecil.
Outputs, in `out`:
  tModLoader.dll     release tModLoader.dll -> console patchers (scripts/tmod/patchers, in
                     CONSOLE_PATCHERS order) -> NxFix (scripts/tmod/nxfix)
  FNA.dll            release FNA.dll -> patchers/patcher_fna (romfs:/ base directory) ->
                     scripts/tmod/patch_tmod_fna (shared audio device, SuppressGCTransition)
  NxCrypto.dll       managed/nx_crypto, built deterministically
  TerrariaHooks.dll  copied from the release
  console/FNA.dll    the romfs:/-patched FNA before patch_tmod_fna: the stock RomFS and
                     flat_libs carry it (scripts/tmod/base_romfs.py)
For the pinned release every step is byte-reproducible.

build_tool() also builds the mod-set tools prepare_modset.py runs and the guided build's hook
scanner, into the same tools/ folder.
"""
from __future__ import annotations

import hashlib
import os
import shutil
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

# name: (source directory, project file, entry assembly)
TOOLS = {
    "patcher": ("scripts/tmod/patchers/patcher", "Patcher.csproj", "Patcher.dll"),
    "patcher_cctor": ("scripts/tmod/patchers/patcher_cctor", "PatchCctor.csproj", "PatchCctor.dll"),
    "patcher_autoload": ("scripts/tmod/patchers/patcher_autoload", "PatchAutoLoad.csproj", "PatchAutoLoad.dll"),
    "patcher_title": ("scripts/tmod/patchers/patcher_title", "PatchConsoleTitle.csproj", "PatchConsoleTitle.dll"),
    "patcher_monolaunch": ("scripts/tmod/patchers/patcher_monolaunch", "PatchMonoLaunch.csproj", "PatchMonoLaunch.dll"),
    "patcher_fna": ("scripts/tmod/patchers/patcher_fna", "PatchFNA.csproj", "PatchFNA.dll"),
    "patch_tmod_fna": ("scripts/tmod/patch_tmod_fna", "TModFnaPatch.csproj", "TModFnaPatch.dll"),
    "nxfix": ("scripts/tmod/nxfix", "NxFix.csproj", "NxFix.dll"),
    "nxcrypto": ("managed/nx_crypto", "NxCrypto.csproj", "NxCrypto.dll"),
    "hook_scan": ("scripts/tmod/hook_scan", "HookScan.csproj", "HookScan.dll"),
    "mod_tuning": ("scripts/tmod/mod_tuning", "ModTuning.csproj", "ModTuning.dll"),
    "il_lowering": ("scripts/tmod/il_lowering", "IlLowering.csproj", "IlLowering.dll"),
    "offline_hooks": ("scripts/tmod/offline_hooks", "Patcher.csproj", "Patcher.dll"),
}
# The tools build_base_dlls runs.
CHAIN_TOOLS = ("patcher", "patcher_cctor", "patcher_autoload", "patcher_title", "patcher_monolaunch", "patcher_fna",
               "patch_tmod_fna", "nxfix", "nxcrypto")
# `patcher` must run first: run after the others it writes different (equivalent) metadata.
# The other four commute.
CONSOLE_PATCHERS = ("patcher", "patcher_cctor", "patcher_autoload", "patcher_title", "patcher_monolaunch")
# NxCrypto is shipped, so its bytes must not depend on where it was built. The PathMap target
# is the repository path the vanilla pipeline (scripts/patch_vanilla/run.py) maps to.
NXCRYPTO_FLAGS = ["-p:Deterministic=true", "-p:ContinuousIntegrationBuild=true", "-p:UseSharedCompilation=false",
                  "-p:EnableSourceControlManagerQueries=false"]


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def dotnet_env(dotnet: Path, tools: Path) -> dict[str, str]:
    return dict(os.environ, DOTNET_ROOT=str(dotnet.parent), DOTNET_CLI_HOME=str(tools / "dotnet-home"),
                DOTNET_SYSTEM_GLOBALIZATION_INVARIANT="1", DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1",
                DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1")


def run(cmd: list[str], env: dict[str, str], cwd: Path, what: str) -> str:
    result = subprocess.run(cmd, env=env, cwd=cwd, capture_output=True, text=True)
    if result.returncode:
        raise SystemExit(f"{what} failed (exit {result.returncode}):\n{(result.stdout + result.stderr)[-3000:]}")
    return result.stdout


def tool_digest(name: str) -> str:
    """Digest of tool `name`'s sources (bin/ and obj/ left out)."""
    source = ROOT / TOOLS[name][0]
    files = sorted(p for p in source.rglob("*") if p.is_file() and not {"bin", "obj"} & set(p.relative_to(source).parts))
    return hashlib.sha256(b"".join(p.relative_to(source).as_posix().encode() + b"\0" + sha256(p).encode()
                                   for p in files)).hexdigest()[:12]


def build_tool(name: str, *, dotnet: Path, cecil: Path, tools: Path, release: Path | None = None) -> Path:
    """Build tool `name` into tools/<name>-<digest of its sources> once; return its entry assembly.
    il_lowering compiles against the MonoMod and Cecil builds in the tModLoader `release`."""
    rel, project, entry = TOOLS[name]
    source = ROOT / rel
    out = tools / f"{name}-{tool_digest(name)}"
    if (out / entry).is_file():
        return out / entry
    staging = tools / f"{name}-source"
    partial = out.with_name(out.name + ".partial")
    for path in (staging, partial):
        shutil.rmtree(path, ignore_errors=True)
    shutil.copytree(source, staging, ignore=shutil.ignore_patterns("bin", "obj"))
    flags = NXCRYPTO_FLAGS + [f"-p:PathMap={staging}=/_/repo/{rel}"] if name == "nxcrypto" else []
    if name == "il_lowering":
        if release is None:
            raise ValueError("il_lowering needs the tModLoader release it compiles against")
        flags.append(f"-p:TmodRelease={release}")
    # `dotnet build` creates an empty metadata/ directory in the dotnet root, after which
    # `toolchain verify` fails; `dotnet msbuild` leaves the toolchain untouched.
    run([str(dotnet), "msbuild", str(staging / project), "-restore", "-t:Build", "-p:Configuration=Release",
         f"-p:OutDir={partial}/", "-nologo", "-v:q", f"-p:CecilPath={cecil}", *flags],
        dotnet_env(dotnet, tools), tools, f"building {rel}")
    shutil.rmtree(staging)
    shutil.rmtree(out, ignore_errors=True)
    partial.rename(out)
    return out / entry


def flat_libraries(release: Path, dest: Path) -> None:
    """Link one copy of every managed release library into `dest`, where NxFix resolves them.
    Per file name the Linux runtime build wins over the portable lib/ build; reference
    assemblies and satellite resources are skipped."""
    def rank(path: Path) -> tuple:
        parts = path.relative_to(release / "Libraries").parts
        return ("linux" not in parts, "runtimes" in parts, parts)
    chosen: dict[str, Path] = {}
    for path in sorted((release / "Libraries").rglob("*.dll"), key=rank):
        parts = path.relative_to(release / "Libraries").parts
        if "ref" in parts or path.name.endswith(".resources.dll") or ("runtimes" in parts and "linux" not in parts):
            continue
        chosen.setdefault(path.name, path)
    for name, path in chosen.items():
        (dest / name).symlink_to(path.resolve())


def build_base_dlls(release: Path, out: Path, *, dotnet: Path, cecil: Path, tools: Path) -> dict:
    """Write out/{tModLoader,FNA,NxCrypto,TerrariaHooks}.dll and out/console/FNA.dll from the
    unzipped release.

    Returns {file name: sha256} for the five outputs plus the console-patched intermediate
    "console/tModLoader.dll"."""
    release, out, tools = release.resolve(), out.resolve(), tools.resolve()
    dotnet, cecil = dotnet.resolve(), cecil.resolve()
    tools.mkdir(parents=True, exist_ok=True)
    built = {name: build_tool(name, dotnet=dotnet, cecil=cecil, tools=tools) for name in CHAIN_TOOLS}
    env = dotnet_env(dotnet, tools)
    out.mkdir(parents=True, exist_ok=True)
    work = out / ".work"
    shutil.rmtree(work, ignore_errors=True)
    work.mkdir()

    # The console patchers resolve FNA (Terraria.Main's base type) from the release; framework
    # assemblies come from the runtime they run on (Cecil's trusted-platform-assemblies lookup).
    search = [str(release)] + sorted(str(p) for p in (release / "Libraries").rglob("*") if p.is_dir())
    current = release / "tModLoader.dll"
    for step, name in enumerate(CONSOLE_PATCHERS):
        target = work / f"tModLoader.{step}-{name}.dll"
        run([str(dotnet), str(built[name]), str(current), str(target), *search], env, work, name)
        current = target
    console_tml = current

    console_fna = work / "FNA.console.dll"
    run([str(dotnet), str(built["patcher_fna"]), str(release / "Libraries/FNA/1.0.0/FNA.dll"), str(console_fna)],
        env, work, "patcher_fna")
    shared_fna = work / "FNA.shared-audio.dll"  # the tool writes its receipt next to this
    run([str(dotnet), str(built["patch_tmod_fna"]), str(console_fna), str(shared_fna)], env, work, "patch_tmod_fna")

    shutil.copyfile(built["nxcrypto"], out / "NxCrypto.dll")

    # NxFix resolves referenced assemblies from its input's directory.
    nxfix_in = work / "nxfix-input"
    nxfix_in.mkdir()
    flat_libraries(release, nxfix_in)
    shutil.copyfile(console_tml, nxfix_in / "tModLoader.dll")
    nxfix_out = work / "tModLoader.nxfix.dll"
    run([str(dotnet), str(built["nxfix"]), str(nxfix_in / "tModLoader.dll"), str(nxfix_out),
         "--nxcrypto", str(out / "NxCrypto.dll"), "--reference", str(release / "tModLoader.dll")], env, work, "NxFix")

    shutil.copyfile(nxfix_out, out / "tModLoader.dll")
    shutil.copyfile(shared_fna, out / "FNA.dll")
    shutil.copyfile(release / "Libraries/TerrariaHooks/0.0.0.0/TerrariaHooks.dll", out / "TerrariaHooks.dll")
    (out / "console").mkdir(exist_ok=True)
    shutil.copyfile(console_fna, out / "console/FNA.dll")
    hashes = {name: sha256(out / name)
              for name in ("tModLoader.dll", "FNA.dll", "NxCrypto.dll", "TerrariaHooks.dll", "console/FNA.dll")}
    hashes["console/tModLoader.dll"] = sha256(console_tml)
    shutil.rmtree(work)
    return hashes
