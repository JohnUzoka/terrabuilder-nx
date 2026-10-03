# Building terrabuilder-nx

This guide builds a Terraria NRO for Switch homebrew from **your own copy** of the
game (GOG Linux build 1.4.5.x). Nothing from the game is in this repository.

> **Status, read first.** A vanilla build is reproducible from your game files plus
> a **toolchain directory** whose files are all pinned by SHA-256 in its manifest.
> The toolchain itself cannot yet be built from a clean checkout in one command: on
> the configured build machine it was imported from the maintainer's engineering
> cache, the artifacts that produced the hardware-verified builds. Parts 1–3 are the
> engineering recipes for its components and mark where a component still depends on
> a retained artifact rather than on a script in this repo. Turning them into a
> single from-scratch toolchain build is open work.

## 0. What you need

- Linux (or WSL2) with Podman (Docker works; replace `podman` with `docker`).
- ~30 GB free disk (measured): .NET runtime checkout and build ~3.5 GB, LLVM
  cross-compiler build ~3.4 GB, NuGet packages ~3.9 GB (the Mono LLVM SDK package alone
  is ~1 GB), build images ~6 GB (shared layers), baselines ~5–10 GB, ~2–3 GB per
  build variant. Old variants can be deleted once their NRO is tested.
- A legal copy of Terraria for Linux (GOG), e.g. `~/FNA-Game/Terraria/game`.
- A Switch running Atmosphère and hbmenu, and an SD card.

The CLI's work directory is `~/.cache/terrabuilder` (override with `--workdir` or
`TERRABUILDER_WORKDIR`), and its toolchain is `<workdir>/toolchain`. The component
recipes in parts 1–3 work in the engineering cache `~/.cache/terraria-switch-build`
(`$W`). Either directory is mounted as `/build` in container commands, and this
repo is mounted read-only as `/work`.

### First-release CLI path

For day-to-day use, prefer the repository-root CLI:

```sh
./terrabuilder doctor
./terrabuilder toolchain verify
./terrabuilder build --target vanilla \
  --game-dir "$HOME/GOG Games/Terraria1_4_5_8/game" \
  --profile release --workdir ~/.cache/terrabuilder -y
```

`doctor` checks Podman, the build images, the toolchain, free disk and memory.
`toolchain` (or `toolchain status`) summarizes `<workdir>/toolchain`;
`toolchain verify` rehashes every file against `toolchain/manifest.json`, which also
records provenance. The toolchain's `notices/` holds `THIRD_PARTY_NOTICES.md`,
`CREDITS.md`, and `licenses/` for the binaries it contains.

A vanilla build runs five stages. Each records its inputs (game-file and repo-source
SHA-256s, toolchain component digests, container image IDs) in
`<workdir>/stamps/vanilla/<stage>.json` and reruns only when one of them changes.
The build reads nothing besides the toolchain, the build images, this repo, your
game folder, and its own work directory.

| Stage | Script | Output under `<workdir>` |
| --- | --- | --- |
| patch | `scripts/patch_vanilla/run.py` (3.2) | `patch/<inputs hash>/` |
| romfs | `scripts/pack_terraria_romfs.py` (3.2) | `vanilla/romfs/` |
| aot | `scripts/compile_terraria_aot.py` (3.6) | `vanilla/aot/` |
| launcher | `scripts/launcher/build_launcher.py` (3.1) | `vanilla/launcher-<profile>/` |
| native | `scripts/native/link_nro.py` (3.6) | `vanilla/native-<profile>/` |

The CLI copies the NRO to `<workdir>/out/Terraria.nro` and writes an `sdcard/`
payload (`mono/config.ini` and the ICU data file `mono/etc/icudt77l.dat`, which the
NRO reads from the SD card at startup) and a receipt JSON that includes the
toolchain summary. Release builds omit timing/profiler diagnostics and drop the
unused OpenAL shim/link. `--profile debug` or `--debug-diagnostics` restores
phase/GPU/audio diagnostics; `--profile profiler` adds the sampling profiler and
writes `Terraria-profiler.nro`. The CLI prints every command it runs (lines
starting with `+`), so a single stage can be rerun by hand.

