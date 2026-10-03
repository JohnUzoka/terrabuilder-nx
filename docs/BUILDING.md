# Building terrabuilder-nx

This guide builds a Terraria NRO for Switch homebrew from **your own copy** of the
game (GOG Linux build 1.4.5.x). Nothing from the game is in this repository.

> **Status, read first.** A vanilla build is reproducible from your game files plus
> a **toolchain directory** whose files are all pinned by SHA-256 in its manifest.
> `./terrabuilder toolchain --from-source --replace` builds that toolchain from the
> pinned public sources in `toolchain.lock.json` (hours; see "Automated path" below).
> One component, `facades`, is still a retained artifact rather than a from-source
> build (hardware-proven as-is; see 3.2 for what rebuilding it from source would
> take). Parts 1–3 double as the engineering recipes for every component, automated
> or not.

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

### Automated path: building the toolchain from source

```sh
./terrabuilder toolchain --from-source --replace --workdir ~/.cache/terrabuilder
```

This runs `scripts/toolchain/build_from_source.py`, which reads every pin from
`toolchain.lock.json` and builds: the Mesa libraries, the mono-nx SDK baseline
(downloaded and SHA-256-verified, not rebuilt), the mono-nx native sources, FNA3D/
FAudio/MojoShader, the release CoreLib/framework and native Mono runtime, the LLVM
AOT cross compiler, the framework AOT objects (CoreLib, System.Text.RegularExpressions,
System.Collections.Concurrent), Cecil (from the `mono.cecil` NuGet package), and the
dotnet SDK. It writes the result to `<workdir>/toolchain.fromsource`, carries
`facades` over from the toolchain at `--reference-toolchain` (default
`<workdir>/toolchain`) since that component has no from-source recipe yet, and runs
`toolchain verify` before printing (or, with `--replace`, performing) the swap into
`<workdir>/toolchain`. Each step is idempotent, so a failed or interrupted run can
just be re-invoked. Expect several hours on an 8-core/10 GB machine: it serializes
every heavy step through `~/.cache/terrabuilder/.heavy.lock`, the same lock the CLI's
own heavy builds take, rather than risk running more than one at a time on limited RAM.

Parts 1–3 below are the same pipeline spelled out by hand component by component,
which is how `build_from_source.py` itself was derived and verified; read them for
the reasoning behind each step or to rebuild one component in isolation.

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

