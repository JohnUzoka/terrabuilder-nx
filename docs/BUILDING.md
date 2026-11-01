# Building terrabuilder-nx

This guide builds a Terraria NRO for Switch homebrew from **your own copy** of the
game (GOG Linux build 1.4.5.x). Nothing from the game is in this repository.

> **Status, read first.** Most machines should install the prebuilt toolchain
> (`./terrabuilder toolchain --bundle`, below; the guided build offers it). The
> from-source toolchain build uses the public pins in `toolchain.lock.json` plus
> three exact, SHA-256-pinned build-52 facade assemblies checked into
> `toolchain-artifacts/facades`. They are preserved byte-for-byte, not regenerated;
> a pre-existing toolchain cache is not needed. That build takes hours (see
> "Automated path" below). Parts 1–3 explain the component recipes; they do not
> regenerate those three facade assemblies.

## 0. What you need

- Linux (or WSL2) with Podman (Docker works; replace `podman` with `docker`).
- ~30 GB free disk for the from-source toolchain build (measured): .NET runtime
  checkout and build ~3.5 GB, LLVM cross-compiler build ~3.4 GB, NuGet packages
  ~3.9 GB (the Mono LLVM SDK package alone is ~1 GB), build images ~6 GB (shared
  layers), baselines ~5–10 GB, ~2–3 GB per build variant. Old variants can be
  deleted once their NRO is tested. The prebuilt toolchain needs 1.85 GB (the
  274 MiB download plus 1.56 GB unpacked) and the build image about 5 GB.
- A legal copy of Terraria for Linux (GOG), e.g. `~/FNA-Game/Terraria/game`.
  tModLoader builds use only its `Content/` and also accept Terraria 1.4.4.9 or a
  copy of Steam's Terraria folder.
- A Switch running Atmosphère and hbmenu, and an SD card.

The CLI's work directory is `~/.cache/terrabuilder` (override with `--workdir` or
`TERRABUILDER_WORKDIR`), and its toolchain is `<workdir>/toolchain`. The component
recipes in parts 1–3 work in the engineering cache `~/.cache/terraria-switch-build`
(`$W`). Either directory is mounted as `/build` in container commands, and this
repo is mounted read-only as `/work`.

### Prebuilt path: downloading the toolchain

```sh
./terrabuilder toolchain --bundle
```

This downloads the release asset pinned in `TOOLCHAIN_BUNDLE`
(`terrabuilder_pkg/cli.py`), 274 MiB, into `<workdir>/downloads/`. The file must
match the pinned SHA-256. It is unpacked next to the work directory's toolchain and
rehashed file by file against its `manifest.json` (as `toolchain verify` does)
before it becomes `<workdir>/toolchain`. A usable toolchain is kept unless
`--replace` is given, which keeps the previous one as `toolchain.bak`;
`--bundle <file>` installs a local copy of the same asset. The command then builds
the container image from `scripts/toolchain/Containerfile` and tags it
`localhost/monobuild:local` and `localhost/monobuild-llvm:local` (a present image
is only re-tagged). The guided build offers both steps when its machine check finds
them missing.

The asset holds the from-source toolchain built 2026-10-03 plus the optional
component `tmod-runtime`, which tModLoader builds need: the open-source runtime
parts of the maintainer's tModLoader builds (`scripts/tmod/runtime/README.md`
records where each file came from; the from-source path does not produce it). It
is written by `./terrabuilder toolchain pack`: an xz-compressed tar of `toolchain/`
holding exactly the files, symlinks and empty directories its manifest records.
Publishing another toolchain means packing it, uploading the file as a release
asset, and updating the pin.

### Automated path: building the toolchain from source

```sh
./terrabuilder toolchain --from-source --replace --workdir ~/.cache/terrabuilder
```