`toolchain --from-source` and `toolchain --bundle` fail explicitly: there is no
clean-checkout toolchain build or bundle import yet, so the CLI works only where a
toolchain directory already exists. The recipes below are engineering recipes for
its components, not a reproducible end-to-end setup for a clean machine. On this
WSL2 machine, run Runtime/BCL/Mesa/LLVM build steps under
`flock ~/.cache/terrabuilder/.heavy.lock`, the lock the CLI takes for its heavy
container steps.

### tModLoader CLI path (experimental)

The CLI can also build the Nintendo Switch tModLoader port from a local
tModLoader 1.4.4.x release folder:

```sh
./terrabuilder build --target tmodloader \
  --game-dir ~/.cache/terraria-switch-build/tmod/release \
  --mods none --workdir ~/.cache/terrabuilder -y

./terrabuilder build --target tmodloader \
  --game-dir ~/.cache/terraria-switch-build/tmod/release \
  --mods FargowiltasSouls --workdir ~/.cache/terrabuilder -y
```

`--game-dir` is the tModLoader folder, not the vanilla GOG folder. The validator
checks `tModLoader.deps.json` for a `tModLoader/1.4.4.x` target and requires
`tModLoader.dll`, `tMLMod.targets`, and `Content/`.

Outputs are under `~/.cache/terrabuilder/out/tmodloader-<selection>/`:

- `tmodloader.nro`
- `sdcard/` payload containing `switch/tmodloader/Terraria/tModLoader/Mods/`
  with `enabled.json`, plus the same `mono/` runtime files as vanilla
- `receipt.json` with the NRO SHA-256, curated mod metadata, build manifest, and
  AOT MVID verifier result

Install the NRO and the whole `sdcard/` payload together, overwriting existing
`.tmod` files. Mono runs AOT-only and checks each mod assembly's MVID against the
AOT module linked into the NRO. Workshop packages and earlier source builds can
share the same file name and version string but have different code, so they
abort at mod load with `Failed to load AOT module '<Mod>' ... doesn't match
assembly`. The build-time verifier checks the staged inputs, not the SD card.

tModLoader builds use the same toolchain but still read validated inputs from the
engineering cache `~/.cache/terraria-switch-build`: the patched `tModLoader.dll`,
the base mod set, and the `main.o` build script `release58/compile_main.sh`. Their
launcher objects are built into `launcher-src/<profile>` there. Heavy
AOT/link/container steps take `~/.cache/terrabuilder/.heavy.lock`.

tModLoader release builds use the same quiet launcher defaults and OpenAL-free
link behavior as vanilla. Debug/profiler profiles opt back into the timing wraps
and launcher diagnostics.

Curated mod metadata is in `terrabuilder_pkg/curated_tmod_mods.json`. The list is
restricted to open-source tested mods pinned to upstream commits:

| Mod | Version | Upstream | License | Dependencies |
| --- | --- | --- | --- | --- |
| Luminance | 1.0.3 | `DominicKarma/Luminance` tag `v1.0.3` (`1c187584...`) | MIT | |
| Fargo's Mutant Mod | 3.3.6.7 | `Fargowilta/Fargowiltas` commit `0153d4aa...` | MIT | |
| StructureHelper | 3.1.1 | `ScalarVector1/StructureHelper` tag `3.1.1` (`f224c11e...`) | MIT | |
| Fargo's Souls Mod | 1.7.3.7 | `Fargowilta/FargowiltasSouls` commit `226fadea...` | MIT | Fargo's Mutant, Luminance, StructureHelper |

The source-build path clones each selected repo at its pinned commit and invokes
tML's `tModLoader.targets` in a container. For the host-only desktop ModCompile
step, the CLI caches .NET 8 under `~/.cache/terrabuilder/dotnet8`, builds
FNA3D 26.07 from source under `~/.cache/terrabuilder/native-host/`, and exposes
that FNA3D plus tML's Linux SDL2/FAudio libraries through `LD_LIBRARY_PATH`.
These host native libraries are build-time inputs only and are never copied into
the Switch SD payload.

Two recorded source patches are currently required for reproducible Linux builds:
Luminance 1.0.3 gets the upstream `SetFactory` compatibility fix, and
StructureHelper 3.1.1 replaces AssGen 3.0.0's Windows-path-only generated assets
with the equivalent checked source wrapper. Fargo's Souls builds from source with
all source-built mod assemblies AOT/LLVM-compiled. The current source-mod
build also native-AOT compiles `tModLoader.dll` (without LLVM optimization).
Its AOT object is verified against the staged assembly MVID. FNA's shared-audio
patch is required: an earlier candidate used stock FNA and crashed when the
XACT audio engine attempted to open a second SDL device.

