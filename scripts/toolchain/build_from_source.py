#!/usr/bin/env python3
"""Builds a toolchain directory from the public, pinned sources in toolchain.lock.json.

Covers: build images, Mesa, the mono-nx SDK baseline + native sources, native deps
(FNA3D/FAudio/MojoShader), the release Mono runtime + CoreLib/framework, the LLVM AOT
cross compiler, Cecil, and the dotnet SDK. Writes the result to a fresh directory (never
the live <workdir>/toolchain) and verifies it with terrabuilder_pkg.toolchain before
printing the swap-in command.

One component has no from-source recipe yet and is carried over from an existing,
already-verified toolchain instead (see toolchain.lock.json's not_yet_from_source):
facades (mscorlib.dll, System.IO.Packaging.dll, System.Security.Permissions.dll
type-forwarding facades). Pass --reference-toolchain to point at the toolchain to copy it
from; it defaults to <workdir>/toolchain.

Each step is idempotent: it checks for its own output before doing any work, so a failed
run can simply be re-invoked. Heavy container steps are serialized through the same
~/.cache/terrabuilder/.heavy.lock flock the CLI uses, and run one at a time regardless
(this script never backgrounds two of its own steps), since the build machine has limited RAM.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import subprocess
import sys
import time
import urllib.request
import zipfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT))
from terrabuilder_pkg import toolchain as tclib  # noqa: E402

HEAVY_LOCK = Path.home() / ".cache/terrabuilder/.heavy.lock"


def log(msg: str) -> None:
    print(f"[from-source {time.strftime('%H:%M:%S')}] {msg}", flush=True)


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def run(cmd: list, **kw) -> None:
    print("+", " ".join(str(c) for c in cmd), flush=True)
    subprocess.run(cmd, check=True, **kw)


def heavy_run(cmd: list, **kw) -> None:
    """Serialize a container step against the same lock the CLI's heavy builds take."""
    HEAVY_LOCK.parent.mkdir(parents=True, exist_ok=True)
    run(["flock", str(HEAVY_LOCK), *cmd], **kw)


def engine() -> str:
    for name in ("podman", "docker"):
        if shutil.which(name):
            return name
    raise SystemExit("podman or docker is required")


def find_one(base: Path, name: str, exclude: str = "/ref/") -> Path:
    all_matches = sorted(base.rglob(name))
    # dotnet/runtime builds also produce reference-assembly stubs (metadata only, no IL
    # bodies) alongside the real implementation DLLs, conventionally under a path
    # containing /ref/; exclude those so the implementation assembly wins.
    matches = [m for m in all_matches if exclude not in m.as_posix()] or all_matches
    if not matches:
        raise SystemExit(f"expected to find {name} under {base}, found nothing")
    if len(matches) > 1:
        log(f"warning: multiple matches for {name} under {base}, using the newest: {matches}")
        matches.sort(key=lambda p: p.stat().st_mtime)
    return matches[-1]


def download_verify(url: str, expected_sha256: str, dest: Path) -> Path:
    if dest.is_file() and sha256(dest) == expected_sha256:
        log(f"{dest.name} already downloaded and verified")
        return dest
    dest.parent.mkdir(parents=True, exist_ok=True)
    log(f"downloading {url}")
    tmp = dest.with_name(dest.name + ".part")
    urllib.request.urlretrieve(url, tmp)
    got = sha256(tmp)
    if got != expected_sha256:
        tmp.unlink(missing_ok=True)
        raise SystemExit(f"sha256 mismatch for {url}: got {got}, expected {expected_sha256}")
    tmp.replace(dest)
    return dest


