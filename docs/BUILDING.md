# Building terrabuilder-nx

This guide builds a Terraria NRO for Switch homebrew from **your own copy** of the
game (GOG Linux build 1.4.5.x). Nothing from the game is in this repository.

> **Status, read first.** The pipeline is reproducible **from verified baseline
> artifacts**, not yet from a clean checkout in one command. Every build since 52
> is derived from the previous accepted build, and the verifiers pin their inputs
> by SHA-256. Part 3 marks where a step depends on a retained artifact rather than
> on a script in this repo. Turning the whole chain into a single from-scratch
> script is open work.

## 0. What you need

- Linux (or WSL2) with Podman (Docker works; replace `podman` with `docker`).
- ~30 GB free disk (measured): .NET runtime checkout and build ~3.5 GB, LLVM
  cross-compiler build ~3.4 GB, NuGet packages ~3.9 GB (the Mono LLVM SDK package alone
  is ~1 GB), build images ~6 GB (shared layers), baselines ~5–10 GB, ~2–3 GB per
  build variant. Old variants can be deleted once their NRO is tested.
- A legal copy of Terraria for Linux (GOG), e.g. `~/FNA-Game/Terraria/game`.
- A Switch running Atmosphère and hbmenu, and an SD card.

The work directory is `~/.cache/terraria-switch-build` (override with
`TERRABUILDER_CACHE`). In every container command below it is mounted as
`/build`, and this repo is mounted read-only as `/work`.

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

## 3. Pipeline

### 3.1 Native dependencies and launcher (once)

FNA3D, FAudio and MojoShader as static libnx libraries, plus the dl-shim tables
generated from their exported symbols:

```sh
podman run --rm $MOUNTS localhost/monobuild:local -c '
  cd /work && scripts/build_native_deps.sh && scripts/gen_dl_shim.sh'
```

Output goes to `native/install/`. `build_native_deps.sh` clones FNA3D/FAudio at their
current HEAD. The shipped builds link the retained copies in
`recovery46/native-deps/install` (FNA3D `2c616bf8`), whose loaded code is verified
against build 42. Pin the same commits if you rebuild them.

The release launcher objects can now be rebuilt from source with the pinned
mono-nx checkout and retained native-deps install:

```sh
scripts/launcher/build_launcher.py --out $W/launcher-src/release
scripts/launcher/build_launcher.py --profiler --out $W/launcher-src/profiler
```

The default profile keeps the input/audio/FPS and swapchain wrappers but omits
`MONO_NX_PROFILER` and `nx_profiler.o`. `--profiler` builds the v88-style sampling
profiler object as well. For provenance checks against v88, add `--v88-sources`
to use the retained v81/v86 input/audio/swap sources; normal builds use the repo
`native/shared` sources (including current `nx_input.c` defaults). Link these
objects by passing `R58_OBJECT_OVERRIDES` for the 18 launcher objects and adding
the extra wrapper objects in `R58_EXTRA_LDFLAGS`.

`build_native.py` still replays build 42's recorded link line first
(`recovery46/link-arguments.txt`) and verifies build 52 allocated sections before
building a candidate.

### 3.2 Game payload (RomFS)

`scripts/pack_terraria_romfs.py` stages your GOG install into a RomFS tree,
excluding the Windows/Framework BCL DLLs and Linux `.so` files.

The game assembly is then modified by Mono.Cecil patchers under `scripts/patch_*`.
The shipped chain is: `patch_fna` (FNA input bridge + `NxInputDiag`),
`patch_time_logger` (build 41: TimeLogger history cost), `patch_aot_inlining`
(build 42: the "clean42" game), `patch_draw_gate` (build 50) and `patch_hint_prepass`
(build 52). Each `run.py` takes the exact previous output (hash-checked) and
writes a fresh directory.
**Retained artifact:** the accepted payload is `hint52/aot-final/runtime-romfs`;
build 58+ start from it. Tested but not adopted: `patch_tile_reuse` (46),
`patch_light_lookup` (47), `patch_property_diagnostics` (48), `patch_light_value`
(57), `patch_tile_stack_state` (54), `patch_tile_helpers`. Measurement-only, never
in a normal build: the `*_profile` sets.

### 3.3 Release CoreLib/framework (build 58+)

```sh
podman run --rm -v $W:/build -v $W/recovery46/sdk-pristine:/mono-nx:ro \
  --entrypoint /bin/bash localhost/monobuild:local /build/release58/build_release_managed.sh
```

The scripts in `scripts/release_bcl/` expect to run from `$W/release58/`; copy them
there (`cp scripts/release_bcl/* $W/release58/`).

### 3.4 Release native Mono runtime (build 65+)

```sh
podman run --rm -v $W:/build -v $W/recovery46/sdk-pristine:/mono-nx:ro \
  --entrypoint /bin/bash localhost/monobuild:local /build/runtime-release/build_runtime_release.sh
cp $W/runtime-source/artifacts/obj/mono/libnx.arm64.Release/out/lib/libmonosgen-2.0.a \
   $W/runtime-release/libmonosgen-2.0-release.a
```

(`build_runtime_release.sh` refuses to build without the allocator fix, which is
committed on the fork branch.) To check that struct layouts match the
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