Hardware status of the current Fargo's Souls build: the mods load, the main menu
and a world are reachable, and audio works. Known issues: in-world performance
is very slow (an earlier build measured roughly 15-19 fps); the D-pad moves
through the main menu and the Start menu but not the inventory; the Plus+Minus
FPS toggle did not work on an earlier build and has not been re-tested;
multiplayer and extended play are untested; only the curated open-source mod
list is available.

## 1. Sources

| What | Where | Revision |
| --- | --- | --- |
| This repo | `github.com/JohnUzoka/terrabuilder-nx` | current branch |
| .NET runtime (libnx port) | `github.com/JohnUzoka/dotnet_runtime`, a fork of `exelix11/dotnet_runtime` | branch `terrabuilder-nx` (`969ed2ab`) = upstream `libnx` `289cdaa5` + 4 fixes |
| mono-nx host | `github.com/JohnUzoka/mono-nx` | branch `fna-support` (`8be547c`) |
| mono-nx prebuilt SDK | mono-nx release `rel-3`, `mono-nx-sdk-rel3-linux-x64.zip` | SHA-256 `3248d136…c098` |

Why the exelix11 fork rather than `dotnet/runtime`: upstream has no Nintendo
Switch target. The fork's `libnx` branch adds it: an `--os libnx` build target,
`HOST_LIBNX` code paths in the Mono runtime (code manager, fake mmap over a fixed
heap, threads, logging) and native libraries, plus libnx variants of CoreLib and
Sockets. That port is what mono-nx and this project run on.

```sh
W=~/.cache/terraria-switch-build
mkdir -p $W && cd $W
git clone --filter=blob:none -b terrabuilder-nx https://github.com/JohnUzoka/dotnet_runtime.git runtime-source
git clone --filter=blob:none -b fna-support https://github.com/JohnUzoka/mono-nx.git release58/mono-nx
mkdir -p recovery46/downloads recovery46/sdk-pristine
# download the mono-nx rel-3 SDK zip into recovery46/downloads, then:
unzip -q recovery46/downloads/mono-nx-sdk-rel3-linux-x64.zip -d recovery46/sdk-pristine
```

## 2. Build images

```sh
# Base image: mono-nx's Dockerfile (devkitA64 + .NET 9 SDK + clang).
podman build -t localhost/monobuild:local $W/release58/mono-nx
# LLVM image: adds clang-19/lld-19 to build Mono's LLVM AOT cross compiler.
podman build -t localhost/monobuild-llvm:local - <<'EOF'
FROM localhost/monobuild:local
RUN apt-get update && apt-get install -y --no-install-recommends clang-19 lld-19 && rm -rf /var/lib/apt/lists/*
EOF
```

Behind a TLS-intercepting proxy, add your CA bundle to both images. mono-nx's
Dockerfile also downloads cmake from cmake.org; if that fails, drop that step, as
the base image's cmake (≥ 3.20) is enough.

Standard mounts, used by every container command below (`$MOUNTS`):

```sh
MOUNTS="-v $W:/build -v $PWD:/work:ro \
  -v $W/recovery46/sdk-pristine:/mono-nx:ro \
  -v $W/release58/mono-nx/native:/mono-nx/native:ro \
  -v $W/recovery46/native-deps/install:/fna-install:ro"
```

The CLI mounts the toolchain instead: `<workdir>/toolchain` at `/toolchain`, and
its `sdk`, `mono-nx-native` and `native-deps` components at `/mono-nx`,
`/mono-nx/native` and `/fna-install`, so scripts see the same container paths.

## 3. Pipeline

### 3.1 Native dependencies and launcher (once)

FNA3D, FAudio and MojoShader as static libnx libraries, plus the dl-shim tables
generated from their exported symbols:

```sh
podman run --rm $MOUNTS localhost/monobuild:local -c '
  cd /work && scripts/build_native_deps.sh && scripts/gen_dl_shim.sh'
```

