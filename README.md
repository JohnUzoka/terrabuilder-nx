# terrabuilder-nx

Builds a Nintendo Switch homebrew NRO of desktop Terraria (FNA, .NET) from **your own
copy of the game**, running on Mono via [mono-nx](https://github.com/JohnUzoka/mono-nx):
static AOT (optionally through LLVM), interpreter fallback, no JIT.

**This repository contains no game files.** Terraria binaries and content are supplied
by the user at build time and never committed; `.gitignore` blocks NROs, DLLs, EXEs,
XNB assets and hardware logs.

## Repositories

| Repo | Pinned revision | Role |
| --- | --- | --- |
| `JohnUzoka/dotnet_runtime` (fork of `exelix11/dotnet_runtime`) | branch `terrabuilder-nx` = `289cdaa5` + 4 commits | Mono runtime, CoreLib/framework for libnx. Adds the AOT switch-table allocator fix, the Release-build Sockets fix, absolute-address fake-mmap alignment and POSIX TLS destructor semantics. |
| `JohnUzoka/mono-nx` | branch `fna-support` @ `8be547c` | Launcher host, `native/shared` dl-shims (FNA3D/FAudio/SDL3/stub registrations). Mounted as `/mono-nx/native`. |
| this repo | | FNA launcher overlay (`native/`), managed helpers, Terraria IL patch tooling, AOT/LLVM/link/verify pipeline, docs. |

The native overlay (`native/interpreter`, `native/shared`) builds against mono-nx's
`native/shared` through a `shared_mono_nx` symlink; see `native/interpreter/Makefile`.

## Layout

- `native/` launcher overlay: `interpreter/` (main.c, Makefile, AOT method-table linker
  script), `shared/` (input latch, FNA3D/FAudio/SDL3 shims), `patches/` (runtime source
  patches, also committed on the dotnet_runtime fork), `tests/` (host regressions).
- `managed/` small helper assemblies (`NxInputDiag`, FNA test app).
- `scripts/` build tooling:
  - `compile_terraria_aot.py`: prepare, AOT-compile (optionally `--llvm-module`), verify
    MVID bindings and emit the module registration header.
  - `release_bcl/`: Release CoreLib/framework, native runtime, and LLVM cross-compiler
    builders plus AOT, link, package, and artifact-verification helpers.
  - `patch_*`: Mono.Cecil IL patch sets applied to the user's game assembly. The
    `*_profile` sets are measurement builds only (see below).
  - `analyze_*`: parsers for the profiler log formats.
- `docs/findings.md`: retained engineering log and technical decisions.
- `docs/testing.md`: historical test record and hardware test procedures.

## Profiling is opt-in

Default builds carry no profiling:

- **Frame timing** (`NX_PHASE` log lines, SDL_PollEvent / FNA3D_SwapBuffers wrappers):
  `make MONO_NX_PHASE_TIMING=1`. Without it the managed hook entry points stay (patched
  Terraria calls them every frame) but only drive the input latch.
- **Profiler IL patches** (`scripts/patch_*_profile`): separate measurement builds,
  never applied by default. `patch_time_logger` is a runtime timing hook, not a
  profiler.
- **GC/memory stats** (`NX_GC` lines): `MONO_NX_GC_STATS=1`. The optional SD file
  `/mono/gc_params.txt` configures SGen through `MONO_GC_PARAMS`.

Retained launcher objects used by the manual release pipeline include phase-timing
instrumentation. CLI builds rebuild the launcher from source, so the timing setting
in this checkout controls those outputs.

## Build environment

Podman images `localhost/monobuild:local` (devkitA64, .NET SDK) and
`localhost/monobuild-llvm:local` (adds clang-19 for the LLVM cross compiler). Host work
directory defaults to `~/.cache/terraria-switch-build` (override with
`TERRABUILDER_CACHE`), mounted as `/build`; this repo is mounted read-only as `/work`.
Engineering recipes and historical results are recorded in `docs/findings.md`.

## CLI quickstart (configured build machine)

The CLI is `./terrabuilder` and uses only Python's standard library on the host.
It builds from the user's own clean GOG Terraria install; game files,
patched assemblies, RomFS trees, icons, and game AOT objects stay local.

```sh
./terrabuilder doctor
./terrabuilder toolchain pack --workdir ~/.cache/terrabuilder
./terrabuilder build --target vanilla \
  --game-dir "$HOME/GOG Games/Terraria1_4_5_8/game" \
  --profile release --workdir ~/.cache/terrabuilder -y
```

These commands currently target the configured build machine and its retained
cross-compilation cache. `toolchain pack` records a local artifact manifest; it
does not yet create a bundle that a clean machine can install and use.
`toolchain --from-source` and `toolchain --bundle` fail explicitly because the
clean-checkout build and bundle-import paths are incomplete. Do not present the
current CLI as a self-service public release until those paths are completed.

Outputs are `~/.cache/terrabuilder/out/Terraria.nro` and a JSON receipt.
Release builds omit timing/profiler diagnostics and do not link OpenAL. Use
`--profile debug` (or `--debug-diagnostics`) for phase/GPU/audio diagnostics, and
`--profile profiler` for diagnostics plus a profiler-enabled NRO at
`Terraria-profiler.nro`.
Heavy container steps are serialized with
`~/.cache/terraria-switch-build/.heavy.lock`; the default workdir is
`~/.cache/terrabuilder` and can be overridden with `--workdir`.

`terrabuilder build` without flags first prompts for either
`Vanilla Terraria (GOG 1.4.5.x)` or `tModLoader (1.4.4) (experimental)`, then
prompts for the matching game directory. Vanilla input must be a clean GOG
1.4.5.x folder. tModLoader input must be a 1.4.4.x release folder containing
`tModLoader.dll`, `tModLoader.deps.json`, `tMLMod.targets`, and `Content/`.

tModLoader builds are experimental:

```sh
./terrabuilder build --target tmodloader \
  --game-dir ~/.cache/terraria-switch-build/tmod/release \
  --mods none --workdir ~/.cache/terrabuilder -y

./terrabuilder build --target tmodloader \
  --game-dir ~/.cache/terraria-switch-build/tmod/release \
  --mods FargowiltasSouls --workdir ~/.cache/terrabuilder -y
```

Outputs are written under `~/.cache/terrabuilder/out/tmodloader-*/`: an NRO,
`sdcard/` payload, and `receipt.json`. Copy the NRO to `sd:/switch/` and copy
the contents of `sdcard/` to the SD root, overwriting existing files. The NRO and
its `.tmod` files are a matched pair: the mods are AOT-compiled into the NRO, so
a Workshop download or a `.tmod` from an earlier build with the same name and
version still aborts at mod load with `Failed to load AOT module '<Mod>' ...
doesn't match assembly`. Do not update these mods from the in-game Mod Browser.

The curated mod catalog lives in `terrabuilder_pkg/curated_tmod_mods.json` and is
limited to tested open-source mods pinned to upstream commits: Luminance,
Fargo's Mutant, StructureHelper, and Fargo's Souls. Dependencies are auto-selected
when `--mods` names a dependent mod. No Steam Workshop packages or mod binaries are
redistributed by the CLI. The CLI builds host-only Linux FNA3D 26.07, reuses the
tML Linux SDL2/FAudio libraries for desktop `ModCompile`, and caches .NET 8 under
`~/.cache/terrabuilder`; those host libraries are not copied into the Switch
payload. Luminance and StructureHelper use recorded compatibility patches for
tML 1.4.4.9/Linux source builds. Fargo's Souls source builds AOT/LLVM the
source-built mod assemblies; `tModLoader.dll` is native-AOT compiled but not
LLVM-optimized in the current CLI path. FNA must be the shared-audio build:
stock FNA crashes during XACT audio initialization because it opens a second
audio device. The build requires native AOT and checks staged module identities
before linking.

The Fargo's Souls build has been tested on a Switch: the mods load, the main
menu and a world are reachable, and audio works. Known tModLoader issues:
in-world performance is very slow (an earlier build measured about 15-19 fps);
the D-pad moves through the main menu and the Start menu but not the inventory;
the Plus+Minus FPS toggle did not work on an earlier build and has not been
re-tested; multiplayer and extended play are untested. The mod list is
intentionally limited to tested open-source mods. See the
[architecture](docs/architecture.md), [porting guide](docs/porting-fna-games.md),
and [build guide](docs/BUILDING.md).

## Status

The vanilla release candidate has passed hardware checks for audio, FPS toggle,
multiplayer join, and frame rate at default clocks. The Fargo's Souls
tModLoader build (native AOT for `tModLoader.dll`, LLVM AOT for FNA and the
curated mods) loads its mods, reaches a world, and plays audio on hardware, but
it is very slow and has the input gaps listed above. tModLoader remains
experimental.

The retained-cache build path is available for the maintainer. A one-command,
clean-checkout from-source pipeline and clean-machine bundle import are still
being completed; `docs/BUILDING.md` describes the retained-cache engineering
recipes.