The .NET build scripts in 3.3–3.5 use `$W/nuget-packages` by default (override
with `NUGET_PACKAGES`). Behind a TLS-intercepting proxy, set
`TERRABUILDER_CA_BUNDLE=/build/path/to/ca-bundle.crt`; otherwise the container's
system CA bundle is used.

### 3.6 AOT compile, link, package: the current working build

Build 64 (LLVM Terraria + FNA + CoreLib) is produced in three stages: an LLVM
Terraria build (60b), then FNA (62), then CoreLib (64) through LLVM:

```sh
POOLS=nimt-trampolines=8192,ngsharedvt-trampolines=4096,nunbox-arbitrary-trampolines=2048
# 60b: Release BCL, CoreLib inlining, larger trampoline pools, Terraria through LLVM
podman run --rm --entrypoint /bin/bash $MOUNTS \
  -e R58_VARIANT=v60b -e R58_CORELIB_INLINE=1 -e R58_LLVM_MODULES=Terraria \
  -e R58_CORELIB_AOT_EXTRA=$POOLS -e "R58_TITLE=Terraria 60b LLVM Terraria" \
  localhost/monobuild-llvm:local -c 'export PATH=/opt/devkitpro/devkitA64/bin:/opt/devkitpro/tools/bin:$PATH
    python3 /build/release58/build_aot.py && python3 /build/release58/build_native.py'
# 62: reuse 60b, recompile FNA through LLVM
podman run --rm --entrypoint /usr/bin/python3 $MOUNTS \
  -e FNA_LLVM_BASE=v60b -e FNA_LLVM_VARIANT=v62 -e FNA_LLVM_BASE_NRO_SHA=<60b NRO sha256> \
  -e "FNA_LLVM_TITLE=Terraria 62 LLVM Terraria+FNA" \
  localhost/monobuild-llvm:local /build/release58/build_fna_llvm.py
# 64: reuse 62, recompile CoreLib through LLVM
podman run --rm --entrypoint /usr/bin/python3 $MOUNTS \
  -e FNA_LLVM_MODULE=System.Private.CoreLib -e FNA_LLVM_BASE=v62 -e FNA_LLVM_VARIANT=v64 \
  -e FNA_LLVM_BASE_NRO_SHA=<62 NRO sha256> -e "FNA_LLVM_TITLE=Terraria 64 LLVM CoreLib" \
  localhost/monobuild-llvm:local /build/release58/build_fna_llvm.py
```

Link variants of a finished AOT set without recompiling (e.g. build 65/66 = 64's
objects + the Release runtime, + GC stats):

```sh
mkdir $W/release58/v66 && cp -al $W/release58/v64/aot-final $W/release58/v66/aot-final
podman run --rm --entrypoint /bin/bash $MOUNTS \
  -e R58_VARIANT=v66 -e R58_RUNTIME=release -e R58_MAIN_DEFINES=-DMONO_NX_GC_STATS=1 \
  -e "R58_TITLE=Terraria 66 GC params" localhost/monobuild-llvm:local -c \
  'export PATH=/opt/devkitpro/devkitA64/bin:/opt/devkitpro/tools/bin:$PATH; python3 /build/release58/build_native.py'
```

### 3.7 Verify

```sh
cd $W && R58_VARIANT=v66 R58_TITLE="Terraria 66 GC params" \
  uv run --with pyelftools python3 release58/verify_artifact.py
```

It checks every native method target and fallback, no RWX segments, read-only
method tables, and that the RomFS differs from build 52 only by the expected
framework swap. The NRO is `$W/release58/<variant>/native/candidate/mono_nx_fna.nro`.

## 4. Install on the Switch

Copy the NRO to `/switch/` and keep the SD runtime from the mono-nx release
(`/mono/lib_net9.0`, `/mono/framework_net9.0`, `/mono/etc`) plus
`terraria-mono/mono/config.ini` → `/mono/config.ini`. Saves go to
`/switch/terraria/`. The log is `/mono/log.txt`, and it **appends** across
launches, so rename or delete it between measured runs.

## 5. Build options

| Option | Where | Default | Effect |
| --- | --- | --- | --- |
| `MONO_NX_PHASE_TIMING` | Makefile | 0 | `NX_PHASE` frame timing and SDL/FNA3D call wrappers |
| `MONO_NX_GC_STATS` | Makefile / `R58_MAIN_DEFINES` | 0 | `NX_GC` lines: GC counts/time, managed allocation, malloc use |
| `/mono/gc_params.txt` | SD card | absent | One line passed to SGen as `MONO_GC_PARAMS` |
| `MONO_NX_EMBEDDED_BCL` | Makefile | 0 | Load CoreLib/framework from the NRO's RomFS |
| `MONO_NX_FATAL_DIAG` | Makefile | 0 | Fatal Mono errors write a crash report |
| `R58_LLVM_MODULES`, `R58_LLVM_AOT_EXTRA` | `build_aot.py` | none | Modules compiled through LLVM, extra LLVM options |
| `R58_CORELIB_AOT_EXTRA` | `build_aot.py` | none | CoreLib AOT options (trampoline pool sizes) |
| `R58_RUNTIME=release` | `build_native.py` | Debug-config runtime | Link the Release native runtime |

Every build's exact recipe and result is recorded in `docs/findings.md`.