Output goes to `native/install/`. `build_native_deps.sh` clones FNA3D/FAudio at their
current HEAD. The toolchain's `native-deps` component holds the copies the shipped
builds link (FNA3D `2c616bf8`, FAudio `41bfee95`, MojoShader `ad5dff84`), imported
from `recovery46/native-deps/install`, whose loaded code is verified against build
42. Pin the same commits if you rebuild them.

The CLI's launcher stage compiles the launcher objects from `native/` and the
toolchain's mono-nx sources. To run it by hand (`--out` must be under `--workdir`):

```sh
TB=~/.cache/terrabuilder
scripts/launcher/build_launcher.py --workdir $TB --toolchain $TB/toolchain --out $TB/scratch/launcher-release
scripts/launcher/build_launcher.py --workdir $TB --toolchain $TB/toolchain --profiler --out $TB/scratch/launcher-profiler
```

The default profile keeps the input/audio/FPS and swapchain wrappers but omits
`MONO_NX_PROFILER` and `nx_profiler.o`. `--debug-diagnostics` enables the
phase/GPU/audio diagnostics, and `--profiler` enables them and adds the sampling
profiler object. `launcher-build.json` records each object's SHA-256 and the link
order for `link_nro.py`.

### 3.2 Game payload (RomFS)

`scripts/patch_vanilla/run.py` applies Mono.Cecil patches to a clean GOG 1.4.5.8
install and emits the managed assemblies the Switch build uses: `Terraria.exe`,
patched `ReLogic.dll` and `FNA.dll`, `NxCrypto.dll`, `NxInputDiag.dll`, the other
dependency DLLs embedded in `Terraria.exe`, and a receipt. Each patch checks the
expected method shapes before rewriting; the patch list is in
`scripts/patch_vanilla/README.md`. `scripts/pack_terraria_romfs.py` stages the
game into a RomFS tree, excluding the Windows/Framework BCL DLLs and Linux `.so`
files. The CLI runs both steps and adds the toolchain's framework assemblies and
CoreLib. Where an embedded DLL shares a framework assembly's name (only
`System.ValueTuple.dll`, a .NET Framework implementation), the RomFS keeps the
net9 facade so its types do not duplicate CoreLib's.
**Retained artifact:** the type-forwarding facades staged at the RomFS root
(patched `mscorlib.dll`, `System.IO.Packaging.dll`,
`System.Security.Permissions.dll`) come from the toolchain's `facades` component,
imported from build 52's RomFS (`hint52/aot-final/runtime-romfs`). No script in
this repo produces them yet.

### 3.3 Release CoreLib/framework (build 58+)

```sh
podman run --rm -v $W:/build -v $W/recovery46/sdk-pristine:/mono-nx:ro \
  --entrypoint /bin/bash localhost/monobuild:local /build/release58/build_release_managed.sh
```

The scripts in `scripts/release_bcl/` expect to run from `$W/release58/`; copy them
there (`cp scripts/release_bcl/* $W/release58/`). The output became the toolchain's
`runtime/corelib` and `runtime/framework`.

### 3.4 Release native Mono runtime (build 65+)

```sh
podman run --rm -v $W:/build -v $W/recovery46/sdk-pristine:/mono-nx:ro \
  --entrypoint /bin/bash localhost/monobuild:local /build/runtime-release/build_runtime_release.sh
cp $W/runtime-source/artifacts/obj/mono/libnx.arm64.Release/out/lib/libmonosgen-2.0.a \
   $W/runtime-release/libmonosgen-2.0-release.a
```

(`build_runtime_release.sh` refuses to build without the allocator fix, which is
committed on the fork branch.) The archive and the runtime component libraries
became the toolchain's `runtime/lib`. To check that struct layouts match the
runtime the AOT code was compiled against, run
`scripts/release_bcl/dwarf_layouts.py` on both archives.

### 3.5 LLVM AOT cross compiler (build 60+)

```sh
git clone --filter=blob:none -b terrabuilder-nx https://github.com/JohnUzoka/dotnet_runtime.git $W/runtime-llvm
cp scripts/release_bcl/build_llvm_cross.sh $W/runtime-llvm/
podman run --rm -v $W:/build -v $W/recovery46/sdk-pristine:/mono-nx:ro \
  --entrypoint /bin/bash localhost/monobuild-llvm:local /build/runtime-llvm/build_llvm_cross.sh
```

