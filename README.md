# terrabuilder-nx

Builds a Nintendo Switch homebrew NRO of desktop Terraria (FNA, .NET) from **your own
copy of the game**, running on Mono via [mono-nx](https://github.com/JohnUzoka/mono-nx):
static AOT (optionally through LLVM), interpreter fallback, no JIT.

**This repository contains no game files.** Terraria binaries and content are supplied
by the user at build time and never committed; `.gitignore` blocks NROs, DLLs, EXEs,
XNB assets and hardware logs.

## Quick start

With Podman or Docker installed (see [Build environment](#build-environment)), the
whole build is one command:

```sh
./terrabuilder
```

It runs like a game installer. The first run creates these folders next to the
script:

| Folder | Put in it |
| --- | --- |
| `Terraria/` | Your Terraria. Vanilla builds need GOG Terraria 1.4.5.8 for Linux (its `game` folder or the whole GOG install); Steam's Windows build of Terraria (XNA) cannot be used for them. tModLoader builds use only its `Content/` folder: Terraria 1.4.4.9 or 1.4.5.x, from GOG or a copy of Steam's `Terraria` folder. |
| `tModLoader/` | Optional: tModLoader 2026.7.3.0. Otherwise the build downloads that release from GitHub and checks its SHA-256. Other releases, such as Steam's current 2026.8.3.0, are not used. |
| `Mods/` | `.tmod` files to build in: Steam Workshop downloads or your own builds. Copied or linked Workshop folders work; the newest usable package of each mod is picked. |
| `Output/` | Finished builds and build logs. |

It checks the machine (container engine, build image, toolchain, disk space). On a
new machine it offers to set up the rest: it downloads the prebuilt toolchain
(274 MiB) and builds the container image (about 5 GB). Then it asks three things:

1. **What to build:** Terraria (vanilla) or tModLoader with mods (experimental).
   Each choice shows what was found in the folders; `r` checks them again.
2. **Mods** (tModLoader only): checkboxes for the curated open-source mods (built
   from pinned source), mods added from a GitHub repository, and the `.tmod`
   files in `Mods/`. Mods that others need are selected and locked automatically.
3. **Summary:** the build type (Release, Debug or Profiler) and where the result
   goes.

The build shows one line per stage and writes its full output to `Output/logs/`.
The first tModLoader build takes about an hour; later builds reuse unchanged
stages. When it finishes, copy everything inside `Output/Terraria/sdcard` or
`Output/tModLoader/sdcard` to the root of the SD card: the NRO lands in
`sd:/switch/`, the runtime files in `sd:/mono/`, and a tModLoader build's mods in
`sd:/switch/tmodloader/`. Debug and Profiler builds go to `Output/<target>-debug`
and `-profiler` with matching NRO names.

**`.tmod` files.** Each package in `Mods/` is checked before it can be selected:

- It must be built for tModLoader 1.4.4 (2023.3.85 or later) and for no newer
  release than this port's 2026.7, the rule tModLoader itself applies when it
  loads mods.
- The hook scanner must be able to convert every hook the mod registers: the
  Switch has no JIT, so hooks are compiled in ahead of time. Calamity 2.2.4, for
  example, is refused: 3 of its hook registrations cannot be resolved without
  running the mod.
- Members it uses that tModLoader 2026.7.3.0 no longer has are listed; code that
  uses them fails only when it runs.

Packages are used as they are and only copied into your own build's SD payload.

**Mods from GitHub.** "Add a mod from GitHub" clones a repository, finds the mod
(`build.txt` and its `.csproj`), pins the current commit and records it, with the
license it found, in `Mods/git-mods.json`. It is built from source like the
curated mods, without recorded patches; its hooks are checked during the build.

Scripts can use `./terrabuilder build`, which takes the same choices as flags
(curated mods only):

```sh
./terrabuilder build --target vanilla --game-dir Terraria/game
./terrabuilder build --target tmodloader --mods FargowiltasSouls --profile profiler
```

`./terrabuilder doctor` lists the machine checks, `./terrabuilder toolchain --bundle`
does the guided build's set-up without menus, and `./terrabuilder toolchain verify`
rehashes the toolchain.

## Repositories

| Repo | Pinned revision | Role |
| --- | --- | --- |
| `JohnUzoka/dotnet_runtime` (fork of `exelix11/dotnet_runtime`) | branch `terrabuilder-nx` = `289cdaa5` + 4 commits | Mono runtime, CoreLib/framework for libnx. Adds the AOT switch-table allocator fix, the Release-build Sockets fix, absolute-address fake-mmap alignment and POSIX TLS destructor semantics. |
| `JohnUzoka/mono-nx` | branch `fna-support` @ `8be547c` | Launcher host, `native/shared` dl-shims (FNA3D/FAudio/SDL3/stub registrations). Mounted as `/mono-nx/native`. |
| this repo | | FNA launcher overlay (`native/`), managed helpers, Terraria IL patch tooling, AOT/LLVM/link/verify pipeline, docs. |

The native overlay (`native/interpreter`, `native/shared`) builds against mono-nx's
`native/shared` through a `shared_mono_nx` symlink; see `native/interpreter/Makefile`.

## Layout

- `terrabuilder`, `terrabuilder_pkg/`: the CLI (`installer.py` and `ui.py` are the
  guided build), the toolchain manifest checks, the curated tModLoader mod
  catalog, and recorded mod compatibility patches.
- `native/` launcher overlay: `interpreter/` (main.c, Makefile, AOT method-table linker
  script), `shared/` (input latch, FNA3D/FAudio/SDL3 shims), `patches/` (runtime source
  patches, also committed on the dotnet_runtime fork), `tests/` (host regressions).
- `managed/` small helper assemblies (`NxInputDiag`, `NxCrypto`).
- `scripts/` build tooling:
  - `patch_vanilla/`, `patch_fna/`, `fna_suppress_gc/`, `fna_shared_audio/`: Mono.Cecil
    IL patchers applied to the user's local game and FNA assemblies.
  - `compile_terraria_aot.py`, `prepare_aot/`: prepare, AOT-compile (optionally
    `--llvm-module`), verify MVID bindings and emit the module registration header.
  - `native/`: `link_nro.py` compiles `main.o` against the AOT registration header,
    links the launcher, AOT objects and toolchain libraries, and packages the NRO.
  - `release_bcl/`: Release CoreLib/framework, native runtime, and LLVM cross-compiler
    builders plus artifact-verification helpers.
  - `launcher/`, `mesa/`: launcher objects and the Switch Mesa build.
  - `tmod/`: the tModLoader port (mod set preparation and tuning, offline hooks, IL
    lowering, AOT, and link).
- `docs/`: [architecture](docs/architecture.md), [build guide](docs/BUILDING.md),
  [testing on a Switch](docs/testing.md), and a
  [guide to porting other FNA games](docs/porting-fna-games.md).

## Profiling is opt-in

Release builds carry no profiling. `--profile debug` adds phase/GPU/audio diagnostics
and `--profile profiler` also emits a sampling-profiler NRO. The underlying launcher
build options are:

- **Frame timing** (`NX_PHASE` log lines, SDL_PollEvent / FNA3D_SwapBuffers wrappers):
  `make MONO_NX_PHASE_TIMING=1`. Without it the managed hook entry points stay (patched
  Terraria calls them every frame) but only drive the input latch.
- **GC/memory stats** (`NX_GC` lines): `MONO_NX_GC_STATS=1`. The optional SD file
  `/mono/gc_params.txt` configures SGen through `MONO_GC_PARAMS`.

## Build environment

One Podman or Docker image, built from `scripts/toolchain/Containerfile` (devkitA64,
.NET SDK, clang-19) and tagged `localhost/monobuild:local` and
`localhost/monobuild-llvm:local`; `toolchain --bundle` and `toolchain --from-source`
build it. Builds run in a work directory (default `~/.cache/terrabuilder`; override
with `--workdir` or `TERRABUILDER_WORKDIR`) mounted as `/build`. This repo is
mounted read-only as `/work`, and the toolchain in `<workdir>/toolchain` read-only
as `/toolchain`.
Engineering recipes are in [docs/BUILDING.md](docs/BUILDING.md).

## Build details

`./terrabuilder` uses only Python's standard library on the host. It builds from
the user's own clean GOG Terraria install; game files, patched assemblies, RomFS
trees, icons, and game AOT objects stay local.

Vanilla builds consume only the toolchain in `<workdir>/toolchain`: the Mono
runtime, CoreLib/framework and their prebuilt AOT objects, the LLVM AOT compiler,
Mesa, FNA3D/FAudio/MojoShader, the mono-nx SDK, Mono.Cecil, the .NET SDK, and
their license notices. Its `manifest.json` records every file's SHA-256;
`toolchain` prints a summary and `toolchain verify` rehashes every file.
`toolchain --bundle` installs the prebuilt toolchain, a release asset of this
repository: the download must match the SHA-256 pinned in `TOOLCHAIN_BUNDLE`
(`terrabuilder_pkg/cli.py`), and every unpacked file is rehashed against the
manifest before the toolchain is used. `toolchain pack` writes that asset from a
verified toolchain. The `toolchain --from-source` path needs no maintainer cache:
it uses the public pins plus the exact build-52 type-forwarding facades checked
into `toolchain-artifacts/facades` and locked by SHA-256; the full clean-machine
from-source build has not yet been run end to end.

Each build writes `Output/<target>/sdcard/`, a copy of the SD-card files (the NRO
in `switch/`, the runtime config and ICU data the NRO reads from `sd:/mono/`),
and `Output/<target>/receipt.json`. `build --out` changes the output root.
[Testing on a Switch](docs/testing.md) covers installation, checks, and logs.
Release builds omit timing/profiler diagnostics and do not link OpenAL. The Debug
build type (`--profile debug`, or `--debug-diagnostics`) adds phase/GPU/audio
diagnostics, and Profiler (`--profile profiler`) adds a sampling profiler too;
their NROs are `Terraria-debug.nro`, `Terraria-profiler.nro` and the
`tmodloader-*` equivalents.
Heavy container steps are serialized with `~/.cache/terrabuilder/.heavy.lock`,
and concurrent vanilla builds in one workdir wait for each other. Each stage reruns
only when its recorded inputs (game files, repo sources, toolchain component
digests, container image IDs) change.

tModLoader builds are experimental. They use tModLoader 2026.7.3.0: a
`tModLoader/` folder (or `build --game-dir`) with exactly that release, else the
GitHub release zip, pinned by SHA-256 and unpacked under `<workdir>/tmodloader`.
The game content is the `Content/` folder of your Terraria 1.4.4.9 or 1.4.5.x in
`Terraria/` (or `build --terraria DIR`); the build records its version and tree
hash. Everything else derived from tModLoader is made on your machine from the
release: the console patches, NxFix, the patched FNA, the stock RomFS and the
icon. The runtime the port was validated with comes from the toolchain's optional
`tmod-runtime` component; without it the guided build offers only vanilla. Stages
and keys are in [docs/BUILDING.md](docs/BUILDING.md#tmodloader-cli-path-experimental).

A tModLoader build's `sdcard/` also holds the mods
(`switch/tmodloader/Terraria/tModLoader/Mods/`). The NRO and those `.tmod` files
are a matched pair: the mods are AOT-compiled into the NRO, so a Workshop download
or a `.tmod` from an earlier build with the same name and version still aborts at
mod load with `Failed to load AOT module '<Mod>' ... doesn't match assembly`. Do
not update these mods from the in-game Mod Browser.

The curated mod catalog lives in `terrabuilder_pkg/curated_tmod_mods.json`: mods
pinned to upstream commits and built from source on your machine. Luminance,
Fargo's Mutant, StructureHelper and Fargo's Souls are tested on hardware. Recipe
Browser, Boss Checklist, Where's My Items, Cheat Sheet, HERO's Mod, Magic Storage
(with SerousCommonLib) and AlchemistNPC Lite are experimental options. Each entry
records its license; several publish source without a license, and Recipe
Browser's author states it is not open source. Fargo's Music is not offered: its
repository has no license for its code or recordings, and the pinned Fargo's
Souls asks for a track (`Assets/Music/LieflightNoCum`) that its source lacks.
When a text box takes input (a mod search box, chat, a sign), the Switch keyboard
opens; the entered text is typed into the box when it closes. The filter boxes
that tModLoader's mod list, mod browser and achievements screens keep active do
not open it, and USB keyboards still type no text. Panels that read the raw
mouse need touch. tModLoader
leaves mod hotkeys unassigned; set them in Settings > Controls, on the Keyboard
or Gamepad tab. Recipe Browser, Where's My Items and Boss Checklist's checklist
open only by hotkey. The Boss Log also opens from a book button in the
inventory, and HERO's Mod's toolbar and Cheat Sheet's hotbar from tabs at the
bottom of the screen; at 1280x720 those bars cover the inventory's Settings
button and the Boss Log button until collapsed. Dependencies are selected
automatically, in the guided build and when `--mods` names a dependent mod. The
CLI redistributes no Steam Workshop packages or mod binaries: `.tmod` files from
`Mods/` go only into the user's own build. The CLI builds host-only Linux FNA3D
26.07, reuses the tML Linux SDL2/FAudio libraries for desktop `ModCompile`, and
caches .NET 8 under
`~/.cache/terrabuilder`; those host libraries are not copied into the Switch
payload. Luminance, StructureHelper, Magic Storage, SerousCommonLib and
AlchemistNPC Lite use recorded compatibility patches for tML 1.4.4.9/Linux
source builds and the AOT runtime. tModLoader builds LLVM-AOT compile
`tModLoader.dll`, FNA, and every mod assembly; the `tModLoader.dll`
LLVM step takes about 26 minutes and needs the build machine's memory to itself.
FNA must be the shared-audio build:
stock FNA crashes during XACT audio initialization because it opens a second
audio device. The build requires native AOT and checks staged module identities
before linking.

The Fargo's Souls build has been tested on a Switch: the mods load, the main
menu and a world are reachable, and Plus+Minus toggles the FPS counter. Audio
worked in early builds but every build from run 39 to v16 was silent (NxFix was
fed its own earlier output); v17 restores it, not yet confirmed on hardware.
Known tModLoader issues: in-world v16 ran at a median of about 55 fps (run 42).
With all 11 mods, v17 averaged 33 fps over a 40-minute session (run 44), and
each autosave paused the game for several seconds. Run each NRO only with the
mods it was built with: turning other mods on in the Mods menu fails to load
them. Multiplayer is untested. See the
[architecture](docs/architecture.md), [porting guide](docs/porting-fna-games.md),
[build guide](docs/BUILDING.md), and [testing guide](docs/testing.md).

## Status

The vanilla release candidate has passed hardware checks for audio, FPS toggle,
multiplayer join, and frame rate at default clocks. The Fargo's Souls
tModLoader build loads its mods and reaches a world on hardware; with
LLVM-compiled `tModLoader.dll`, FNA and mods, v16 ran at a median of about
55 fps in-world (run 42), with the occasional save and content-load hitches
listed above. tModLoader remains experimental.

Steam Workshop packages, so far only in Ryujinx: Thorium 1.7.2.8 on its own loads
(tModLoader reports 550 MB for it), reaches a world and keeps running there with
enemies around. With Fargo's Souls added, mod loading stops: after about 20,000
GPU mappings, the emulated kernel answers memory and IPC calls with
`OutOfResource` and a thread pool thread cannot start, so large content mods may
not combine. Calamity 2.2.4 is refused by the hook check, and Calamity
Mod Music 2.1 passes it.

tModLoader builds no longer need the maintainer's engineering cache: a Thorium
build made without it, with Steam's Terraria 1.4.5.8 `Content/`, reached a world
in Ryujinx and kept running there. Every hardware run so far used GOG 1.4.4.9
content and the cache's NxCrypto build, so 1.4.5.8 content and NxCrypto built
from this repository still need a hardware check.

Fargo's Souls 2.0 is announced for 2026 and not yet released: the Workshop's
latest is 1.7.3.9, and public `master` is still the pinned 1.7.3.7 (`dev` is not
2.0). On release, pin its source commit and versioned dependencies, including
Best of Both Worlds, which 2.0 makes a dependency and which publishes no license.
Moving the port to another tModLoader release means regenerating its inputs:
packages built for a release newer than 2026.7 are refused (older 1.4.4
packages load). For tModLoader 2026.8.3.0, Steam's current release, the console
patchers (`scripts/tmod/patchers`) and all NxFix patches apply unchanged and its
public API only adds members, so that update is new version pins (`TMOD_RELEASE`
and the shared-audio FNA hash in `terrabuilder_pkg/cli.py`, `PORT_LOADER_VERSION`
in `scripts/tmod/tmod_file.py`; the patch and base stages then rebuild from the
new release), then hardware checks. The recorded Luminance and
StructureHelper patches and `mod_tuning`'s banner patch are pinned to current
versions and may need rebasing.

Vanilla builds need only the toolchain and the build image, which a new machine
gets through `toolchain --bundle` (or the guided build's set-up). tModLoader
builds also need the toolchain's `tmod-runtime` component (`doctor` reports it),
which the bundle includes. The bundled toolchain is the maintainer's from-source
build of 2026-10-03 plus `tmod-runtime`, the open-source runtime parts of the
maintainer's tModLoader builds (`scripts/tmod/runtime/README.md` records where
each file came from). A clean-machine from-source toolchain build has not been run
end to end, and it does not produce `tmod-runtime`; `docs/BUILDING.md` describes
the engineering recipes for the individual components.