`toolchain --from-source` builds the toolchain as described above; `toolchain
--bundle` still fails explicitly, since there is no bundle import yet. Manual
Runtime/BCL/Mesa/LLVM build steps (parts 1–3) should still run under
`flock ~/.cache/terrabuilder/.heavy.lock`, the lock the CLI and
`build_from_source.py` both take for their heavy container steps.

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
| .NET runtime (libnx port) | `github.com/JohnUzoka/dotnet_runtime`, a fork of `exelix11/dotnet_runtime` | branch `terrabuilder-nx` (`7f043851`) = upstream `libnx` + 2 of 4 historical fixes (switch-table-data-memory, sockets-console-DEBUG-guard already upstream; `native/patches/mono-coop-managed-allocator.patch` and `native/patches/mono-aot-alc-resolve.patch` applied on top, pinned in `toolchain.lock.json`) |
| mono-nx host | `github.com/JohnUzoka/mono-nx` | branch `fna-support` (`8be547c`) |
| mono-nx prebuilt SDK | published by upstream author `exelix11/mono-nx`, release `rel-3`, `mono-nx-sdk-linux-x64.zip` (built by mono-nx's own `gather_sdk.sh`: Debug-config ICU + mono-aot-cross + runtime bundle) | SHA-256 `3248d136…c098` |
| Cecil | `mono.cecil` NuGet package (bundles Mono.Cecil, .Rocks, .Mdb, .Pdb) | `0.11.5` |
| dotnet SDK | `dotnet-install.sh --channel 9.0` | `9.0.102` |
| Mesa | `archive.mesa3d.org` tarball + devkitPro's `switch-mesa-20.1.0-5` patch set + 2 local glthread/gpu-wait patches | `20.1.0-rc3` |
| Native deps | `FNA-XNA/FNA3D`, `FNA-XNA/FAudio` (MojoShader pinned transitively via FNA3D's submodule) | FNA3D `2c616bf8`, FAudio `41bfee95`, MojoShader `ad5dff84` |

Every pin above (except the fork tip, which tracks this repo's own branch) is recorded in
`toolchain.lock.json`, which `scripts/toolchain/build_from_source.py` reads instead of
hardcoding versions.

Why the exelix11 fork rather than `dotnet/runtime`: upstream has no Nintendo
Switch target. The fork's `libnx` branch adds it: an `--os libnx` build target,
`HOST_LIBNX` code paths in the Mono runtime (code manager, fake mmap over a fixed
heap, threads, logging) and native libraries, plus libnx variants of CoreLib and
Sockets. That port is what mono-nx and this project run on.

```sh
W=~/.cache/terraria-switch-build
mkdir -p $W && cd $W
git clone --filter=blob:none -b terrabuilder-nx https://github.com/JohnUzoka/dotnet_runtime.git runtime-source
cd runtime-source && git apply /path/to/terrabuilder-nx/native/patches/mono-coop-managed-allocator.patch \
  && git apply /path/to/terrabuilder-nx/native/patches/mono-aot-alc-resolve.patch && cd ..
git clone --filter=blob:none -b fna-support https://github.com/JohnUzoka/mono-nx.git release58/mono-nx
mkdir -p recovery46/downloads recovery46/sdk-pristine
# download https://github.com/exelix11/mono-nx/releases/download/rel-3/mono-nx-sdk-linux-x64.zip
# into recovery46/downloads (SHA-256 3248d136…c098), then:
unzip -q recovery46/downloads/mono-nx-sdk-linux-x64.zip -d recovery46/sdk-pristine
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
podman run --rm $MOUNTS --entrypoint /bin/bash localhost/monobuild:local -c '
  cd /work && scripts/build_native_deps.sh && scripts/gen_dl_shim.sh'
```

(The image's own `ENTRYPOINT` sources `env.sh` and drops into an interactive shell,
ignoring any args/CMD, so every scripted invocation needs `--entrypoint /bin/bash`
like above.) Output goes to `native/install/`. `build_native_deps.sh` clones FNA3D/
FAudio and pins them to `FNA3D_COMMIT`/`FAUDIO_COMMIT` (defaults: FNA3D `2c616bf8`,
FAudio `41bfee95`; MojoShader `ad5dff84` comes along transitively via FNA3D's own
submodule reference). The toolchain's `native-deps` component is these built outputs;
override the commit env vars only if you intentionally want to track newer upstream.

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

(`build_runtime_release.sh` refuses to build without the allocator fix. It isn't
committed on the fork branch itself; apply `native/patches/mono-coop-managed-allocator.patch`
to `runtime-source` first, as in section 1.) The archive and the runtime component
libraries became the toolchain's `runtime/lib`. To check that struct layouts match the
runtime the AOT code was compiled against, run
`scripts/release_bcl/dwarf_layouts.py` on both archives.

### 3.5 LLVM AOT cross compiler (build 60+)

```sh
git clone --filter=blob:none -b terrabuilder-nx https://github.com/JohnUzoka/dotnet_runtime.git $W/runtime-llvm
cd $W/runtime-llvm && git apply /path/to/terrabuilder-nx/native/patches/mono-coop-managed-allocator.patch \
  && git apply /path/to/terrabuilder-nx/native/patches/mono-aot-alc-resolve.patch && cd -
cp scripts/release_bcl/build_llvm_cross.sh $W/runtime-llvm/
podman run --rm -v $W:/build -v $W/recovery46/sdk-pristine:/mono-nx:ro \
  --entrypoint /bin/bash localhost/monobuild-llvm:local /build/runtime-llvm/build_llvm_cross.sh
```

It builds a second checkout of the same fork branch and the same two patches,
separately from `$W/runtime-source` so the two configurations' build artifacts never
mix, producing
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
assemblies differ. `scripts/build_framework_aot.py` rebuilds this component from
the from-source runtime build (3.3's CoreLib/framework output and 3.5's cross
compiler); `scripts/toolchain/build_from_source.py` runs it automatically and
re-runs it whenever CoreLib or either framework assembly changes.

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