This runs `scripts/toolchain/build_from_source.py`, which reads every source and
artifact pin from `toolchain.lock.json` and builds the Mesa libraries, mono-nx
SDK baseline (downloaded and SHA-256-verified, not rebuilt), mono-nx native
sources, FNA3D/FAudio/MojoShader, the release CoreLib/framework and native Mono
runtime, LLVM AOT cross compiler, framework AOT objects, Cecil, and dotnet SDK.
It copies the three build-52 facades from `toolchain-artifacts/facades` only
after checking the exact file set, byte lengths, executable modes and SHA-256s
against the lock. The hashes pin the original type-forwarding metadata; altered
or missing files stop the build. The generated `manifest.json` records this
provenance and hashes all toolchain files, then `toolchain verify` checks the
assembled result before printing (or, with `--replace`, performing) the swap
into `<workdir>/toolchain`. Each step is idempotent, so an interrupted run can
be re-invoked. Expect several hours on an 8-core/10 GB machine: heavy container
steps serialize through `~/.cache/terrabuilder/.heavy.lock`, the same lock used
by the CLI, to limit RAM use.

Parts 1–3 below are the same pipeline spelled out by hand component by component,
which is how `build_from_source.py` itself was derived and verified; read them for
the reasoning behind each step or to rebuild one component in isolation.

### First-release CLI path

For day-to-day use, run the repository-root CLI without a command: the guided
build described in the [README](../README.md#quick-start) (folders `Terraria/`,
`tModLoader/`, `Mods/` and `Output/` next to the script, then menus). Scripts use
the same stages through flags:

```sh
./terrabuilder doctor
./terrabuilder toolchain --bundle
./terrabuilder toolchain verify
./terrabuilder build --target vanilla \
  --game-dir "$HOME/GOG Games/Terraria1_4_5_8/game" \
  --profile release --workdir ~/.cache/terrabuilder
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

The CLI writes `Output/Terraria/sdcard/` (or `--out <root>/Terraria/sdcard/`), a
copy of the SD-card files: the NRO at `switch/Terraria.nro` plus `mono/config.ini`
and the ICU data file `mono/etc/icudt77l.dat`, which the NRO reads from the SD card
at startup. `Output/Terraria/receipt.json` includes the toolchain summary. Release
builds omit timing/profiler diagnostics and drop the unused OpenAL shim/link.
`--profile debug` or `--debug-diagnostics` restores phase/GPU/audio diagnostics;
`--profile profiler` adds the sampling profiler. Those build types write
`Output/Terraria-debug/` and `Output/Terraria-profiler/` with matching NRO names.
`build` prints every command it runs (lines starting with `+`), so a single stage
can be rerun by hand; the guided build writes the same lines to `Output/logs/`.

`toolchain --bundle` and `toolchain --from-source` are described above. Manual
Runtime/BCL/Mesa/LLVM build steps (parts 1–3) should still run under
`flock ~/.cache/terrabuilder/.heavy.lock`, the lock the CLI and
`build_from_source.py` both take for their heavy container steps.

### tModLoader CLI path (experimental)

The CLI also builds the Nintendo Switch tModLoader port:

```sh
./terrabuilder build --target tmodloader --mods none
./terrabuilder build --target tmodloader --mods FargowiltasSouls
./terrabuilder build --target tmodloader --mods ThoriumMod --terraria ~/Steam-copy/Terraria
```

It uses tModLoader 2026.7.3.0, the release the patch chain and NxFix were made for.
`--game-dir` may name a tModLoader folder with exactly that `tModLoader.dll`
(SHA-256 `d530e508...`); without it, the CLI downloads the GitHub release zip
(SHA-256 `6f51610f...`, 941 files) into `<workdir>/downloads` and unpacks it under
`<workdir>/tmodloader/2026.7.3.0`. `--mods` takes curated mods and the names of
`.tmod` packages in `Mods/` (a curated name is built from source); the guided build
also adds mods built from a GitHub commit (`Mods/git-mods.json`).

The game content is the `Content/` folder of your own Terraria 1.4.4.9 or 1.4.5.x:
`--terraria DIR` names a folder holding the install (searched up to three levels
deep, like the guided build's `Terraria/` folder, which is the default). GOG's
Linux `game` folder and a copy of Steam's `Terraria` folder (with `Content/` beside
`Terraria.exe`) both work; the version comes from `changelog.txt`. Vanilla builds
still need clean GOG 1.4.5.8 for Linux.

A tModLoader build reads only the toolchain, which must include the optional
`tmod-runtime` component (the Mono runtime, framework, CoreLib, AOT compiler and
precompiled CoreLib/framework objects the port was validated with; see
`scripts/tmod/runtime/README.md`; `doctor` reports it), the build images, this repo,
the tModLoader release, your Terraria `Content/`, and its work directory. Everything
derived from the game or tModLoader is made on your machine:

| Stage | Code | Output under `<workdir>` |
| --- | --- | --- |
| patch tModLoader | `scripts/tmod/base_dlls.py`: console patchers (`scripts/tmod/patchers`), NxFix, the `romfs:/` and shared-audio FNA patches, NxCrypto from `managed/nx_crypto` | `tmod/dlls-<key>/` |
| base | `scripts/tmod/base_romfs.py` (`flat_libs/`, the stock `romfs/`), `scripts/tmod/launcher/native_assets.py` (NACP, icon from the release's logo) | `tmod/base-<key>/` |
| launcher | `scripts/launcher/build_launcher.py` (3.1) | `launcher-src/<profile>/` |
| mod sources | catalog and GitHub mods, built in the image | `tmod/packages/<key>/` |
| prepare | `scripts/tmod/prepare_modset.py`: mod tuning, hook scan, IL and hook lowering | `tmod/<variant>/input/` |
| compile, link, package | `scripts/tmod/build_tmod_modset_nro.py` in `localhost/monobuild-llvm:local` | `tmod/<variant>/` |
| verify | `scripts/tmod/verify_aot_dependencies.py`: AOT dependency MVIDs | `tmod/<variant>/aot-mvid-check.json` |

The C# tools are built once per source digest into `<workdir>/tools/<name>-<digest>`
with the toolchain's .NET SDK and Mono.Cecil (`dotnet msbuild -restore -t:Build`).
The patch stage is keyed on the release files it reads, the tool sources and the
toolchain's `dotnet`/`cecil`; the base stage on those plus the hash of your
`Content/` tree, the `tmod-runtime` digest and its scripts; the variant on the base,
the build scripts and launcher sources, the mods, the build type and the toolchain.
A stage is written to `<name>.partial` and renamed when complete. For the pinned
release the patch stage reproduces the validated assemblies byte for byte (the
shared-audio FNA is checked against `TMOD_SHARED_AUDIO_FNA_SHA256`), and with GOG
1.4.4.9 Content the base RomFS equals the one earlier builds shipped except for the
current NxFix `tModLoader.dll`, the repo-built NxCrypto, and three asset names
(`Gore_240`, `Projectile_179`, `Projectile_618`) renamed to the case tModLoader
expects, as it does on desktop (`TryFixFileCasings`). The icon is regenerated from
the release's `Libraries/Native/tModLoader256.bmp`.

Every build starts from `scripts/tmod/base_modset.json` (the replacements, heap and
input settings and LLVM module list of the validated mod-set builds); `--mods none`
uses it without mods. `tModLoader.dll`, FNA, ReLogic, NxCrypto, NVorbis, MP3Sharp
and the mod assemblies are compiled for each variant; CoreLib and the framework's
System.Linq, System.Text.RegularExpressions and System.Collections.Concurrent reuse
the `tmod-runtime` objects when their input hashes match.

Outputs are under `Output/tModLoader/` (`-debug`, `-profiler` for those build
types):

- `sdcard/switch/tmodloader.nro`
- `sdcard/switch/tmodloader/Terraria/tModLoader/Mods/` with the packages and
  `enabled.json`, plus the same `mono/` runtime files as vanilla
- `receipt.json` with the NRO SHA-256, each mod's source (catalog entry, GitHub
  commit, or `.tmod` path and SHA-256), the Terraria `Content/` used (path,
  version, tree hash, renamed assets), the base and patch-stage records, the build
  manifest, and the AOT MVID verifier result

Install the whole `sdcard/` folder, overwriting existing `.tmod` files. Mono runs
AOT-only and checks each mod assembly's MVID against the AOT module linked into
the NRO. Workshop packages and earlier source builds can share the same file name
and version string but have different code, so they abort at mod load with
`Failed to load AOT module '<Mod>' ... doesn't match assembly`. The build-time
verifier checks the staged inputs, not the SD card.