It builds a second checkout of the same fork branch at `$W/runtime-llvm`, producing
`artifacts/bin/mono/linux.x64.Debug/cross/linux-x64/libnx-arm64/{mono-aot-cross,opt,llc}`.
Together with `libc++.so.1` and `libc++abi.so.1` they form the toolchain's
`aot-compiler` component.

The .NET build scripts in 3.3–3.5 use `$W/nuget-packages` by default (override
with `NUGET_PACKAGES`). Behind a TLS-intercepting proxy, set
`TERRABUILDER_CA_BUNDLE=/build/path/to/ca-bundle.crt`; otherwise the container's
system CA bundle is used.

### 3.6 AOT compile and link

The CLI's `aot` stage runs `scripts/compile_terraria_aot.py` in the LLVM image. It
AOT-compiles the game-derived assemblies in the RomFS (Terraria, FNA, ReLogic,
Newtonsoft.Json, NxCrypto, NxInputDiag and the other game libraries), Terraria and
FNA through LLVM (`--llvm-module`), checks that every object binds the MVID of the
staged assembly, and writes `build-manifest.json` and the module registration
header `mono_aot_modules.h`.

CoreLib and the framework are not compiled per build. The toolchain's
`framework-aot` component holds their objects: CoreLib,
System.Text.RegularExpressions and System.Collections.Concurrent, compiled once
through LLVM, CoreLib with larger trampoline pools
(`ntrampolines=65536,nimt-trampolines=8192,ngsharedvt-trampolines=4096,nunbox-arbitrary-trampolines=2048`).
`framework-aot.json` records the original commands and the exact assemblies they
were compiled from; `--framework-aot` rejects a build whose CoreLib or framework
assemblies differ. No script in this repo rebuilds these objects yet.

The `native` stage runs `scripts/native/link_nro.py` in the LLVM image. It checks
every AOT and launcher object against the SHA-256 recorded in `build-manifest.json`
and `launcher-build.json`, compiles `native/interpreter/source/main.c` against
`mono_aot_modules.h` (`scripts/native/compile_main.sh`), links with the link line
recorded in `native/interpreter/link-arguments.json` (the toolchain's runtime, Mesa
and native-deps libraries), and packages the ELF and RomFS with `elf2nro`.
`native-build.json` and `link.map` are written next to `mono_nx_fna.nro`.

To link a variant without recompiling, rerun the `link_nro.py` command the CLI
printed with a fresh `--out` and extra `--main-define` options; for example,
`--main-define=MONO_NX_GC_STATS=1` builds a GC-stats variant.

### 3.7 Verify

```sh
cd $W && R58_VARIANT=v66 R58_TITLE="Terraria 66 GC params" \
  uv run --with pyelftools python3 release58/verify_artifact.py
```

It checks every native method target and fallback, no RWX segments, read-only
method tables, and that the RomFS differs from build 52 only by the expected
framework swap. The NRO is `$W/release58/<variant>/native/candidate/mono_nx_fna.nro`.
`verify_artifact.py` reads only that engineering-cache layout; it does not check
CLI builds yet.

## 4. Install on the Switch

Copy the NRO to `sd:/switch/` and the contents of the build's `sdcard/` folder to
the SD card root, then start hbmenu in full application mode (hold R while
launching a game). [Testing on a Switch](testing.md) covers the checks, save
locations, and logs. The log is `sd:/mono/log.txt`, and it **appends** across
launches, so rename or delete it between measured runs.

## 5. Build options

| Option | Where | Default | Effect |
| --- | --- | --- | --- |
| `MONO_NX_PHASE_TIMING` | Makefile | 0 | `NX_PHASE` frame timing and SDL/FNA3D call wrappers |
| `MONO_NX_GC_STATS` | Makefile / `link_nro.py --main-define` | 0 | `NX_GC` lines: GC counts/time, managed allocation, malloc use |
| `/mono/gc_params.txt` | SD card | absent | One line passed to SGen as `MONO_GC_PARAMS` |
| `MONO_NX_EMBEDDED_BCL` | Makefile | 0 | Load CoreLib/framework from the NRO's RomFS |
| `MONO_NX_FATAL_DIAG` | Makefile | 0 | Fatal Mono errors write a crash report |
| `--llvm-module`, `--llvm-aot-extra` | `compile_terraria_aot.py` | CLI: Terraria, FNA | Modules compiled through LLVM, extra LLVM options |