def git_clone_pinned(url: str, commit: str, dest: Path) -> None:
    if not (dest / ".git").exists():
        dest.parent.mkdir(parents=True, exist_ok=True)
        run(["git", "clone", "--quiet", url, str(dest)])
    have = subprocess.run(["git", "-C", str(dest), "cat-file", "-e", commit + "^{commit}"],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0
    if not have:
        run(["git", "-C", str(dest), "fetch", "--quiet", "origin", commit])
    run(["git", "-C", str(dest), "checkout", "--quiet", "--force", commit])
    run(["git", "-C", str(dest), "clean", "-qfdx"])


def apply_patch(dest: Path, patch_rel: str) -> None:
    patch_path = REPO_ROOT / patch_rel
    already = subprocess.run(["git", "-C", str(dest), "apply", "--reverse", "--check", str(patch_path)],
                              stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0
    if already:
        log(f"{patch_rel} already applied to {dest.name}, skipping")
        return
    run(["git", "-C", str(dest), "apply", str(patch_path)])
    log(f"applied {patch_rel} to {dest.name}")


def ensure_image(eng: str, lock_entry: dict, extra_tags: tuple = ()) -> None:
    tag = lock_entry["tag"]
    exists = subprocess.run([eng, "image", "exists", tag]).returncode == 0
    if exists:
        log(f"image {tag} already present, skipping build")
    else:
        containerfile = REPO_ROOT / lock_entry["containerfile"]
        heavy_run([eng, "build", "-t", tag, "-f", str(containerfile), str(containerfile.parent)])
    for t in extra_tags:
        run([eng, "tag", tag, t])


def copy_tree(src: Path, dst: Path) -> None:
    dst.mkdir(parents=True, exist_ok=True)
    for item in src.iterdir():
        target = dst / item.name
        if item.is_dir():
            shutil.copytree(item, target, dirs_exist_ok=True)
        else:
            shutil.copy2(item, target)


# ---------------------------------------------------------------------------
# Steps. Each takes (lock, scratch, eng) and is safe to re-run.
# ---------------------------------------------------------------------------

def step_images(lock: dict, scratch: Path, eng: str) -> None:
    log("== images ==")
    # The Mesa image is built by scripts/mesa/build_mesa.sh itself (it hardcodes podman and
    # its own tag); only the mono/LLVM toolchain image needs building here.
    ensure_image(eng, lock["images"]["toolchain"],
                 extra_tags=("localhost/monobuild:local", "localhost/monobuild-llvm:local"))


def step_mesa(lock: dict, scratch: Path, eng: str) -> Path:
    log("== Mesa (libEGL.a, libglapi.a) ==")
    out = scratch / "mesa-build" / "glthread-lib"
    if (out / "libEGL.a").is_file() and (out / "libglapi.a").is_file():
        log("Mesa already built, skipping")
        return out
    mesa_scratch = scratch / "mesa-build"
    mesa_scratch.mkdir(parents=True, exist_ok=True)
    heavy_run([str(REPO_ROOT / "scripts/mesa/build_mesa.sh"), str(mesa_scratch)])
    return out


def step_mono_nx_sdk(lock: dict, scratch: Path, eng: str) -> Path:
    log("== mono-nx prebuilt SDK baseline ==")
    out = scratch / "sdk-pristine"
    if (out / "icu/libnx/lib/libicuuc.a").is_file():
        log("sdk-pristine already extracted, skipping")
        return out
    zip_path = download_verify(lock["mono_nx_sdk"]["url"], lock["mono_nx_sdk"]["sha256"],
                                scratch / "downloads" / "mono-nx-sdk-linux-x64.zip")
    out.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(zip_path) as z:
        z.extractall(out)
        # ZipFile.extractall() ignores the Unix permission bits stored in each entry's
        # external_attr, so executables (mono-aot-cross, mcs, etc.) come out mode 644 --
        # restore the stored mode explicitly, matching what a real unzip(1) would do.
        for info in z.infolist():
            mode = (info.external_attr >> 16) & 0o777
            if mode:
                (out / info.filename).chmod(mode)
    return out


def step_mono_nx_native(lock: dict, scratch: Path, eng: str) -> Path:
    log("== mono-nx native/ sources (mono-nx-native component) ==")
    dest = scratch / "src" / "mono-nx"
    m = lock["mono_nx"]
    git_clone_pinned(m["repo"], m["commit"], dest)
    return dest / "native"


def step_native_deps(lock: dict, scratch: Path, eng: str) -> Path:
    log("== native deps (FNA3D/FAudio/MojoShader) ==")
    build_dir = scratch / "native-build"
    install_dir = scratch / "native-install"
    if (install_dir / "lib/libFNA3D.a").is_file() and (install_dir / "lib/libFAudio.a").is_file():
        log("native deps already built, skipping")
        return install_dir
    build_dir.mkdir(parents=True, exist_ok=True)
    install_dir.mkdir(parents=True, exist_ok=True)
    nd = lock["native_deps"]
    mounts = [
        "--userns=keep-id",
        "-v", f"{REPO_ROOT}:/work:ro",
        "-v", f"{build_dir}:/work/native/build:rw",
        "-v", f"{install_dir}:/work/native/install:rw",
        "-e", f"FNA3D_COMMIT={nd['fna3d']['commit']}",
        "-e", f"FAUDIO_COMMIT={nd['faudio']['commit']}",
        "-e", "TERRABUILDER_NATIVE_BUILD_DIR=/work/native/build",
        "-e", "TERRABUILDER_NATIVE_INSTALL_DIR=/work/native/install",
    ]
    # build_native_deps.sh is meant to run inside the image; the image's own ENTRYPOINT is a
    # fixed `bash -c "source env.sh; exec bash"` that silently ignores any args/CMD, so every
    # scripted (non-interactive) invocation must override --entrypoint explicitly.
    heavy_run([eng, "run", "--rm", *mounts, "--entrypoint", "/bin/bash",
               "localhost/monobuild:local", "/work/scripts/build_native_deps.sh"])
    return install_dir


def step_runtime_release(lock: dict, scratch: Path, eng: str, sdk_pristine: Path) -> tuple[Path, Path]:
    """Release CoreLib/framework + release native Mono runtime. Returns (runtime_dir, dotnet_dir)."""
    log("== runtime-source: release CoreLib/framework + native runtime ==")
    # build_release_managed.sh/build_runtime_release.sh hardcode `cd /build/runtime-source`,
    # so the checkout must sit directly under scratch (mounted at /build), not nested deeper.
    dest = scratch / "runtime-source"
    out_runtime = scratch / "runtime-release"
    if (out_runtime / "lib/libmonosgen-2.0.a").is_file() and (out_runtime / "corelib/System.Private.CoreLib.dll").is_file():
        log("release runtime already built, skipping")
        dotnet_dir = dest / ".dotnet"
        return out_runtime, dotnet_dir

    def find_release_corelib():
        # The mono.corelib subset's own direct build output -- NOT libs.sfx's illink-arm64
        # shared-framework bundle, which also happens to carry a same-named CoreLib copy
        # (needed for the sfx trimmer's own reachability closure) but is newer by mtime, so
        # a plain find_one() across all of artifacts/ picks it over this one. That copy is
        # trimmed for a generic app's reachability, not the Mono VM's own needs, and lacks
        # runtime-critical types the AOT compiler's bootstrap requires (e.g.
        # System.Diagnostics.MonoStackFrame; see mono_class_load_from_name calls in
        # mono_init_internal, mono/metadata/domain.c) -- confirmed via "strings" showing the
        # type present in this directory's copy and absent from the illink-arm64 one.
        base = dest / "artifacts" / "bin" / "mono"
        if not base.is_dir():
            return None
        try:
            return find_one(base, "System.Private.CoreLib.dll")
        except SystemExit:
            return None

    def find_release_framework_dir():
        try:
            return find_one(dest / "artifacts", "System.Text.RegularExpressions.dll").parent
        except SystemExit:
            return None

    def find_libmono():
        try:
            return find_one(dest / "artifacts", "libmonosgen-2.0.a")
        except SystemExit:
            return None

    corelib, framework_dir, libmono = find_release_corelib(), find_release_framework_dir(), find_libmono()
    if corelib is not None and framework_dir is not None and libmono is not None:
        log("release runtime already built in runtime-source/artifacts, skipping rebuild")
    else:
        r = lock["runtime"]
        git_clone_pinned(r["repo"], r["commit"], dest)
        for patch in r["apply_patches"]:
            apply_patch(dest, patch)

        (scratch / "nuget-packages").mkdir(parents=True, exist_ok=True)
        # Under --userns=keep-id, podman synthesizes a passwd entry whose home directory is the
        # image's WORKDIR (/mono-nx), which we mount read-only -- dotnet's first-run sentinel
        # write then fails with "Read-only file system". Point HOME at the writable scratch mount.
        mounts = ["--userns=keep-id", "-e", "HOME=/build",
                  "-v", f"{scratch}:/build", "-v", f"{sdk_pristine}:/mono-nx:ro"]

        # 1) Release CoreLib + shared framework.
        managed_script = dest / "build_release_managed.sh"
        shutil.copy2(REPO_ROOT / "scripts/release_bcl/build_release_managed.sh", managed_script)
        heavy_run([eng, "run", "--rm", *mounts, "--entrypoint", "/bin/bash",
                   "localhost/monobuild:local", "/build/runtime-source/build_release_managed.sh"])

        # 2) Release native Mono runtime (libmonosgen + components).
        runtime_script = dest / "build_runtime_release.sh"
        shutil.copy2(REPO_ROOT / "scripts/release_bcl/build_runtime_release.sh", runtime_script)
        heavy_run([eng, "run", "--rm", *mounts, "--entrypoint", "/bin/bash",
                   "localhost/monobuild:local", "/build/runtime-source/build_runtime_release.sh"])

        corelib, framework_dir, libmono = find_release_corelib(), find_release_framework_dir(), find_libmono()
        if corelib is None or framework_dir is None or libmono is None:
            raise SystemExit(f"release managed/native build did not produce the expected "
                              f"artifacts under {dest / 'artifacts'}")

    out_runtime.mkdir(parents=True, exist_ok=True)
    (out_runtime / "corelib").mkdir(exist_ok=True)
    (out_runtime / "framework").mkdir(exist_ok=True)
    (out_runtime / "lib").mkdir(exist_ok=True)

    shutil.copy2(corelib, out_runtime / "corelib/System.Private.CoreLib.dll")
    for dll in framework_dir.glob("*.dll"):
        if dll.name != "System.Private.CoreLib.dll":
            shutil.copy2(dll, out_runtime / "framework" / dll.name)

    shutil.copy2(libmono, out_runtime / "lib/libmonosgen-2.0.a")
    for component in ("debugger", "diagnostics_tracing-stub", "hot_reload-stub", "marshal-ilgen"):
        lib = find_one(dest / "artifacts", f"libmono-component-{component}-static.a")
        shutil.copy2(lib, out_runtime / "lib" / lib.name)

    return out_runtime, dest / ".dotnet"


def step_llvm_cross(lock: dict, scratch: Path, eng: str, sdk_pristine: Path) -> Path:
    log("== runtime-llvm: LLVM AOT cross compiler ==")
    # Separate checkout (sibling of runtime-source, both directly under scratch == /build):
    # build_llvm_cross.sh hardcodes `cd /build/runtime-llvm`.
    dest = scratch / "runtime-llvm"
    out = scratch / "aot-compiler"
    out_files = ("mono-aot-cross", "opt", "llc", "libc++.so.1", "libc++abi.so.1")
    if all((out / name).is_file() for name in out_files):
        log("LLVM cross compiler already built, skipping")
        return out

    def find_cross_dir():
        # Not a plain find_one(): build_llvm_cross.sh runs two steps that each produce a
        # "mono-aot-cross" somewhere under artifacts/ -- a libnx-arm64-TARGET offsets build
        # (runnable only on Switch hardware, useless here) and the real LLVM-enabled
        # linux-x64-HOST cross compiler (runs on this build machine, targets libnx-arm64 as
        # output) -- and their mtimes don't reliably order newest-last, nor does bundling
        # with opt/llc disambiguate them (both configs' output dirs end up with all three).
        # The linux.x64 path component is what actually distinguishes the host-runnable
        # build; matches docs/BUILDING.md 3.5's documented layout and this script's own
        # "Copying ... to .../linux.x64.*/cross/linux-x64/libnx-arm64" build log line.
        # build_llvm_cross.sh's own bundling step also already stages libc++.so.1/
        # libc++abi.so.1 alongside the three tools there (confirmed via ldd on the built
        # mono-aot-cross) -- no need to hunt them down separately in the image's /usr/lib
        # (clang/lld in that image link against libstdc++, not libc++, anyway).
        if not (dest / "artifacts").is_dir():
            return None
        candidates = [p.parent for p in (dest / "artifacts").rglob("mono-aot-cross") if p.is_file()]
        bundled = [d for d in candidates if all((d / n).exists() for n in out_files[1:])]
        host = [d for d in bundled if "linux.x64" in d.as_posix()]
        if not host:
            return None
        if len(host) > 1:
            log(f"warning: multiple linux.x64-host directories bundle {out_files}, "
                f"using the newest: {host}")
        return max(host, key=lambda d: (d / "mono-aot-cross").stat().st_mtime)

    cross_dir = find_cross_dir()
    if cross_dir is not None:
        log("LLVM cross compiler already built in runtime-llvm/artifacts, skipping rebuild")
    else:
        r = lock["runtime"]
        git_clone_pinned(r["repo"], r["commit"], dest)
        for patch in r["apply_patches"]:
            apply_patch(dest, patch)

        cross_script = dest / "build_llvm_cross.sh"
        shutil.copy2(REPO_ROOT / "scripts/release_bcl/build_llvm_cross.sh", cross_script)
        # Same HOME fix as step_runtime_release (see comment there): avoid writing to the
        # read-only /mono-nx mount that --userns=keep-id would otherwise pick as $HOME.
        mounts = ["--userns=keep-id", "-e", "HOME=/build",
                  "-v", f"{scratch}:/build", "-v", f"{sdk_pristine}:/mono-nx:ro"]
        heavy_run([eng, "run", "--rm", *mounts, "--entrypoint", "/bin/bash",
                   "localhost/monobuild-llvm:local", "/build/runtime-llvm/build_llvm_cross.sh"])
        cross_dir = find_cross_dir()
        if cross_dir is None:
            raise SystemExit(f"no linux.x64-host directory under {dest/'artifacts'} bundles "
                              f"{out_files} together after building")

    out.mkdir(parents=True, exist_ok=True)
    for name in out_files:
        shutil.copy2(cross_dir / name, out / name)
        (out / name).chmod(0o755)
    return out


def framework_aot_matches(out: Path, runtime_out: Path) -> bool:
    """True if out/framework-aot.json's recorded modules still match runtime_out's current
    corelib/framework DLLs (same check build_from_source's own objects must pass later, in
    compile_terraria_aot.py's framework_modules())."""
    manifest_path = out / "framework-aot.json"
    if not manifest_path.is_file():
        return False
    try:
        record = json.loads(manifest_path.read_text())
    except (OSError, ValueError):
        return False
    if record.get("schema") != "terrabuilder-framework-aot/1" or not record.get("modules"):
        return False
    for module in record["modules"]:
        expected = module["assembly"]
        subdir = "corelib" if expected["name"] == "System.Private.CoreLib" else "framework"
        source = runtime_out / subdir / (expected["name"] + ".dll")
        obj, llvm = out / module["object"], out / module["llvm_object"]
        if not (source.is_file() and obj.is_file() and llvm.is_file()):
            return False
        if sha256(source) != expected["sha256"]:
            return False
    return True


def step_framework_aot(lock: dict, scratch: Path, eng: str, runtime_out: Path, aot_compiler: Path) -> Path:
    log("== framework-aot: CoreLib/RegularExpressions/Concurrent LLVM AOT objects ==")
    out = scratch / "framework-aot"
    if framework_aot_matches(out, runtime_out):
        log("framework-aot already built and matches the current runtime build, skipping")
        return out
    if out.exists():
        shutil.rmtree(out)
    # runtime_out and aot_compiler are both direct children of scratch (see step_runtime_release/
    # step_llvm_cross); derive their in-container paths from their names rather than hardcoding,
    # so a future rename of either doesn't silently desync this step.
    mounts = ["--userns=keep-id", "-v", f"{scratch}:/build", "-v", f"{REPO_ROOT}:/work:ro"]
    heavy_run([eng, "run", "--rm", *mounts, "--entrypoint", "/usr/bin/python3",
               "localhost/monobuild-llvm:local", "/work/scripts/build_framework_aot.py",
               "--corelib-dir", f"/build/{runtime_out.name}/corelib",
               "--framework-dir", f"/build/{runtime_out.name}/framework",
               "--llvm-compiler-dir", f"/build/{aot_compiler.name}",
               "--output-dir", f"/build/{out.name}"])
    if not framework_aot_matches(out, runtime_out):
        raise SystemExit(f"framework-aot build at {out} does not match the current runtime build "
                          f"after running build_framework_aot.py")
    return out


def step_cecil(lock: dict, scratch: Path) -> Path:
    log("== Cecil ==")
    out = scratch / "cecil"
    if (out / "Mono.Cecil.dll").is_file():
        log("Cecil already fetched, skipping")
        return out
    c = lock["cecil"]
    nupkg = download_verify(c["nupkg_url"], c["nupkg_sha256"], scratch / "downloads" / "mono.cecil.nupkg")
    out.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(nupkg) as z:
        for name in z.namelist():
            if name.startswith("lib/netstandard2.0/") and name.endswith(".dll"):
                data = z.read(name)
                (out / Path(name).name).write_bytes(data)
    return out


def step_dotnet_sdk(lock: dict, scratch: Path) -> Path:
    log("== dotnet SDK ==")
    out = scratch / "dotnet"
    if (out / "dotnet").is_file():
        log("dotnet SDK already installed, skipping")
        return out
    script = scratch / "downloads" / "dotnet-install.sh"
    script.parent.mkdir(parents=True, exist_ok=True)
    if not script.is_file():
        urllib.request.urlretrieve("https://dot.net/v1/dotnet-install.sh", script)
    script.chmod(0o755)
    out.mkdir(parents=True, exist_ok=True)
    run(["bash", str(script), "--version", lock["dotnet_sdk"], "--install-dir", str(out), "--no-path"])
    return out


def step_notices(scratch: Path) -> Path:
    out = scratch / "notices"
    out.mkdir(parents=True, exist_ok=True)
    shutil.copy2(REPO_ROOT / "THIRD_PARTY_NOTICES.md", out / "THIRD_PARTY_NOTICES.md")
    for extra in ("CREDITS.md",):
        src = REPO_ROOT / extra
        if src.is_file():
            shutil.copy2(src, out / extra)
    licenses_dir = REPO_ROOT / "licenses"
    if licenses_dir.is_dir():
        copy_tree(licenses_dir, out / "licenses")
    return out


def step_retained(reference: Path, component: str, dest: Path) -> None:
    """Carry over a component with no from-source recipe yet from a known-good toolchain."""
    src = reference / component
    if not src.is_dir():
        raise SystemExit(f"--reference-toolchain is missing component {component!r} at {src}; "
                          "pass a working toolchain with --reference-toolchain")
    log(f"== {component}: carrying over from {src} (no from-source recipe yet) ==")
    copy_tree(src, dest / component)


def assemble(scratch: Path, final: Path, mesa_out: Path, sdk_pristine: Path, mono_nx_native: Path,
             native_deps: Path, runtime_out: Path, aot_compiler: Path, framework_aot_out: Path,
             cecil_out: Path, dotnet_out: Path, notices_out: Path, reference: Path, lock_path: Path) -> None:
    log(f"== assembling toolchain at {final} ==")
    if final.exists():
        shutil.rmtree(final)
    final.mkdir(parents=True)

    (final / "mesa" / "lib").mkdir(parents=True, exist_ok=True)
    for name in ("libEGL.a", "libglapi.a"):
        shutil.copy2(mesa_out / name, final / "mesa" / "lib" / name)

    copy_tree(sdk_pristine, final / "sdk")
    copy_tree(mono_nx_native, final / "mono-nx-native")
    copy_tree(native_deps, final / "native-deps")
    copy_tree(runtime_out, final / "runtime")
    copy_tree(aot_compiler, final / "aot-compiler")
    copy_tree(framework_aot_out, final / "framework-aot")
    copy_tree(cecil_out, final / "cecil")
    copy_tree(dotnet_out, final / "dotnet")
    copy_tree(notices_out, final / "notices")
    step_retained(reference, "facades", final)

    provenance = {"built_by": "scripts/toolchain/build_from_source.py", "lock": "toolchain.lock.json",
                  "retained_from_reference_toolchain": ["facades"],
                  "reference_toolchain": str(reference)}
    manifest = tclib.write_manifest(final, origin="from-source", provenance=provenance,
                                     lock_sha256=sha256(lock_path))
    log(f"manifest written: {len(manifest['components'])} components, "
        f"{len(manifest['files'])} files")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--workdir", type=Path, default=Path.home() / ".cache/terrabuilder")
    ap.add_argument("--scratch", type=Path, default=None,
                     help="staging directory (default: <workdir>/fromsource-build)")
    ap.add_argument("--lock", type=Path, default=REPO_ROOT / "toolchain.lock.json")
    ap.add_argument("--reference-toolchain", type=Path, default=None,
                     help="toolchain to copy facades from (default: <workdir>/toolchain)")
    ap.add_argument("--out", type=Path, default=None,
                     help="final toolchain output dir (default: <workdir>/toolchain.fromsource)")
    args = ap.parse_args()

    workdir = args.workdir.expanduser().resolve()
    scratch = (args.scratch or workdir / "fromsource-build").expanduser().resolve()
    reference = (args.reference_toolchain or workdir / "toolchain").expanduser().resolve()
    final = (args.out or workdir / "toolchain.fromsource").expanduser().resolve()
    scratch.mkdir(parents=True, exist_ok=True)

    lock = json.loads(args.lock.read_text())
    eng = engine()

    step_images(lock, scratch, eng)
    mesa_out = step_mesa(lock, scratch, eng)
    sdk_pristine = step_mono_nx_sdk(lock, scratch, eng)
    mono_nx_native = step_mono_nx_native(lock, scratch, eng)
    native_deps = step_native_deps(lock, scratch, eng)
    runtime_out, _dotnet_bootstrap = step_runtime_release(lock, scratch, eng, sdk_pristine)
    aot_compiler = step_llvm_cross(lock, scratch, eng, sdk_pristine)
    framework_aot_out = step_framework_aot(lock, scratch, eng, runtime_out, aot_compiler)
    cecil_out = step_cecil(lock, scratch)
    dotnet_out = step_dotnet_sdk(lock, scratch)
    notices_out = step_notices(scratch)

    assemble(scratch, final, mesa_out, sdk_pristine, mono_nx_native, native_deps, runtime_out,
             aot_compiler, framework_aot_out, cecil_out, dotnet_out, notices_out, reference, args.lock)

    problems = tclib.verify(final)
    if problems:
        log(f"toolchain verify FAILED with {len(problems)} problem(s):")
        for p in problems[:20]:
            print(" ", p)
        return 1

    log("toolchain verify: ok")
    print(f"\nBuilt and verified at {final}.")
    print(f"To use it: mv {reference} {reference}.bak && mv {final} {reference}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