A `.tmod` from `Mods/` is used as it is. The guided build checks it first: its
loader version must be a tModLoader 1.4.4 release no newer than 2026.7
(`loader_version_problem` in `scripts/tmod/tmod_file.py`, which `prepare_modset.py`
and `build_tmod_modset_nro.py` apply too), and `hook_scan` must report no
unsupported hook registration for it. Besides registrations it cannot resolve,
`hook_scan` reports `UNSUPPORTED_TARGET` for hooks on targets that
`offline_hooks` cannot chain (generic, vararg or ref-returning methods, generic
parameters, and runtime detours on value-type methods), so such a mod is refused
before its build starts. On_ hooks on value-type methods are lowered (HookGen's
delegates take `ref Self`), and so are hooks on nested types; Thorium needs both.
`hook_scan` also lists `missing_members`: references into tModLoader, FNA,
ReLogic, TerrariaHooks or another scanned mod that do not resolve against this
build's assemblies. They are not blockers; the method holding one throws when it
runs.

The guided build's `.tmod` check scans with the patch stage's `TerrariaHooks.dll`
and NxFix'd `tModLoader.dll`; the first check downloads tModLoader and runs that
stage if needed. Heavy AOT/link/container steps take
`~/.cache/terrabuilder/.heavy.lock`.

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
all source-built mod assemblies AOT/LLVM-compiled, and so is `tModLoader.dll`.
Its AOT object is verified against the staged assembly MVID. FNA's shared-audio
patch is required: an earlier candidate used stock FNA and crashed when the
XACT audio engine attempted to open a second SDL device.

Hardware status of the current Fargo's Souls build: the mods load, the main menu
and a world are reachable, and audio works. Known issues: in-world performance
is very slow (an earlier build measured roughly 15-19 fps); the Plus+Minus
FPS toggle did not work on an earlier build and has not been re-tested;
multiplayer and extended play are untested.

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
(`mscorlib.dll`, `System.IO.Packaging.dll`,
`System.Security.Permissions.dll`) are exact build-52 `runtime-romfs` files,
checked into `toolchain-artifacts/facades`. `toolchain.lock.json` pins each
file's bytes, mode and SHA-256. The builder copies and verifies these exact
files; it does not regenerate their metadata.

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
| `MONO_NX_SWKBD` | `build_tmod_modset_nro.py` (tModLoader builds) | on | Text boxes open the system keyboard; `nx_input.c` checks `PlayerInput.WritingText` when FNA's event drain ends (`--wrap=SDL_PollEvent`) and returns the text as `SDL_TEXTINPUT` events |
| `MONO_NX_GC_STATS` | Makefile / `link_nro.py --main-define` | 0 | `NX_GC` lines: GC counts/time, managed allocation, malloc use |
| `/mono/gc_params.txt` | SD card | absent | One line passed to SGen as `MONO_GC_PARAMS`. Without it, tModLoader builds set `max-heap-size` to the managed region minus 160 MB, `soft-heap-limit` 256 MB lower, and synchronous sweeping (`no-lazy-sweep,no-concurrent-sweep`), logged as `NX_GC params=...`; vanilla builds use SGen's defaults. The v18 CLI builds used a 64 MB gap and need this file with `max-heap-size=968m,soft-heap-limit=712m,no-lazy-sweep,no-concurrent-sweep` (runs 45-46) |
| `MONO_NX_EMBEDDED_BCL` | Makefile | 0 | Load CoreLib/framework from the NRO's RomFS |
| `MONO_NX_FATAL_DIAG` | Makefile | 0 | Fatal Mono errors write a crash report |
| `--llvm-module`, `--llvm-aot-extra` | `compile_terraria_aot.py` | CLI: Terraria, FNA | Modules compiled through LLVM, extra LLVM options |
