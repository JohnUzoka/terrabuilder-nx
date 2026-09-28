# FNA on Switch via mono-nx — Findings & Nuances

**Latest (2026-09-25):** LLVM codegen is the biggest win so far.
[60b (LLVM Terraria): **+37% Draw/s over 58e**](#alternating-same-spot-ab-58e-vs-60b-2026-09-25);
[62 (60b + LLVM FNA): **~+6% over 60b**](#alternating-same-spot-ab-60b-vs-62-2026-09-25),
~37–41 Draw/s stationary with frameskip off at one spot. [63 (Cortex-A57
tuning)](#alternating-same-spot-ab-62-vs-63-2026-09-25) gave no measurable gain; keep 62.
[64 (62 + LLVM CoreLib)](#tie-breaker-pair-62-vs-64-2026-09-25) is the **working build**:
loads ~23% faster, gameplay equal or up to ~9% faster (not isolatable).
[65 (64 + Release native runtime)](#build65-release-native-mono-runtime-2026-09-27) is the
next hardware test. 52 remains the formally adopted baseline until heavier scenes are checked.

Earlier context: [build58 Release CoreLib/framework A/B](#build58-release-corelibframework-ab-2026-09-24) (boots; short hardware A/B, not adoption-grade), the [L4T matched-clock comparison and runtime-quality findings](#l4t-matched-clock-comparison-and-runtime-quality-findings-2026-09-24) (goal: 30FPS handheld at stock clocks), the [build57 lighting value-local comparison](#build57-lighting-value-local-comparison), [solid-tile experiments and rejected allocation proposal](#solid-tile-follow-up-slice-address-trial-and-allocation-gate), [solid-tile priority and30FPS budget](#solid-tile-priority-and-realistic30fps-budget), [build56 hardware results](#build56-hardware-results-nighttime-world-item-growth), and the [compatibility discussion gate](#compatibility-tradeoffs-and-retained-reference-safety).
53 hardware measurement passed:80 valid complete packets,15892 reconciled Draw frames,2389 eligible gameplay frames and no deaths/observer failures. Longest stationary section47–67 has2078 frames: solid loop14.566ms, nonsolid1.687ms, TileBatch.End1.117ms inclusive, uploads0.149ms and indexed submissions0.410ms per frame. The scopes overlap; no GPU-only or FPS-gain claim.52 remains the performance baseline.
Current priority: **the user explicitly selected consistent solid-tile rendering as the primary optimization target**.53 measured a stable14.566ms solid loop per Draw. Focus returns to TileDrawing.Draw/DrawSingleTile and real repeated preparation/lighting/traversal work, not another unproven scratch/call-overhead tweak.56's nighttime world-item/mob-related investigation remains a **secondary** known cost, not a ruled-out cause or the lead workstream.52 remains baseline;54 parked;55/56 measurement only.
**Checkpoint:**57 is a verified, published, instrument-free A/B candidate on52. It changes only Lighting.GetColor(int,int) value handling, retaining all four calls/order, float/color semantics, signatures, fields and construction behavior. Native stack144→96 bytes and code488→428 bytes are real codegen reductions, **not a measured FPS gain**; host timings were inconclusive.52 remains baseline and57 is not adopted. Compare original52 (log57A.txt) with57 (log57B.txt), preferably a repeated52 control. Deferred colorSlices allocation is declined; the small slice-ref trial remains unpublished.
**Mandatory discussion gate:** bring up potential protocol, simulation, gameplay, multiplayer-visible/save-state, vanilla object-lifetime or mod-compatibility impacts before applying a tradeoff. Compatibility is not an absolute veto: the user may accept breakage for significant measured gains or modest adaptations, but not a large mod/loader overhaul for a small gain. Safe measurement may continue.
Earlier b18/b17 mapping conclusions are superseded: **Plus is b10; Minus is b11**.

Date: 2026-09-16; updated 2026-09-24
Status: Terraria has reached menus and gameplay on hardware. Current work addresses severe Horizon slowdown and controller input; new builds require another hardware run.

## Goal

Port desktop Terraria (FNA/XNA, .NET managed) to Nintendo Switch homebrew
using mono-nx as the .NET runtime, borrowing the approach from Rotating Art
Launcher (RAL) — an Android app that runs .NET desktop games on ARM64.

## What was proven

A minimal FNA program runs on Switch: Mono interpreter → FNA → FNA3D (OpenGL)
→ SDL2 → switch-mesa → Tegra GPU. A procedurally generated texture renders
and moves with controller input. The managed-to-hardware graphics path works
end to end.

## What was NOT modified

These were built from upstream source with build flags only:

- **FNA3D** — built with `-DBUILD_SDL3=OFF -DBUILD_SHARED_LIBS=OFF`. No source changes.
- **FAudio** — built with `-DBUILD_SDL3=OFF -DBUILD_SHARED_LIBS=OFF`. No source changes.
- **MojoShader** — built as a submodule of FNA3D. No source changes.
- **FNA** (C# framework) — built from `FNA.Core.csproj` targeting net8.0. No source changes.
- **SDL2** — devkitPro portlib, prebuilt. Not touched.
- **switch-mesa, EGL, glad, glapi, drm_nouveau, OpenAL** — devkitPro portlibs. Not touched.

## What WAS modified

- **mono-nx `native/shared/dl_shim.c`** — patched in-place to register FNA3D
  and FAudio as known libraries. Adds `REGISTER_LIBRARY`, `CHECK_LIB_NAME`,
  and `CHECK_LIB_SYMBOL` entries. **This should be a fork, not an in-place patch.**
- **mono-nx `native/interpreter/source/main.c`** — copied and modified to set
  `FNA_PLATFORM_BACKEND=SDL2` and `FNA3D_FORCE_DRIVER=OpenGL` env vars before
  launching managed code. This is our own file in our own directory, not a
  modification to the original.

## Architecture

```
Terraria managed assemblies (.dll)
  ↓ loaded by
mono-nx interpreter (Mono runtime on Horizon via libnx)
  ↓ calls into
FNA.dll (C# XNA reimplementation, SDL2 backend selected at runtime)
  ↓ DllImport resolved by
dl_shim (static P/Invoke resolver — no dlopen on Switch)
  ↓ calls into
FNA3D (OpenGL driver) → MojoShader (shader translation)
FAudio (audio engine, SDL2 backend)
SDL2 (window, input, context) — devkitPro portlib
  ↓ renders via
switch-mesa OpenGL (GL 4.3 core on Tegra X1 via nouveau/drm)
  ↓ presents through
EGL → Switch display
```

## Key technical decisions

### SDL2 instead of SDL3

Terraria 1.4.5+ uses SDL3. SDL3 has no Switch homebrew port — it's NDA-gated
for licensed developers only. The [backport request][sdl3-issue] has been open
since 2022 with no resolution.

FNA ships both SDL2 and SDL3 backends in the same DLL, selected at runtime:

```csharp
bool useSDL2 = Environment.GetEnvironmentVariable("FNA_PLATFORM_BACKEND") == "SDL2";
```

Setting `FNA_PLATFORM_BACKEND=SDL2` in `main.c` before `mono_jit_exec` forces
FNA to use `SDL2_FNAPlatform.cs` and `SDL2#` bindings, which resolve through
the dl_shim to devkitPro's SDL2. The managed Terraria code calls FNA, not SDL
directly — it does not know which SDL version is underneath.

FNA3D and FAudio both have `-DBUILD_SDL3=OFF` which makes them link against
SDL2 headers and use SDL2 code paths.

Terraria also directly calls `SDL3.SDL_GetVersion()` and
`SDL3.SDL_GetRevision()` in `Program.LogFNANativeLibVersions()` before the
FNA backend is selected. The fork now registers an SDL3 compatibility library
(`b2161a9`) and the NRO includes ABI-correct wrappers for those two calls.
This is a narrow startup compatibility shim, not a general SDL3 port; all
actual FNA window/input work still uses SDL2.

[sdl3-issue]: https://github.com/libsdl-org/SDL/issues/6822

### dl_shim instead of dlopen

Switch/Horizon has no `dlopen`. Mono's P/Invoke normally loads native
libraries dynamically. mono-nx solves this with a `dl_shim` — a fallback
handler registered via `mono_dl_fallback_register` that maps library names
to statically-linked function pointers.

Each native library needs:
1. `REGISTER_LIBRARY(name, "string_id", handle_id)` at file scope
2. `CHECK_LIB_NAME(name, lib)` in `dlshim_loadLibrary`
3. `CHECK_LIB_SYMBOL(lib)` in `dlshim_getSymbol`
4. A `getsym_XXX.c` file with `SYM_RESOLVE` entries for every exported function

The shim files (`dl_shim_FNA3D.c`, `dl_shim_FAudio.c`) are generated from
compiled library symbols using `nm` — not from header parsing. Header parsing
captures type names and enums that would cause link errors.

### OpenGL via switch-mesa

FNA3D's OpenGL driver loads GL functions through `SDL_GL_GetProcAddress`, so
it doesn't need `libGL` at link time. switch-mesa provides GL 4.3 core profile
via the nouveau/drm driver on Tegra X1. The link line uses:

```
-lglad -lEGL -lglapi -ldrm_nouveau
```

No `libGL.a` exists — GL functions are resolved at runtime through EGL/glad.

### Memory: 50/50 heap split

mono-nx partitions the application heap roughly 50/50 between newlib malloc
(native allocations, Mesa/OpenGL, some Mono internals) and Mono's fake mmap
allocator (GC-managed pages). This is a simple implementation choice in
`native/shared/heap.c` — not a hardware requirement. It can be tuned by
changing `size / 2` to a configurable ratio.

The split causes Mesa to fail to initialize in applet mode (~120 MB for
libnx). Full application mode (title takeover) is required.

### Interpreter, not JIT

mono-nx runs in `MONO_AOT_MODE_INTERP_ONLY` mode. Horizon enforces W^X
memory — no RWX pages. mono-nx solved the P/Invoke trampoline problem using
libnx's JIT API (dual rw_addr/rx_addr views), but there is no full JIT for
game logic. This is the primary performance risk for Terraria.

Mono AOT is also available — it compiles IL to native code ahead of time.
It's faster but restricts reflection and dynamic code generation. tModLoader's
runtime assembly loading likely won't work under AOT.

### One-file RomFS packaging

FAT32 handles one large NRO much better than 15,997 separate Content files.
The resulting development NRO is about 751–772 MB depending on the managed
facades included, and contains the staged RomFS. FAT32 sees one file instead
of thousands. The output intentionally keeps configuration, runtime files,
and logs outside the NRO so they remain editable and debuggable:

- `SD:/mono/config.ini` — external launch/runtime settings
- `SD:/mono/log.txt` — external Mono/native log
- `SD:/mono/lib_net9.0/` and `framework_net9.0/` — shared runtime DLLs
- `SD:/mono/etc/icudt77l.dat` — ICU data

Important implementation detail: load `/mono/config.ini`, initialize ICU and
Mono's external BCL first, then call `romfsInit()` and `chdir("romfs:/")`.
Mounting RomFS before `application_initialize()` makes `/mono/config.ini`
resolve against the embedded filesystem, producing `Can't load app config`
before logging is redirected. The corrected launcher preserves external
configuration and logs while using RomFS only for Terraria's files.

First hardware run reached Terraria assembly loading but failed on its
`.NETFramework,Version=v4.0` reference to `mscorlib, Version=4.0.0.0`.
The corrected packer now embeds mono-nx's small runtime facade assemblies
(`mscorlib.dll`, `System.dll`, and related facades) at the RomFS root. It
does not embed the GOG framework assemblies; those conflict with
`System.Private.CoreLib`.

`scripts/pack_terraria_romfs.py` defaults to
`~/FNA-Game/Terraria/game`, stages to the interpreter's default `romfs/`
root, removes `:Zone.Identifier` sidecars, excludes the GOG BCL DLLs and
Linux `.so` files, and emits an external `mono/config.ini`. Optional legacy
assemblies can be explicitly requested with `--include-legacy-dll`.

This is BYO-data packaging: the tool must never ship Terraria assets; each
user runs it against an installation they obtained lawfully.

### Terraria hardware iteration log

- **Baseline FNA test:** managed FNA rendering, SDL2 input, FNA3D OpenGL,
  and switch-mesa all worked on hardware.
- **RomFS config failure:** mounting RomFS before loading `/mono/config.ini`
  produced `Can't load app config`; initialization order was corrected.
- **.NET Framework binding:** embedding mono-nx's `mscorlib` and full facade
  closure allowed Terraria, ReLogic, Newtonsoft.Json, and FNA assemblies to
  load under the net9 Mono runtime.
- **SDL3 startup diagnostics:** Terraria's direct `SDL_GetVersion` and
  `SDL_GetRevision` calls now resolve through ABI-correct SDL2 wrappers.
- **Current compatibility fix:** the next NRO embeds GOG
  `System.Windows.Forms.dll`/`System.Drawing.dll` and includes `XNA_*` FAudio
  symbols. The previous log reached FNA3D device creation and printed the
  Switch OpenGL renderer before failing on missing WinForms.
- **Writable save path:** ReLogic's Linux path service uses `XDG_DATA_HOME`
  or `HOME`; Horizon provides neither. The launcher now sets a writable
  `/switch/terraria` data root and switches the cwd back to `sdmc:/` after
  loading the RomFS assembly, keeping game assets read-only and preferences
  writable.
- **Native FAudio crash:** Atmosphère reported `2168-0002` Data Abort at
  `FAudio_INTERNAL_InitSIMDFunctions → FAudio_OPERATIONSET_CommitAll →
  FACTAudioEngine_ShutDown`, immediately after SDL reported `OpenAudioDevice
  failed: Audio device already open`. The current diagnostic NRO forces
  `SDL_AUDIODRIVER=dummy` to validate the graphics/title path without
  hardware-audio initialization. Real Switch audio remains future work.

## Build environment

- **Host:** WSL2 Ubuntu, podman (not Docker — Docker not installed)
- **Container image:** `devkitpro/devkita64:20260219` with additional deps
  (cmake 3.31.6 from base image, dotnet 9.0, clang, libclang)
- **TLS:** AMD corporate CA bundle injected into the container image. The
  original Dockerfile's cmake.org download fails under Zscaler interception.
  The base image's cmake 3.31.6 is sufficient (≥ 3.20 minimum).
- **Dockerfile:** `Dockerfile.local` in the mono-nx checkout, based on the
  original `Dockerfile` with the cmake step removed and CA cert injected.

## Build steps (what actually worked)

1. Clone mono-nx rel-3, extract prebuilt SDK into the tree
2. Build base interpreter: `cd native/interpreter && make` — verifies toolchain
3. Build FNA3D: `cmake -DCMAKE_TOOLCHAIN_FILE=$DEVKITPRO/cmake/Switch.cmake -DBUILD_SDL3=OFF -DBUILD_SHARED_LIBS=OFF` then `make`
4. Build FAudio: same flags as FNA3D
5. Install both to `fna-nx-test/native/install/`
6. Generate dl_shim C files from compiled `.a` symbols via `nm`
7. Patch mono-nx's `dl_shim.c` to register FNA3D, FAudio, and experimental stubs
8. Build custom interpreter: `cd fna-nx-test/native/interpreter && make`
9. Build FNA.dll: `dotnet build FNA.Core.csproj -c Release`
10. Build test app: `dotnet build -c Release`
11. For Terraria, run `scripts/pack_terraria_romfs.py`; it stages the default
    GOG directory into `native/interpreter/romfs/` and builds with
    `MONO_NX_USE_ROMFS=1`
12. Copy only the resulting ~751 MB Terraria NRO and the generated external
    `mono/config.ini` to the SD card. Keep runtime DLLs/ICU/log external.

## Gotchas encountered

- **devkitPro Makefile template prepends `$(CURDIR)/` to SOURCES and INCLUDES.**
  Absolute paths like `$(MONO_NX_DIR)/native/shared` get double-prefixed into
  broken paths. Fix: use relative paths and symlinks (`../shared_mono_nx`).
- **FNA3D needs MojoShader as a submodule.** A `--depth=1` clone without
  `--recurse-submodules` produces a "mojoshader.c not found" cmake error.
- **FAudio latest requires SDL3 headers by default.** Must pass
  `-DBUILD_SDL3=OFF` or it fails with `SDL3/SDL_stdinc.h: No such file`.
- **MojoShader must be explicitly linked.** FNA3D's CMake builds it as a
  separate `libmojoshader.a`, but it's not automatically added to the link
  line. Must add it to `LIBS` in the Makefile.
- **dl_shim generated files must include `dl_shim_base.h` via the correct
  relative path.** Files in `fna-nx-test/native/shared/` need
  `#include "../shared_mono_nx/dl_shim_base.h"` not `#include "../dl_shim_base.h"`.
- **FNA's `FNA.Core.csproj` targets net8.0.** It builds fine with dotnet 9 SDK
  and the output DLL runs on mono-nx's net9.0 runtime. The test app references
  it via `<Reference>` with `<HintPath>`, not `<ProjectReference>`.
- **dotnet is not in PATH inside the container.** The Dockerfile installs it
  via `dotnet-install.sh` to `~/.dotnet/`. Must export
  `PATH="$HOME/.dotnet:$PATH"` and `DOTNET_ROOT="$HOME/.dotnet"`.

## What the "update loop" means

Terraria's `Game.Update()` method runs every frame (target 60 FPS). It
processes: player input, AI for hundreds of NPCs, tile updates, projectile
physics, collision detection, lighting calculations, and world generation
chunks. This is CPU-heavy managed code executed by the Mono interpreter.

The test app's `Update()` does almost nothing — read gamepad, move a vector.
Terraria's `Update()` does orders of magnitude more work per frame. The
interpreter's speed running Terraria's actual game logic is the untested risk.
## Fork strategy

Forked repository: `https://github.com/JohnUzoka/mono-nx`

- Branch: `fna-support`
- `29e3af0`: FNA3D/FAudio dl_shim registrations
- `bedfb70`: experimental Steam/Windows/nfd stub registrations
- `e7b1704`: generic stub source with case aliases

The fork's `native/shared/dl_shim.c` contains the library registrations and
`native/shared/dl_shim_stubs.c` contains the opt-in stub implementation. The
stubs remain experimental until the RomFS NRO runs on hardware.

- **FNA3D** — no source changes needed, just build flags. No fork required
  unless we need Switch-specific patches.
- **FAudio** — same as FNA3D.
- **FNA** — no source changes. No fork required.

The `fna-nx-test` project in `~/workshop/fna-nx-test/` contains the custom
interpreter, Makefile, scripts, and test app. It references the mono-nx fork
source tree rather than silently modifying upstream.


## What's next

1. **Test the one-file RomFS NRO on hardware** — copy the NRO and external
   `mono/config.ini`; no Content directory needs to be copied.
2. **Capture `/mono/log.txt`** from the SD card. The first fatal error will
   determine whether .NET Framework binding, Steam stubs, Windows stubs, or
   a missing managed API is the next fix.
3. **Add the stub source to the mono-nx fork** as a proper commit after the
   hardware test validates which stubs are actually needed.
4. **Measure interpreter performance** with real Terraria gameplay and a
   CPU-heavy synthetic test.
5. **Tune the heap split** based on Terraria's actual memory usage.
6. **Investigate Mono AOT** for vanilla Terraria if interpreter performance is
   too slow; tModLoader's dynamic assembly loading remains a separate issue.

## File locations

- Project: `~/workshop/fna-nx-test/`
- Findings: `~/workshop/fna-nx-switch-findings.md`
- Build tree: `/tmp/mono-nx-build/mono-nx/` (ephemeral)
- Packer: `fna-nx-test/scripts/pack_terraria_romfs.py`
- One-file NRO: `/tmp/terraria-romfs-output/mono/mono_nx_fna_terraria.nro`
- External config: `/tmp/terraria-romfs-output/mono/config.ini`
- Earlier test NRO: `fna-nx-test/native/interpreter/mono_nx_fna.nro`

## Terraria hardware findings (2026-09-18)

The following facts are now hardware-observed and must be preserved in every
new Terraria NRO:

- The patched Terraria assembly must retain all of the Chroma, Windows/
  WeGame, background `ForceLoadThread`, `LoadedEverything`, and cached ReLogic
  resolver changes. `Terraria.Player.InternalSavePlayerFile` and
  `Terraria.Player.LoadPlayer` are redirected to the embedded `NxCrypto`
  AES-128-CBC implementation because the bundled Aes provider throws
  `PlatformNotSupportedException` on Switch.
- The NRO RomFS must include `NxCrypto.dll`, the runtime facade closure,
  `System.IO.Packaging.dll`, `System.Security.Permissions.dll`, and the
  Switch controller database. These are embedded; `config.ini`, ICU, runtime
  directories, and logs remain external on SD.
- World generation completed successfully on hardware with seed `1499515246`,
  width 6400, height 1800. The recorded `Total Generation Time` was
  `5547530` (approximately 92 minutes if interpreted as milliseconds).
- The post-generation crash was first caused by missing `System.IO.Packaging`
  behind `WindowsBase` and then masked by FNA's bound render-target disposal
  exception. The FNA cleanup patch unbinds the target before disposal.
- The stable path is Mono interpreter-only mode. Full ARM64 JIT experiments
  (`nochroma19` and `nochroma20`) abort in Mono's `mini-arm64.c:1335` with
  assertion `ji` not met. Do not use those JIT profiles as fallbacks.
- SDL detects four `Switch Controller` devices. The reported mapping is
  `leftshoulder:b6`, `rightshoulder:b7`, `lefttrigger:b8`, and
  `righttrigger:b9`. Menu binding works; in-game control verification remains
  pending because the interpreter frame rate is extremely low.

### Required NRO preservation checklist

Before building a new NRO, verify that the staging RomFS still contains:

1. The patched Terraria executable with the NxCrypto encryptor/decryptor
   calls and all startup stability patches.
2. The FNA assembly with SDL2 selection, `FNA_NX_TITLE_LOCATION`, the
   hardcoded Switch content root `romfs:/Content`, public `FNA3D` version
   visibility, and bound-render-target cleanup.
3. `NxCrypto.dll`, `System.IO.Packaging.dll`,
   `System.Security.Permissions.dll`, and `gamecontrollerdb.txt`.
4. `main.c` setting SDL2/OpenGL/dummy audio, writable SD paths, RomFS
   mounting order, and `MONO_AOT_MODE_INTERP_ONLY`.

`nochroma25` was the raw-input diagnostic attempt, but its rebuilt FNA
assembly regressed audio finalization. `nochroma26` is now hardware-validated:
the log loads `System.IO.Packaging`, initializes OpenGL, reaches the world,
and records in-game chat activity without an unhandled exception. Its input
buttons still need targeted verification because it uses the known-good FNA
binary without raw transition logging.
***

### FNA rebuild regression and recovery

- `nochroma22` through `nochroma24` used a rebuilt FNA.Core diagnostic assembly
  and initially failed before input at `sdmc:/Content`. The custom content-root
  behavior was not fully preserved by rebuilding the framework.
- `nochroma25` hardcoded the public FNA content root and reached graphics, but
  the rebuilt FNA audio implementation then raised
  `NoAudioHardwareException` from `SoundEffect.Finalize`. This proves that a
  source rebuild of FNA is not a safe substitute for the known-good FNA DLL.
- `nochroma26` restores the original game FNA binary, then applies only the
  required Cecil patches: `romfs:/Content` for both ContentManager root
  getters, public `FNA3D_LinkedVersion`, and bound-render-target cleanup. It
  preserves the known-good audio behavior and is now hardware-validated.

Rule: do not replace the working FNA binary with a freshly rebuilt FNA.Core
assembly unless every known FNA patch and the no-audio behavior are revalidated.
Patch the known-good FNA binary surgically instead.
***

### Current input diagnostic status

- The latest successful run reached the world and recorded gameplay chat with
  the original FNA binary. SDL reported four Switch Controllers and the
  expected `leftshoulder:b6`, `rightshoulder:b7`, `lefttrigger:b8`, and
  `righttrigger:b9` mapping.
- `nochroma25` failed only because its rebuilt FNA audio finalizer was not
  compatible with the no-audio setup. Do not use it for input conclusions.
- `nochroma27` injects a raw-button logger into the known-good FNA
  `GetGamePadState` method and loads the small external `NxInputDiag.dll`.
  This avoids rebuilding FNA and preserves audio/content behavior. It remains
  untested on hardware.
***

### Raw input diagnosis and mapping fix

`nochroma27` reached gameplay and produced these raw transitions:

- `buttons=0x00000080` — raw button 7, matching the logged right-shoulder
  mapping;
- `buttons=0x00040000` — raw button 18, not represented by the previous
  `Switch Controller` mapping, which exposed `start:b10`;
- additional observed masks included raw buttons 1 and 2.

The raw button 18 event matches the user's `+` report. `gamecontrollerdb.txt`
now overrides the Switch XInput mapping with `start:b18` and `back:b17` while
preserving the known shoulder/trigger mappings. `nochroma28` contains this
mapping and the known-good FNA/input diagnostic path.
***

### Complete XInput mapping

The XInput Switch mapping now explicitly covers the remaining controls:

- face buttons: `a`, `b`, `x`, `y` in both physical-label variants;
- D-pad: `up:b13`, `down:b15`, `left:b12`, `right:b14`;
- L3/R3: `leftstick:b4`, `rightstick:b5`;
- ZL/ZR: `lefttrigger:b8`, `righttrigger:b9`;
- L/R: `leftshoulder:b6`, `rightshoulder:b7`;
- minus/plus: `back:b17`, `start:b18`.

`nochroma29` contains this mapping and preserves the known-good FNA and
diagnostic helper. Hardware verification of these controls remains pending.
***

### SDL mapping database load fix

`nochroma29` still logged the built-in mapping (`start:b10`) even though the
RomFS database contained the corrected `start:b18` entry. The original FNA
binary was using its SDL base path on SD, so it never found the embedded
`gamecontrollerdb.txt`. The known-good-FNA patch now hardcodes the SDL2 title
root to `romfs:/`, allowing the embedded database to load.

`nochroma30` attempted this title-root patch but accidentally returned
`romfs:/Content` from the SDL base-path getter, so the mapping file still did
not load. `nochroma31` returns `romfs:/` for SDL's title root and retains
`romfs:/Content` only for ContentManager.
***

### Gameplay edge-input finding

The `nochroma29` raw trace observed both shoulder transitions:

- `0x00000040` = raw button 6;
- `0x00000080` = raw button 7.

Therefore both physical bumper signals reach FNA. The user's report that a
held R works while a quick click does not is consistent with missed edge
transitions in the extremely slow managed update loop, not absent hardware
input. The same run shows frequent large GC pauses while the game is active.

The built-in mapping still reported `start:b10` in `nochroma30`, confirming
that the embedded database was not loaded. `nochroma31` separates the SDL
title root (`romfs:/`) from the content root (`romfs:/Content`), so the
XInput override (`start:b18`, `back:b17`) can be discovered.
***

### Direct SDL mapping registration

`nochroma31` still reported the built-in `start:b10` mapping. The embedded
database path correction alone was insufficient because SDL retained the
driver's existing `xinput` mapping. `nochroma32` directly calls
`SDL_GameControllerAddMapping` after SDL initialization, registering
`start:b18`, `back:b17`, and the complete Switch button map before controllers
are opened. The helper logs the registration result and raw transitions.
***
## Performance and input correction (2026-09-18)

### User-reported comparison baseline

- Horizon: approximately 5–10 minutes to the menu, about 10 FPS in menus,
  and less than 1 FPS in-game. L/R and D-pad taps are missed unless held;
  plus/minus do not work. Menu keybinding detects the shoulder/D-pad controls.
- The same Terraria binary on Switch **Linux L4T** reportedly reaches about
  **50 FPS with frame skip**, using **Mono and OpenGL GLCompatibility**, not
  SDL_GPU or D3D11. This is the user's hardware observation, not a benchmark
  performed in this session. It is a comparison target, not evidence that
  the current Horizon runtime and driver have equivalent performance.
- [INFERENCE] The L4T Mono launch probably uses JIT by default; its actual
  flags have not been supplied or verified. Do not treat that as established.
- **Superseded 2026-09-24:** the ~50 FPS figure was at L4T's default handheld
  CPU clock of 1581 MHz. At Horizon's 1020 MHz with 3 cores and the same
  settings, L4T gives 24–26 FPS with Frame Skip On. See
  [the matched-clock comparison](#l4t-matched-clock-comparison-and-runtime-quality-findings-2026-09-24).

### Corrections to the earlier mapping diagnosis

The previous `start:b18,back:b17` conclusion was **wrong**. Those entries map
left-stick directions, not plus/minus. The native SDL backend's
`pad_mapping_default` array places **Plus at raw button 10** and **Minus at
raw button 11**; indices 17/18 are StickLUp/StickLRight. This was checked both
against the [devkitPro SDL source](https://github.com/devkitPro/SDL/blob/switch-sdl-2.28/src/joystick/switch/SDL_sysjoystick.c)
and the `pad_mapping_default` data in the installed `libSDL2.a` driver object.

The supplied `fna-nx-test/log.txt` is the direct-registration build's run:
line 1652 reports `NX Switch mapping install result=0`, and lines 1743–1746
show the incorrect b18/b17 mapping actually installed for all four controllers.
Lines 8034 and 8037 contain raw masks `0x800` and `0x400` (buttons 11 and 10).
Therefore another database path/loading change is not the fix. Both the
database and direct-registration helper now use **`start:b10,back:b11`**.
Earlier notes claiming b18/b17 are correct are historical, superseded findings.

The new input path samples independent libnx PadStates every 4 ms on a native
worker and latches raw rising edges. SDL and Mono are called only on the game
thread. One snapshot is consumed per managed Update, never per GetState:
short taps survive a slow update, repeated reads are identical, steady holds
stay held, and release/re-press gets a released update before the next press.
Multiple taps between updates are bounded/coalesced rather than queued without
limit. Disconnect/style changes reset pending input. This cannot remove the
latency of a slow update; it prevents taps disappearing entirely.

Only the original FNA's 21 SDL button reads in GetGamePadState are redirected;
the existing state construction, packet numbers, analog axes, and triggers
remain intact. The unconditional raw logger is removed. A source FNA rebuild
is still prohibited by the earlier audio/content regressions.

### What the existing performance evidence does and does not prove

- The launcher forces `MONO_AOT_MODE_INTERP_ONLY`. No game method uses AOT.
- The library paths say `Debug`, but DWARF in the shipped
  `libmonosgen-2.0.a` member `interp.c.obj` explicitly records **GCC 15.2.0,
  `-O2`**. Replacing the launcher with another `-O2` build is not a fix for
  an alleged `-O0` interpreter.
- The log contains 7,548 `Mono debug` lines. The external config had
  `runtime_logging=true`, enabling mask `all` at level `debug`. New configs
  disable this verbose trace while keeping normal logging, error reporting,
  and the new sparse phase measurements. The speedup from reducing logging
  has not been measured on hardware.
- The 150 recorded minor/concurrent-start GC events total **1,057.18 ms STW**
  (maximum 91.35 ms); 42 concurrent-major finishes total **270.17 ms STW**
  (maximum 10.89 ms). Roughly **1.327 seconds** of recorded stop-the-world
  pauses does not explain minutes of startup. The large `time` values on
  concurrent collections are not stop-the-world pauses or a direct CPU-time
  measurement. The log has no wall-clock timestamps, so a GC rate or actual
  FPS cannot be derived from it.
- The Horizon renderer log is **OpenGL ES 3.2 / Mesa 20.1.0-rc3 / nouveau**,
  renderer **NV120**, MojoShader **glsles3** (lines 2067–2071). That is not
  the desktop GLCompatibility profile in the L4T comparison. Native driver
  and managed execution differences must be investigated separately.
- FNA's source polls events once per Tick and can perform multiple fixed
  updates before drawing, clamping accumulated time at 500 ms. Whether that
  catch-up behavior dominates this run is not measured yet. Scheduling and
  Terraria's simulation timestep are deliberately unchanged in this update.

### Safe acceleration direction and new timing

The experimental acceleration path is **static ARM64 AOT plus interpreter
fallback**, not the previously crashing full JIT. In the actual libnx runtime
source, `MONO_AOT_MODE_INTERP` sets `mono_aot_only`, `mono_use_interpreter`, and
AOT trampolines; `INTERP_ONLY` instead forces interpreted execution
([driver.c](https://github.com/exelix11/dotnet_runtime/blob/libnx/src/mono/mono/mini/driver.c#L2795-L2822)).
The earlier JIT assertion is inside `create_thunk`; the fork itself notes that
this function confuses RX/RW addresses
([mini-arm64.c](https://github.com/exelix11/dotnet_runtime/blob/libnx/src/mono/mono/mini/mini-arm64.c#L1296-L1335)).
Do not claim AOT is hardware-validated merely because its modules compile.

Corelib compiled successfully with `full,interp,static`: **75,230/75,888**
methods, exported `mono_aot_module_System_Private_CoreLib_info`, AArch64 ELF.
The `interp` option generates the bridge trampolines required for fallback.
Do not trim the reflection-heavy game or use `direct-pinvoke`: retain the
working dynamic-library shim and compile the exact patched managed bytes.
The cross compiler's `--path=` accepts one directory; multiple search roots
need repeated `--path=` arguments, not a colon-separated value. Omitting the
corelib directory aborts at `assembly.c:2718` before compilation.

`NX_PHASE` reports at most once per completed Tick after a 5-second interval,
plus shutdown: elapsed/window seconds, tick/update/draw window and total
counts, summed and maximum durations, maximum updates in one tick, and first
tick/update/draw offsets. Divide window draw count by window seconds for
Game.Draw call rate, not a guarantee of distinct presented frames when frame
skip is active. Draw timings cover Game.Draw, not EndDraw/present;
Tick includes the remaining work and pacing. Startup offsets begin after Mono
initialization, before game assembly execution. Register the native internal
calls **after `mono_jit_init`**, which initializes Mono's internal-call table.

### Verification completed before hardware retest

- Native input scenario passed with `-Wall -Wextra -Werror` and separately
  with ASan/UBSan: taps between updates, holds, release/re-press, 100 repeated
  reads, 1,000 coalesced taps, simultaneous buttons, four devices, b10/b11,
  rotated single Joy-Cons, disconnect/style changes, SDL fallback, worker
  sleeping, thread failure paths, and phase timing/report throttling.
  These are host API-fixture scenarios, not Switch scheduler measurements.
- Helper and Cecil patcher build with zero warnings/errors. The preservation
  verifier found **6,930 unaffected FNA method bodies identical**; GetGamePadState
  differs only in its 21 button targets, and Tick only by nine timing-hook
  sites. Content/title roots, FNA3D access, disposal cleanup, and all original
  assembly references are preserved, with only NxInputDiag added.
- The interpreter-only input-fix NRO cross-build completed without warnings
  or errors and its linked ELF is AArch64. Existing Terraria/NxCrypto/facade
  payloads and all prior NROs were retained, not rebuilt or deleted.
- All new build work is on disk under
  `~/.cache/terraria-switch-build/`; `/tmp` is a nearly full 8 GB tmpfs, not
  an appropriate place for another large NRO or runtime build.

### Final AOT compilation and integration findings

This uses **mono-nx's shipped `mono-aot-cross` and existing runtime libraries**,
not a new runtime, a Terraria source rewrite, or a full-JIT implementation.
The compiler's printed `JIT time` is offline native-code generation, not JIT
execution on Horizon. All seven static modules compiled and linked:

| Assembly | Compiler-reported compiled/total methods |
| --- | ---: |
| System.Private.CoreLib | 75,230 / 75,888 |
| Terraria | 52,872 / 52,896 |
| FNA | 20,275 / 20,324 |
| NxCrypto | 7,409 / 7,409 |
| NxInputDiag | 11 / 11 |
| ReLogic | 20,199 / 20,203 |
| Newtonsoft.Json | 16,388 / 16,399 |

These counts include compiler-generated methods/wrappers; they are not FPS or
runtime coverage measurements. Interpreter fallback remains enabled.

Two build blockers were reproduced and fixed without rewriting game IL:

1. **AOT inliner crash.** Default Terraria compilation SIGSEGVed while
   compiling `Main.SetDisplayMode(int,int,bool)`. A native debugger found a
   null basic-block dereference in `method-to-ir.c:12389`, reached through
   `inline_method:4863`. `--optimize=-inline` for **Terraria only** makes the
   same bytes compile. Other modules keep the default optimizer settings.
   The reproducible script exposes `--no-inline-assembly Terraria`.
2. **ARM64 method-table branch range.** The combined AOT code and metadata
   placed `.data.rel.ro` method-address tables beyond CALL26's ±128 MiB reach.
   These are encoded branch addresses that Mono reads/decodes, not code that
   must execute or data that must be modified. The small
   `aot-method-tables.ld` fragment puts only AOT method tables into read-only
   memory before the large metadata region. Its `.specs` wrapper orders the
   fragment before the existing libnx linker script; libnx's normal layout
   and W^X protections remain in force. No full linker-script fork is needed.
   Input checks reject non-CALL26 relocations or invalid table words. Final
   linked-image checks verified **192,384 exact native method targets** and
   **15,010 missing-method sentinels**, no branch veneers, read-only/non-executable
   tables, and no RWX load segment in either final AOT build.

**Important startup requirement:** Mono loads AOT dependency metadata eagerly,
before `LinuxLaunch.Main` installs Terraria's embedded-assembly resolver.
`aot-runtime.c:2395–2406` rejects a module if a dependency cannot be found or
its MVID differs. Merely linking ReLogic/Newtonsoft native code is insufficient.
The AOT RomFS therefore also exposes nine **byte-identical** embedded DLLs at
its root: CsvHelper, Ionic.Zip.CF, Newtonsoft.Json, MP3Sharp, NVorbis,
RailSDK.Net, ReLogic, Steamworks.NET, and SteelSeriesEngineWrapper.
The existing patched Terraria executable and its embedded resources are not
changed. The old embedded System.ValueTuple is deliberately excluded in favor
of the existing net9 facade. All **85 name/MVID bindings decoded from the
actual seven AOT object image tables** resolve using the staged payload, these
nine files, and the matching corelib; none relies on an SDK-only dependency.
Missing-provider and wrong-MVID negative checks both failed as intended.

The native launcher also requires the Terraria entrypoint to resolve through
the AOT module and logs `NX_AOT Terraria entrypoint resolved to native code` on
success. This is an acceptance check to observe on the **next Switch run**,
not a log line observed on hardware yet. The lookup is wrapped with balanced
`mono_threads_enter_gc_unsafe_region` / `mono_threads_exit_gc_unsafe_region`:
`mono_jit_init` returns in GC-safe mode and the internal AOT lookup requires
GC-unsafe mode. Do not remove those transitions or use an unguarded private API.

The SDK corelib used for AOT matches the packaged SD runtime byte-for-byte:
`ddcbdd24a642d9bc923aab3df519c68803b51afccfe252b5846e9898eea191fd`.
Do not mix in an unrelated corelib/runtime update while comparing these builds.

### GLCompatibility comparison is now a real build option

The shipped FNA3D object contains
`FNA3D_OPENGL_FORCE_COMPATIBILITY_PROFILE`. Its implementation requests GL 2.1
with `SDL_GL_CONTEXT_PROFILE_COMPATIBILITY`. Switch SDL supplies an ES default,
but delegates creation to the generic EGL backend, which selects
`EGL_OPENGL_BIT` / `EGL_OPENGL_API` for a desktop request and forwards the
profile attributes. The installed SDL config enables both desktop OpenGL and
ES2; its actual EGL object directly references the statically linked EGL API.

Sources:
- [FNA3D profile selection](https://github.com/FNA-XNA/FNA3D/blob/master/src/FNA3D_Driver_OpenGL.c)
- [Switch SDL default profile](https://github.com/devkitPro/SDL/blob/switch-sdl-2.28/src/video/switch/SDL_switchopengles.c)
- [SDL EGL selection/context creation](https://github.com/devkitPro/SDL/blob/switch-sdl-2.28/src/video/SDL_egl.c#L779-L795)

There is no source basis for claiming SDL categorically blocks compatibility
profiles or that a direct-EGL rewrite is required. `MONO_NX_GL_COMPAT=1` now
requests the real FNA3D compatibility option, with ES/core forcing disabled.
**Whether switch-mesa accepts that context and whether it is faster must still
be measured on hardware.** A request log is not confirmation: inspect the
actual OpenGL version/profile and MojoShader profile printed afterward.

### Builds 33–35 pre-test handoff (superseded by hardware results below)

Files are in `~/workshop/fna-nx-test/terraria-mono/switch/`:

| NRO | Managed execution | Graphics request | Bytes |
| --- | --- | --- | ---: |
| `mono_nx_fna_terraria_nochroma33.nro` | Interpreter-only; new input fix/timing | Existing SDL default | 808,948,041 |
| `mono_nx_fna_terraria_nochroma34_aot.nro` | AOT + interpreter fallback | Existing SDL default | 946,984,705 |
| `mono_nx_fna_terraria_nochroma35_aot_glcompat.nro` | AOT + interpreter fallback | Desktop GL compatibility | 946,988,801 |

All three builds retain **15,997 Content assets**. The interpreter NRO contains
16,174 files; the AOT builds contain 16,183 because of the nine early metadata
DLLs. Actual NRO RomFS contents, not just staging directories, were checked
against the preserved Terraria/NxCrypto/FNA/facade payloads. Final checksums and
the per-build hardware outcomes are recorded in
`fna-nx-test/terraria-mono/build-verification.json`.

Recommended hardware comparison:

1. Copy **only the NRO being tested** into `SD:/switch/`; do not copy the whole
   host folder of older large builds. Also copy the updated
   `terraria-mono/mono/config.ini` to **`SD:/mono/config.ini`**. Keep existing
   runtime DLL/ICU directories, player/world data, and input bindings intact.
2. Use full application/title-takeover mode. Start with **34** to isolate AOT
   while keeping the previously working graphics selection. Then compare
   **35** with the same test world, resolution, and frame-skip setting.
   **33** is the interpreter/input-only control if AOT fails or for comparison.
3. Time menu readiness, leave the menu active briefly, load an existing test
   world, and exercise short taps and holds of L/R, D-pad, plus and minus.
   Avoid regenerating a large world just to measure gameplay performance.
4. Save `/mono/log.txt` under the build's name after each run. Check for the
   `NX_AOT` success line, `NX_INPUT sampler started`, actual renderer/profile,
   and `NX_PHASE` samples. First-draw timestamps are not an exact menu-ready
   predicate. If startup fails, enabling `runtime_logging=true` for one
   diagnostic run provides dependency details; turn it off for FPS comparisons.

*Historical pre-test status, superseded by the hardware report below:*

**No new Switch boot, input response, graphics-context result, menu load time,
or FPS result has been claimed in this session.** Host scenarios, cross-builds,
native/managed preservation checks, and packaged-artifact checks pass; hardware
comparison is the next external step. The user's L4T ~50 FPS remains the
comparison baseline, not a measured result for these NROs.

### Persistent sources and reproduction

- `native/shared/nx_input.{c,h}` and `nx_input_latch.h`: native sampling,
  buffered state, lifecycle, sparse timing.
- `managed/nx_input_diag/`: managed bridge using the pinned FNA binary.
- `scripts/patch_fna/`: strict original-binary Cecil patcher, never a FNA.Core
  rebuild. Arguments are original FNA, output FNA, and NxInputDiag DLL.
- `scripts/prepare_aot/` and `scripts/compile_terraria_aot.py`: exact embedded
  assembly extraction, AOT compilation, relocation checks, early metadata
  staging, name/MVID closure checks and manifests.
- `native/interpreter/`: runtime mode, GC-balanced AOT acceptance probe,
  optional GLCompatibility, and narrow linker augmentation.
- `native/tests/test_nx_input_latch.c`: retained deterministic regression
  checks, compiled with `-std=c11 -Wall -Wextra -Werror -O2` and executed.

Build cache: `~/.cache/terraria-switch-build/`. Container paths are `/work`
(persistent fna-nx-test), `/mono-nx` (existing checkout/SDK), `/build` (cache),
and `/input/FNA-Game` (read-only original game). The image remains
`localhost/monobuild:local`, run with Podman; dotnet is `/root/.dotnet/dotnet`.
Keep assembler tools on PATH and TMPDIR on disk, not the full host tmpfs.

After compiling corelib with the existing SDK, the game-module workflow is:

```sh
python3 /work/scripts/compile_terraria_aot.py \
  --game-dir /build/nochroma33/native/interpreter/romfs \
  --output-dir /build/aot --corelib-log /build/aot/corelib-aot.log \
  --no-inline-assembly Terraria --jobs 3
```

It emits `runtime-metadata/` and `runtime-metadata-manifest.json`; copy those
nine DLLs into an isolated AOT RomFS without overwriting any existing facade.
`--runtime-metadata-only` rechecks/stages metadata without recompiling objects.
The complete workflow was rerun successfully, followed by final native relinks
and verification. **A normal full rerun recompiles the six game/helper modules
and changes AOT build IDs/object hashes; always relink afterward.** Native
Makefile dependencies now include the AOT objects and linker files.

Use separate native build directories for interpreter, AOT/default graphics,
and AOT/compatibility so changes in C compiler flags cannot reuse stale objects.
Native environment: `MONO_NX_DIR=/mono-nx`,
`MONO_NX_ROOT=/mono-nx/dotnet_runtime`, `ICU_NX_INSTALL_DIR=/mono-nx/icu/libnx`,
`FNA_NX_INSTALL_DIR=/mono-nx/fna-nx-test/native/install`.
Build 34 with `MONO_NX_USE_ROMFS=1 MONO_NX_USE_AOT=1 MONO_NX_AOT_DIR=/build/aot`;
35 additionally uses `MONO_NX_GL_COMPAT=1`. Retain the original binary's patches
and the new nine metadata files; **do not rerun the raw installer packer over
this validated RomFS**, because it would replace the patched assemblies.

Detailed evidence is under `aot/build-manifest.json`,
`aot/runtime-metadata-manifest.json`, `aot/verification/`,
`nochroma34/link-verification-final.json`, `nochroma35/link-verification-final.json`,
and each build's `nro-payload-final.json` / `native-final-build.log` in the cache.

## Builds 34 and 35 hardware failure (2026-09-18)

User supplied `fna-nx-test/log34.txt` and `log35.txt`. After testing 33, the
user reports the original missed-button issue appears resolved. Downward menu
navigation consistently skips some entries; exact entries/control details are
not yet known. The user explicitly deferred that issue to focus on AOT.

Both AOT runs fail at the same runtime site, before graphics initialization:

| Evidence | Build 34 | Build 35 |
| --- | --- | --- |
| Native Terraria entrypoint accepted | log line 905 | log line 906 |
| First fatal code-write translation | line 1298 | line 1301 |
| Pointer incorrectly treated as executable | `0x4e743f6f0` | `0x418641e50` |
| Native heap interval | `0x4e1b4f000–0x540c00000` | `0x412d50000–0x471c00000` |
| Assert | `mono-codeman.c:982`, `res != NULL` | same |
| Calling site | `mini-runtime.c:1528` | same |

All seven static AOT modules are found in both logs. Corelib AOT functions and
Terraria's native entrypoint run; the nine-file metadata staging and native
entrypoint acceptance check passed on hardware. Neither log reaches
`FNA3D_CreateDevice`, an OpenGL renderer banner, the input sampler startup,
or a completed `NX_PHASE` report. Therefore **this is not a GLCompatibility
result, an input result, or an FPS result**. Repeating 34 versus 35 cannot
separate renderer behavior until the shared runtime failure is corrected.

### Cause: data allocation incorrectly passed through code-address translation

`mono_resolve_patch_target` handles `MONO_PATCH_INFO_SWITCH` as follows:

- Dynamic methods allocate a table through a Mono code manager.
- Ordinary non-AOT execution also allocates through code-managed memory.
- With `mono_aot_only` true (including `MONO_AOT_MODE_INTERP`), an ordinary
  method instead uses `mono_mem_manager_alloc`: **plain writable heap data**.
- The old code unconditionally called `mono_codeman_enable_write_ex` and
  `mono_codeman_disable_write_ex` for every table. The libnx helpers require
  an RX/RW JIT-area mapping, except for their existing FULL-only bypass.

Both failing addresses are inside the native heap, exactly matching the
ordinary-data allocation path. There is no JIT mapping to find for those
addresses. This is a source-level allocator/write-operation mismatch, not
evidence that AOT needs full JIT or that an assertion should be suppressed.

The fix records which allocator was used and applies the write-alias/flush/
executable-alias operations **only to code-managed allocations**. AOT data
tables remain normal writable data. Dynamic-method precedence and legitimate
RX/RW transitions remain intact; global translation helpers/assertions are
unchanged. Persistent patch:
`fna-nx-test/native/patches/mono-switch-table-data-memory.patch`.

The source-derived host regression reproduces the original SIGABRT for both
method-present and method-absent AOT data paths. After the patch, **42 cases**
pass; **30 previously working cases** retain their behavior. Eight expected
assertions check the old reproductions, unknown pointers, and no-code-manager
boundary. Tests cover target offsets, allocation sizes/owners, returned heap
or RX pointers, RW writes, and flushing before publishing executable aliases.
This is a host allocator/alias model of the real source branch, not a claim
that the corrected runtime has already booted on Switch.

Reproduce the local check from `fna-nx-test/`:

```sh
python3 native/tests/test_mono_switch_table.py \
  --runtime-source "$HOME/.cache/terraria-switch-build/runtime-source"
```

The native mono-nx component has now been rebuilt and integrated into build 36.
The managed game/corelib DLLs, all seven AOT modules, input changes, and
full-JIT-disabled execution mode remain unchanged.

### Build 36: source-fixed runtime, existing graphics profile

Published: `fna-nx-test/terraria-mono/switch/mono_nx_fna_terraria_nochroma36_aot.nro`

- Size: **946,988,801 bytes**.
- SHA-256: `d440c96322aaafe5680322ddc2bf696622f67d21cb742ef59a19ebe23235c355`.
- Same AOT + interpreter fallback and existing/default graphics choice as 34.
  No new GLCompatibility comparison is needed until this shared startup path works.
- The narrow allocation-provenance fix is compiled from the existing mono-nx
  runtime fork, commit `289cdaa5d288809891e3e2fbac82cd6e0cdcfcd7`.
  This is not a replacement runtime or a managed-game rewrite.

The full native Mono build supplied the corrected `mini-runtime.c.obj`.
To keep the change isolated, only that member was replaced in a copy of the
original SDK archive. **277 archive members were checked: exactly one changed.**
The original SDK, other native runtime components, managed DLLs, AOT objects,
and earlier NROs were not overwritten. Native compiler target/options match
the shipped object exactly: GCC 15.2.0, ARM64 Cortex-A57, `-O2` and `-fPIE`.
All **107 named aggregate/enum layouts**, exported/imported symbols, and the
`9.0.3.0 (42.42.42.42424)` runtime identity are unchanged. The final linked ELF
contains the source-built component with its allocator-provenance guard.

Corrected archive:
`~/.cache/terraria-switch-build/runtime-fix/libmonosgen-2.0.a`

Archive SHA-256:
`d8ad7939d7956506976a109c455a5e684c800c55548daa6781de6465f2716d20`.

Final NRO checks pass: **192,384 native AOT method targets**, **15,010 fallback
sentinels**, read-only/non-executable method tables and no RWX load segment.
All **16,183 embedded files / 15,997 Content assets** remain, and the required
game/FNA/NxCrypto/facade/early-dependency bytes match the preceding AOT payload.
The NRO cross-build has no warnings/errors. The separate full runtime build
reported three warnings in unchanged `mini-posix.c`/`mini-arm64.c`; those
freshly rebuilt components were not substituted into the SDK archive.

**Hardware update:** 36 gets past the previous allocation/write failure, but
hits a later EventPipe/exception-initialization failure described below. The
earlier host/ABI/artifact checks remain valid; they were not a claim that all
runtime paths had been exercised. Do not repeat 34–36 for a renderer comparison.
Use full application/title-takeover mode. For performance measurement, set
`runtime_logging=false` while retaining `logging=true` and the `NX_PHASE` logs.
The 34/35 uploads had verbose Mono tracing enabled. If 36 fails, preserve the
new `/mono/log.txt`; its next failure should be evaluated independently.

### Rebuilding the native component

The source checkout must be complete: the native-only build still imports
shared generator projects. The initial sparse checkout failed on a missing
LibraryImportGenerator props file; hydrating the checkout resolved that.
The canonical runtime build bootstrapped .NET SDK **9.0.102**, not a new managed
runtime for the SD card. Use the existing `localhost/monobuild:local` Podman
image, disk-backed `/build`, existing read-only SDK/ICU under `/mono-nx`, and
trusted corporate CA environment variables for downloads.

Inside that container, from `/build/runtime-source` with the source patch applied:

```sh
export ROOTFS_DIR=/opt/devkitpro
export ICU_NX_INSTALL_DIR=/mono-nx/icu/libnx
export NUGET_PACKAGES=/build/runtime-fix/nuget-packages
export CMAKE_BUILD_PARALLEL_LEVEL=4
./build.sh --subset mono.runtime --cross --arch arm64 --os libnx \
  --configuration Debug --keepnativesymbols true /p:MonoVerboseBuild=true
```

The corrected object is
`artifacts/obj/mono/libnx.arm64.Debug/mono/mini/CMakeFiles/monosgen-objects.dir/mini-runtime.c.obj`.
Copy the original SDK `libmonosgen-2.0.a` to a separate destination, replace
only `mini-runtime.c.obj` with `aarch64-none-elf-ar rcsD`, and verify the
member inventory/ABI before linking. `native/interpreter/Makefile` now accepts
`MONO_NX_RUNTIME_LIBRARY` and tracks it as a link dependency; its default
continues to select the unmodified SDK.

Build 36 uses the established native environment and these explicit flags:

```sh
make -C /build/nochroma36/native/interpreter -j4 \
  MONO_NX_USE_ROMFS=1 MONO_NX_USE_AOT=1 MONO_NX_AOT_DIR=/build/aot \
  MONO_NX_RUNTIME_LIBRARY=/build/runtime-fix/libmonosgen-2.0.a
```

Evidence: `runtime-fix/verification.json`, the source-derived regression logs,
and `nochroma36/native-build.log`, `link-verification.json`, `nro-payload.json`
under `~/.cache/terraria-switch-build/`. Do not rerun managed AOT compilation
for this native-only fix: the existing seven objects were verified unchanged.

### Deferred: consistent menu-entry skipping in 33

The user reports that navigating down skips some menu entries consistently,
after the original input issue appears resolved. Menu identity, skipped entry
names, and D-pad versus stick / tap versus hold are not yet established.
Per the user's instruction, no speculative navigation, repeat-timing, or
frame-step change was made; keep focus on AOT startup and revisit this later.

Post54 user follow-up: **Not seen recently**, trigger **Not sure**. Treat the old
report as no longer currently reproduced, not as proof of a new code fix. No
additional navigation/repeat/frame-step patch was applied.

## Build 36 EventPipe failure (2026-09-18)

The newly uploaded `fna-nx-test/log.txt` was preserved as
`fna-nx-test/log36.txt` and `~/.cache/terraria-switch-build/log36-hardware.txt`.
Build 36 reaches `NX_AOT Terraria entrypoint resolved to native code` (line 905)
and proceeds past the previous stop at RegexCharClass initialization: lines
1298 onward now initialize CompareInfo and complete more regular-expression
and buffer operations. The earlier switch-table allocation correction is
therefore passed on this hardware run.

The later failure sequence is:

1. `ArrayPoolEventSource` static initialization starts at line 1609.
2. `EventSource` initializes with its default support setting and calls
   `EventPipeInternal.CreateProvider` at lines 1691–1692.
3. `NotImplementedException` and `System.SR` resource-message initialization
   follow at lines 1693–1700.
4. ResourceManager reads through UnmanagedMemoryStream/Stream and re-enters
   byte/char ArrayPool operations (lines 1854–1945).
5. Native exception construction asserts in `exception.c:137` after
   `mono_runtime_object_init_handle` returns a NullReferenceException error
   (lines 1947–1952). Graphics and normal input update have still not started.

The unsupported provider is verified in both source and the shipped native
object, not inferred from a missing DLL: libnx CMake sets
`ENABLE_PERFTRACING=0`, and the actual provider ICall unconditionally calls
`mono_error_set_not_implemented` and returns null. Managed EventSource's
`InitializeIsSupported` defaults to true unless the AppContext switch
`System.Diagnostics.Tracing.EventSource.IsSupported` is set to false.

[INFERENCE] The final NullReferenceException is secondary initialization
recursion: `SharedArrayPool.Rent` reads `ArrayPoolEventSource.Log` while that
static singleton is still being constructed, and later calls `log.IsEnabled()`.
The hardware log is not a managed stack trace; `AOT: FOUND` entries report
method resolution, so they do not prove every logged method executed. There
is no evidence here of heap exhaustion or a generic-allocation ABI defect.

The targeted correction registers the supported AppContext capability as
false before `mono_jit_init`, so the managed EventSource constructors skip
provider registration on this tracing-disabled platform. This is independent
of `runtime_logging`, which controls Mono diagnostic output. Normal logging,
`NX_PHASE`, exception checks, the previous switch-table fix, game code and AOT
modules remain intact. No fake provider, exception suppression, or resource
message bypass is introduced.

The narrow exported `mono_runtime_register_appctx_properties` hook is used
instead of `monovm_initialize`: the latter also installs different loader
hooks and enables strict assembly-name matching, which must not be changed
incidentally for the existing .NET Framework compatibility setup.

The source-matched host reproduction and effective-switch check now pass.
They verify the shared production capability helper, not a claim that build 37
has already run successfully on Switch.

### Build 37: tracing capability correction

Published: `fna-nx-test/terraria-mono/switch/mono_nx_fna_terraria_nochroma37_aot.nro`

- Size: **946,988,801 bytes**.
- SHA-256: `223eec69d64a1d7939cf7816d126f5786a26d922b5dd6d30cafd10b19796afdc`.
- Same AOT/interpreter mode, default graphics profile, corrected runtime archive,
  game DLLs, seven AOT objects, nine early metadata DLLs, and input fix as 36.
- New `native/shared/nx_runtime_config.h` supplies
  `nx_runtime_register_capabilities()`, called before `mono_jit_init`.
  The only capability it changes is
  `System.Diagnostics.Tracing.EventSource.IsSupported=false`.
  This does not change assembly-loader policy, disable normal exceptions,
  alter resource messages, or turn off native `NX_PHASE`/error logging.

The actual shipped EventPipe provider object was disassembled and confirmed
to call `mono_error_set_not_implemented` unconditionally. The existing AOT
CoreLib implementation of `EventSource.InitializeIsSupported` was also
disassembled: it really reads `AppContext.TryGetSwitch`, rather than being
constant-folded to true. The runtime copies the native property strings and
installs them into AppContext during initialization, before EventSource use.

### Real-runtime before/after regression

An isolated Linux x64 Mono runtime was built from the same fork with native
EventPipe unavailable. It executes the **exact shipped libnx CoreLib IL/BCL**
in interpreter mode, using real Linux native support libraries. The small
native host includes the same production capability header as the NRO.

| Fresh process | Effective EventSource support | Native CreateProvider calls | ArrayPoolEventSource.ConstructionException |
| --- | --- | ---: | --- |
| Default | true; switch absent | 2 | NullReferenceException |
| Early production capability | false; switch found/value=false | 0 | null |

Native provider calls were counted with debugger breakpoints in the real
unsupported ICall, not a mock provider. The default case supplies an actual
managed stack confirming the reentrant path: CreateProvider →
NotImplementedException constructor → SR/resource reader → byte ArrayPool →
NullReferenceException constructor → SR → char ArrayPool. On this x64
interpreter host, EventSource catches and retains the exception and the process
continues; **the fatal ARM64 assertion itself was not reproduced on the host**.

Both processes also pass first char/byte pool rent/return, regex match/replace,
normal NotImplementedException/NullReferenceException construction, throwing
and formatting an InvalidOperationException with its stack, and BinaryReader
integer/string reads. With the capability disabled, the nested construction
exception disappears and no unavailable provider is called. The unrelated
host JIT-startup investigation is not part of the permanent regression.

Persistent checks: `native/tests/test_eventsource_unavailable.{c,cs,py}`.
The source-built host runtime stays in the disk-backed cache, not the NRO.
The existing verified environment can rerun the real regression with:

```sh
python3 "$HOME/.cache/terraria-switch-build/eventsource-repro/reproduce.py"
```

The direct runner accepts explicit runtime-source, native-build, corelib/BCL,
dotnet, Linux native-support, debugger, and work-directory paths. It builds only
the tiny native/managed probe, not the runtime or game. Evidence is in
`eventsource-repro/project-regression/run-4rq0euy5/` under the build cache:
`results.json`, `default.log`, `false.log`, `debug-default.log`, `debug-false.log`.
The parent rerun exited zero with `REPRODUCTION_VERIFIED`.

Build 37's cross-build and final NRO checks pass: **192,384 exact AOT method
targets**, **15,010 fallback sentinels**, read-only/non-executable tables, no
RWX segment, and **16,183 files / 15,997 Content assets**. Required payload
hashes and the corrected runtime archive match 36. Per-build records are in
`nochroma37/capability-verification.json`, `link-verification.json`,
`nro-payload.json`, and `native-build.log` in the cache.

**Hardware update:** 37 now initializes OpenGL/NV120 and graphics resources,
confirming the prior tracing failure is passed. It then exhausts the precompiled
AOT trampoline pool described below; no menu/gameplay FPS measurement exists yet.
The external `runtime_logging` setting remains separate: use false with
`logging=true` for performance tests. Menu-entry skipping remains deferred.

## Build 37 trampoline pool exhaustion (2026-09-18)

The latest upload is preserved as `fna-nx-test/log37.txt` and
`~/.cache/terraria-switch-build/log37-hardware.txt`.

Positive hardware progress:

- `NX_RUNTIME EventSource disabled: native EventPipe is unavailable` at line 9.
- Native Terraria AOT entrypoint accepted at line 925.
- No EventPipe CreateProvider call or earlier exception-initialization failure.
- **FNA3D OpenGL / NV120 / OpenGL ES 3.2 Mesa 20.1.0-rc3 / nouveau / glsles3**
  at lines 5682–5686. Vertex/index buffer and effect setup follows.

The fatal message at line 6827 is complete even though the subsequent repeated
fatal message is truncated at the end of the uploaded file:

```text
Ran out of trampolines of type 0 in '/mono/lib_net9.0/System.Private.CoreLib.dll' (limit 4096)
```

This is a fixed AOT call-dispatch-bank limit, not ordinary heap exhaustion.
`aot-runtime.h` defines type 0 as `MONO_AOT_TRAMP_SPECIFIC`.
`get_numerous_trampoline` always allocates from CoreLib's AOT module, checks its
per-kind index against `info.num_trampolines`, and advances that index under a
lock. The compiler default is **4096 specific trampolines** and the supported
compile option is **`ntrampolines=`**. Each specific trampoline has emitted
native code and two writable GOT slots; changing only the metadata limit would
be unsafe. More capacity must be generated by the existing AOT compiler.

Only CoreLib's native AOT object needs a larger specific pool. The original
CoreLib DLL, six game/helper AOT objects, corrected native runtime, graphics
selection, and previous input/tracing fixes remain unchanged in build 38.
Its 65,536-entry specific pool has real generated code/GOT capacity and passed
boundary checks. Other pool types are unchanged.

### Was EventSource/EventPipe needed by Terraria?

Not for normal gameplay: these are .NET diagnostic tracing facilities, not
Terraria's gameplay event/delegate system. Framework classes such as ArrayPool
create diagnostic EventSources internally, but their implementation explicitly
supports disabling the feature when no native provider exists. That was the
platform mismatch in 36. The normal Mono error log and native `NX_PHASE`
measurements are separate and remain available. Build 37's renderer progress
confirms the tracing correction passed the prior startup blockage; it does
not yet establish a complete playable game or measured FPS.

### Build 38: enlarged precompiled specific-trampoline capacity

Published: `fna-nx-test/terraria-mono/switch/mono_nx_fna_terraria_nochroma38_aot.nro`

- Size: **948,709,121 bytes**.
- SHA-256: `963f96b6b1242d4fc18ce51f9d16e9b3032be23cbad12b23631ff7db33d4ce36`.
- Only CoreLib's native AOT object was regenerated, with the existing compiler
  and `full,interp,static,ntrampolines=65536`.
- CoreLib's managed DLL/MVID is unchanged. Its new native object still reports
  75,230/75,888 compiled methods; this is not a managed runtime update.
- All six game/helper AOT objects are byte-identical copies. The corrected
  native runtime, nine early metadata DLLs, input fix, EventSource capability,
  and previously working default graphics selection are retained.

Decoded pool counts, before → after:

| Type | Pool | Before | After |
| ---: | --- | ---: | ---: |
| 0 | Specific | 4,096 | 65,536 |
| 1 | Static RGCTX | 4,096 | 4,096 |
| 2 | IMT | 512 | 512 |
| 3 | GSharedVT argument | 512 | 512 |
| 4 | Function-pointer argument | 0 | 0 |
| 5 | Arbitrary unbox | 256 | 256 |

Each specific stub is **28 bytes of ARM64 code** plus two 8-byte GOT slots.
The actual increase is **1,720,320 code bytes + 983,040 GOT/BSS bytes =
2,703,360 bytes (2.578 MiB)** of allocated sections. The NRO file grows only
by the code amount because the additional GOT storage is zero-initialized BSS.
The larger object file also contains relocations/symbols; its size is not the
runtime memory cost.

This is intentional headroom, not a guarantee that lifetime demand is below
65,536. The bank remains finite and its exhaustion check remains active. No
allocation counter was reset, no assertion suppressed, no runtime code
generation enabled, and no metadata-only capacity patch applied.

Verification:

- ABI offsets were derived by compiling the actual `MonoAotFileInfo` source
  declaration for AArch64. Real old/new objects decode as 4,096/65,536 entries.
- Every one of the **65,536 generated stubs**, its two GOT-slot references,
  emitted code range, and GOT/BSS reservation was checked. A fake limit-only
  metadata enlargement, fake GOT size, and corruption of stub 4,096 all fail
  verification.
- The exact source `get_numerous_trampoline` allocator was extracted into a
  host boundary harness: old requests 1–4,096 pass and request 4,097 aborts;
  the new bank serves 4,097 and all 65,536 requests with unique code/GOT pairs,
  then request 65,537 aborts as intended. The host **does not execute ARM64
  stubs or reproduce a full Switch game run**.
- Final linked ELF checks independently confirm 65,536 complete stubs with
  correct relocated references to their two GOT slots. Code is RX and GOT
  storage is RW; there is no RWX segment.
- Existing checks still pass for 192,384 AOT method targets, 15,010 fallback
  sentinels, and the unchanged required game/metadata payload. The NRO retains
  16,183 files and all 15,997 Content assets.

Persistent tooling:
- `scripts/rebuild_corelib_aot.py`: targeted CoreLib rebuild/stage/verification;
  reuses the existing AOT verifier and refuses to overwrite a previous output.
- `native/tests/test_corelib_trampoline_pool.{py,c}`: real-object validation,
  adversarial copies and source-derived allocator boundaries.

Inside the established container mounts/environment, build a fresh output:

```sh
python3 /work/scripts/rebuild_corelib_aot.py \
  --base-dir /build/aot --output-dir /build/aot38 \
  --runtime-source /build/runtime-source
```

The assembler toolchain must be on PATH. Existing `/build/aot` is preserved;
do not run this on the original objects or replace the external SD corelib.
The native build uses the same options as 37 except
`MONO_NX_AOT_DIR=/build/aot38`.

Parent-executed regression:

```sh
python3 native/tests/test_corelib_trampoline_pool.py \
  --runtime-source "$HOME/.cache/terraria-switch-build/runtime-source" \
  --old-object "$HOME/.cache/terraria-switch-build/aot/System.Private.CoreLib.dll.o" \
  --new-object "$HOME/.cache/terraria-switch-build/aot38/System.Private.CoreLib.dll.o" \
  --abi "$HOME/.cache/terraria-switch-build/aot38/abi/layout.json" \
  --work-dir "$HOME/.cache/terraria-switch-build/aot38/verification"
```

Evidence: `aot38/build-manifest.json`, `trampoline-validation.json`,
`runtime-metadata-manifest.json`, `verification/run-2s0zmyry/results.json`, and
`nochroma38/{link-verification,linked-pool-verification,nro-payload,verification-summary}.json`
under the build cache.

**Hardware update:** 38 reaches gameplay and the larger trampoline bank passes
the previous startup failure. Performance measurements and the next controlled
comparison are below. Menu skipping remains deferred; the L4T ~50 FPS figure
is still a user-reported comparison target, not the Horizon result.

## Build 38 gameplay performance (2026-09-18)

The user reports that 38 works: menus are almost full speed, but in-game
rendering is roughly **1–5 FPS**, improved from sub-1 FPS interpreter gameplay.
The upload is preserved as `fna-nx-test/log38.txt` and
`~/.cache/terraria-switch-build/log38-hardware.txt`.

The log contains 55 `NX_PHASE` records. It reaches normal game/input updates,
graphics and in-game chat, rather than another startup assertion. First
measured Tick/Update/Draw offsets are 25.034/25.055/25.211 seconds after native
registration; **these are not exact menu-ready timestamps**. Early intervals
include long asset/world-loading work inside Draw and are not steady FPS tests.

### What the frame measurements show

A steady gameplay range (24 windows ending 176.880–296.759s; 124.941 seconds
of window time) gives:

| Measurement | Observed |
| --- | ---: |
| Game.Update calls/sec | 59.044 |
| Game.Draw calls/sec | 3.266 |
| Mean time per Update | 5.767 ms |
| Mean time per Draw | 50.971 ms |
| Mean Tick time outside Update/Draw | 150.934 ms |
| Update share of measured Tick time | 34.06% |
| Draw share | 16.65% |
| Remaining/unseparated share | 49.30% |

FNA performs fixed-step catch-up updates before drawing; some ticks contain
29 updates. This explains why Update call rate and Draw rate differ. It does
not justify calling the game simulation “3 FPS”, nor does it prove every
Update call is a distinct full world step. No timestep/catch-up/AI reduction
is being used to manufacture an FPS improvement.

A menu-like window ending at 78.048s records 60.092 Draw calls/sec and 10.937 ms
per Draw. Late intervals have low Update/Draw costs but substantial remaining
Tick time too. These call rates are not guaranteed unique presented-frame
rates when frame skip is active.

The largest missing measurement is **outside the two existing hooks**:
polling, managed event dispatch, BeginDraw/EndDraw/presentation, pacing and
native hook boundaries can contribute. The current log does **not** establish
whether GPU/vblank waiting, event polling, or another part dominates that
residual. Calling all of it “GPU time” would be unjustified.

For the nearby explicit 176.880–296.759s interval, 753 recorded minor GCs sum
to 1,621.94 ms stop-the-world, with maximum 2.77 ms. That recorded pause total
does not account for approximately half the frame time. It does not measure
all background GC work or diagnostic overhead.

### Logging is still enabled in this hardware run

24,146 of 24,541 log lines are `Mono debug`. The Mono configuration path only
installs that all/debug handler when `runtime_logging` is true. The provided
workshop config is false, but the uploaded run is not a quiet benchmark.
The source file writer is buffered; there is no basis for asserting a
per-line fsync cost or claiming logging alone explains the slowdown.

Builds 39/40 print the effective settings after loading the external config:

```text
NX_CONFIG file=/mono/config.ini logging=1 runtime_logging=0
```

If the last value is 1, they also print a warning that timings include verbose
diagnostic overhead. The NRO does not silently override the user's setting.
Copy/edit the actual **SD:/mono/config.ini**, keeping `logging=true` and
`runtime_logging=false`, before comparing performance. No need to replace the
existing runtime DLLs/ICU/saves or the already validated managed payload.

### Builds 39/40: native frame-cost and GL profile comparison

Both use the same AOT objects, enlarged trampoline bank, source-fixed runtime,
EventSource capability, input buffering and game payload as 38. They add two
sparse native measurements without rebuilding or changing FNA/game IL:

- `poll=count/total sum/max=...ms`: inclusive wall time in `SDL_PollEvent`,
  including native event/input pumping; excludes managed event dispatch.
- `swap=count/total sum/max=...ms`: inclusive wall time in
  `FNA3D_SwapBuffers`, including presentation, synchronization/waiting and
  driver work; **not GPU-only time**.

GNU linker wrapping redirects the actual function-address references returned
by the static SDL2/FNA3D shims. Each wrapper forwards all arguments and calls
the real API once, preserving return values. No per-call allocations/logging
were added. Tick/Update/Draw hooks and game scheduling are unchanged.
The same ≥5-second end-Tick reporting cadence and final shutdown record remain.

These native counts include completed calls outside Tick and inclusive callback
reentry; totals may overlap existing phases or each other. Do not sum them as
disjoint categories or blindly subtract all native time from the Tick residual.

| NRO in `terraria-mono/switch/` | Graphics request | Purpose |
| --- | --- | --- |
| `mono_nx_fna_terraria_nochroma39_aot.nro` | Existing SDL default/ES path | Control with poll/presentation timings |
| `mono_nx_fna_terraria_nochroma40_aot_glcompat.nro` | Desktop GLCompatibility | Same measurements, compare the L4T rendering choice |

Both are **948,709,121 bytes**. Checksums:
- 39: `93099ddaf66cbdede5a053ace5278331a9c04b82c0eaaad097eff5f1073524bb`
- 40: `acc087892c27cf839deb68659a212ab4e634d6e8f92a14bb29f3d8d437566734`

Normal and ASan/UBSan host scenarios passed exact/null argument forwarding,
Poll return values -7/0/1, once-only calls, accounting and reentry, interval/
lifetime totals, report throttling and prior input/worker behavior. Switch SDK
function-type checks compile cleanly. Actual AArch64 shim relocations resolve
to the wrappers, and final linked wrappers target the original APIs rather
than recursively wrapping themselves. Final AOT-target/protection and NRO
payload checks pass; all 16,183 files / 15,997 assets are retained.

**Hardware update:** both 39 and 40 reached gameplay with quiet Mono logging.
Both remain slow; the user reports 40 slightly faster. The requested desktop
GLCompatibility profile was selected in 40. Native polling/presentation do not
account for the large residual frame cost; detailed measurements and the next
controlled optimization follow below. Preserve these logs as comparison data.

Evidence: `log38-phase-analysis.json`, `frame-comparison-39-40.json`,
`native-timing-host/verification.json` and its host/SDK proof logs, plus each
new build's `native-build.log`, `link-verification.json` and `nro-payload.json`
under `~/.cache/terraria-switch-build/`. Menu skipping remains deferred.


## Builds 39/40 hardware comparison (2026-09-18)

Inputs: `fna-nx-test/log39.txt` and `log40.txt`. User report: both remain slow,
with 40 slightly faster. These are not synchronized, scene-identical benchmarks;
the logs do not establish a controlled percentage gain from changing GL profile.

| Setting | Build 39 | Build 40 |
| --- | --- | --- |
| Effective config | `logging=1 runtime_logging=0` | Same |
| Renderer | NV120 / nouveau | Same |
| OpenGL | ES 3.2 Mesa 20.1.0-rc3 | 4.3 Compatibility Profile, same Mesa |
| MojoShader | `glsles3` | `glsl120` |

Representative steady-world windows in 40 span 227.293–242.959s:

| Measurement | Observed |
| --- | ---: |
| Update calls/sec | 59.939 |
| Draw calls/sec | 3.064 |
| Mean Update | 5.375 ms |
| Mean Draw | 57.594 ms |
| Tick minus Update minus Draw, per Tick | 163.501 ms |
| Native Poll total divided by Tick count | 0.029 ms |
| Native Swap total divided by Tick count | 0.443 ms |

Poll/Swap are inclusive measurements, not disjoint phases. They are displayed
separately, **not subtracted from the Tick residual**. Their sub-millisecond
costs cannot account for its tens/hundreds of milliseconds. Draw-call rate is
not necessarily a unique presented-frame rate, and Update-call rate is not
proof that every call performs a full world step.

The residual grows gradually during gameplay and falls after returning to
menus. Recovered-menu windows retain approximately 25–27 ms/Tick outside the
Update/Draw hooks. During the gameplay ramps, residual/Tick versus mean
cumulative Tick index is almost linear:

- 39, 13 windows ending 151.140–212.921s: +0.46255 ms per additional frame,
  R²=0.99816.
- 40, 14 windows ending 165.803–232.563s: +0.48044 ms/frame, R²=0.99871;
  the later residual plateaus around 164 ms/Tick.

### Source-backed candidate, not a hardware profile attribution

Exact staged IL shows this unconditional per-frame path:

`Main.EndDraw -> DetailedFPS.StartNextFrame -> TimeLogger.StartNextFrame
-> each TimeLogData.DataSeries.StartNextFrame`.

`DetailedFPS.FrameCount` is **300**. Each series rescans its counted history,
copies used values into a shared scratch array, sorts them with `Array.Sort<int>`,
and computes previous/median/p90/max. This runs even without drawing the debug
overlay. It occurs after the existing Draw measurement has ended.

**[INFERENCE]** The 300-frame histories filling/draining are a strong candidate
for the measured ramp/recovery. The correlation and actual unnecessary work
justify optimizing this path, but the original TimeLogger duration has not
been sampled directly on Switch. Do not label all residual time as TimeLogger.

The separate executable pacing reproduction did **not** reproduce accumulating
negative debt or tens-of-seconds recovery. Actual IL clamps the accumulator to
0–3 target frames. Synthetic active/fixed/inactive-mode cases remained stable;
an 80 ms spike recovered within five modeled frames. Per-delta timestamp
truncation exists, including a synthetic sub-100 ns zero-credit case, but these
clock/workload parameters are not Switch measurements. Actual hardware
FrameSkipMode, IsActive and Stopwatch.Frequency were not sampled. No pacing
change was made from that hypothesis.

Evidence under `~/.cache/terraria-switch-build/`:
`hardware-comparison-39-40.json`, `hardware-comparison-39-40-aggregates.json`,
`logs39-40-phase-analysis.json`, exact TimeLogger/DetailedFPS IL dumps, and
`pacing-repro/{run.py,results.json,repro.log}`. Parent reran the pacing proof.

## Build 41 incremental timing history (2026-09-18)

Build 41 keeps 40's GLCompatibility profile, native instrumentation, input fix,
simulation/frame-skip logic, source-fixed runtime and 65,536-entry CoreLib bank.
The game change is confined to **private diagnostic-history bookkeeping**.
It does not disable TimeLogger, reduce its 300-frame window, remove statistics,
or skip gameplay work.

`scripts/patch_time_logger/` contains the guarded Cecil patcher and executable
comparison. Each DataSeries maintains an ordered history with integer binary
insertion/removal and overlapping `Array.Copy`, instead of scanning and sorting
the complete history every frame. Statistics are published before expiring the
oldest slot, exactly as before. Reset clears the new ordered count but preserves
the original arrays' unusual stale-slot behavior.

- Changed: `DataSeries.StartNextFrame`, plus ordered-state initialization/reset
  in its instance constructor and `Reset`.
- Added: private `_ordered`, `_orderedCount`, `OrderedInsert`, `OrderedRemove`.
- Removed: obsolete static `_sort` and its allocation-only static constructor.
- Unchanged IL: `Add`, `Quantile`, both getters and unrelated game methods.
  Quantile float rounding and spike behavior are preserved, not rewritten.
- No new runtime assembly dependency. All 21 assembly references are preserved.

### Executed verification

The probe executes the **actual extracted original IL**, serialized patched IL,
and original FNA Lerp, rather than a handwritten approximation of the baseline.
It compares every original scalar, every history-array element and identity,
both getters (including float bits), and spike results after transitions.

- **997,503 transitions**, including 548,481 frame advances, 447,584 Add/spike
  checks and 1,438 resets; parent independently reran acceptance successfully.
- Empty/partial/full/wrapped and interleaved histories; sparse/duplicate/zero/
  negative samples; multiple Adds; integer extremes/overflow; percentile
  rounding; every reset occupancy 0–300 and cursor 0–299.
- 21,037 unrelated method bodies, 32,057 fields, 2,957 types, 100 managed
  resources and 9 native PE-resource payloads preserved.
- Repeated guarded runs produce byte-identical game/probe outputs. Modified
  game/FNA inputs and an already-patched input are rejected without an output.
- Parent's .NET 9.0.20 x64 JIT benchmarks: **5.3–27.3× faster steady diagnostic
  bookkeeping** across dense/sparse/mixed 128/512-series cases. These are host
  microbenchmarks, **not Switch FPS**. Both versions allocate zero bytes in the
  measured frame loops; the new constructor costs +1,232 bytes per series on
  that host. Tiered compilation was disabled; rounds alternate execution order.

The guarded patcher changes Terraria's MVID. All six game/helper AOT modules
were rebuilt against the exact patched image, retaining the existing enlarged
CoreLib object. The five changed/added methods have native AArch64 symbols.
Final ELF verification passes **192,384 method targets**, 15,010 interpreter
fallback sentinels, read-only/non-executable method-table memory and no RWX
segment. All 85 name/MVID bindings resolve. Packaged Terraria SHA matches the
accepted image; all 16,183 files / 15,997 Content assets remain.

Build-environment finding: overriding the container entrypoint bypasses its
devkitA64 PATH setup. The first attempt compiled IL but could not find
`aarch64-none-elf-as`, including for unchanged helpers. Adding
`/opt/devkitpro/devkitA64/bin` to PATH fixed the build; no IL workaround was used.

### Reproduction and artifact identity

Inside the existing build container (`/work` = project, `/build` = cache,
`/mono-nx` = read-only SDK), use a **fresh output directory**:

```bash
python3 /work/scripts/patch_time_logger/run.py \
  --input /build/aot/runtime-romfs/Terraria.exe \
  --fna /build/aot/runtime-romfs/FNA.dll \
  --output /build/timelogger-check
```

This runs acceptance twice plus rejection guards. Its accepted game is
`/build/timelogger-check/first/Terraria.exe`. The input is the reviewed patched
build-40 game, **not an arbitrary fresh GOG executable**. Preserve the original
RomFS; stage the accepted image in a separate copy before AOT compilation.
Build 41 used:

```bash
export PATH=/opt/devkitpro/devkitA64/bin:/opt/devkitpro/tools/bin:$PATH
python3 /work/scripts/compile_terraria_aot.py \
  --game-dir /build/aot41/runtime-romfs --output-dir /build/aot41 \
  --corelib-object /build/aot41/System.Private.CoreLib.dll.o \
  --corelib-log /build/aot38/logs/System.Private.CoreLib.dll.log \
  --no-inline-assembly Terraria --jobs 3
```

The native relink uses `MONO_NX_USE_ROMFS=1`, `MONO_NX_USE_AOT=1`,
`MONO_NX_AOT_DIR=/build/aot41`, `MONO_NX_GL_COMPAT=1`, and
`MONO_NX_RUNTIME_LIBRARY=/build/runtime-fix/libmonosgen-2.0.a` with the existing
mono-nx/ICU/FNA library environment. Do not reuse the old Terraria AOT object.

- NRO: `fna-nx-test/terraria-mono/switch/mono_nx_fna_terraria_nochroma41_aot_glcompat.nro`
- Size: **948,702,836 bytes**.
- NRO SHA256: `b4af968ebd5b7e7854a313c3586b32466a588c088cf7c035915d10d99fb2951f`.
- Embedded Terraria SHA256: `34e3b59f5736ec5ea3d590f87cf9d09aa8f7e8e67ed16d7f7f59b8df8d3344f3`.
- Terraria MVID: `7b2a493d-1dc3-66fe-a3d1-620d76453f0c`.

Proof records: `timelogger-opt/parent-check/{acceptance,behavior,benchmark}.json`,
`timelogger-opt/repro/runner-results.json`, `timelogger-opt/repro/first/acceptance.json`,
`aot41/{build-manifest,runtime-metadata-manifest}.json`, `aot41/Terraria-symbols.log`,
and `nochroma41/{verification-summary,link-verification,nro-payload}.json` under
the build cache. Only intended patcher sources remain in its project directory.

### Build 41 hardware result (2026-09-18)

`fna-nx-test/log41.txt` contains 106 timing records across 599.553 seconds and
ends with normal application shutdown. It confirms native Terraria AOT entry,
quiet Mono logging, and the same NV120 / GL4.3 Compatibility / `glsl120` profile.
No gameplay crash/assertion is observed. The user reports a significant gain,
estimates 15–20 FPS without a counter, and confirms **Frame Skip On for all runs**.

| Measurement | Build 40 representative world | Build 41 sustained world |
| --- | ---: | ---: |
| Interval | 227.293–242.959s | 170.902–463.100s |
| Draw calls/sec | 3.064 | 13.539 |
| Update calls/sec | 59.939 | 59.914 |
| Mean Update | 5.375 ms | 6.435 ms |
| Mean Draw | 57.594 ms | 44.474 ms |
| Tick minus Update minus Draw, per Tick | 163.501 ms | 0.881 ms |

These are different live gameplay intervals, not a frame-locked identical-scene
benchmark. Nevertheless, the intended residual reduction is directly visible:
**99.46% lower**, sustained over 3,956 Draw calls after history fill. The former
300-frame growth/recovery pattern is absent. This is hardware evidence that the
history optimization removes the dominant previous overhead, not merely a host
microbenchmark. It does not attribute every remaining millisecond to one subsystem.

A later 55.434-second interval (367.389–422.823s) records **15.875 Draw calls/sec**;
its 5-second windows range 14.45–20.37. A middle interval is nearer 12.8. Treat
these as Draw-call rates, not a guarantee of unique presented FPS with frame skip.
The user's estimate is consistent with the later interval, not every scene.

Menus recover quickly: the first full return window reaches 57.55 Draw/sec,
then 24 windows average **59.936 Draw/sec**. Remaining menu Tick time includes
normal frame pacing; it must not be treated automatically as wasted work.

The remaining sustained world cost is **60.24% inside Draw**, **38.57% inside
Update**, and **1.19% outside those hooks**. Native Poll/Swap remain small and
inclusive. Draw includes managed/render-driver work and possible waits; this
is not a GPU-only or CPU-only breakdown. The next optimization must target
actual Update/Draw work, not reduce simulation frequency or chase the removed
history overhead. User-reported Frame Skip On also excludes the Subtle-only
EndDraw pacing hypothesis, subject to the lack of native setting readback.

The separate AOT compiler investigation concerns a **historical build-time
failure on the build machine**, not this Switch run. The existing compiler
workaround disables inlining for all Terraria. Narrowing that workaround is
being tested separately; failed experimental compiles do not replace build 41.

Evidence: `~/.cache/terraria-switch-build/hardware-build41-analysis.json` records
window selections, cumulative-count validation, raw phases and this log's SHA256:
`15723918ccc50acc821d8febb2f2511af4a860efce8d20901489fdb4c3d218eb`.
Keep 41 as the working fallback. Menu-entry skipping remains deferred.


## Build 42 default AOT inlining comparison (2026-09-18)

**This addresses an old build-machine compiler defect, not a gameplay crash.**
Build 41's hardware log ends normally. Earlier AOT bring-up disabled inlining
for the entire Terraria assembly because Mono's compiler SIGSEGVed while
inlining desktop WinForms accessors. With the history overhead removed, the
remaining cost is inside Update/Draw; normal compiler optimization is now worth
a controlled comparison. No Switch improvement from 42 is claimed yet.

### Narrowed workaround and rejected experiments

The default-inlining compile of the exact 41 image reproduces the old failure
in `Main.SetDisplayMode`, while inlining `Form.get_FormBorderStyle`. GDB confirms
the inlined basic-block chain reaches null before its end block at
`method-to-ir.c:12389`. The initial theory that inlining the throw-only
`Terraria.Control.FromHandle` caused it was insufficient: preventing that
callee's inlining still failed. Its exact exception is `NotImplementedException`,
not `NotSupportedException`; the method is unchanged in the final build.

`MethodImpl.NoOptimization` on **the caller** prevents nested inlining in this
Mono compiler (`method-to-ir.c:6439–6440`, checked by `is_inlineable` at 4046–4047).
Guarding `SetDisplayMode` passed that failure, then exposed the same compiler
defect later in `WindowStateController.TryMovingToScreen`, inlining WinForms
`Control.get_Bounds`. Both failures were inspected in the debugger.

The final guarded metadata change applies only to:

| Caller | MethodDef token | ImplFlags file offset |
| --- | --- | ---: |
| `Terraria.Main.SetDisplayMode(int,int,bool)` | `0x06001116` | 24372908 |
| `Terraria.Graphics.WindowStateController.TryMovingToScreen(string)` | `0x06002B42` | 24493508 |

Each preserves its existing flags and sets `NoOptimization` (`0x0040`). This
is not `NoInlining` on the caller, which would merely prevent the caller itself
from being inlined elsewhere. **All six game/helper AOT modules now compile
with default optimization, without assembly-wide `--optimize=-inline`.**

### Exact change and verification

`scripts/patch_aot_inlining/` uses framework PEReader/MetadataReader APIs to
change those two MethodDef fields and a deterministic module MVID in place.
There is no Cecil reserialization, runtime dependency, new game instruction,
frame-skip change, display-behavior change or exception suppression.

- **18 bytes differ** from the accepted build-41 game: one changed flag byte
  per caller plus 16 MVID bytes. Every other byte, all IL, signatures, resources
  and file length are identical. Metadata and on-disk bytes are read back.
- Parent independently accepted the same image. Repeated image/manifest output
  is byte-identical; **23 rejection guards** pass, including separate wrong
  method/body/flag cases for each caller, signing, corruption, existing outputs
  and path/link aliases. The original and accepted images remain unchanged.
- Full AOT succeeds: Terraria **52,792 / 52,816** methods; all seven registered
  modules have an available exact name/MVID dependency closure (85 bindings).
- Final ELF checks pass **192,304 native method targets** and **15,010 fallback
  sentinels**, with read-only/non-executable method-table memory and no RWX
  segment. The old cached verifier assumed the previous aggregate 192,384;
  per-entry checks passed, and final counts were validated per-module against
  the newly compiled object manifests rather than that historical constant.
- NRO game bytes match the accepted metadata patch. Other required managed
  payloads, native runtime, FNA/SDL/input code, GLCompatibility profile and
  65,536-entry CoreLib object are retained. All 16,183 files / 15,997 assets remain.

No changes to the Mono compiler or Switch runtime were needed for this cutover.
Failed prototype outputs remain only as build-cache evidence; no failed NRO
was published and no unsupported prototype option remains in the patcher.

### Reproduction and next test

Inside the existing container, with a fresh output directory:

```bash
python3 /work/scripts/patch_aot_inlining/run.py \
  --output /build/inline42-check \
  --reference /build/inline41/window-guards/accepted/Terraria.exe
```

The runner's pinned input is build 41, not an arbitrary game installation.
Stage the accepted image in a separate validated RomFS. Build 42 used
`/build/aot42-windows/runtime-romfs`, output `/build/aot42-windows`, the unchanged
65536 CoreLib object and the existing compiler script **without**
`--no-inline-assembly Terraria`. Keep the devkitA64 compiler directory on PATH.
The native relink otherwise uses 41's flags and source-fixed runtime library.

- NRO: `fna-nx-test/terraria-mono/switch/mono_nx_fna_terraria_nochroma42_aot_glcompat.nro`
- Size: **950,591,092 bytes**.
- NRO SHA256: `47e8bb11814e021c0533a04b8a3002a2942e410966eb2d3d7d209350c0a1a5a2`.
- Embedded Terraria SHA256: `d4c767a545d5f1a4cacbb6b0d3b16be68b6d1b9af1962d0cd439c3b7946b7298`.
- Terraria MVID: `2a9040da-3f4b-844f-a14b-3056cdda95ba`.

**Hardware update:** 42 boots, reaches gameplay, returns to menus and shuts down
normally. The user reports another FPS improvement. Keep 42 as the current
working baseline, with Frame Skip On and the existing SD runtime/configuration.
Measured results and the next profiling target follow below; no newer NRO was
produced by this analysis.

Evidence under `~/.cache/terraria-switch-build/`: `inline41/{baseline-evidence.json,
baseline-compile.log,narrow-gdb.log,caller-gdb.log}`, `inline41/window-parent-check/manifest.json`,
`inline41/window-guards/repro/runner-results.json`, `aot42-windows/{build-manifest,
runtime-metadata-manifest}.json`, and `nochroma42/{verification-summary,
link-verification,nro-payload}.json`. Dedicated debugger sessions were stopped.


## Build 42 hardware results and remaining optimization room (2026-09-18)

`fna-nx-test/log42.txt` contains **115 timing records across 634.579 seconds**.
Cumulative Tick/Update/Draw counts agree with the final record. Native AOT entry,
`logging=1 runtime_logging=0`, NV120, GL4.3 Compatibility and `glsl120` are
confirmed again. The log ends with normal shutdown; no crash/assertion is seen.
Frame Skip remains On per the user's confirmation for this test series.

### Measured improvement

| Interval | Seconds | Draw calls/sec | Update calls/sec | Mean Update | Mean Draw | Other/Tick |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Early steady world, 160.179–281.064s | 120.886 | 18.795 | 59.866 | 5.432 ms | 35.054 ms | 0.826 ms |
| Later mixed world, 286.111–507.526s | 221.412 | 17.497 | 59.947 | 4.211 ms | 41.900 ms | 0.801 ms |
| Combined world selection, 140.067–507.526s | 367.457 | 17.931 | 59.955 | 4.690 ms | 39.252 ms | 0.809 ms |
| Settled menus, 517.540–627.803s | 110.262 | 59.912 | 60.003 | 1.213 ms | 14.721 ms | 0.743 ms |

The early steady windows range **17.05–19.92 Draw/sec**. The combined world
selection averages 17.93 versus 41's earlier 13.54, a 32.4% higher observed
Draw-call rate. **This is not a controlled percentage speedup**: these are
different live scenes/intervals, and later 42 intervals alternate between
roughly 1.1 ms and 5–6 ms per Update without logging pause/inventory/map state.
Those low-cost intervals must not be labelled paused from timing alone.

Draw counts match buffer-presentation counts in these selections, supporting
their use as approximate rendered FPS. Frame skip and possible repeated images
still prevent treating them as a direct measurement of distinct screen images.
The first full menu-return window is already 59.45 Draw/sec; no old gradual
history-fill/drain slowdown reappears.

### Where further optimization could matter

In the early steady window, **65.91% of measured Tick time is inside Draw**,
**32.53% inside Update**, and only **1.55% outside those hooks**. Native Poll
cost is 0.061 ms/Tick and Swap 0.361 ms/Tick. These native metrics are inclusive
and remain separate, not subtracted as disjoint categories. The large previous
unexplained overhead is not the next target.

Rendering is therefore the priority, followed by expensive update subsystems.
The current log cannot distinguish CPU draw preparation, GPU/driver stalls,
tile/render-target work, lighting, backgrounds or UI. Those are candidates to
measure—not established bottlenecks. No quality, resolution, AI-frequency,
simulation-timestep or clock changes were made to obtain these gains.

**[INFERENCE: simplified time-budget model]** If 60 Update calls/sec continue
costing the observed 5.432 ms each, ignoring the small residual:

- 30 rendered FPS leaves about **22.47 ms per Draw**, versus the current
  35.05 ms—approximately a 36% Draw-cost reduction, or a combination of Draw
  and Update savings.
- 60 rendered FPS leaves about **11.23 ms per Draw** with unchanged Update
  cost—approximately a 68% Draw-cost reduction. It needs substantially more
  work than another small compiler switch.

These are workload/serial-accounting estimates, not achievable-FPS guarantees;
costs, contention and scene complexity may change with frame rate. Room for
optimization is supported by the measured costs, not a promise of 30/60 FPS.

### Concrete next profiling step

Export sparse completed-frame subsystem summaries while retaining 42's
working behavior and improved history algorithm. Existing TimeLogger fields
cover tile draw/render/flush variants, lighting, backgrounds, entities and UI;
field existence alone does not establish active coverage or cost.

Actual current-build IL confirms:

- `Main.Draw`: `EnsureRenderTargetContent` at IL_001d, `DoDraw` at IL_0024,
  and `TransferCompletedAssets` at IL_0040. The first and last are outside
  `TimeLogger.TotalDraw` and need separate timing if closing the outer gap.
- `Main.DoDraw`: existing measured entries include `PrepareRequests` (IL_0127),
  `MapSectionUpdate` (IL_04b3), `SkyBackground` (IL_0c43), `SunMoonStars`
  (IL_0cc7), `SurfaceBackground` (IL_0dad), `Interface` (IL_0e99/IL_1b12),
  `DrawFullscreenMap` (IL_0ff6), and `Overlays` (IL_1561).
- `TotalDraw` spans from IL_0022 to its AddTime at IL_1c44, so subsystem
  timers are **nested/overlapping**, not independent values to sum.
- `TimeLogData.AddTime` uses `StartTimestamp.ElapsedTicks`: actual Stopwatch
  timestamp difference, narrowed to Int32. Export must report the real
  `Stopwatch.Frequency`; do not assume milliseconds or a particular clock rate.

A profiler should sample used current-frame slots before they are advanced/
cleared, not blindly aggregate sticky `DataSeries.previous` values. Capture
game/menu/pause/map/inventory state, render count, and Update calls per Draw;
multiple updates are accumulated into one rendered-frame history slot.
Keep interval aggregation bounded and sparse—do not restore per-frame full
history sorting or print a large log every frame. Verify each chosen subsystem
callsite before interpreting its label. Build 43 implements this measurement
proposal below; hardware profiling results are still pending.

Evidence under `~/.cache/terraria-switch-build/`:
`hardware-build42-analysis.json`, `hardware42-Main-Draw.il`,
`hardware42-Main-DoDraw.il`, `hardware42-TimeLogData-AddTime.il`,
`hardware42-StartTimestamp-ElapsedTicks.il`, and the preserved earlier IL dumps.
Log SHA256: `d3d4f095a22cd9e7868a5e6aba03eda4abf3e3cc1cfc393ee178cd27e41d57bc`.

## Build 43 measurement-only frame profiler (2026-09-18)

Build 43 is based on the exact hardware-tested 42 payload. It keeps graphics
quality/profile, assets, input buffering, simulation/frame-skip behavior,
default AOT optimization, the two window-method compiler guards and the
65,536-entry CoreLib bank. It adds observations, not a gameplay optimization.

### High-level explanation: how it works

The profiler answers **where the time in a frame goes**, rather than merely
showing an FPS number. It reuses the stopwatch measurements already built into
Terraria and adds measurements around a few outer drawing steps that those
timers miss, such as preparing render-target content and transferring assets.
At frame boundaries it collects finished measurements, groups them by game
state, and writes a summary roughly every five seconds. Keeping gameplay,
menus, inventory and fullscreen-map samples separate prevents a cheap menu
frame from making an expensive world frame look faster than it is.

The output identifies which parts deserve investigation. It does not by itself
explain why a part is slow, or prove that its time is CPU work rather than a
graphics-driver/GPU wait. Timers can overlap, and profiling has some overhead.

### What the results will be used for

**The investigation is not limited to the compiler.** The next fix should be
made in the layer responsible for the measured cost:

- **Terraria/game-side code:** if tile drawing, lighting, render-target refresh
  or another subsystem dominates, inspect that code for redundant work,
  avoidable rebuilding, allocations or missed opportunities to reuse results.
- **FNA/native graphics integration:** if batch flushing, texture transfers or
  driver synchronization dominates, investigate that path and distinguish CPU
  submission cost from GPU waiting before changing it.
- **Compiler/Mono runtime:** revisit these only if evidence points to generated
  code, interpreter fallback, GC or another runtime cost—not simply because
  compiler changes helped build42.

These are possible responses to measurements, not findings that any named
subsystem is currently faulty. The workflow is: identify a dominant cost,
inspect its implementation, make a targeted change, and repeat comparable
hardware measurements. Preserve gameplay behavior and visual quality; reducing
simulation/AI frequency or graphics quality is not the default optimization.
Verify any claimed FPS gain in a build without the temporary profiling hooks,
or otherwise account explicitly for their measurement overhead.

### How game edits reach the NRO

The user-owned Terraria.exe is a managed .NET assembly: method bodies contain
IL and type/method metadata, not just opaque native instructions. This port
does not require reconstructing and recompiling the entire original game from
decompiled C# source. Instead, a reproducible tool inspects named methods and
applies a narrowly scoped patch to a copy of the reviewed assembly.

- For behavior/algorithm changes, a helper can be written and compiled in C#,
  then Mono.Cecil transplants its IL with correctly rebound types/members, or
  inserts a small observer/compatibility splice into an existing method.
  Builds41 and43 use guarded IL changes of this kind.
- For metadata-only changes, a raw metadata patch may suffice.42 changed
  two method optimization flags and the module identity without changing IL.
- FNA/native libraries with source are edited and rebuilt through their own
  normal toolchains when those layers are the measured target.

Each patch checks the input version/hash and expected method shape, preserves
unrelated code/resources, and has behavior/metadata checks appropriate to the
change. Original inputs and previous working NROs remain available. A new game
version is not silently accepted by a patch written for different bytes.

The current iterative build flow is:

```text
verified staged game -> guarded patch -> accepted Terraria.exe
                                         |              |
                                         v              v
                                  ARM64 AOT objects   RomFS copy + assets
                                         |              |
                                         +------+-------+
                                                v
                                    native runtime/FNA link -> NRO
```

`scripts/compile_terraria_aot.py` compiles the exact staged patched assembly,
extracts embedded dependencies, verifies input hashes and dependency name/MVID
bindings, and emits the AOT registration header only for a complete build.
The native interpreter Makefile links these objects and embeds the same managed
payload and Content directory into RomFS. Final checks compare embedded hashes
with compiler inputs; old native code must not be paired with new managed bytes.

The original `scripts/pack_terraria_romfs.py` is a fresh-install/demo staging
helper, **not an automatic patch orchestrator**. It recreates RomFS from its
input installation and its plain build invocation does not select the current
AOT/profile flags. Running it over a validated patched staging tree can replace
the patched game or omit needed overlays. Current iterations use separate,
validated per-build staging plus the explicit AOT/native build instead.
The packager does not itself understand or optimize Terraria methods.

### What it records

- Every registered TimeLogger metric with observed current-frame data,
  including tile/render-target work, lighting, backgrounds, entities, UI/map,
  update subsystems, tile/wall batch flushes and batching counters.
- Separate outer `Main.Draw` regions: `EnsureRenderTargetContent`, `DoDraw`,
  optional `OnPostDraw`, and `TransferCompletedAssets`. It also reports gross
  Draw-body time and a remainder excluding measured observers.
- Draw-begin state groups: `gameMenu=1`, `gamePaused=2`, `playerInventory=4`,
  `mapFullscreen=8`. Cohort ID is these bits plus 16 times the sample-time
  A/B series. Auxiliary fields include activity/autopause counts, frame-skip
  mode range, render-count range/render-now count, and Update calls per frame.
- State-change observations at stage completion/end/frame boundary. These
  are sampled changes, not continuous tracking of every state transition.

The exporter reads current `used[next]` / `values[next]` slots **before** queued
callbacks, A/B switching and original history advancement. It does not use
sticky `previous` values or restore full-history sorting. Original Reset leaves
backing arrays uncleared; an observer-only dirty flag excludes used reset-dirty
samples until successful original advancement clears the next slot. This
conservatively excludes legitimate writes between Reset and that advancement,
and explicitly reports the exclusions rather than inventing fresh samples.

Exactly one completed Draw per observed epoch is required. Partial draws,
repeated-draw epochs, no-draw boundaries, invalid storage and unregistered data
are diagnosed separately. Original guard returns, call order/identity and game
exceptions are preserved. A final partial report is emitted only after normal
return from `Game.Run`, not after an exceptional exit.

### Reading the log

`NX_PROFILE BEGIN/STATUS/OVERHEAD/STATE/METRIC/END` blocks are emitted no more
often than every **5 seconds**, plus the normal-exit flush. Unused metric rows
are omitted; used zero values are retained. The registry is bounded at 512
entries; overflow is explicit in status counters, never silently truncated.

Time rows report `unit=ms` using the actual logged `Stopwatch.Frequency`;
batching counters report `unit=count`, not milliseconds or FPS. Rows include
frames, used samples, raw/valid totals, mean-per-frame, mean-per-valid-used-frame,
maximum and invalid/reset exclusion counts. Negative original Int32 samples
remain visible but are excluded from valid averages; no-data averages are null.
The original unchecked Int32 timer representation cannot detect positive wraps
after a sufficiently long interval. Outer stage measurements use Int64 ticks.

**Do not add all metric rows.** TimeLogger totals/subsystems overlap and may
include worker or graphics-driver waits. The four outer sibling regions are
separate; `outer.DoDraw` contains many TimeLogger rows. Native `poll`/`swap`
remain inclusive measurements in `NX_PHASE`, not additive GPU-only phases.

`OVERHEAD` and report footer measurements cover timed observer/capture/report
regions. They do **not** cover every instruction: report-footer output and
array clearing are excluded. Treat them as scoped/lower-bound measurements,
not proof of zero profiler overhead. Reporting can affect FPS and cadence;
43 is for hotspot attribution, not a clean 42-versus-43 speed benchmark.

### Executed verification and encoding correction

`scripts/patch_frame_profile/` contains the runtime template, guarded injection,
serialized-hook fixtures, preservation checks, target-reference/raw-signature
audits and reproducible runner. Eight original methods receive observer-only
splices: Main.Update/Draw, both TimeLogger factories, TimeLogger.StartNextFrame,
DataSeries.Reset/StartNextFrame, and Program.RunGame. A nested profiler type,
23 observer methods and observer fields are added. No new assembly reference
or custom runtime DLL is needed; only the accepted Terraria.exe is staged.

- **67 behavior checks** execute serialized hooks or extracted original/patched
  IL: call order and exceptions, early returns, all16 state combinations,
  counters/time units, sparse/zero/negative values, multiple updates, A/B/queued
  resets, repeated/partial/no draws, registry overflow, reporting failures,
  cadence, label escaping and normal-only final flush.
- Parent independently accepted the same output. Its 20,000-frame host fixture
  with512 registry entries, two updates/frame and sparse fixture writes allocates
  **0 bytes in the measured loop**, averaging29.56 microseconds/frame on that
  run. The fixture includes population/advancement work. This is not Switch
  profiler overhead or a hardware performance claim.
- 21,034 unrelated method bodies,32,059 original fields,2,957 original types,
 100 managed resources,21 assembly references and native PE resource payloads
  are preserved. The42 compiler guards remain set. Target SDK audit resolves
 18 emitted BCL members against the actual CoreLib/runtime, not host fallback.
- An early draft incorrectly encoded primitives as named CLASS/VALUETYPE
  references. Host fixture reconstruction had normalized them, but the Switch
  AOT compiler rejected profiler signatures. That candidate was **not shipped**.
  The mapper now uses target.TypeSystem primitives, an independent reader
  checks115 raw method/field/member/local signatures, the probe refuses such
  normalization, and a malformed CLASS-System.Void fixture is rejected.
- Corrected AOT compiles Terraria **52,818/52,842** methods with the same24
  fallbacks as42. All23 added observer methods have native AArch64 symbols.
  Final ELF checks validate192,330 method targets,15,010 fallback sentinels,
  read-only/non-executable method tables and no RWX segment.85 name/MVID bindings
  resolve. The NRO contains the exact accepted game and all16,183 files /
 15,997 Content assets; other required managed payloads match42 byte-for-byte.

Reproduce inside the existing container with a **fresh** output directory:

```bash
python3 /work/scripts/patch_frame_profile/run.py \
  --input /build/aot42-windows/runtime-romfs/Terraria.exe \
  --fna /build/aot42-windows/runtime-romfs/FNA.dll \
  --output /build/profile43-check
```

The pinned input is build42, not an arbitrary fresh installation. Never copy
host fixture DLLs from acceptance output into RomFS.43's actual AOT directory
is `/build/aot43-primitives`; the earlier `/build/aot43` is rejected encoding
evidence. Native linking uses the unchanged42 environment/flags with
`MONO_NX_AOT_DIR=/build/aot43-primitives` and the source-fixed runtime archive.

- NRO: `fna-nx-test/terraria-mono/switch/mono_nx_fna_terraria_nochroma43_aot_profile.nro`
- Size: **950,630,004 bytes**.
- NRO SHA256: `370bbb6ad91dad7470a0901430fb7e74c0ddb92a03362ef7577fea2cffc2a639`.
- Terraria SHA256: `87b833138e342ff8ac8378535d181bcf2e185d9505e76d612cd9918869bfb061`.
- MVID: `689c1a33-8aff-12ce-a940-803508ad5db3`.

Proof under `~/.cache/terraria-switch-build/`: `frame-profile43/parent-encoding-check/`
(`acceptance.json`, `proof.json`, `raw-signatures.json`, `sdk-references.json`,
`report-example.log`), `frame-profile43/implementation/accepted-ecma/runner-results.json`,
`frame-profile43/timer-coverage.json`, `frame-profile43/TileDrawingBase.il`,
`frame-profile43/native-symbols.txt`, `aot43-primitives/` manifests/logs and
`nochroma43/{verification-summary,link-verification,nro-payload}.json`.

### Next Switch capture

**Hardware update:**43 runs on Switch and produces complete state-separated
profiling reports. Keep42 as the clean performance baseline;43 includes
measurement and logging overhead. Existing SD runtime/configuration stays
unchanged. The completed capture is analysed below.

Suggested capture in the same existing world:

1. Stand still for about30 seconds.
2. Move around/scroll the world for about60 seconds.
3. Open inventory for10–15 seconds, then the fullscreen map for10–15 seconds.
4. Return to gameplay briefly, then menus for15 seconds and exit normally.
5. Upload `/mono/log.txt` as `log43.txt`.

This capture produced usable state-separated measurements. Solid-tile drawing
and UI are the leading measured areas for further source investigation; no
new optimized NRO was produced by this analysis. Menu-entry skipping remains deferred.


## Build 43 hardware profiler results (2026-09-18)

Input: `fna-nx-test/log43.txt`, SHA256
`40965934d76144b4b4b0bc963d73e47baac09cda09cc7fb30663fa9f5990ebce`.
It confirms native AOT entry, NV120 / GL4.3 Compatibility / glsl120,
`logging=1 runtime_logging=0`, FrameSkipMode1, and normal application shutdown.

### Reliability and measurement overhead

- **69 complete BEGIN/END intervals**, including the normal-exit final report;
 90 registered metrics,81 state rows and4,116 metric rows.
- Profiler frame groups total **9,500**, exactly matching native Draw calls.
  Status records count20,686 Update calls, matching the native count.20,685
  belong to completed-Draw groups; one final no-draw boundary is reported.
- No partial/repeated-draw exclusions, invalid storage, registry overflow,
  unregistered used entries, reset exclusions or report failures are recorded.
- Clock frequency is **1,000,000,000 ticks/sec**. Long startup Int32 timer wraps
  are visible and must not be treated as reliable total times. The selected
  active-world interval/cohort below has no negative timer samples.
- Capture totals544.97ms over9,501 calls: **57.36 microseconds/capture**. Timed
  observers total75.93ms. Report output totals **7.293 seconds**, with a maximum
 298.15ms report. Report time is about1.82% of wall-plus-report time over the
  reported lifetime; this excludes some footer/clearing overhead. Reporting
  causes periodic hitches, so43 is not a clean FPS benchmark against42.

The repeated `sampled_change_frames=frames` in world groups is not evidence
that the player constantly entered menus. `Observe()` also compares renderCount,
which normally cycles. Likewise series1 is the game's existing A/B selection:
the actual ABTestFlag getter returns TileDrawingBase.DrawOwnBlacks. Neither
value should be misinterpreted as an additional game-state transition.

### Where the time goes

The normal-world group (cohort16: menu/paused/inventory/fullscreen-map all false,
series1) from interval15 onward contains **2,715 frames and10,175 Updates**.
Rows below are mean milliseconds per rendered frame, not per Update call:

| Measured region | ms/frame | Interpretation |
| --- | ---: | --- |
| Outer DoDraw | 37.679 | Main drawing body |
| Draw Solid Tiles | **14.574** | Largest measured non-total draw region |
| Flush Solid Tiles | **0.475** | Nested inside solid-tile drawing, not additional |
| Interface | **6.778** | Normal-world UI |
| Sun, Moon & Stars | 3.403 | Sky objects |
| Draw Non-Solid Tiles | 1.781 | Separate non-solid pass |
| Lighting | 1.726 | Lighting aggregate; inner pass rows overlap |
| Prep Render Target Content | 1.223 | Existing inner preparation timer |
| Map Section Update | 1.178 | Map update work |
| Draw Wall Tiles | 1.078 | Wall drawing |
| Outer EnsureRenderTargetContent | 0.0017 | Not the dominant outer gap |
| Outer TransferCompletedAssets | 0.0151 | Negligible in this world selection |

Total Update is21.427ms/rendered frame because multiple updates precede each
Draw—about **5.717ms per Update call**. Its inner rows include world tiles
(5.055ms/frame), players(3.685), NPCs(2.302), items(1.431) and projectiles(1.249).
Do not add these rows to their enclosing totals.

Inventory group20 contains518frames and averages45.782ms DoDraw, of which
**17.061ms is Interface**. Its solid-tile region remains14.132ms. Fullscreen-map
group24 contains879frames and averages10.456ms DoDraw; paused group18 contains
55frames and59.444ms DoDraw. The profiler now distinguishes these states instead
of mixing them into a misleading single game average.

### Next code target—not another blind compiler change

**Solid-tile drawing is the priority.** `Main.DrawTiles` starts its timer before
TileDrawingBase.Begin, calls TileDrawing.Draw, guarantees TileDrawingBase.End in
a finally block, then records DrawSolidTiles. The End/restart helpers separately
record batch flush time and draw-call counts. Thus roughly14.10ms of the14.57ms
solid-tile region is outside the measured flush; this is not proof that every
remaining microsecond is CPU-only or that no graphics calls occur elsewhere.

Exact TileDrawing.Draw IL shows setup/cache clearing, a rectangular Tile[,]
traversal, active/layer filtering, texture-loaded checks, per-type handling,
liquid-behind-tile drawing and DrawSingleTile dispatch. **[INFERENCE]** Tile-side
preparation/traversal/queuing is the most promising next game-code investigation.
The present profile does not isolate those inner operations; it does not yet
justify skipping effects, changing lighting, shrinking bounds or adding a cache
without correct invalidation and differential behavior checks.

UI is the second target. This log also contains repeated ItemPrefixCombiner
reflection traces. The **actual shipped** System.ComponentModel.TypeConverter.dll
GetValue IL formats a diagnostic message and calls `Debug.WriteLine` atIL_0069;
the matching runtime source is ReflectPropertyDescriptor.cs:903. These calls
are separate from native Mono runtime_logging and remain present with that flag
false. They are diagnostics, but their cost is **not isolated** by the Interface
timer. Buffered output still has caller-side cost; there is no basis to declare
it asynchronous or free. A targeted trace/formatting-removal comparison could
be tested separately while preserving property lookup, return values, errors
and assertions. No global stdout/error suppression is proposed.

Evidence: `~/.cache/terraria-switch-build/hardware-build43-analysis.json` stores
strictly parsed complete intervals, arithmetic/counter checks and weighted
cohort aggregates. Exact inspected IL is in `hardware43-Main-DrawTiles.il`,
`hardware43-TileDrawing-Draw.il`, `hardware43-ABTestFlag.il`,
`hardware43-ReflectPropertyDescriptor-GetValue.il`, and the earlier
`frame-profile43/TileDrawingBase.il`. No runtime/game patch or new NRO was made
during this result-analysis step.

## RAL source review and provenance boundary (2026-09-18)

RAL has concrete native rendering changes, not just launcher settings. This
review pins [RotatingartLauncher at3258140](https://github.com/FireworkSky/RotatingartLauncher/tree/3258140fa689a975c8e75ca83e6f9cc88e05f9cf)
and its [FNA3D fork at39a1246](https://github.com/RotatingArtDev/FNA3D/tree/39a1246b72ce566459e6c695750f68a59abdaf16).
The pinned [.gitmodules](https://github.com/FireworkSky/RotatingartLauncher/blob/3258140fa689a975c8e75ca83e6f9cc88e05f9cf/.gitmodules)
uses RotatingArtDev forks, not a blanket set of unmodified upstream libraries.

### Applicable techniques and limits

- **Dynamic-buffer uploads:** the fork's
  [OpenGL driver](https://github.com/RotatingArtDev/FNA3D/blob/39a1246b72ce566459e6c695750f68a59abdaf16/src/FNA3D_Driver_OpenGL.c)
  uses `glMapBufferRange` when `supports_ARB_map_buffer_range` is available,
  advertises NoOverwrite support accordingly, uses `GL_MAP_UNSYNCHRONIZED_BIT`
  for NoOverwrite and invalidates the whole buffer for Discard. A failed map
  falls back to the existing BufferSubData path. The final guard is capability
  based, not a blanket GLES-only condition. The environment override
  `FNA3D_OPENGL_USE_MAP_BUFFER_RANGE=0` disables the path.
  [39a1246, by LaoSparrow](https://github.com/RotatingArtDev/FNA3D/commit/39a1246b72ce566459e6c695750f68a59abdaf16)
  removes range invalidation from NoOverwrite to address upload stalls.
  **[INFERENCE]** This is a candidate for a controlled Switch experiment if
  upload synchronization is measured as costly. Correct buffer-range lifetime
  and driver behavior still matter; Android results are not Switch results.
- **Graphics diagnostics:** [9f2be87, authored by FireworkSky](https://github.com/RotatingArtDev/FNA3D/commit/9f2be8740d6d2b1b136a169c9b12db97582146db)
  adds `RAL_GL_DIAGNOSTICS`, draw/clear/render-target/effect counts, vertex/index
  upload counts/bytes, map-versus-subdata counts and swap timing. The inspected
  swap timer uses SDL performance counters around `SDL_GL_SwapWindow`; it is
  not an asynchronous GPU timer query. These are useful measurement ideas,
  not evidence that their monitored work is currently our main bottleneck.
- **Quality and pacing controls are not free speedups:** the driver contains
  quality-setting parsing and an optional `FNA3D_TARGET_FPS` sleep limiter.
  Parsing a render-scale/precision setting alone does not prove every setting
  affects the final rendering path. A limiter does not accelerate slow frames,
  and reducing image quality would change this port's comparison contract.
  No such setting is adopted in44.
- **Runtime and thread placement:** the pinned
  [CoreCLR host hooks](https://github.com/FireworkSky/RotatingartLauncher/blob/3258140fa689a975c8e75ca83e6f9cc88e05f9cf/core/src/dotnet/corehost_hooks.cpp)
  address Android pthread/clock/stack/affinity compatibility. Its
  [thread-affinity manager](https://github.com/FireworkSky/RotatingartLauncher/blob/3258140fa689a975c8e75ca83e6f9cc88e05f9cf/core/src/thread_affinity_manager.cpp)
  identifies high-frequency cores through Linux CPU information and calls
  `sched_setaffinity`. These implementations depend on Android/Linux APIs;
  they are not drop-in changes to mono-nx/Horizon. The
  [runtime launcher](https://github.com/FireworkSky/RotatingartLauncher/blob/3258140fa689a975c8e75ca83e6f9cc88e05f9cf/core/src/dotnet/dotnet_launcher.cpp)
  hosts CoreCLR. Replacing our runtime is not a prerequisite for isolating the
  already-measured game-code cost.

**Decision:** preserve the working renderer/runtime for44 and isolate tile
operations first.43 measured14.574ms in the solid-tile region but only0.475ms
in its nested batch flush. That supports investigating the enclosing work;
it neither proves all remaining time is CPU-only nor rules out other graphics
costs. No RAL speedup or FPS claim has been transferred to this Switch port.

### Attribution, existing inheritance and unresolved origins

- RAL remains credited as the architectural reference for running desktop
  .NET/FNA games on ARM64. **No RAL source was copied into the build44 observer
  patch.** This statement is limited to this change, not proof of every older
  helper's origin.
- The current launcher/build system is **derived from mono-nx**, not wholly
  original: the findings' original launcher record says copied-and-modified,
  and `fna-nx-test/native/interpreter/Makefile:5-6` explicitly names its base.
  The existing [mono-nx fork](https://github.com/JohnUzoka/mono-nx/tree/fna-support)
  and its recorded commits remain the source reference. Existing Mono runtime
  patches and upstream FNA/FNA3D/FAudio/SDL/Mesa components also retain their
  respective origins; a different platform/API is not evidence of independent
  authorship. The inspected `runtime-source/LICENSE.TXT` is the .NET Foundation
  MIT notice, not the license of every component in the final NRO.
- **Legacy source/provenance gap:** `/tmp/terraria-il-inspect/NxCrypto.cs` and
  `PatchChroma.cs` are not disposable, non-shipped-only research. NxCrypto.dll
  is embedded and AOT-compiled in current builds; PatchChroma's changes feed
  the already-patched Terraria baseline. The crypto DLL's verified SHA256 is
  `ad4d9c1120a5415face6e0a75dad137f92916929f1f1ff66087aab780ce285d1`.
  Their `/tmp` source location is a reproducibility gap, and original source
  provenance was not established by this review. AES being a published
  algorithm does not establish authorship or licensing of its implementation.
  No claim of copied RAL code, or of confirmed independent authorship, is made
  for those legacy files.
- The pinned [RAL root license](https://github.com/FireworkSky/RotatingartLauncher/blob/3258140fa689a975c8e75ca83e6f9cc88e05f9cf/LICENSE)
  is GPLv3. Its separately pinned
  [FNA3D license](https://github.com/RotatingArtDev/FNA3D/blob/39a1246b72ce566459e6c695750f68a59abdaf16/LICENSE)
  is the zlib-style Ethan Lee notice: preserve origin and notices and mark
  altered source. These are separate license scopes; neither licenses
  Terraria assets nor certifies the entire port for redistribution. Any later
  copied/adapted optimization must retain its source/commit attribution and
  applicable notices; attribution alone does not discharge all obligations.

The [current development process and provisional BYO workflow](fna-nx-test/README.md#current-development-process)
remain deliberately separate. A complete retail-files-to-NRO end-user tool
does not yet exist. Supplied game files stay local; the proposed distributed
project is tools/patch logic and appropriately licensed runtime components,
not Terraria executables/assets. Final UX and supported-version packaging
remain provisional. This review is not distribution clearance.

Research evidence: `~/.cache/terraria-switch-build/ral-review/`
contains `FNA3D-comparison.json` and `FNA3D-OpenGL.diff`; links above pin the
actual source rather than relying on release-note performance claims.

## Build 44 tile-operation isolation (2026-09-18)

**44 is measurement-only and now hardware-verified:** the [completed capture](#build-44-hardware-tile-results-2026-09-18) contains sufficient tile data and normal shutdown.
It consumes the exact hardware-tested43 assembly, adds observers to
`TileDrawing.Draw(bool,bool,int)`, and keeps the existing renderer, assets,
input behavior, simulation/frame skip, native runtime fixes, CoreLib bank and
two build42 compiler guards. No RAL renderer change or gameplay optimization
is included.42 remains the clean performance baseline.

### What operation isolation means

43 tells us **which subsystem is expensive**.44 separates work **inside that
subsystem**: setup before the tile loop, the complete loop, post-loop work,
how many tiles are visited/eligible, how many reach DrawSingleTile, and timing
of selected DrawSingleTile calls. This distinguishes a costly setup phase
from a high amount of traversal or costly per-tile work instead of optimizing
based only on the name DrawSolidTiles.

**[INFERENCE]** A dominant setup/post region would direct investigation there;
a dominant loop plus large visit counts would justify examining traversal and
filtering; expensive selected DrawSingleTile calls would justify splitting
that routine next. The measurements do not by themselves authorize skipping
tiles/effects, shrinking bounds, changing lighting, or adding a cache without
correct invalidation.44 narrows the next target rather than claiming the
14.57ms bottleneck is already fixed.

### Metrics and interpretation

Twenty rows are added to the existing NX_PROFILE registry: ten each under
`tile44.solid.` and `tile44.nonsolid.`. The accepted initialization check grows
the full registry from90 to110, within the existing512-entry capacity.

| Suffix | Unit | Meaning |
| --- | --- | --- |
| `setup.completed_region` | ms | Completed pre-loop region wall time |
| `loop.completed_region` | ms | Completed full tile-loop wall time, including nested work |
| `post.completed_region` | ms | Completed post-loop region wall time |
| `passes.started` / `passes.completed` | count | Entered calls versus normal completions |
| `tiles.visited_attempted` | count | Attempted tile fetches, including null/create paths |
| `tiles.layer_eligible` | count | Active/layer filter passed, before later special/debug filtering |
| `DrawSingleTile.calls_attempted` | count | Actual original callsite invocations attempted |
| `DrawSingleTile.samples_completed_1in32` | count | Selected calls which returned successfully |
| `DrawSingleTile.sampled_time_only_1in32` | ms | Sum of durations of those completed selected calls only |

Sampling selects zero-based calls where `(index + phase) & 31 == 0`.
Solid/non-solid phases rotate independently through0..31 across passes,
including empty/throwing passes, without touching game RNG. Arguments stay
on the original evaluation stack and the original call executes exactly once.
The sample is **not full DrawSingleTile time and is not extrapolated**.
Do not multiply it by32 and label the result measured time, or subtract it
from the loop and call the remainder exclusive traversal cost.

Region timers are completed-region wall measurements, not exclusive CPU/GPU
time. The loop includes liquid/special dispatch and counter/sample overhead;
liquid-behind calls are unchanged and are not separately sampled in44.
Registration runs once and publication is outside timed regions where possible,
but this does not remove all observer cost. Existing enclosing TimeLogger and
outer-Draw rows still overlap these rows. Do not sum nested metrics as peers.

Per-pass local state isolates nested calls. A finally block publishes partial
counts and rethrows original failures unchanged. **The43 outer-frame policy
still excludes incomplete/repeated Draw epochs**; publishing partial-pass
counts does not force those excluded frames into a report. Existing cohort,
counter-unit, reset/negative-sample and five-second reporting rules remain.

### Executed proof and artifact

The parent reran `scripts/patch_tile_profile/run.py` in a fresh output directory:

```sh
podman run --rm --entrypoint python3 \
  -v /home/juzoka/workshop/fna-nx-test:/work \
  -v /home/juzoka/.cache/terraria-switch-build:/build \
  -v /tmp/mono-nx-build/mono-nx:/mono-nx:ro \
  localhost/monobuild:local /work/scripts/patch_tile_profile/run.py \
  --output /build/tile-profile44/fresh-check
```

Use a new output directory; this command intentionally rejects existing output.
It is a guarded43-to44 patcher, not a raw-retail patching entry point.

- **1,430 tile checks plus67 retained43 checks passed.** The fixture executes
  complete serialized original/patched Draw IL with deterministic external
  graphics/world/TimeLogData.Add boundaries. Covered cases include empty/sparse/
  dense bounds, all1000 tile type IDs with frame variants, solid/non-solid
  dispatch, nested calls, partial failures and original exception identity.
- Hook removal reconstructs all **815 original instructions and17 locals**
  exactly. **21,064 unrelated bodies**,32,124 original fields,100 managed
  resources, native resources and existing references are preserved. Raw
  signature decoding and pinned target-SDK member resolution pass; malformed
  primitive signatures and unsupported/repeated/aliased/symlink/hardlink
  output cases are rejected. Repeated accepted output is byte-identical.
- A warmed host fixture executes100 passes of12,288 tile calls: **0 bytes in
  both baseline and instrumented loops**. A normal12,288-call pass has384
  completed samples and774 clock calls (`6 + 2 * samples`). These are fixture
  results, not Switch timings, GPU equivalence, enabled TimeLogger text-logging
  allocation results or evidence of zero runtime overhead.
- ARM64 AOT compiles **52,826 /52,850 Terraria methods**, with default
  optimization and the two retained method guards. Six game/helper modules
  are rebuilt; the verified65,536-entry CoreLib object is reused. All85
  dependency name/MVID bindings pass.
- Native linking completes without warnings/errors. Every **192,338 native
  method target** and **15,010 fallback sentinel** is checked against the exact
  module objects. All seven module-info pointer bindings are checked; method
  tables are read-only/non-executable and no load segment is RWX.
- Actual NRO inspection finds **16,183 embedded files /15,997 Content files**,
  the exact accepted Terraria bytes and unchanged bytes for all18 other
  checked dependency/configuration files. No host fixture DLL is staged.

Published NRO:
`fna-nx-test/terraria-mono/switch/mono_nx_fna_terraria_nochroma44_aot_tile_profile.nro`

- Size: **950,646,529 bytes**.
- NRO SHA256: `044cca1246ba64a9fddff9c8aa5354368b6c556a19f631972f24d4c5a7145cf8`.
- Terraria SHA256: `e909fee83e8664b431cc21fbd980909ca99c8765c2c90ea2e19dedc73c940611`.
- MVID: `e89b915c-54de-900b-8bfc-895e1a970aa3`.

Evidence under `~/.cache/terraria-switch-build/`:
`tile-profile44/parent-run/` (runner results and accepted assembly/acceptance/
tile-proof/prior43-proof/report example), `aot44/` (compiler and metadata
manifests/logs), and `nochroma44/{verification-summary,link-verification,nro-payload}.json`.
The report example is a deterministic host fixture, not hardware data.
Native linking uses the unchanged43 environment with `MONO_NX_AOT_DIR=/build/aot44`,
`MONO_NX_GL_COMPAT=1` and `/build/runtime-fix/libmonosgen-2.0.a`.

### Capture guide used for44

1. Keep42/43 and copy44 alongside them; leave SD runtime DLLs, ICU, saves,
   bindings and configuration unchanged. Use full application mode and the
   same world/settings, with Frame Skip On.
2. Confirm `NX_CONFIG` says `logging=1 runtime_logging=0`. Spend15 seconds in
   menus, then at least60 seconds in normal-world play: stand in one place,
   then move through a tile-dense area. Briefly open inventory, close it and
   return to ordinary play so the existing cohorts remain comparable.
3. Return to menus and exit normally. Supply `/mono/log.txt` as `log44.txt`.
   If startup fails, retain that log instead of changing the SD runtime.

Check the new solid/non-solid region/count/sample rows alongside the enclosing
timers, valid-frame counts and observer/report overhead before choosing an
optimization. **Hardware update:**44 completed the capture analysed below.
The exact15/60-second sequence was unnecessary; this is still not a controlled
FPS comparison or a full visual-equivalence test.

## Build 44 hardware tile results (2026-09-18)

Input: `fna-nx-test/log44.txt`, SHA256
`cfe91c8ef48a9f37ca265d030d43a441fc0f3432eee73877886818fd03798686`.
The run confirms native AOT entry, NV120 / OpenGL4.3 Compatibility /
Mesa20.1.0-rc3 / glsl120, `logging=1 runtime_logging=0`, Frame Skip1 and normal
application termination at462.267 seconds of native elapsed time.

### Is the capture sufficient?

**Yes; no repeat is needed merely for missing the prescribed timing sequence.**
The sequence was a coverage guide, not a parser requirement:

- **78 complete, consecutive BEGIN/END reports**, including final=1 at78.
- **9,556 completed frames** exactly match native Draw calls;23,129 profiler
  Update calls match native Updates.23,128 Updates are attributed to captured
  frames and one final Update has no Draw boundary. Capture attempts are not
  rendered-frame counts: there are9,557 attempts versus9,556 completed frames.
- Normal-world cohort16 contains3,933 frames. Excluding the3 world-entry frames
  in interval16 leaves **3,930 frames and15,580 Updates across intervals17–67**.
  This cohort has menu/paused/inventory/fullscreen-map false and A/B series1.
- **47 unmixed normal-world windows** contain3,683 frames and236.353 seconds
  of reported wall time. There are also5,525 menu frames across the two startup
  series,76 inventory frames and22 paused frames. No fullscreen-map cohort was
  captured; it is not required to diagnose this ordinary tile-loop hotspot.
- All5,723 metric-row arithmetic checks and116 tile-layer/cohort invariant
  groups pass. No partial/repeated epoch exclusions, storage/reset-dirty errors,
  unregistered use, registry drops or report failures occur. Registry90 grows
  to110 when tile metrics first appear at interval16.

Four negative original Total Draw+Update samples occur during startup/entry
(intervals1,5,8,16), consistent with the previously documented unchecked Int32
timer limitation. They are not treated as negative work. The selected normal-
world group has no negative samples; its44 setup/loop/post timers are clean.
Both layers record3,930 started and3,930 completed passes. The maximum native
Draw in the47 unmixed world windows is166.98ms, below the original timer's
approximately2.147-second signed-wrap threshold.

### Where tile time goes

The table uses **frame-weighted totals**, not averages of report averages.
Scope: cohort16, intervals17–67,3,930 frames. Times are inclusive wall time
with observer cost, not exclusive CPU or GPU execution time.

| Region | Solid ms/frame | Non-solid ms/frame |
| --- | ---: | ---: |
| Setup | **0.01794** | 0.02075 |
| Complete tile loop | **13.55626** | 1.89141 |
| Post-loop work | **0.46815** | 0.32066 |
| Sum of these three regions | 14.04235 | 2.23282 |

The solid loop accounts for **96.54% of its three measured regions**. The
enclosing Draw Solid Tiles timer averages14.11272ms; Flush Solid Tiles is
0.47860ms and is nested, not another cost to add. Outer DoDraw averages37.76779ms
and normal-world Interface6.96707ms. Setup is demonstrably small here.

Excluding more early gameplay does not move the tile conclusion: starting the
selection at interval24 gives a solid-loop mean of13.55464ms, versus13.55626ms
from17. This is a sensitivity check, not a matched-scene performance benchmark.

### Work counts and selected-call observations

Each layer visits **4,785 tile slots per frame**. For this world group:

| Measure | Solid | Non-solid |
| --- | ---: | ---: |
| DrawSingleTile calls/frame | 2,586.87 | 150.62 |
| Total attempted calls | 10,166,386 | 591,922 |
| Completed timed samples | 317,695 | 18,487 |
| Observed sample fraction | 3.12496% | 3.12322% |
| Mean per completed selected call | **5.06047 microseconds** | 10.07507 microseconds |

Solid eligible tiles and call attempts match; the non-solid layer has159.45
eligible tiles/frame but150.62 calls after later filtering/special handling.
Counts obey completed<=started, samples<=calls<=eligible<=visited, and the
rotating sample-count bounds. Sampled call times stay inside the loop totals.

**Do not extrapolate these sample times into a measured full-call total.**
Phase rotation avoids always selecting the same index, but is deterministic,
not proof of statistically unbiased sampling. Scene/call-order correlations
remain possible. Call duration excludes argument preparation before the
original call and does not measure GPU rasterization separately.

**Next source target:** `TileDrawing.DrawSingleTile` and the work around its
callsite inside the confirmed expensive solid loop. **[INFERENCE]** The large
number of draw calls and their sampled durations make this a better next
inspection target than setup. The capture does not yet identify an expensive
inner helper or prove that traversal, lighting, texture checks or graphics
submission alone is responsible. No quality reduction, skipped tiles, cache
without invalidation, or wholesale renderer swap is justified by this result.

### Overhead and comparison limits

Existing43 counters report643.877ms over9,557 captures (**67.37 microseconds per
capture**, maximum1.110ms), plus63.356ms of timed observers. Reporting totals
**10.093 seconds**, maximum421.375ms, about2.29% of wall-plus-report time.
Those counters do not separately isolate44's inline per-tile counter/sample
cost or every reporting footer; periodic reporting can still cause hitches.

Inventory and pause cohorts are short/mixed-context samples (76 and22 frames).
Their Interface means24.195ms and45.546ms are not a controlled comparison with
43's inventory results. Likewise, changes in frame rate or absolute costs
between42/43/44 cannot be assigned to this patch from different scene captures.
42 remains the clean performance baseline.

Machine-readable evidence:
`~/.cache/terraria-switch-build/hardware-build44-analysis.json` preserves every
complete block, row arithmetic, native-counter reconciliation, cohort totals,
sample interpretation and the later-window sensitivity check. Original build
receipts remain historical host-build evidence; the hardware result is recorded
here and in `fna-nx-test/terraria-mono/build-verification.json`.
**No new game patch or NRO was produced during this capture analysis.**

## Next optimization experiment: per-tile scratch allocations (2026-09-18)

Inspection of the exact accepted44 assembly found a concrete allocation
candidate inside the measured hotspot—not merely another broad timer label:

- `TileDrawing.DrawSingleTile` begins at IL_0000 with
  `newobj TileDrawInfo::.ctor()`, before lighting, texture selection and even
  the early liquid/type518 return.
- `TileDrawInfo` is a reference object; its constructor allocates
  **`new Vector3[9]`** into `colorSlices` and calls `System.Object::.ctor()`.
- The actual44 ARM64 object retains both operations. DrawSingleTile calls
  `plt_wrapper_alloc_object_AllocSmall_intptr_intptr` at0x1b22bb8 with size120,
  followed by the descriptor constructor. The constructor calls
  `plt_wrapper_alloc_object_AllocVector_intptr_intptr` at0x1da7e8c with length9.
  These allocations were not eliminated by AOT compilation.
- At the recorded call counts, this code performs about **5,174 allocation
  operations per frame for solid tiles**, or5,475 across both layers. This
  derives from two unconditional allocation operations per call and the actual
  call counters—not extrapolated sampled timing. It is not a measurement of
  allocator time, GC pauses, retained memory or all game allocations.

The earlier zero-extra-allocation result for44's observer fixture does not
contradict this: that fixture executes the complete outer Draw IL with a
deterministic DrawSingleTile boundary, not the complete game implementation
of DrawSingleTile. It verifies the observer's added allocation behavior.

### Recommended first experiment

**[INFERENCE]** Reusing this scratch state could reduce allocation/collection
work in the tile loop without reducing graphics quality. The first candidate
is draw-invocation-owned scratch storage, reused across sequential tiles and
reset to the exact fresh-constructor state for each call. Avoid a single global
scratch object: nested/concurrent draw invocations must not share mutable state.
This is scratch reuse, not caching rendered tiles or lighting results.

The lifetime/reset proof is still required before implementation is accepted:

1. Trace the descriptor and its array through all consumers and establish that
   no deferred renderer/cache retains either past the call. The inspected
   CacheSpecialDraws_Part2 mutates descriptor fields and caches position/ID
   values; that alone is not a complete escape analysis.
2. Preserve all zero/default scalar, colour, rectangle and reference fields,
   including special-tile, glow, early-return and exception paths. The sliced-
   block helper uses the array through `GetColor9Slice`/`GetColor4Slice` and can
   select shared glow-paint colours locally; reset must not clear shared data.
3. Compare original and candidate draw commands, lighting/colour results,
   special-tile ordering, RNG/particle effects and errors across representative
   tile variants, consecutive calls and nested invocations. Confirm allocation
   reduction directly rather than only relying on source inspection.
4. AOT/link/payload-check the accepted candidate, then compare the same scene
   with identical instrumentation/settings. Check tile-loop/full-frame time
   and allocation/GC evidence; fewer objects alone does not establish an FPS
   improvement. Keep the change only with a justified measured benefit.

If reuse does not improve the measured cost, the next targeted split is inside
the real helper chain: `Lighting.GetColor`, `GetTileDrawData`,
`GetTileDrawTexture`, `DrawTiles_GetLightOverride`, `DrawBasicTile` and its
sliced-lighting/batch calls. Their presence is source-confirmed, but their
individual cost is not. Do not infer that the unsampled outer-loop filtering
is waste, or that it owns99% of the loop; the existing measurements do not say
that. Required eligibility checks and tile placement rules stay intact.

The separate second candidate remains the confirmed debug-message formatting
and `Debug.WriteLine` in `ReflectPropertyDescriptor.GetValue` on the UI path.
Any comparison must preserve property lookup/return values, exceptions and
assertions; global output suppression is not the proposed fix. Its benefit
also needs measurement. No renderer fork or quality reduction is recommended
ahead of these narrower source-level experiments.

Evidence: `~/.cache/terraria-switch-build/tile-optimization-review/` contains
the exact method/helper IL dumps and `allocation-candidate.json` with native
allocation addresses, input hashes and scope limits. **This turn inspected and
recommended the experiment; it did not patch the game or produce a new NRO.**

## Profiling choice and compatibility priorities (2026-09-18)

### Current decision: performance first

The user explicitly prefers a fast game without validated multiplayer over a
slow game with multiplayer. **Do not require a multiplayer smoke test before
continuing performance work.** Multiplayer validation and mod-loader bring-up
are deferred until the game runs at an acceptable speed. This defers work; it
does not request deleting or intentionally disabling existing networking.

**Optimization escalation gate (user instruction):** before applying any change
with a plausible effect on protocol/version handshakes, packet formats, IDs,
serialization/saves, simulation timestep or update policy, gameplay rules,
RNG/particle side effects, multiplayer-visible state, or mod-hook/object lifetime,
pause the affected change and bring it to the user. This includes indirect
effects discovered while optimizing a method named Draw; a render-local name
does not establish safety.

Present the exact proposed semantic change, affected behavior, evidence and
uncertainty, expected or measured speed benefit, and a behavior-preserving
alternative if available. The user decides whether the gain justifies an
explicit, isolated experiment or whether to reject it. Do not implement a
potentially compatibility-affecting experiment first and disclose it later.
Preserve a baseline and keep any approved tradeoff separate and reversible.
Read-only analysis and observation-only instrumentation may continue without
making that semantic change; their overhead must still be reported honestly.

Multiplayer tests remain deferred until speed is acceptable. That deferral is
not blanket permission to break compatibility, and it is not a requirement to
validate multiplayer before a safe measurement-only build. The current45 work
adds observations only: no scratch reuse, packet/update changes, changed RNG
ordering, quality reduction or intentional removal of features is authorized.

The recommendation is a short, focused measurement step followed by the
previous scratch-reuse experiment, rather than treating them as alternatives:

1. Split a few relevant regions inside the already sampled DrawSingleTile
   calls: allocation/constructor initialization, important lighting/texture
   preparation, and drawing/batch work. Keep sample counts, whole-call and
   whole-loop timing, and account for added timer overhead. Do not time every
   tiny operation on every tile or call this an exact partition of13.56ms.
2. Use that information to prioritize the allocation candidate and perform its
   lifetime/reset proof. Implement a single-variable reuse comparison only
   where behavior can be preserved.
3. Judge the result by matched-scene whole-loop/full-frame measurements as well
   as allocation evidence. Timing an allocation does not include all possible
   later garbage-collection cost, so a constructor timer alone cannot replace
   the before/after experiment or prove that reuse is unhelpful.

No such extra profiler or reuse patch was built during this discussion. The
existing44 capture remains the current hardware evidence.

### Multiplayer compatibility is not renderer identity

Peers exchange game-state messages; they do not need identical graphics code,
GPU APIs or FPS. The
[tModLoader network source](https://github.com/tModLoader/tModLoader/blob/stable/patches/tModLoader/Terraria/NetMessage.cs.patch)
documents the vanilla client/server message path and shows examples of player,
item and world data serialization. It also shows how tModLoader changes the
version handshake and adds mod-specific data—unlike a purely local drawing
storage optimization.

**[INFERENCE]** Correct scratch reuse should not require other players to use
our rendering patch, provided protocol/version behavior, IDs, serialization,
simulation/update policy and gameplay side effects remain compatible. This is
not proof that the current Switch networking stack already works. In particular,
the inspected `DrawSingleTile` also performs particle/RNG and special-tile work;
changing or skipping those callbacks is not equivalent to only changing storage.

When performance is satisfactory, the first multiplayer target should be the
current desktop game version1.4.5.8 connecting by direct IP to a matching PC
server, alongside an unmodified PC client. Later checks should cover shared
movement/combat/tile edits/items/chests and reconnect/save behavior. This test
is **deferred, not a prerequisite for the next optimization**.

Steam invitations/lobbies are separate: the current mono-nx SteamAPI shim
returns a generic zero/no-op, not a working Steam integration. Direct-IP
networking is a different path; the stub does not prove that all networking is
disabled or functional. No successful hardware multiplayer session is recorded
in the current verification ledger.

The desktop-on-Horizon port is not automatically the retail Switch edition.
Official console/mobile crossplay has separate version/protocol/platform
requirements. Re-Logic's
[August30,2026 update](https://store.steampowered.com/news/app/105600/view/692018222142062736)
describes Crossplay Phase One as upcoming1.4.6 work. Do not promise retail
console/mobile crossplay from this1.4.5.8 port merely because it runs on Switch.

### What the RAL example establishes

The user reports/believes RAL can join servers. Its
[pinned README](https://github.com/FireworkSky/RotatingartLauncher/blob/3258140fa689a975c8e75ca83e6f9cc88e05f9cf/README.md)
advertises Terraria/tModLoader support and **EasyTier Multiplayer**, described
as P2P VPN networking. This supports using RAL as a reference for the approach,
but is not an independently reproduced server-join test for every RAL patch,
server version, or this mono-nx port. A VPN addresses reachability; it does not
by itself make incompatible game protocols compatible.

### Future modding: preserve options, do not promise compatibility

Modding can observe drawing internals even when multiplayer cannot. The current
[GlobalTile API](https://docs.tmodloader.net/docs/stable/class_global_tile.html)
exposes `DrawEffects(..., ref TileDrawInfo drawData)` and other draw hooks.
**[INFERENCE]** Reusing a descriptor can affect a hook that observes/replaces it
or retains a reference, so a vanilla-only lifetime proof is not automatically a
mod-hook compatibility proof. Keep patches version-guarded and do not blindly
apply a vanilla assembly patch to a different/modded assembly.

tModLoader also requires its own integration. Its
[player guide](https://github.com/tModLoader/tModLoader/wiki/tModLoader-guide-for-players)
currently identifies stable as Terraria1.4.4.9-based, while our input is1.4.5.8.
Its [MonoModHooks implementation](https://github.com/tModLoader/tModLoader/blob/stable/patches/tModLoader/Terraria/ModLoader/MonoModHooks.cs)
uses runtime detours/IL hooks; supporting these on our AOT-plus-interpreter,
no-full-JIT runtime requires separate investigation. RAL uses a different
runtime environment, so its mod support is not inherited by copying a renderer.
The [official tModLoader description](https://store.steampowered.com/app/1281930/tModLoader/)
also requires other players to use tModLoader for modded multiplayer; vanilla
and tModLoader sessions are not interchangeable.

For now: retain clean patch boundaries and avoid unnecessary gameplay/protocol
changes, but spend implementation and testing effort on speed—not crossplay,
platform matchmaking, or mod-loader support.

## Build 45 focused tile-cost measurements (2026-09-18)

**Measurement build45 is now hardware-verified; results are below.** It keeps44's
working behavior and original per-tile allocations. It adds28 metric rows to
distinguish allocation/initialization from important helper calls inside the
measured solid-tile loop. It is not a speedup patch and does not establish that
scratch reuse is worthwhile yet.

### What is measured

Sampling matches44's1-in32 rotating selection, separately for solid/non-solid
passes.45 uses a per-thread value-type context saved/restored for each Draw
invocation; nested calls and exceptions cannot replace their parent's context.
Original DrawSingleTile signatures and all original calls/arguments remain.

Under each `tile45.solid.` / `tile45.nonsolid.` prefix:

| Group | Direct callsites timed inside DrawSingleTile |
| --- | --- |
| `alloc_init` | Original TileDrawInfo allocation and constructor, including Vector3[9] |
| `light_and_frame` | GetColor, GetTileDrawData, GetTileOutlineInfo, DrawTiles_GetLightOverride, GetFinalLight |
| `texture_lookup` | Each direct GetTileDrawTexture call |
| `base_draw` | The mutually exclusive DrawBasicTile / minecart / Christmas-tree calls |

Each group emits `.completed_samples` in time units and `.operations` in count
units. Only operations belonging to a normally completed, valid selected call
contribute. An optional call which did not execute has unused time, not a fake
zero timing sample. The static callsite inventory is1/6/4/3 for the four groups;
it is not the number of executed operations on every tile.

Additional rows are `call_body.completed_samples`,
`other_body_including_observers.completed_samples`, `calls.selected`,
`calls.completed`, `calls.invalid_timing`, and
`time_metrics.omitted_out_of_range`. The remainder is the matched selected body
minus those disjoint direct-call groups. It includes other game work and
observer bookkeeping—not exclusive traversal or CPU time. The older44
caller-side sample also includes boundary work outside45's body clock.

Negative/backward timestamps, invalid group sums and accumulation overflow are
not published as valid timings. Int32 publication is checked against the
**existing current-frame value plus the new pass total**, not just the new
value. Two new read-only `NXCost45Current` accessors on TimeLogData/DataSeries
read that slot without changing original fields, visibility or behavior.
The expected full registry is110+28=138, below512.

Do not multiply sampled times by32, interpret them as GPU timing, or claim that
they are an exact partition of the whole13.56ms outer loop. Constructor timing
does not capture all later garbage-collection cost.43's frame/cohort exclusions,
five-second reports and original timer limitations remain in force.

### Executed verification

- Fresh `scripts/patch_tile_cost_profile/run.py` acceptance and repeat runs pass;
  accepted assembly and both executable probe assemblies are byte-identical.
  Unsupported/already-patched/FNA-mismatched inputs and existing/alias/symlink/
  hardlink outputs are rejected. Accepted game output is read-only.
- **362 focused checks plus67 retained43 checks pass.** The fixture executes
  complete serialized original/patched DrawSingleTile, the actual44/45 Draw
  caller, original TileDrawInfo constructor and45 helpers. External graphics/
  world calls are deterministic boundaries, not a Switch renderer simulation.
- Covered cases include all five returns, optional outline bypass, all texture
  callsites, basic/minecart/tree alternatives, early liquid/type518 return,
  RNG/particle call order, byref/value returns, original exception identity,
  empty/unscoped/unsampled calls, nested passes, independent thread contexts,
  bad timestamps, excessive group sums and cumulative publication overflow.
- Reverse-splice verification reconstructs44 Draw's879 instructions/20 locals/
  one handler and Single's2,312 instructions/61 locals/no handlers exactly.
  **21,068 unrelated bodies,32,139 original fields and100 managed resources**
  remain unchanged, as do native resources, original signatures/references and
  the two42 compiler guards. ThreadStatic survives the template clone.
- 102 raw-signature rows and three SDK member references used by45 are verified
  with the existing strict decoder/pinned resolver, without host fallback.
- Across20,000 warmed Single calls, baseline and instrumented fixtures each
  allocate **5,120,000 host bytes**:256 bytes/call and **zero extra45 bytes**.
  The real original object and array allocations remain; this is not a
  zero-allocation game or a measurement of Switch object sizes.
- Unsampled/unscoped Single calls make no45 timestamp reads. A selected normal
  basic call uses16 (`2 + 2 * executed grouped operations`). The retained44
  caller clocks are additional. Enter/Finish add no clocks.
- Nine alternating warmed host timing rounds are retained in
  `host-overhead.json`. The final run's paired median was **82.038ns extra per
  call**; paired20,000-call batch differences ranged0.300–3.844ms. These include
  clock-counting wrappers, host allocation/GC and scheduling noise; they are
  **not Switch overhead or a game speedup benchmark**.
- ARM64 AOT compiles52,848/52,872 Terraria methods with the unchanged compiler
  policy. All192,360 linked method targets,15,010 fallback sentinels, seven
  module-info bindings and85 dependency name/MVID bindings pass verification.
  Method tables remain read-only/non-executable; no RWX load segment exists.
- Native/tool builds complete without warnings/errors. Actual NRO inspection
  finds16,183 files /15,997 Content assets, the accepted game bytes and unchanged
  bytes for18 other checked dependency/config files. No host fixture is staged.

NRO: `fna-nx-test/terraria-mono/switch/mono_nx_fna_terraria_nochroma45_aot_tile_cost_profile.nro`

- Size: **950,676,225 bytes**.
- NRO SHA256: `8970a8d51e20ccd870688182ae757ab5dda341cac0cd6923930708b6b0c47607`.
- Game SHA256: `dc0201ce33feed6b527a42f658d979a37baf35c181c661e11ca7e75f55738d5f`.
- MVID: `9b6d3616-ba90-083e-7226-9a6755455eaf`.

Evidence under `~/.cache/terraria-switch-build/`: `tile-cost45/final-run/`,
`aot45/`, `nochroma45/{verification-summary,link-verification,nro-payload}.json`,
and `tile-analyzer-check45/`. The earlier main-integration emission is explicitly
unaccepted debugging output; only final-run/accepted is the accepted source.

### Capture and analysis

Keep42/44 and copy45 alongside them. Keep the same SD runtime, ICU, saves,
bindings, graphics settings and Frame Skip On. Use full application mode and
confirm `logging=1 runtime_logging=0` in NX_CONFIG.

Let the world finish loading, then spend a reasonably steady period in normal
gameplay: stand in one location, then move through a tile-dense area. A few
dozen seconds in each is useful, but the sequence is not a rigid15/60-second
requirement. Brief inventory time is optional and analysed separately. Exit
normally and provide `/mono/log.txt` as `log45.txt`. No multiplayer test is
requested at this stage.

The new SDK-independent `scripts/analyze_tile_profile.py` validates complete
reports, metric arithmetic, sample accounting and available native totals,
then writes explicit state-cohort aggregates. For example, after choosing the
first post-loading interval from the capture:

```sh
python3 fna-nx-test/scripts/analyze_tile_profile.py fna-nx-test/log45.txt \
  --cohort 16 --first-interval N --output /path/to/new-analysis.json
```

Replace N with the selected interval and use a new output file. The analyzer
reproduces the real44 aggregate, accepts the actual serialized45 report,
preserves null/all-invalid original measurements, and rejects truncated,
duplicated, arithmetically inconsistent or mismatched sample/native counts.
Invalid/omitted45 groups receive no per-operation timing verdict. Synthetic
parser scenarios are labelled as parser tests, not hardware evidence.

An optional `--baseline previous.log --baseline-first-interval N` emits only a
**descriptive comparison**, never an automatic speedup verdict. Different scenes,
warm-up, state cohorts or observer levels are not controlled comparisons.

### How we decide whether an optimization is worth it

Use45 to identify which selected operations matter, checking valid counts and
observer/omission diagnostics. A later behavior-preserving candidate must show
a repeatable improvement in matched-scene tile-loop/full-frame cost or frame
stalls with comparable instrumentation—not merely fewer objects or one fast
sample. If the benefit is small, inconsistent or outweighed by complexity/risk,
do not keep the change. If a potentially protocol/simulation/gameplay-affecting
change is discovered, the documented **halt-and-discuss rule applies before
implementing that experiment**, even if its predicted gain is large.

## Build 45 hardware operation costs (2026-09-18)

Input: `fna-nx-test/log45.txt`, SHA256
`c92d55e8baa9f4743ae68da61fdf36690c7176894ae5ebd864cf05d6390c7aba`.
The run confirms AOT entry, NV120 / GL4.3 Compatibility / Mesa20.1.0-rc3 /
glsl120, `logging=1 runtime_logging=0`, Frame Skip1 and normal application
termination at587.782 seconds of native elapsed time.

### Capture quality and selected population

- **100 complete consecutive reports**,106 state rows and9,335 metric rows.
  The analyzer validates report/metric arithmetic and available native totals.
- **12,167 captured frames match native Draws**;30,435 Update calls match native
  Updates, with one no-Draw boundary. No partial/repeated frame exclusions,
  storage/reset-dirty errors, unregistered metrics, registry drops or report
  failures occur. Registry90 grows to138 as expected.
- Across all captured cohorts,441,575 selected45 calls complete; invalid-timing
  and omitted-time counters are zero. Each layer/cohort's45 completed sample
  count agrees with44's independently retained sample counter.
- Normal-world cohort16 has4,965 frames. Excluding its single entry frame in
  interval18 gives **4,964 frames across intervals19–82**. The table below uses
  this post-entry group, not menus, inventory or pause.62 unmixed normal-world
  windows contain4,870 frames and312.496 seconds of reported wall time.
- Inventory has291 frames and pause52; menus have6,859 frames across startup
  series. Original timers have five negative samples during startup/entry
  (intervals1,5,8,18), not new45 timing failures. The selected post-entry45
  population is valid, with391,437 solid and15,929 non-solid sampled calls.

### Measured cost per selected solid-tile call

All times below are **microseconds per valid completed selected call**, with
each group weighted by actual execution frequency. They are not costs for
every tile, not a full-loop extrapolation, and not GPU timings.

| Group | us / selected call | Share of measured selected body |
| --- | ---: | ---: |
| Allocation + constructor initialization | **1.362** | **14.24%** |
| Lighting + frame helpers | **2.262** | **23.65%** |
| Texture lookup | 0.481 | 5.03% |
| Base-draw alternatives | 0.370 | 3.87% |
| Other body work **including observers** | 5.087 | 53.20% |
| Measured selected body total | **9.563** | 100% |

There is one allocation/init operation and one texture lookup per selected
solid call. Lighting/frame operations average3.125 per selected call, at0.724us
per executed helper operation. Base drawing averages2.972us **when executed**,
but only12.46% of these selected calls reach one of the three base-draw
alternatives. Therefore2.972us must not be charged to every selected tile.
The execution frequency is measured; the capture does not identify the exact
reason for every early/conditional path.

The allocation result is stable under a later-window check: starting at30
instead of19 gives1.353us versus1.362us. Lighting/frame gives2.279us versus
2.262us. This supports the operation ranking; it is not a controlled speedup
comparison or proof that the samples are statistically unbiased.

Non-solid selected calls average16.481us inside45's body:1.530 allocation/init,
3.851 lighting/frame,1.092 texture lookup,2.957 base drawing and7.050 other/
observer work. They are a different workload, not interchangeable samples of
the solid path. Normal-world call rates are2,523.35 solid and102.48 non-solid
calls/frame; both layers still visit4,785 tile slots/frame.

### Observer overhead must not become a false game bottleneck

45's matched solid caller-side sample averages12.021us versus9.563us inside
the45 body clock. The2.459us difference includes original call boundaries/
prologue and45 begin/end bookkeeping; it is not a pure observer-cost estimate.
The5.087us remainder also includes observers. **Do not declare all of that
remainder to be unoptimized game logic.**

The instrumented solid loop averages17.496ms/frame, enclosing Draw Solid Tiles
18.183ms (including0.590ms nested flush), outer DoDraw40.863ms and normal-world
Interface6.913ms. The previous44 loop was13.556ms in a different capture.
Different scenes plus new instrumentation prevent assigning the whole
difference to either gameplay or profiler overhead. This is not a measured
regression or speed improvement in the uninstrumented game.

Existing capture counters total987.022ms over12,168 capture attempts
(81.12us/attempt), plus113.528ms in timed outer observers. Reporting totals
**19.584 seconds**; median report cost198.217ms. **Interval50 spent3.897 seconds
in reporting**, while its state group was normal-world gameplay. Reporting
is3.46% of measured wall-plus-report time, excluding some footer work. These
figures do not include all45 inline overhead.45 is diagnostic, not the clean
performance baseline.

### Optimization decision and halt boundary

Allocation/init is a real measurable candidate, **not the entire bottleneck**.
Lighting/frame helpers are a larger combined selected-call region, though45
does not separate the individual helper contributions. Reducing allocations
may also affect later GC work, which the constructor bracket does not measure.
Thus the capture alone cannot predict a substantial FPS gain from pooling.

**[INFERENCE]** A single-variable scratch-reuse comparison is worth considering,
but only after its ownership/reset proof; a smaller allocation count alone is
not the acceptance criterion. Use the same scene and comparable profiling for
before/after, and confirm whole-loop/frame benefit with diagnostic overhead
removed or controlled. Avoid another broad change based only on the remainder.

**Risk raised; no optimization applied:** reusing TileDrawInfo changes its
object lifetime and could matter to retained references or future mod draw
hooks. A candidate which exposes that change, alters callbacks/RNG/ordering,
or affects protocol/simulation/gameplay must be discussed with the user before
application under the standing stop rule. This result does not authorize such
a tradeoff. Multiplayer validation remains deferred as requested.

Evidence: `~/.cache/terraria-switch-build/hardware-build45-analysis.json`
contains the validated blocks, all cohort summaries, post-entry and later
selections, matched sample ratios, integrity checks and reporting costs.
`hardware45-world-analysis.json` and `hardware45-later-analysis.json` are the
direct CLI outputs. **This step analysed the log; it did not create a new
game patch or NRO.**

### Requested scratch-reuse gain estimate

**[INFERENCE — a what-if model, not a measured allocation budget or speedup.]**
The user asked for a rough expected gain. If the selected-call allocation means
represent all calls,45's normal-world rates imply approximately3.44ms/frame of
solid allocation brackets plus0.16ms non-solid, or **3.59ms/frame combined**.
This projection must not be substituted for measured whole-loop attribution in
the analyzer. Deterministic sampling, probe cost and scene dependence remain.

| Hypothetical fraction of that bracket removed | Projected time saved | Draw-only throughput gain | Fixed Draw+Update-work throughput gain |
| --- | ---: | ---: | ---: |
| 25% | 0.90ms/frame | 2.25% | 1.46% |
| 50% | 1.80ms/frame | 4.60% | 2.96% |
| 100% | 3.59ms/frame | 9.64% | 6.09% |

The denominators are45's instrumented40.863ms Draw and21.720ms Update work per
rendered frame. Real Update work per rendered frame can change with FPS, so
neither column is a direct FPS prediction. The full-removal row is an optimistic
direct-cost scenario, not a hard bound: reuse needs resets/bookkeeping, the
bracket includes probe work, and later GC effects are not isolated. No net gain
is also possible. A useful planning scenario is **a few percent**, not a jump
from roughly18FPS to30/60FPS; substantial larger gains would need additional
evidence or other optimizations. No reuse patch is authorized or applied by
this calculation, and the lifetime/compatibility stop rule remains unchanged.

### Compatibility tradeoffs and retained-reference safety

The user clarified that existing mods do not all have to work unchanged on
Switch. This is **not** a blanket waiver of mod compatibility. Surface the
scope of required changes: a small mod adaptation or a significant speed gain
may justify some breakage, while a large mod/loader overhaul for a small gain
is not desirable. Discuss the concrete tradeoff before applying it. Protocol,
simulation, gameplay, saves and vanilla correctness still require protection;
multiplayer testing and full mod-loader bring-up remain deferred.

**Retained-reference risk is shared mutable state, not automatically freed
memory.** If code queues the descriptor for tileA, we recycle it for tileB,
and the queued code later reads it, the reference remains valid but now contains
tileB's state. **[INFERENCE: possible failure modes, not observed bugs]** This
could produce wrong textures/frames, colours/glow, flicker, incorrect special-
tile drawing, or null/invalid drawing arguments and crashes. Incomplete resets
can leak one tile's fields into the next even without retained references.
A single shared object also risks nested/concurrent calls overwriting an outer
draw. These are reasons to prove ownership/reset behavior, not evidence that
pooling must break Terraria or corrupt saves.

Read-only inspection of the actual45 assembly provides useful bounded evidence:

- No field declaration in that assembly directly names TileDrawInfo, including
  generic/array field types. Seven method signatures take the descriptor,
  including DrawBasicTile, flames/sliced-block helpers, Christmas tree,
  back-rope, minecart and CacheSpecialDraws_Part2. This does not rule out storage
  through object/interface references, closures, reflection or outside code.
- The inspected CacheSpecialDraws_Part2 caches position-to-ID mappings and
  mutates descriptor fields; those cache entries are not TileDrawInfo objects.
- The inspected tile-batch callsites pass texture references and value-type
  position/rectangle/vertex-colour data, not the descriptor itself. That is a
  favorable boundary, not a complete proof of every caller/callee lifetime.
- The exact Vector3-array GetColor9Slice/GetColor4Slice overloads write into the
  supplied array synchronously. They pass coordinates—not that array—to the
  lighting engine and receive Vector3 values. The nine-slice method overwrites
  all nine slots; the four-slice method writes slots0–3. Neither inspected method
  stores the array elsewhere or allocates a replacement. Other consumers and
  all reset requirements still need checking.

**Monitoring is possible, but does not replace the lifetime audit.** A debug
experiment can track ownership scopes, capture a generation alongside a borrowed
reference, and assert at instrumented uses that the expected generation remains
valid. Merely stamping the reused object is insufficient: an old alias sees the
new stamp too. Diagnostic poisoning/quarantine of only the scratch-owned fields
and array can expose some late reads; never poison the real Tile or Texture
objects they reference. These checks belong in a controlled diagnostic build or
harness, not an unannounced gameplay change.

GC survival, weak references or reference counts alone do not establish safe
reuse: an object may stay alive without being used, or a bad late read may happen
and release its reference before a GC/snapshot. Runtime checks cover exercised
paths only. Combine source/IL escape analysis with original-versus-candidate
draw-command/field/RNG/exception comparisons, nested-call cases and hardware
visual checks. No single global scratch object and no assumption that a passing
ordinary-world run covers every special tile.

Evidence: `~/.cache/terraria-switch-build/lifetime-inspect/` contains the
read-only inspector and `typed-uses-and-lighting.il`. Existing helper IL is in
`tile-optimization-review/`. No pooling or live lifetime-monitoring patch was
applied during this assessment.

## Third-party advice: claim review and multiplayer scope (2026-09-18)

The user supplied a lower-model summary for fact-checking, not as an optimization
plan. The later instruction narrows the **second** pasted response to its
multiplayer section; its repeated build/mod-pipeline advice is not adopted.
No game changes, networking tests or new NRO result from this review.

### Initial high-level claims: established limits

- **Main-thread/update throttling:** a serial hot path can matter, but the actual
  WorldGen.UpdateWorld uses bounded/random tile selection, a world-update rate,
  and Liquid.skipCount scheduling. It is not simply every tile being processed
  every frame. Altering these rates changes gameplay even if item IDs and save
  serialization remain unchanged; no universal safety claim follows.
- **Independent threading:** possible only after proving ownership, ordering,
  snapshot and graphics-context requirements. A named subsystem such as minimap
  or asset loading is not automatically independent of world state or GPU work.
- **Particles/gore:** alpha overdraw can be expensive, but the current measurements
  do not establish a particle-driven GPU bottleneck. Caps can change visuals,
  callback/RNG behavior and return-index expectations; they are not a proven
  free speedup.
- **AOT and hooks:** the current launcher selects MONO_AOT_MODE_INTERP for the
  AOT build, with interpreter fallback—not .NET NativeAOT or the SDK's older
  interpreter-only launcher. Stock Harmony/MonoMod execution is not verified.
  Source defaults/API presence or an AOT call trace do not establish the runtime
  value of IsDynamicCodeSupported; the Mono source notes intrinsic folding in
  some configurations. Before-AOT Cecil IL rewriting is already in use and
  does not require the game's original C# source. AOT does not prove that every
  mod requires rewriting, nor that all mods will be plug-and-play.
- **GC:** allocations are confirmed, but GC pause attribution is not. The build
  links Mono's libmonosgen runtime, not a .NET NativeAOT Server GC. The pinned
  GCSettings.Mono.cs reports IsServerGC=false and exposes Batch latency only;
  that is not proof of a single-threaded or specially low-latency collector.
  "Conservative Workstation Mode" is not an established setting for this port.
- **Lighting:**45 measured a combined lighting/frame-helper group, not an isolated
  full lighting-engine cost. Historical low-end performance anecdotes are not
  proof of this capture's dominant bottleneck. Integer/low-precision conversion
  can change image quality or behavior and needs its own measured tradeoff.
- **1GB/header limit:** contradicted for this build by actual1516MiB libnx and
  1514MiB Mono allocator regions (3030MiB combined). heap.c derives available
  memory through svcGetInfo/svcSetHeapSize or a launcher override and splits it;
  those figures are reservations/regions, not measured live textures. The NRO
  ASET header describes icon/NACP/RomFS sections, not a heap-size override.
- **Texture promises:** the inspected installed FNA3D_SurfaceFormat exposes
  DXT/BC formats but no ASTC/ETC2 formats. GPU capability alone is insufficient
  for a working loader/backend path. "All textures are uncompressed in RAM"
  was not verified; the1.5GB-to400MB claim has no asset/residency measurement or
  tested conversion behind it. No such memory reduction is promised.
- **Decision:** retain source-verified, measured candidates. Do not replace the
  existing working architecture or relax compatibility checks on the strength
  of this summary.

Sources: local `native/interpreter/source/main.c:70-82`, SDK
`native/shared/heap.c`, installed `include/FNA3D/FNA3D.h:119-148`,
`runtime-source/src/mono/System.Private.CoreLib/src/System/Runtime/GCSettings.Mono.cs`,
and `RuntimeFeature.NonNativeAot.cs`; `log45.txt:1-10`.
External references: [Mono SGen](https://www.mono-project.com/docs/advanced/garbage-collector/sgen/),
[Mono AOT](https://www.mono-project.com/docs/advanced/aot/),
[.NET NativeAOT limitations](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/#limitations-of-native-aot-deployment),
[.NET GC configuration](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector#flavors-of-garbage-collection),
and [NRO/ASET layout](https://switchbrew.org/wiki/NRO0#Assets).

### Multiplayer excerpt: not a binary safe/unsafe checklist

The useful principle is to preserve protocol identity and gameplay semantics,
but the excerpt overstates both its "will break" and "completely safe" lists.

**Terraria is not strict deterministic lockstep across every client.** The
[tModLoader Basic Netcode guide](https://github.com/tModLoader/tModLoader/wiki/Basic-Netcode)
describes a server-relayed state/message model with different authorities:
NPCs and world state are server-owned, player-spawned projectiles are generally
owned by their spawning client, and state is synchronized through messages.
Clients need compatible data/behavior, not identical rendering code or FPS.
Non-deterministic decisions can be synchronized; every machine need not make
the same random decision independently on the same rendered frame.

- **Liquid time-slicing:** potentially gameplay-affecting, but not an automatic
  instant-desync/inventory-loop result. Determine which code runs on client,
  server or single player, which side owns the liquid/world state, and how
  changes are replicated. A wall-clock budget can make simulation depend on
  device speed. Existing scheduling/queues and semantics must be understood.
- **Random tile throttling:** changes growth/spread rates and random-call
  behavior; unchanged save layout does not make it behavior-preserving, even
  in single player. On an authoritative host it changes shared world behavior.
  Client-side effects depend on the actual authority/call path, not a universal
  lockstep rule.
- **Physics precision:** high risk when it changes movement, collision, AI or
  projectile outcomes, but correction/disconnect behavior is not universally
  immediate. Numerical rewrites which preserve required results differ from
  deliberately changing the simulation. Identify ownership and synchronization.
- **Entity indexing:** the warning is sound where network identity is tied to
  an NPC/item slot. Do not compact/reindex externally referenced slots without
  maintaining every corresponding reference/protocol identity. A separate
  iteration list which preserves identities is a different proposal.
- **Texture/audio encoding:** usually local representation changes rather than
  packet changes. Still preserve format support, asset names/dimensions,
  appearance/timing, resource lifetime and any gameplay-dependent metadata.
  This does not establish the advertised RAM or CPU savings.
- **SpriteBatch flattening:** usually does not directly change packet formats,
  but reordering draws can break alpha/layer order, targets, shaders, sampler/
  blend state and special effects. The measured bottleneck must justify it;
  "client-side" does not mean every rewrite is correct or faster.
- **Dust/gore caps:** no blanket safety guarantee. In the inspected actual
  DrawSingleTile, Dust.NewDust's returned index is used immediately to access
  and modify Main.dust. Arbitrarily shrinking arrays or returning an invalid
  sentinel can fail even without networking. Check effect callbacks, RNG,
  caller assumptions and mod hooks before classifying a drop as purely visual.
- **Pooling/text buffers:** protocol-neutral only if ownership, resets,
  reentrancy and observed values remain correct. Retained-reference bugs do
  not disappear because the allocation was made in a rendering/UI method.
- **Threaded minimap work:** snapshot ownership, synchronization, world unload,
  cancellation and graphics-thread upload rules still apply. Moving an entire
  subsystem onto a worker is not automatically independent or safe.

### Concrete error in the suggested netMode guard

The [official NetmodeID definitions](https://docs.tmodloader.net/docs/stable/class_netmode_i_d.html)
are0=SinglePlayer,1=MultiplayerClient,2=Server. Therefore the pasted
`if (Main.netMode == 1) vanilla; else optimized;` runs the new simulation on
**both single player and servers**. It does not isolate the change to single
player and can alter authoritative multiplayer behavior.

If the intended boundary is single-player-only, it must explicitly distinguish
mode0 from both multiplayer roles. Even a corrected mode check is not a general
multiplayer proof: state ownership, transitions, synchronization and resulting
gameplay still matter. The claimed "isolate the net loop" rule is insufficient;
gameplay data consumed by the networking layer is produced throughout the game.

The existing policy remains: optimize speed using measured evidence; preserve
or explicitly discuss semantic/mod-compatibility tradeoffs. Multiplayer testing
stays deferred as requested. Do not silently classify the excerpt's preferred
optimizations as approved or safe.

Additional local evidence: `lifetime-inspect/WorldGen-UpdateWorld.il` and
`tile-optimization-review/TileDrawing-DrawSingleTile.il:168-222` under the build
cache. This was read-only analysis; no suggested optimization was applied.

## Current optimization order

The third-party advice does not change the overall direction. Build45 has
provided enough focused data to move from broad profiling to a bounded code
experiment; it is not itself a clean performance baseline.

1. **Finish scratch ownership/reset verification.** Trace TileDrawInfo and its
   array through the remaining consumers, including late use, early returns,
   special tiles and nested drawing. Use diagnostic checks where inspection
   leaves uncertainty; do not assume a single global reusable object is safe.
2. **Try one isolated scratch-reuse candidate if justified.** Keep unrelated
   renderer, compiler, simulation and content behavior unchanged. Verify fresh-
   state equivalence, draw commands/colours, effect/RNG ordering and exceptions.
   If lifetime changes escape or imply significant compatibility work, discuss
   them with the user before application rather than forcing the optimization.
3. **Measure a clean before/after pair on Switch.** Same scene/settings and
   equivalent instrumentation; remove or control45's expensive detailed probes/
   report output for the final performance verdict. Compare loop/full-frame
   time and stalls, not only allocation counts or a single FPS observation.
   Keep only a repeatable benefit worth its complexity and adaptation cost.
   The present few-percent gain scenario remains hypothetical.
4. **Then inspect the measured lighting/frame-helper group.** Isolate individual
   contributors only as needed. Prefer eliminating redundant work over silently
   lowering precision, changing lighting behavior or throttling world updates.
5. **Then address other measured costs, including UI.** Confirmed debug-string
   formatting/output and allocation paths are candidates, not proven savings.
   Move to batching, other subsystems or runtime changes only when evidence
   identifies them, not because they appeared in generic advice.

Performance remains first. Multiplayer testing and full mod-loader bring-up are
deferred, but protocol/gameplay/save/ownership changes and material mod-adaptation
costs must still be raised before a tradeoff. Significant gains or small mod
adaptations may justify compatibility changes by the user's decision; a large
overhaul for a small gain is not the default. This is the next-work roadmap,
not a claim that scratch reuse or any new optimization has been implemented.

### Scheduled next work versus conditional future candidates

Status clarification requested by the user: multithreading, compression and the
other broad suggestions were **reviewed possibilities, not already scheduled
experiments**. The committed next investigation remains scratch ownership/reuse
and a clean comparison, followed by the measured lighting/frame and UI paths.
Batching/runtime work was already mentioned conditionally. None of the plausible
categories is excluded forever, but this is not a promise to implement or test
every technique from the supplied advice.

Keep these as an explicit later screening list:

| Candidate | Evidence needed before a concrete experiment |
| --- | --- |
| Multithreading selected work | Meaningful main-thread CPU cost plus a demonstrated ownership/synchronization boundary; account for work already parallelized |
| Texture compression or streaming | Actual texture residency, memory pressure, upload/bandwidth cost and a supported format/loader/backend path; no assumed400MB outcome |
| Asynchronous asset/audio loading | Measured blocking loads/startup stalls and preserved resource readiness, ordering and graphics-thread requirements |
| Batching or buffer-upload changes | Measured submission/upload/state-change cost and a way to preserve render ordering/state |
| GC or native/managed heap tuning | Actual collection/allocator pressure and settings supported by this Mono/libnx configuration |
| Particle limits or reduced lighting quality | A representative effects-heavy bottleneck and an explicit quality/behavior tradeoff for user review |
| Simulation throttling/time-slicing | A concrete high-cost algorithm and explicit gameplay/authority consequences; never assumed safe because saves retain their format |

After the first measured optimization round, revisit CPU parallelism,
loading/render-data flow and memory pressure so these candidates are not lost.
Promote a candidate to an actual experiment when the evidence supports it.
Claims based on the wrong runtime, nonexistent heap-header controls or
unverified backend capabilities are not experiments to perform blindly.

## Build 46 clean scratch-reuse A/B experiment (2026-09-18)

**Hardware update: both variants completed the comparison; the outcome below
does not justify promoting46B.** The experiment tests the agreed candidate without
45's intrusive per-tile profiling. It is not a confirmed performance fix.

### A versus B

| Variant | Game contents | Native timing |
| --- | --- | --- |
| **46A — Control** | Exact clean42 Terraria bytes, including41 history fix and42's two compiler guards | Existing sparse NX_PHASE |
| **46B — Reuse** | Same baseline plus scoped TileDrawInfo/Vector3[9] reuse | Identical sparse NX_PHASE |

Original method signatures, original fields/visibility, renderer, input,
simulation policy, effects/RNG ordering, assets and save/network methods remain
unchanged in the accepted candidate. Only Draw and DrawSingleTile bodies receive
the storage/lifetime splice; a reset method and private helper/scope types are
added. No43/44/45 detailed observer types are included in either game.

Each Draw installs a thread-local, invocation-owned scope and restores the
previous scope in a finally. The descriptor/array is allocated lazily on its
first eligible tile, then reset to fresh-constructor state between tiles.
Reset covers all18other fields and all27 vector components, restores the owned
array reference, and does not clear real Tile/Texture objects or a shared glow
array. Empty/fully filtered passes allocate only the scope.

Acquire captures a lease and marks the scope busy before fallible work. A
Single-level finally releases that captured lease on all five return paths and
exceptions. Unscoped calls, other-owner calls and recursive Single calls while
the parent is busy receive fresh original storage. Nested Draw invocations get
separate scopes, including same-owner nesting; ThreadStatic separates threads
without claiming that Terraria's general renderer is thread-safe.

### Ownership audit and executed proof

The bounded source/IL audit covered the complete original Draw/Single paths,
all seven descriptor consumers, draw-data/outline byrefs, both Vector3-array
lighting fillers, original constructor/class metadata and the FNA Color/
Rectangle interior-this boundaries. No descriptor/owned-array retention,
escaping interior pointer or scratch-identity requirement was found on those
vanilla paths. Typed-field absence alone was not treated as proof. The class
is Object-derived, with no interfaces and only its constructor—no custom
finalizer or identity override. Owner-cache mutations, particles and other
side effects were retained, not mistaken for disposable rendering work.

Fresh parent acceptance and repeat runs pass **258 checks /48 scenarios**.
The executable fixture runs full serialized original/candidate Draw and Single,
the original scratch constructor, real reset and actual scope/lease helpers.
It checks poisoned fields/vector elements, consecutive different tiles,
special/early-return branches, null/bounds/throw paths, byref/value returns,
effect/RNG boundary order, original exception identity, reset failure, recursive
Single, nested Draw, owner mismatch and overlapping thread-local scopes.
External game/graphics calls are deterministic boundaries, not a real GPU or
complete game simulation; exhaustive branch/mod/multiplayer/OOM coverage is
not claimed.

Reverse-splice checks reconstruct the baseline's Draw815 instructions/17 locals
and Single2,312 instructions/61 locals, both originally without handlers.
**21,040 unrelated bodies,32,059 original fields and100 managed resources** are
preserved, as are native resources, references, signatures and compiler guards.
Canonical raw signatures and pinned target SDK references pass. Repeated game
and probe bytes match; unsupported/already-patched/FNA-mismatched and aliased/
existing/symlink/hardlink outputs are rejected. Accepted output is read-only.

Host allocation results—not Switch object sizes or performance:

| Fixture workload | Control | Reuse |
| --- | ---: | ---: |
| 20,000 Single calls, one active scope | 5,120,000B | 304B |
| 20,000 unscoped Single calls | 5,120,000B | 5,120,000B |
| 1,000 Draw calls ×65 tiles | 16,640,000B | 304,000B |
| 1,000 empty Draw calls | 0B | 48,000B |

The host scope is48B; the original descriptor/array pair is256B. Scope overhead
and the empty-pass increase are included rather than hidden. Reentrant fresh
fallback also retains its allocation cost. Nine alternating warmed host rounds
are recorded; the final fixture's median was13.600ms control versus11.915ms
reuse per1,000×65 calls. **This is not a Switch FPS result**: external boundaries,
host runtime/GC and scheduling differ from the game on hardware.

### Recovered build prerequisites and matched native inputs

The old /tmp SDK disappeared during the resumed work. Recovery now lives under
`~/.cache/terraria-switch-build/recovery46/`, not temporary storage. The rel-3
SDK archive SHA256 is `3248d136503e50af337b0ba373771d471fb48a85bf777b5010e570f47de2c098`;
its CoreLib matches the already verified deployed bytes/MVID. The fixed native
runtime and65,536-entry CoreLib AOT object are retained unchanged.

Recovered pinned FNA3D/FAudio/MojoShader libraries are shared by A and B. The
OpenGL driver object matches the retained original byte-for-byte. Original
full-library archive identity was not independently proven, so recovery did
not assume that matching version numbers meant identical binaries. Crucially,
linking A with the retained18exact clean42 native launcher/shim objects and
original AOT objects produces **all26 allocated ELF sections at identical
addresses, sizes and bytes to original42**. This verifies the actual loaded
control code/data, stronger than merely comparing library version strings.
Debug/file-container bytes and the new A/B NACP labels are not claimed identical.

B uses those same native objects/libraries and all six original non-game AOT
objects; only the Terraria AOT object changes. Reused module dependency name/
MVID lists match and contain no Terraria identity dependency. Full native
verification checks **192,304 A targets /192,311 B targets**,15,010 fallback
sentinels each and seven module-info bindings each. Method tables are read-only
and non-executable; no RWX load segment exists.

Actual RomFS inspection hashes **every embedded file** in both NROs:16,183 files,
including15,997 Content files. **Only Terraria.exe differs**;16,182 other file
hashes/sizes match. No fixture DLL or build-time helper is embedded.

### Published artifacts

Both are under `fna-nx-test/terraria-mono/switch/`:

- **A:** `mono_nx_fna_terraria_nochroma46A_aot_control.nro`
  - NACP title: `Terraria 46A Control`.
  -950,591,092 bytes; SHA256 `6f0a96d45af05ee4d40da27e10243baf46f2066e46e23ae95457497a8247ef8b`.
  - Game SHA256 `d4c767a545d5f1a4cacbb6b0d3b16be68b6d1b9af1962d0cd439c3b7946b7298`.
- **B:** `mono_nx_fna_terraria_nochroma46B_aot_scratch_reuse.nro`
  - NACP title: `Terraria 46B Reuse`.
  -950,596,724 bytes; SHA256 `75bcec03236587d80afbbf842ffd0aa6ba05d5d39cc4fedff5b15cf150499763`.
  - Game SHA256 `42894033581282145402dc984df22f18027a54ec63c05932de274a6b19a1be08`;
    MVID `519e8068-9dce-ed93-41da-bff742edcf73`.

### Hardware comparison procedure

1. Keep42/44/45. Copy A and B alongside them; do not replace SD runtime DLLs,
   ICU, configuration, saves or bindings. Use full application mode, identical
   graphics/zoom/lighting settings and Frame Skip On. Confirm logging=1 and
   runtime_logging=0 in each log.
2. Run **A first**. After loading has settled, use the same player/world and
   camera position planned for B. Aim for at least a minute of steady gameplay,
   then a similar short movement route. Keep inventory/map/pause out of the
   principal measured segment. Record roughly when each segment occurred.
3. Exit normally. **Copy `/mono/log.txt` to `log46A.txt` before launching B**,
   because the next run can replace the log.
4. Run **B** with the same scene/settings and comparable activity. Save its log
   as `log46B.txt`. If practical, repeat A afterward to expose warm-up or scene
   drift. Day/night, weather, NPCs and world changes can affect the comparison;
   use a disposable world copy if a precisely repeatable start is needed.
5. In B, also watch for wrong colours/glow/animation frames, flicker, special-
   tile glitches or crashes. Stop using B and retain its log if anything differs
   incorrectly. No multiplayer test is requested now.

These clean builds emit NX_PHASE, **not NX_PROFILE**. The detailed-profile
analyzer is not the right parser for these logs. Compare matched post-loading
native Draw/Tick/Update windows and stalls; do not average menus/loading into
gameplay or compare B's clean FPS directly with45's instrumented FPS. A few-
percent result needs repeated comparable windows, not one momentary counter.
Keep B only if the measured benefit is repeatable and worth the added code and
any identified adaptation cost; reject it if performance or correctness is worse.

### Reproducibility and scope

Tooling: `scripts/patch_tile_reuse/`. Run its `run.py` inside monobuild with
project/cache mounts and the persistent recovered SDK mounted at /mono-nx;
the optional frame-probe input is host-fixture infrastructure only. Current
final acceptance is `scratch46-final/`. Audit extraction/evidence is
`scratch46-audit/`; native recovery provenance is `recovery46/native-deps/`;
all-file/native verification and published identities are under `ab46/`.
`ab46/verify_pair.py` is the executed full comparison verifier.

The vanilla ownership model passed its bounded audit and executable checks;
arbitrary mod hooks that retain scratch references are not certified. Existing
method signatures are preserved, but this does not promise unchanged private-IL
hook compatibility. The user-approved discussion gate still applies to any
newly discovered gameplay/protocol/save or material mod-adaptation consequence.
Multithreading, compression, simulation throttling and other broad changes were
not added to this experiment. **Hardware captures are now analysed below.**

## Build 46 hardware A/B outcome (2026-09-18)

**Decision: do not promote scratch reuse. Keep46A/control (or clean42) as the
working performance baseline and retain46B/tooling as experimental evidence.**
The user reported no noticeable lighting, tile-colour or update differences,
but no perceived speedup. The timing captures show no demonstrated worthwhile
benefit. They do not establish a precise intrinsic slowdown because activity
and scene equivalence are not controlled closely enough.

### Capture integrity

- `log46A.txt`, SHA256
  `48787f3c8366034f52b3faa69ee1e41dcf30eb48cdc66b9b529fe1c348fc410b`:
  137 complete NX_PHASE windows,747.282s native elapsed,19,589 Draws and40,260
  Updates; normal termination.
- `log46B.txt`, SHA256
  `8b48078b39473a043373ae297ec161dc52cc4a4467a22e86ab2c9b41b4707052`:
  78 complete windows,452.335s,14,948 Draws and22,519 Updates; normal termination.
- Every window parses and reconciles with cumulative tick/update/draw/poll/swap
  counts. Both captures report native AOT entry, the same NV120 / GL4.3
  Compatibility / Mesa20.1.0-rc3 / glsl120 path and logging=1/runtime_logging=0.
  There are no detailed NX_PROFILE cohorts in these clean builds.

World entry is inferred from loading/render-target/game markers and the change
to sustained world Update/Draw timings: the transition windows end at225.949s
in A and163.634s in B. Exact menu/inventory/pause state is not encoded in
NX_PHASE, so the selections below are explicit approximate workload selections,
not secretly inferred perfect scene matches.

### Comparable early-window check

Use complete windows30–60 seconds after those entry boundaries. This gives five
windows per run, avoiding the entry interval and later obvious UI-heavy segment:

| Measure | A control | B reuse |
| --- | ---: | ---: |
| Actual selected time | 256.149–281.281s | 193.830–218.920s |
| Window duration | 25.132s | 25.090s |
| Draw calls | 475 | 411 |
| Draw calls/sec | **18.900** | **16.381** |
| Mean Draw cost | **35.691ms** | **37.085ms** |
| Mean Update cost | 5.166ms | 6.319ms |
| Updates/sec | 60.003 | 59.984 |
| SDL polls per Draw | 1.000 | 4.372 |
| Maximum Draw in these windows | 54.169ms | 127.553ms |

Descriptively B's mean Draw is3.91% higher and draw rate13.33% lower here, not
better. **Do not label those percentages the causal cost of reuse.** B also
has22.31% higher Update cost and much more input polling. Polling is an activity
indicator, not proof of any particular player action or CPU cause.

Other selections do not reveal a win:

- A's longer settled section256.149–336.630s averages19.122 Draw/s and35.188ms
  per Draw. B193.830–223.954s averages16.432 Draw/s and37.509ms. Unequal duration
  and activity make this descriptive only.
- An active-input sensitivity check uses A628.132–673.307s:17.709 Draw/s,
  36.058ms/Draw,4.045 polls/Draw, versus B's16.432 Draw/s,37.509ms/Draw and4.388
  polls/Draw above. Similar polling does not establish the same scene/UI state.
- Whole inferred-world averages are especially unsuitable as a controlled
  comparison: A has91 windows/457.394s versus B25 windows/126.451s. A includes
  several death markers and a long low-polling period; B includes earlier
  item-prefix UI activity and a mining achievement. Do not average those
  differences into a claimed optimization effect.

Window maxima show no demonstrated stall benefit in the selected sections,
but they are not per-frame percentiles and cannot establish a complete GC or
frame-stall distribution. No CPU/GPU clock or identical-scene proof is available.

### What this means for the optimization

The host allocation reduction remains real, and the user-reported visual result
is encouraging for the bounded correctness work. Neither establishes a useful
hardware speed improvement. **[INFERENCE]** Field/array resetting, scope/lease
and ThreadStatic access, finally cleanup or changed AOT code generation can
offset fast allocation savings; these logs do not isolate their individual
costs. Different workloads can also mask or exaggerate a small effect.

The correct adoption rule is therefore **no demonstrated benefit, no promotion**,
not "keep it because allocations dropped" and not "prove reuse is universally
slow." A tighter repeated standing-still A–B–A test is optional if resolving a
few-percent effect becomes worthwhile; it is not required to decline adding
this complexity now. No game files or earlier builds were deleted or changed
by this analysis.

Next investigation returns to the measured lighting/frame-helper group, then
other measured areas such as UI. Do not automatically pile more pooling tweaks,
simulation changes, threading or compression onto this result. The existing
compatibility tradeoff/discussion policy and deferred multiplayer testing stay.

Machine-readable evidence:
`~/.cache/terraria-switch-build/ab46/hardware-comparison.json` preserves both
complete native-window parses, marker positions, all selections/weighted totals,
descriptive deltas, limitations and the non-promotion decision. **No new NRO
was produced during this analysis.**

### Next source target: an unconditional lighting lookup with limited use

Continue from46A/clean42, not the unpromoted reuse variant. A targeted read of
the original GetTileDrawData found a concrete candidate inside the measured
lighting/frame group:

- DrawSingleTile already calls Lighting.GetColor(x,y) at IL_0050.
- GetTileDrawData unconditionally calls the same coordinate lookup at IL_0046
  and stores the result in localV_0 at004b.
- All localV_0 load/address forms were checked in the extracted IL. The only
  reads are IL_291d and296c, both in special glow-colour Lerp paths. Most paths
  do not consume this second result.

**[INFERENCE]** Skipping an unused second lookup may be a smaller, more direct
optimization than pooling plus ownership bookkeeping. It is not yet proven
safe or worthwhile. First identify the exact branches which need it and audit
Lighting.GetColor/callees for side effects, state/read-timing and valid-input
exception requirements. Prefer guarding the lookup at its existing point for
the paths that use it, rather than blindly substituting the caller's older
colour or moving reads across state-changing work.

If that audit supports a behavior-preserving change, implement only that change
and run a clean A/B with matching scene/activity and sparse timing. Otherwise
retain the lookup and use a targeted timing split to find the actual expensive
helper. No lower-precision lighting, throttled simulation, quality reduction or
claimed FPS gain is part of this candidate. No game patch was applied in this
source-target assessment.

Evidence: `scratch46-audit/clean42/03-TileDrawing-DrawSingleTile.il` and
`12-TileDrawing-GetTileDrawData.il` in the build cache, particularly the entry
and IL_28ea–297c glow paths. The existing45 group measurement remains combined;
it does not isolate the cost of this lookup.

## Build 47 guarded lighting lookup experiment (2026-09-20)

The source audit supports a single, narrow change for the pinned vanilla client
render paths. **Hardware update:** the A/B runs completed normally, with no
user-noticed visual difference, but no demonstrated useful speedup. The
[hardware outcome below](#build-47-hardware-ab-outcome-2026-09-20) leaves47 unpromoted.

### Change and semantic boundary

- Start from exact42/46A, SHA256
  `d4c767a545d5f1a4cacbb6b0d3b16be68b6d1b9af1962d0cd439c3b7946b7298`.
- Only `TileDrawing.GetTileDrawData` changes. Five IL instructions implement
  the unsigned test `(uint)(typeCache - 637) > 1`, bypassing the original
  `Lighting.GetColor(x,y)` and its local store for all other types.
- Types **637 and638** retain the original lookup at its original operation
  point, after the existing output defaults. No older caller colour is reused,
  and no required read is moved down to the glow calculation.
- The only reads of localV_0 are original IL_291d/296c, in the two corresponding
  glow-colour Lerp paths. All5,140 original instructions remain; five are added.
  No helper, field, type, dependency, timer or instrumentation is added.
- Renderer quality, simulation/frame skip, input, runtime/CoreLib, and the
  sparse native NX_PHASE logger remain unchanged.43–45 profilers and46B reuse
  are absent.

The getter itself is not universally safe to delete. `Lighting` initialization
allocates engines/maps/scanners and seeds random state; its static initializer
alone does not populate `_activeEngine`. The supported lifecycle resolves this:
the client splash path calls `Initialize_AlmostEverything` and
`Lighting.Initialize` before enabling world drawing. Capture has its own
initialization before tile drawing. Both shipped engine getters then read
existing state without shared writes, random sampling or cache updates.

All seven direct callers and GetTileDrawData are private. DrawSingleTile,
DrawVineStrip and DrawRisingVineStrip already complete a same-coordinate lookup
before calling the helper. The four grass/multitile callers instead perform
their other lookup later, after `_rand.Next`; their safety relies on the
initialized built-in render-state invariants, not a supposed earlier lookup.

**Explicit limit:** malformed/partially initialized lighting state is not
exception-equivalent. A skipped query that formerly threw can now permit more
output writes or a random call before a later failure. A future stateful
lighting hook or substituted engine would also observe fewer calls. These
differences were demonstrated/reported, not silently waived. The audit found
no concrete normal-rendering, initialization/lifetime, protocol/save or material
mod-adaptation regression in this exact unmodded source. Re-audit modified
getters/new callers; this is not an arbitrary-mod compatibility guarantee.

### Verification completed

- Exact source/CFG audit across all65,536 possible UInt16 argument values:
  only637/638 can reach the two light-local reads, and their retained lookup
  dominates those reads. This is an argument-domain proof, not a claim that
  every UInt16 value denotes a valid game tile.
- Whole-module preservation:21,041 other methods,32,059 fields,2,957 types,
  100 managed resources, native resources, signatures/flags and references are
  preserved.17 raw-signature rows independently decode identically. The two
  existing42 managed compiler guards remain intact.
- Executed real serialized entry/glow-tail IL with explicit host dependencies:
  131,072 normal prefix calls plus28 failure calls;24 normal consumer-tail calls
  plus96 failure calls. Checks cover coordinates/query counts, all12 by-reference
  outputs, colour propagation, textures/rectangles and exception identity/order.
  Seven malformed guard/CFG variants are rejected. **The full5,140-instruction
  helper was structurally checked, not executed end to end on the host.**
- Repeated acceptance produces identical game and probe bytes. Unsupported
  input/FNA, already-patched input, existing outputs and path aliases are rejected;
  source files remain unchanged and the accepted game is read-only.
- Only Terraria was recompiled:52,792/52,816 methods, matching42 coverage. Six
  non-game AOT objects are reused byte-for-byte. Early name/MVID bindings pass.
  Native disassembly confirms the UInt16 load, subtract637, compare1 and unsigned
  branch over the original GetColor call/store.
- Replayed control linking matches **every allocated section** of46A exactly.
  The initial replay exposed a build detail: changing the ELF basename changes
  `.nx-module-name` and the build ID. Both variants now link as `mono_nx_fna.elf`
  in separate directories; the mismatch was corrected, not excluded from checks.
- Both images pass192,304 native method-target checks and15,010 fallback-sentinel
  checks, with read-only/non-executable method tables and no RWX load segment.
  All16,183 embedded files were hashed: **only Terraria.exe differs**;15,997
  content files and all non-game payloads match. Only the displayed NACP title
  changes outside game code/metadata; the icon and other NACP fields match.

### Published comparison

Both files are under `fna-nx-test/terraria-mono/switch/`:

| Role | File | Status |
| --- | --- | --- |
| A | `mono_nx_fna_terraria_nochroma46A_aot_control.nro` | Existing46A, unchanged; active control |
| B | `mono_nx_fna_terraria_nochroma47_guarded_light.nro` | Hardware-tested experiment; no user-noticed visual difference; no demonstrated useful gain, not promoted |

B is950,591,092 bytes, SHA256
`05ed7890b2c5eae063845c0f11730b9d73c6ca813d4e328360e491839f24b2a5`.
Its game SHA256 is
`f40c90127b76b79ae18b9a412cc17398e6103c149fccb3034c01657b833a6a0c`,
MVID `c0a5a648-b53a-f514-f13d-1b27ea8a5983`.
A retains SHA256
`6f0a96d45af05ee4d40da27e10243baf46f2066e46e23ae95457497a8247ef8b`.

### Hardware capture procedure and acceptance

1. Run **A/46A again**, then B/47. Do not reuse the differently active46 captures
   as the main comparison. Use full application mode, the same world/player,
   position/camera, lighting/graphics/zoom settings and Frame Skip On. Keep the
   current SD runtime, ICU, configuration, saves and bindings; no runtime update
   or multiplayer test is required.
2. In each run, load the world, return to the chosen position and let loading
   settle. Then record a clearly identified **60-second stationary segment**:
   no movement, inventory/menu actions or item use. Note when it starts/ends and
   any scene/weather/time-of-day differences. Keep movement/testing as a
   separate, similarly timed segment afterward.
3. Exit normally so the final counters are emitted. Copy `/mono/log.txt` before
   starting the next run: A as `log47A.txt`, B as `log47B.txt`. An optional second
   A run (`log47A2.txt`) helps distinguish a small change from scene/time drift.
4. Confirm `logging=true`, `runtime_logging=false` and the actual NX_CONFIG
   `runtime_logging=0`. Both use NX_PHASE, not the detailed NX_PROFILE analyzer.
5. Report incorrect colours/glow, animation changes, flicker or crashes as well
   as speed. If the relevant glow tiles are absent, do not claim visual coverage
   of those special paths. Unchanged appearance does not establish a speedup.

Judge comparable steady windows by Draw cost/rate, Update, stalls and polling;
do not merge stationary and movement periods or subtract inclusive poll/swap
times blindly. Keep46A/42 unless hardware demonstrates a useful benefit without
a supported-path correctness regression. Fewer lookups alone are not adoption
evidence, and45's combined lighting/frame measurement is not this query's cost.

### Reproducibility evidence

Persistent source: `fna-nx-test/scripts/patch_light_lookup/` (pinned patcher,
metadata/raw-signature checks, independent CFG/serialized-slice proof and runner).
Run `run.py --output <fresh-container-path>` in monobuild with the existing SDK,
project scripts and build cache mounted; do not overwrite accepted outputs.

Under `~/.cache/terraria-switch-build/`:

- `light47-audit/semantic-review.json` and its `closure42/`, `dependencies42/`,
  `callers42/`, `lifecycle42/` extracted IL retain the source audit and limits.
- `light47/host-check/runner-results.json`, `accepted/acceptance.json` and
  `accepted/proof/light-lookup-proof.json` retain executable/metadata evidence.
- `light47/build_aot.py`, `build_native.py`, `aot/build-manifest.json`,
  `aot/runtime-metadata-manifest.json` and `native-final/native-build.json`
  record the single-assembly build and exact native replay.
- `light47/native-guard-verification.json`, `pair-verification.json` and
  `published-builds.json` identify the verified code/payload and delivered files.

No hardware speedup, exhaustive visual result or mod-loader compatibility is
claimed by these host checks.47 remains an experiment, not the working baseline.

## Build 47 hardware A/B outcome (2026-09-20)

The new uploads are **`fna-nx-test/logA.txt` and `logB.txt`**, not the earlier
scratch-reuse `log46A.txt`/`log46B.txt` captures. The user reports no visual
difference and says B included movement before standing still. **No useful
hardware speedup is established. Keep46A/control or clean42; retain47 only as
an experiment.** This is not a proof of zero intrinsic effect or a causal
slowdown, and no new NRO was built during this analysis.

### Capture validation

| Measure | A —46A/control | B —47 lookup guard |
| --- | ---: | ---: |
| Complete native records, including final partial record |49|52|
| Final elapsed seconds |306.536|319.479|
| Total Tick calls |7,046|6,494|
| Total Update calls |13,802|14,618|
| Total Draw/Swap calls |7,045|6,493|
| Total SDL poll calls |7,580|7,830|
| Normal termination |Yes|Yes|

All window counts sum to the reported cumulative/final totals. Both logs show
`logging=1`, `runtime_logging=0`, native Terraria entrypoint resolution, AOT plus
interpreter, OpenGL4.3 Compatibility/NV120 and glsl120. Heap addresses and
reported allocations differ slightly (A: libnx1518MB/Mono1512MB; B:1516/1514MB).
Whole-run rates are not a gameplay comparison: both include loading, menus and
shutdown. Build assignment follows the user's A/B labels; these logs do not
print the NRO hash, game MVID or47 title. They cannot independently certify
the complete binary identity or identical graphics/scene/hover state.

Capture SHA256:

- A: `3b0328792bfef2caea6188131437e02501b848d1a8fe553c91f6fbc516be2063`
- B: `617d9ed3dd8e2445e7f84e16ddc2a888ae68ce52c29c03f0d2fbf3035f6bd6c5`

### Movement, deaths and unequal label work

B has about3.94–4.52 polls per Draw during elapsed140.737–165.942s, followed by
lower polling and later1.00-poll-per-Draw periods. **[INFERENCE]** This aligns
with the reported early movement; there is no explicit stationary marker.
Polling is an activity proxy, not proof that a held input is absent. The main
matched comparison below excludes that early portion.

Death messages also bracket changing workloads:

- A:172.779–177.814s (Demon Eye),197.939–202.945s (Zombie).
- B:186.027–191.141s and251.473–256.483s (Zombie).

These are bounding native-report intervals, not precise death timestamps.
Dead/respawning windows have different Update and Draw costs; mixing them into
a stationary average can change its apparent result.

The important additional confound is repeated label-getter work. A contains
**2,914** exact `[PrefixName]: GetValue(ItemPrefixCombiner)` /
`[ItemName]: GetValue(ItemPrefixCombiner)` messages; B contains **722**. A emits
two messages per Draw throughout most of its quiet gameplay. B's later quiet
period has none after the report at196.162s. Standing still did not produce
matching label-access/diagnostic activity.

The origin is source-verified, not a guessed warning: the shipped
`ReflectPropertyDescriptor.GetValue` IL formats an interpolated string and
calls `Debug.WriteLine` atIL_0069; matching runtime source is
`System.ComponentModel.TypeConverter/.../ReflectPropertyDescriptor.cs:903`.
This diagnostic remains with native `runtime_logging=false`. It establishes
repeated property access/printing, **not which exact inventory, hover or
controller state caused it**. Its time cost is not isolated here, so the
difference cannot be subtracted or credited to the lighting guard.

### Closest short comparison and sensitivity

Two consecutive complete windows in each run have1.00 polls per Draw, two
label-getter messages per Draw, and no death message within them:

| Measure | A:187.874–197.939s | B:175.993–186.027s |
| --- | ---: | ---: |
| Duration |10.065s|10.034s|
| Draw calls |172|170|
| Draw calls/sec |17.09|16.94|
| Mean Draw cost |37.134ms|37.124ms|
| Mean Update cost |5.861ms|5.945ms|

B's Draw cost is0.028% lower here—essentially flat at this evidence resolution,
not a useful demonstrated improvement. Another matched-label A pair at
218.021–228.106s gives37.315ms versus the same B37.124ms (0.514% lower), while
B's Draw rate is still slightly lower. **These short windows do not establish
statistical equivalence or identical scene geometry.**

A broader rule-based check keeps all complete low-polling world intervals
after a30-second warmup and excludes each death-containing interval plus10s
after its upper bound. **[INFERENCE]** World bounds come from loading/menu
transitions, not explicit state markers. Splitting this cohort by label activity:

- Matching two-label-messages-per-Draw: A has6 windows/30.227s at37.489ms and
  16.872Draws/sec; B has2 windows/10.034s at37.124ms and16.942Draws/sec. The
  apparent Draw-cost reduction is0.975%, with only0.415% higher Draw rate.
- B's zero-message cohort has10 windows/50.257s at36.386ms and17.510Draws/sec.
  A has no eligible zero-message counterpart, so this is not a matched gain.

Other reasonable but less-controlled selections change the sign/size:
50-second low-polling periods give A35.716ms versus B36.410ms, while a later
alive comparison gives A37.386ms versus B36.407ms. The former mixes different
death/respawn periods; the latter compares A's two label messages per Draw with
B's none. Neither is a causal penalty or a clean2.6% lighting speedup.

All means are count-weighted: sum of phase milliseconds divided by call count;
Draw rate is call count divided by summed window duration. Poll/swap are
inclusive and are not treated as independent costs. Selections were exploratory,
not pre-registered or randomized. The unequal durations and small matched B
sample remain limits rather than being hidden behind a single percentage.

### Decision and next target

- Mark47 hardware-tested with normal termination and user-reported visual
  consistency. This does not certify coverage of the special637/638 glow paths,
  exhaustive visuals or arbitrary mods.
- **Do not promote47.** Keep46A/42 and preserve the candidate/proof as experiment
  history. The matched evidence does not justify adopting it for performance;
  it also does not prove the skipped lookup has absolutely no effect.
- Return to the already identified UI label/reflection path and its diagnostic
  string formatting/`Debug.WriteLine`. Any next comparison must isolate that
  source-level work while preserving property lookup, results, exceptions and
  assertions—not suppress all stdout/errors or silently change simulation.
  Its speed benefit remains to be measured; no such patch is applied here.

`~/.cache/terraria-switch-build/light47/hardware-comparison.json` retains both
complete parsed timelines, counter validation, regex/formulas, event intervals,
label-message counts, all selections/cohorts/deltas and the non-promotion decision.
Byte-identical copies of the uploads are in `light47/hardware-captures/`; generic
`logA.txt`/`logB.txt` upload names may be replaced in a later test. Existing
host/native proof records remain records of what was verified at build time;
the current hardware status is recorded here and in the build ledger.

### UI target also runs outside inventory

A direct clean42 IL trace answers the inventory-scope question. This is not
only an inventory-grid path: `Main.GUIHotbarDrawInner` reads `playerInventory`
atIL_0000 and returns atIL_04db when it is true. With inventory closed (and the
player not a ghost/spectator), a nonempty selected item's name is obtained via
`Item.AffixName` atIL_00b2, then measured and drawn above the hotbar. A hovered
hotbar slot also calls AffixName atIL_02fb. Thus item-name formatting can occur
during normal walking/mining/combat rendering without an inventory screen.

`Item.AffixName` returns the plain Name for prefix0/out-of-range prefixes; a
valid prefix calls `Lang.GetPrefixedItemName` atIL_002b. The latter constructs
an ItemPrefixCombiner containing PrefixName and ItemName and calls
`LocalizedText.FormatWith` atIL_005a. `Main.DrawMouseOver` also calls AffixName
atIL_011a while handling the hitbox of an active world item. These are concrete
non-inventory-grid uses, not an inference from the diagnostic text alone.

The previous43 normal-world cohort explicitly had inventory closed and measured
6.778ms/frame for the whole Interface region, versus17.061ms with inventory
open. **Neither number is the isolated cost of these diagnostics.** Removing
diagnostic formatting/output could help frames taking this label path; it is
not a proven universal gameplay speedup, and no benefit from these particular
calls should be assumed when the calls are absent. Different selected-item
prefixes are a source-backed possible reason for differing label activity,
not a reconstruction of what the user selected in either47 capture.

Exact inspection output is under
`~/.cache/terraria-switch-build/ui-label-audit/hotbar42/`, especially
`Main-GUIHotbarDrawInner-06000fae.il`, `Main-DrawMouseOver-06000f9e.il`,
`Item-AffixName-0600089b.il` and `Lang-GetPrefixedItemName-060001ef.il`.
Only the read-only inspection tool/IL evidence and these notes were added;
no game/runtime patch or new NRO was produced for this clarification.

## Build 48 UI getter diagnostic experiment (2026-09-20)

**Hardware update:** the new A/B captures completed normally and the entry-trace
removal is observed. A consistent gameplay speedup is not established; see the
[hardware outcome](#build-48-hardware-outcome-2026-09-20).48 remains unpromoted.
This experiment starts from46A/clean42, not47 or46B. Its only managed payload
change is `System.ComponentModel.TypeConverter.dll`; Terraria.exe, FNA.dll,
content, renderer settings, input and the external SD runtime remain unchanged.
No full JIT, runtime replacement, UI-label cache or simulation change is added.

### Exact change

`System.ComponentModel.ReflectPropertyDescriptor.GetValue(object)` originally
formats and emits this entry diagnostic on every call:

```text
[{Name}]: GetValue({component runtime type name})
```

48 removes original IL_0001–006e:39 instructions constructing that interpolated
string and calling `Debug.WriteLine`. The original first nop, all122 remaining
instructions,11 locals, method flags, MaxStack4 and catch region remain.
The original InitLocals=false flag is retained; no new helper or field is added.

Preserved behavior includes the extender predicate/null result and its separate
diagnostic, Debug.Assert for a null component, the other null diagnostic,
GetInvocationTarget, custom property-owner resolution, actual reflected getter
invocation, returned values/null/identity, and all error wrapping/site-name
handling. Constructor diagnostics and other Debug.WriteLine calls remain.
This is not a global stdout/error filter or a Release rebuild that drops asserts.

The concrete descriptor is sealed, has no Name override, and inherits a simple
stored-name getter. Removing the entry formatting does not skip a component
getter or supported custom property-owner callback. It intentionally removes
the entry debug-provider/output/pool effects and failures caused solely by that
diagnostic. Arbitrary DebugProvider/EventListener hooks doing application work
are not promised equivalent; no such gameplay dependency was established here.

The hotbar path runs with inventory **closed**, and a selected item with a valid
prefix invokes this formatting route. This makes the target relevant to normal
gameplay, not just inventory screens. Its isolated Switch cost remains unknown;
the earlier6.778ms whole-Interface measurement is not the expected saving.

### Framework identity and AOT binding

Original framework identity:

- SHA256 `0ce46870f3f384b10f37da068bdbbbeeb567cb58aa69bb0ad0d6511305689ab6`
- MVID `b17425f9-1fbc-4be3-9b13-ecda72163fe0`

Candidate framework identity:

- SHA256 `0264aae0ccf065df1febc3f20d5ebdc48e0a839cf0e1881961a5143dc89dea9e`
- MVID `4992c31a-f9a4-64a8-8f68-3eb1ef736214`

Assembly name/version9.0.0.0, public key/token `b03f5f7f11d50a3a` and PE flags
remain. The original128-byte strong-name slot is **all zero**, and has no
Authenticode certificate. Its zero-slot/public-signed convention is retained;
no cryptographically valid Microsoft signature is claimed.

The parsed AOT image tables hard-bind the original DLL from **Terraria, FNA and
Newtonsoft.Json**. All three were recompiled against the genuine new staged
MVID; the other four AOT objects stay byte-identical. We did not keep the old
DLL MVID or merely relabel stale original AOT metadata. Mono's dependency loader
rejects a GUID mismatch by marking the module out-of-date; all85 name/MVID
bindings now validate against their actual providers.

The existing launcher mounts/enters RomFS before opening the game and validating
its native entrypoint, then returns the writable cwd to SD. AOT dependencies load
eagerly by default. Loaded-assembly caches precede filesystem probes, so there is
no blanket all-context RomFS-precedence claim; the new Switch startup still needs
verification. An unavailable native Terraria entrypoint is already fail-closed.

### Recompilation finding: unwritten ARM64 alignment filler

The initial exact-code gate correctly stopped the raw build: freshly recompiling
the three bound modules changed139 `.text` bytes despite identical method counts,
sizes and source assemblies. Every difference was subsequently proven to be in
one of26 **post-return alignment gaps**, before an unchanged aligned Vector128
constant—not an instruction or a changed SIMD constant.

The ARM64 emitter in `mini-arm64.c:6644-6645,6691-6700` reserves16 constant bytes
plus alignment, advances the cursor to a16-byte boundary, fixes the literal load
to that aligned address, and copies the constant. It does not write the skipped
filler. That accounts for the otherwise nondeterministic bytes.

The finalizer restores **only those verified gaps** to baseline bytes in separate
link-input copies. It checks symbols, relocation meaning, decoded control flow,
literal reads and references, aligned constants, and unchanged code/EH/GC layout;
six corrupted-input cases are rejected. Original and raw compiled objects remain
untouched. All fresh compiler metadata stays intact: only the recorded new AOTIDs
and the required TypeConverter GUID differ in other allocated data. No GUID-only
rebinding, arbitrary pool-value substitution or SIMD behavior change was used.

Final native `.text` is byte-identical to46A:101,050,176 bytes, SHA256
`933c503a28a69209df660ac618a7e14c2c3ffe24dfad01bff369b60c29a40969`.
The independently reviewed padding proof is in `ui48/aot-padding-verification.json`.
Raw compilation's stopped gate is retained as evidence; the complete authoritative
AOT manifest/header are under `ui48/aot/link-inputs/`, not the raw-stage manifest.

### Verification completed

- Full getter differential proof:42 property/error/side-effect scenarios plus6
  explicitly different diagnostic-only failure scenarios;13 malformed structural
  variants rejected. Both complete serialized methods execute with real reflection
  and explicit descriptor/Debug/SR boundaries. This is not just a prefix slice.
- Metadata preservation:2,355 other methods,682 fields,309 types,2 managed
  resources, the native resource,19 assembly references and298 type references
  retained.6,094 signature entries decode identically and retain exact raw bytes.
- Repeated output is deterministic/read-only;26 unsafe/unsupported inspect/accept
  cases reject. Original DLLs and patch/proof sources retain their recorded hashes.
- An additional smoke run loads **both complete actual framework DLLs** into
  isolated host AssemblyLoadContexts and uses real TypeDescriptor/provider/
  ReflectPropertyDescriptor/GetValue paths. Live values, nulls, object identity,
  exactly-once getters and wrapped property-error identity/message match.
- The explicit-boundary proof observed1,120,000 versus0 allocated bytes per10,000
  warmed calls; the actual-framework smoke observed2,080,000 versus0 for its own
  component. Different component/diagnostic paths have different allocation costs.
  Neither host measurement predicts exact game allocations or Switch FPS.
- Three rebuilt modules retain their original coverage: Terraria52,792/52,816,
  FNA20,275/20,324 and Newtonsoft.Json16,388/16,399. The changed external GetValue
  body is not present in the AOT objects; its catch also prevents Mono inlining.
- Replayed control matches every allocated46A section. Both linked images pass
  192,304 native method-target and15,010 fallback-sentinel checks, with read-only,
  non-executable method tables and no RWX load segment.
- All16,183 embedded files were hashed: **only TypeConverter.dll differs**.
  All15,997 content files and the exact clean42 game remain. The icon and all
  non-title NACP fields match; the displayed candidate title changes intentionally.

### Published pair and hardware test

Files are in `fna-nx-test/terraria-mono/switch/`:

| Role | File | Status |
| --- | --- | --- |
| A | `mono_nx_fna_terraria_nochroma46A_aot_control.nro` | Existing unchanged control; active baseline |
| B | `mono_nx_fna_terraria_nochroma48_ui_trace_off.nro` | Hardware-tested: entry diagnostics removed, normal shutdown; no consistent FPS gain demonstrated, not promoted |

B is950,590,580 bytes, SHA256
`9f946b7e47783bcb05623f4ff7a4e15fad25c76ec0270a0f4cbec266382a8dca`.
A keeps SHA256
`6f0a96d45af05ee4d40da27e10243baf46f2066e46e23ae95457497a8247ef8b`.
The game SHA256 remains
`d4c767a545d5f1a4cacbb6b0d3b16be68b6d1b9af1962d0cd439c3b7946b7298`.

1. Run A again, then B, in full application mode. Keep Frame Skip On and existing
   SD runtime DLLs, ICU, saves, bindings and configuration. **Copy the new NRO
   only; do not replace an SD framework DLL for this comparison.**
2. Use the same world/player/location/camera and graphics/lighting/zoom settings.
   Choose a safe spot so enemy deaths/respawns do not alter the comparison.
3. Keep the **same prefixed tool/weapon selected**, with inventory closed and the
   hotbar visible. Confirm the selected item's full name/prefix displays correctly
   in both. A plain/no-prefix item can bypass the target entirely. Do not change
   selected items or hover state during the measured stationary segment.
4. Let world loading settle, then record60 seconds stationary. Note its start/end.
   Record comparable movement separately afterward; report any deaths, scene or
   weather differences. An optional second A run helps check small effects.
5. Exit normally and save `/mono/log.txt` before the next run: `log48A.txt` and
   `log48B.txt` (optional repeat `log48A2.txt`). Keep `logging=true` and
   `runtime_logging=false`; check the actual NX_CONFIG and native-AOT entry banner.
6. With this prefixed-item path active, A should show the repeated entry getter
   messages and B should not. Descriptor-creation messages, assertions and other
   diagnostics remain. Missing B messages alone do not establish speedup or prove
   the path was active; the fixed selected item and unchanged name output matter.

Compare weighted steady Draw cost/rate and Update/stalls without mixing loading,
movement, deaths or inventory-open periods. No multiplayer test is required now.
Keep46A/42 unless the hardware result demonstrates a useful benefit and retains
correct item names/prefixes, interaction and rendering.48 is not promoted yet.

### Reproducibility

Persistent patcher/proof: `fna-nx-test/scripts/patch_property_diagnostics/`.
Run its `run.py --output <fresh-container-path>` in the existing monobuild/SDK
environment; use an explicit container entrypoint instead of the image's old
shell wrapper that sources a missing `/mono-nx/env.sh`.

Under `~/.cache/terraria-switch-build/`:

- `ui48-audit/exact/`, `semantic-review.json` and `binding-audit.json`: exact
  getter/owner/name/signing metadata and semantic/load-sequence limits.
- `ui48-patcher-final/runner-results.json`, `accepted/acceptance.json` and
  `accepted/proof/descriptor-diagnostic-proof.json`: final patch-time acceptance.
- `ui48/smoke/actual-assemblies.json`: real complete-framework host smoke.
- `ui48/build_aot.py` and raw `ui48/aot/`: original recompilation evidence and
  exact-code interlock; `ui48/finalize_aot.py` performs strict padding validation.
- `ui48/aot/link-inputs/build-manifest.json`, `runtime-metadata-manifest.json`,
  `mono_aot_modules.h` and `finalization-run.json`: final verified link inputs.
- `ui48/aot-alignment-investigation.json`, `aot-padding-verification.json`,
  `native/native-build.json`, `pair-verification.json` and `published-builds.json`:
  source/byte-level isolation, native replay and actual delivered identities.

All raw/known-good artifacts are retained. No hardware performance, exhaustive
visual or arbitrary diagnostic-hook compatibility claim follows from host proof.

## Build 48 hardware outcome (2026-09-20)

The actual new pair is **`fna-nx-test/log46A1.txt`** (A/control) and
**`fna-nx-test/log48B.txt`** (B/candidate). Generic `logA.txt`/`logB.txt` still
match the archived47 captures, and `log46A.txt`/`log46B.txt` match the old46
captures. In particular, old46B is the scratch-reuse experiment and must not be
mistaken for this control.

The user reports that most gameplay was standing still, with one death in48B
while building shelter. No explicit48 visual/item-prefix correctness report was
supplied; the earlier47 visual report is not transferred to48.

### What worked

Both logs have normal termination, final NX_PHASE summaries and a native
Terraria entrypoint. Window counts reconcile with every cumulative/final
Tick/Update/Draw/Poll/Swap count. Both show runtime_logging=0, AOT plus interpreter,
OpenGL4.3 Compatibility/NV120 and glsl120. These are user/test-labelled runs;
the logs still do not print a complete NRO hash or framework MVID.

| Capture | Native records | Final elapsed | Draw/Swap calls | Update calls | Poll calls |
| --- | ---: | ---: | ---: | ---: | ---: |
| A — log46A1 |74|428.868s|9,130|21,112|10,169|
| B — log48B |62|370.751s|6,970|17,600|9,423|

The intended diagnostic change is observed: A contains **6,462** item-label
GetValue entry messages (6,464 entry messages including startup properties),
while B contains **zero**. B retains the ItemPrefixCombiner descriptor-creation
messages. This establishes the trace-removal behavior, not a speedup. Unlike47,
the differing entry-message rate is the planned experimental change, not itself
an accidental logging-state mismatch.

### Shelter/death exclusion and quiet windows

B's Zombie death is bracketed by elapsed **169.955–174.980s**. The selection
excludes that entire interval plus10 seconds after its upper bound. It also
excludes the later higher-polling activity before the long quiet block; the
result below is therefore not simply an average contaminated by the death.

The rule is: complete nonfinal gameplay windows, at least30 seconds after the
inferred world-entry transition, one poll per Draw, no excluded death interval;
then select the longest contiguous eligible block by duration, **not Draw cost**.
No Draw-cost outlier filter is applied. It produces:

- A: **200.706–376.761s**,35 windows,176.057 summed seconds.
- B: **215.263–310.916s**,19 windows,95.652 summed seconds.

**[INFERENCE]** These are low-input, stationary-like periods, supported by the
user's report—not explicit stationary/build-complete markers. Poll equality does
not rule out held input. Shelter construction, scene geometry, lighting/time/
weather and the selected prefixed item are not independently matched by the log.

### Performance result: selection-dependent, not a consistent gain

| Selection | A Draws/sec | B Draws/sec | A mean Draw | B mean Draw |
| --- | ---: | ---: | ---: | ---: |
| First12 quiet windows (about60s each) |16.30|17.09|40.119ms|37.008ms|
| Last12 quiet windows (about60s each) |17.73|16.17|36.764ms|38.221ms|
| Entire longest quiet block (unequal durations) |17.23|16.70|37.873ms|37.462ms|

The early minute favors B:7.756% lower Draw cost and4.872% higher Draw rate.
The late minute favors A: B has3.961% higher Draw cost and8.778% lower Draw rate.
The first/last B selections overlap; these are sensitivity checks, not independent
replications. The whole-block result is1.086% lower B Draw cost but3.117% lower
B Draw rate, not an FPS improvement.

Update is also different:5.543ms/call in A's whole quiet block versus6.004ms in B
(8.309% higher). A's quiet Draw cost moves from roughly40.4ms to36.8ms; B moves
from roughly36.1ms to38.3ms. **[INFERENCE]** Changing world/simulation/render state
may explain some of this drift, but its cause was not measured. Standing still
does not hold all of those variables fixed, and the patch cannot be assigned
either the early gain or the later penalty from these captures alone.

All means are count-weighted: phase-ms sum divided by call count. Rates use call
count divided by summed window duration. Loading, menus and final partial windows
are not gameplay averages; inclusive poll/swap times are not subtracted as if
disjoint. Selection is exploratory rather than randomized/pre-registered.

### Decision and limits

- Mark48 hardware-tested with normal startup/termination and observed entry-trace
  suppression. Do not claim explicit visual/prefix correctness from a report that
  did not address it, or exhaustive/arbitrary debug-hook compatibility.
- **Do not promote48 as a performance baseline.** Keep46A/control or clean42.
  Retain48 as a tested diagnostic-cleanup experiment. This is not a proof of zero
  intrinsic benefit or a causal slowdown; it is a failure to establish a consistent
  useful gameplay FPS gain in this pair.
- The remaining UI formatting/reflection and measured tile work are separate
  targets from Debug.WriteLine. Do not infer that all UI is cheap, or silently
  stack another speculative optimization on48. A next targeted measurement should
  keep scene/selected-item state and phase boundaries matched before another fix.
- No new NRO, runtime change or gameplay patch was produced in this analysis.

Complete parsed records, diagnostic counts, automatic selection rules, death
bounds, all windows/deltas and decision limits are in
`~/.cache/terraria-switch-build/ui48/hardware-comparison.json`. Byte-identical
uploads are archived under `ui48/hardware-captures/`.

Capture SHA256:

- A: `aefe9e62ec4fce00d2a24152131b0b2ca02668b859c9a21c33389f493b7c7c23`
- B: `24942a56482ff23c2d30c582c52ca8acc411eefe344cef1e2b106cf9d15e9a9b`

Build-time host/native proof artifacts remain historical verification records;
current hardware status is in this outcome, the publication record and the ledger.

## Build 49 focused tile-helper measurement (2026-09-20)

**Measurement-only; host/native verification and hardware capture completed. Hardware results are recorded below.**
The previous profiles identify substantial tile-rendering work, but45 combined
several lighting/frame helpers and had significant observer/report overhead.
49 returns to exact clean42 and measures those helpers individually. It does
not contain46B scratch reuse,47's lighting guard,48's framework diagnostic
removal or the43–45 full profiling infrastructure.

### Measured operations and sampling

Direct callsites inside original `TileDrawing.DrawSingleTile` are bracketed:

| Metric | Original callsites |
| --- | --- |
| `GetColor` | `Lighting.GetColor(int,int)` at0050 and06f4 |
| `GetTileDrawData` |00d3|
| `GetTileOutlineInfo` |016b|
| `DrawTiles_GetLightOverride` |0570|
| `GetFinalLight` |07de|

Sampling selects **one in128 DrawSingleTile invocations**, independently phased
for solid/non-solid eligible top-level passes. The phase rotates without RNG,
including empty/throwing eligible passes. This samples calls after earlier tile
filters, not one in128 world tiles. Raw operation counts, summed ticks and maximum
ticks are retained separately for each helper and layer; GetColor can execute twice.
An unexecuted helper has operations0 and a **null mean**, not a fake zero-cost call.

Visited tile-fetch attempts, eligible slots and attempted Single calls increment
inline Int64 counters in the existing loop. Sample selection occurs at the caller.
An unsampled Single only reads the pending flag into a local and takes conditional
guards; it does not call timing helpers or clocks. Selected calls use a consumed
pending token so nested/unscoped Single calls cannot overwrite a parent sample.
Pass scopes restore thread-local state in finally; nested/partial/invalid work is
explicitly counted or discarded rather than donated to valid helper timings.

Pass/loop gross time and selected-call gross time remain **inclusive of observers**.
A bare consecutive-clock-pair metric and snapshot/report costs are indicators,
not quantities automatically subtracted from helper time. Deterministic rotating
sampling is not proof of statistical representativeness; do not multiply a sampled
cost by every visited slot or turn it directly into a predicted FPS gain.

### State records and exact interpretation

Frame begin/end snapshots read existing fields only—no game property/method calls
or game-state writes. They record menu, pause, inventory, fullscreen map, dead,
ghost/spectator, auto-pause and invalid-state counts, plus player/camera changes,
world time/day state, viewport, raw zoom target and Frame Skip setting.

The scene fields are deliberately precise about their scope:

- Player is `Main.player[Main.myPlayer]`, not a certified identity match to the
  separately stored `SceneMetrics.PerspectivePlayer`.
- Camera coordinates are `Main.screenPosition`, exactly the field returned by
  `Camera.UnscaledPosition`. Width/height are the raw Main viewport fields.
- Zoom is **raw `Main.GameZoomTarget`**, not effective GameViewMatrix.Zoom;
  DoDraw applies ForcedMinimumZoom and clamping separately.
- First/last/min/max ranges and boundary-change counts do not observe a transient
  change-and-restore within a frame, classify every capture/render-target mode,
  or establish identical scene geometry. Normal render-target passes are not
  incorrectly discarded merely because drawToScreen is false.

Only completed eligible normal-world frames with stable boundary state donate
pass measurements. Inventory/map/paused/dead/ghost/spectator/invalid-player or
nonfinite-state frames are excluded. Complete snapshot pairs commit atomically;
`capture_failure_frames` separately counts failed initialization/begin/end captures.
Attempted and accepted work are not conflated:

```text
passes = completed_passes + aborted_passes + nested_passes
selected = completed_samples + aborted_samples
completed_samples = valid_samples + invalid_samples + discarded_samples
```

InvalidPasses is a subset, not an extra pass outcome. Valid completions discarded
with an aborted/invalid pass contribute no timing. Any measurement-invalid flag
invalidates performance interpretation even though raw diagnostics remain available.

### Compact reporting and failure handling

49 reuses the **NX_PROFILE BEGIN/END envelope with version=49,
schema=tile_helpers**. A normal block has19 fixed rows: BEGIN, STATE, SCENE,
two PASS rows, six METRIC rows per layer (five helpers plus clock_pair), REPORT_COST
and END. Reporting has a five-second floor and uses accumulated raw data—not
per-frame/per-tile logging or a scan of the old138-metric registry. The native
NX_PHASE logger is retained unchanged alongside it.

Normal Game.Run completion emits one final envelope even when the last periodic
packet already cleared the window. A zero-frame final packet is truthful, with
unused/null scene and metric means. Flush becomes idempotent only after success.
Output failures retain pending data; invalid clock chains are explicitly invalidated.
Every publish-validation clock precedes END. The next periodic cooldown is anchored
at a later valid root-frame timestamp without moving the window start over already
accepted work. Consequently raw window wall time may include the preceding footer;
report cost excludes that footer and is not observer-free CPU/frame time.

### Verification completed

- Four originals only are instrumented: Main.Draw, TileDrawing.Draw,
  DrawSingleTile and Program.RunGame. Main.Draw's early guards still bypass the
  observer; its original game-flag failure behavior, original calls/arguments/
  byrefs/results and original exception identity/order are preserved.
- Reverse-splicing reconstructs every original instruction, local and handler
  in those methods.21,038 other methods,32,059 fields,2,957 original types,
  100 managed resources and native resources remain unchanged; no assembly
  reference is added. New helper/DTO metadata is separately audited, including
  ThreadStatic and exact SDK primitive/reference encodings.
- Full serialized original/candidate Draw, Single, Main.Draw and RunGame plus
  actual runtime-helper proof passed **462 checks**. Coverage includes five
  returns, conditional helper absence, two GetColor sites,128-pass layer rotation,
  nesting, exceptions, overflow/backward clocks, snapshot/report failure/recovery,
  cooldown and empty final packets.17 actual packets passed the real analyzer;
  35 malformed-report variants were rejected. Ten unsafe/unsupported runner cases
  reject and repeated accepted images/probes are identical.
- In warmed host fixtures, unsampled original/candidate Single calls retain the
  same original256 bytes/call of allocation, with **zero added bytes and zero
  candidate timing-clock calls**. State begin/end10,000-call tests allocate zero
  extra bytes and emit no output; actual host-clock timing tests also passed.
  These are host boundary proofs, not a Switch overhead measurement or exhaustive
  game/GPU execution proof.
- Fresh game AOT passes **52,856/52,880 methods**, retaining the baseline24
  compiler fallbacks;41 helper native bodies are present. Six non-game AOT objects,
  the native runtime/CoreLib and original TypeConverter DLL remain unchanged.
- Replayed control linking matches every allocated46A section.49 passes
  **192,368 native method-target** and15,010 fallback-sentinel checks, with85
  exact assembly/MVID bindings, read-only/non-executable method tables and no RWX
  load segment. All16,183 embedded files were hashed: only Terraria.exe differs;
  all15,997 Content files and non-game payloads match. Only the displayed NACP
  title changes outside the expected game/AOT change; icon/other NACP fields match.

### Published artifact and capture procedure

File under `fna-nx-test/terraria-mono/switch/`:

`mono_nx_fna_terraria_nochroma49_tile_helpers.nro`

- NACP title: `Terraria 49 Tile Helpers`
- Size:950,714,996 bytes
- NRO SHA256: `defde366a80e84735e35db5d8128733dacb08104ab43dd799ded728474fa590c`
- Game SHA256: `7447250546a72af18c555c21335502edc03eb5c54f2618ac3401598eb327c953`
- Game MVID: `c0933f1d-b7e9-3e9d-cd4f-a311c3edd11e`

The procedure below produced the completed49 measurement capture recorded below;
it was not an FPS A/B against46A. Keep46A/42 as the working performance baseline.

1. Copy the NRO only; keep the current SD runtime, ICU, config, saves and bindings.
   Use full application mode, `logging=true`, `runtime_logging=false`, Frame Skip On.
2. Use an existing safe shelter/location, without construction during the quiet
   segment. Keep inventory/map closed and avoid screenshot/capture/spectator modes.
3. Select an **empty slot or an unprefixed item** for the quiet segment. Unlike48,
   49 deliberately keeps the stock framework; this avoids unrelated per-frame
   ItemPrefixCombiner diagnostic spam without another code change.
4. After world loading settles, stand still for roughly60 seconds; then walk around
   for another30–60 seconds as a separate segment. Note any construction, deaths,
   inventory use or unusual events. State records now help locate the phases.
5. Exit normally so the final envelope is emitted. Copy `/mono/log.txt` to
   `fna-nx-test/log49.txt` before the next run. Upload the complete log, not just
   the NX_PHASE lines.

Use the matching parser, not the old43–45 analyzer:

```sh
python3 fna-nx-test/scripts/analyze_tile_helpers.py fna-nx-test/log49.txt --output /tmp/tile49-analysis.json
```

It validates the versioned complete records, retains raw state/counters and derives
operation-weighted helper means and contributions per valid sampled invocation.
The strict parser rejects malformed/incomplete packets; invalid data must not be
quietly treated as a performance sample. Measurements can slow the game: do not
use49's FPS as the clean baseline or assume every helper executes on every tile.

### Next optimization gate and reproducibility

The completed hardware capture below separates individual helpers in valid normal
world periods, using actual execution frequency and scene/overhead limits. No
single helper dominates. The next source-audit target is unused preparation before
the late final-drawing gate, not the entire observer-inclusive remainder. Behavior,
generated native code and compatibility must be audited before building one
isolated candidate. No optimization was applied by producing or analyzing49.

Persistent source: `fna-nx-test/scripts/patch_tile_helpers/` and
`scripts/analyze_tile_helpers.py`. Exact baseline field/IL/callsite/mode evidence
is in cache `tile49-audit/`; final semantic boundary review is
`tile49/safety-review.json`. Accepted source/managed proof is
`tile49/patcher-accept03/` (including runner/source hashes, actual report logs and
parser/differential proof). The earlier accept01/02 snapshots are superseded.
Final AOT metadata is `tile49/aot-final/`, native replay/package evidence is
`tile49/native/`, and complete delivered identities are in
`tile49/artifact-verification.json` and `tile49/published-build.json`.
These build-time proofs alone establish no GPU timing or FPS improvement. The
completed hardware measurement is documented next;49 remains measurement-only.

## Build 49 hardware measurements (2026-09-20)

**Valid capture; no optimization/FPS comparison and no new NRO.** Input:
`fna-nx-test/log49.txt` (297,356 bytes,1,660 lines), SHA256
`2b089da1ddfb69074953424007e567468c50013ab8e3a414b60211bae5078c73`.
The unmodified upload is archived as cache `tile49/hardware-captures/log49.txt`.

### Capture validity and scene separation

- The actual version49 analyzer accepted all **65 complete packets**, including
  the final packet. No measurement-invalid flags, report failures, capture failures,
  aborted frames or incomplete sampled calls. Native AOT entrypoint resolution and
  normal application termination are logged.
- **7,617 completed frame snapshots =7,617 native Draw calls**. There are3,722
  eligible world frames and3,895 excluded frames. Menu/inventory/pause counters
  overlap and must not be added as disjoint categories; two boundary-state changes
  are recorded, with no death/ghost/spectator frames.
- All67 native NX_PHASE records reconcile cumulative Tick/Update/Draw/Poll/Swap
  totals:7,618/19,239/7,617/13,141/7,617. Final native elapsed time398.310s.

| Cohort | Packets | Eligible frames | Reported window wall time | Solid loop ms/frame | Non-solid loop ms/frame |
| --- | --- | ---: | ---: | ---: | ---: |
| Stationary primary |25–40|1,490|81.483s|14.9196|1.4877|
| Stationary later subset |29–40|1,124|61.130s|14.9218|1.4848|
| Movement |47–56|963|50.928s|14.9127|1.9394|

The primary stationary block has zero raw player/camera position range in every
packet: player `(51200,5558)`, camera `(50570,5219)`. Viewport is1280×720, raw zoom
target1, Frame Skip value1, and recorded world time advances19177→24081 during
daytime. Inventory/map/pause/death are absent in these selected packets. Movement
packets47–56 contain changing positions and are kept separate from stationary and
mixed/inventory transitions. The later subset is a sensitivity check, **not an
independent replication**. Similar solid-loop cost during movement is descriptive,
not an optimization result or proof of identical scene contents.

### Operation-weighted helper costs

Stationary primary, **30,655 valid selected solid DrawSingleTile calls**:

| Helper | Actual sampled operations | µs per executed operation | µs per valid selected call |
| --- | ---: | ---: | ---: |
| `GetColor` |30,655|1.1806|1.1806|
| `GetTileDrawData` |30,655|1.1589|1.1589|
| `DrawTiles_GetLightOverride` |30,655|0.6119|0.6119|
| `GetFinalLight` |5,209|0.8707|0.1480|
| `GetTileOutlineInfo` |0|not executed|null|
| Bare clock pair, not a game helper |30,655|0.2334|0.2334|

Means are calculated from summed raw ticks/actual operation counts, not an average
of per-packet averages. The frequency-weighted column matters: GetFinalLight runs
in only16.9923% of these sampled calls. Zero outline operations do not establish
zero intrinsic outline cost. GetColor and GetTileDrawData are effectively the two
leading measured helpers here; the small difference does not justify claiming
one uniquely dominates.

The solid loop visits4,785 positions and calls Single2,635 times per frame. The
non-solid loop visits the same4,785 positions but calls Single only111 times per
frame. Its1,287 valid samples show GetTileDrawData2.0811µs, GetColor1.7594µs,
override0.6481µs and GetFinalLight0.9276µs per executed operation. Outline has only
48 sampled operations,1.6131µs each (0.0602µs per selected call). This is not a
reason to optimize a rare outline path instead of the high-volume solid path.

Moving solid samples (19,419 calls) preserve the ranking: GetColor1.2136µs,
GetTileDrawData1.1691µs, override0.6125µs; GetFinalLight executes3,440 times
(17.7146%). All fully eligible packets provide a separate descriptive aggregate
in the evidence JSON; it does not replace the state-separated primary cohorts.

### Observer cost and interpretation limits

- Recorded report bodies total1.497027s versus375.289534s summed window wall time.
  `body/(window_wall+body)` is **0.3973%**, excluding footer output and other
  instrumentation. This is not the total profiler overhead fraction.
- 15,234 boundary snapshots consume36.254608ms in total, **4.7597µs per completed
  frame**. The stock label diagnostics remain present:30 messages in the whole
  capture. No framework logging change was folded into49.
- Gross sampled solid-call time is9.9609µs; the five measured helper sums contribute
  3.0993µs per sample. **Do not label the remaining6.8616µs pure game cost**: it also
  includes sampling/timing instrumentation and unmeasured nested work.
- Clock-pair cost is not a universal correction to subtract. The sample rotation
  does not prove statistical representativeness; sampled wall costs cannot be
  multiplied by every visited/world tile and advertised as recoverable FPS.
- Raw stationary coordinates, raw zoom target and stable UI flags do not freeze
  lighting/world contents, identify effective render zoom, certify all capture
  modes or establish equivalence to another build's scene. No clean FPS, exclusive
  CPU/GPU time or causal speedup is claimed.

### Source-backed next target: late final-drawing gate

The pinned clean42 `DrawSingleTile` IL is in
`tile49-audit/clean42/TileDrawing-DrawSingleTile-06004538.il`.

- The first GetColor is0050; GetTileDrawData00d3 and the light override0570 each
  execute once in every completed selected solid call in the primary cohort.
  The second GetColor06f4 is conditional **type72/shroom-cap drawing**, not generic
  cached-light reuse; it is not executed in those samples.
- `V_4` is formed from glow/light/flame/ignore-light/fullbright-wall conditions and
  `IsVisible`. Existing `DrawBlack`063f and special-draw cache calls065f/0669 occur
  before the eventual `ldloc V_4; brfalse IL_1ac1` at07b3/07b5. True falls through
  GetFinalLight07de; false reaches the final return.
- **[INFERENCE, exact-source + sampled-count derivation]**25,446/30,655 completed
  selected solid calls (**83.0077%**) take that false gate; movement82.2854%.
  These calls have already executed Data/override, excluding the earlier liquid
  return007a; the variant returns0916/094c/0ce5 occur after GetFinalLight. This does
  **not** mean83% of world tiles are invisible or nothing was drawn: black/special
  work may already have occurred, and the exact dark-versus-invisible cause was not
  recorded.
- Immediately **before** the gate,0713–07b1 contains66 original IL instructions
  preparing local source Rectangle `V_6`, tile-width/array-derived offset `V_7` and
  screen Vector2 `V_8`. The false-gate path then returns without using them.
  This identifies repeated unused preparation, not66 ARM64 instructions or a
  measured recoverable time budget.

**Next audit candidate:** move only that existing false gate ahead of0713,
**after** all preceding RNG/particles, DrawBlack, special-draw caching and optional
shroom-cap drawing. Do not insert an entry cull, skip those side effects, change
lighting quality, adopt46B allocation reuse or reinstate47's query removal.
Before a patch: verify generated native work, branch retargeting/def-use, struct
constructor effects, and array/null/bounds/type-init exception boundaries. Any
malformed/modded-state behavior difference must be scoped and discussed rather
than silently called universally equivalent. A candidate still needs differential
proof and matched hardware A/B; **no patch or benefit is established by this log**.

The independent bounded source review **recommends further audit, not a patch**:
it confirms the sampled gate-frequency derivation, no later backedge to the
measured helpers, and no false-path consumer/escaping alias of `V_6/V_7/V_8`.
All three entries to0713 must converge on any relocated gate: branches0676 and
0683 plus fallthrough from070e. Preserve the already-computed `V_4`; do not
recompute visibility or merely insert a guard after the optional Draw call.

Exact source prerequisites still open: clean42 `TileID.Sets` initialization,
SetFactory length/construction and all built-in writes/address-taken uses of
`DoNotAdjustDrawPositionBasedOnTileWidth`, to prove its nonnull/in-range invariant.
Earlier accesses to **different arrays** do not prove that invariant. Also inspect
the pinned FNA Rectangle four-int ctor and Vector2 two-float ctor, addition,
zero getter, type initializer and type flags; existing prior Rectangle/Vector2
uses support initialization order but do not substitute for those exact bodies.
Only then consider actual ARM64 cost and a serialized false/true differential
proof preserving prior RNG/particle/black/cache/shroom-cap effects. Arbitrary
hooked constructors/operators or malformed state are not implicitly equivalent.

### Evidence and decision

- Parsed complete raw packets: cache `tile49/hardware-parsed.json`.
- Weighted cohorts, raw totals, limits, gate inference and decision:
  `tile49/hardware-analysis.json`.
- Bounded source review and exact remaining prerequisites:
  `tile49/gate-source-audit.json`.
- Original upload: `tile49/hardware-captures/log49.txt` (hash above).
- Existing build/proof provenance remains in `tile49/patcher-accept03/`,
  `tile49/aot-final/`, `tile49/native/` and `tile49/artifact-verification.json`.
  Those build-time proofs are not rewritten as hardware-speed evidence.

**Retain46A/control or clean42 as the active performance baseline.**49 successfully
narrows the source investigation; it is not a speedup build. No further49 capture,
SD runtime replacement or new optimization NRO was requested/produced by this
analysis.48/47/46B remain unpromoted.

## Build 50 late drawing-gate candidate (2026-09-20)

**Host/AOT/native/payload proof retained; hardware A/B completed. Adopted as the working baseline; outcome and limits below.**
This implements the source-audit target identified by49, not an accumulated
46B/47/48/49 build. The user approved proceeding after the49 measurements.

### Exact change and preserved work

Only clean42 `TileDrawing.DrawSingleTile` changes. Its existing load of `V_4`
and false branch move from IL07b3/07b5 to before0713. Both incoming shroom-skip
branches0676/0683 now enter that gate, as does fallthrough after the optional
shroom-cap draw070e. The false destination remains the final return1ac1; the
already-computed decision is not replaced with another visibility calculation.

The true path retains all66 original preparation instructions in their original
order: Rectangle `V_6`, width offset `V_7`, screen Vector2 `V_8`. False skips their
unused results. All earlier work stays, including scratch allocation, lighting,
texture access, RNG/dust/particles, `DrawBlack`, both special-draw cache calls,
and optional shroom-cap lighting/drawing. In particular this is **not an entry
cull** and does not skip black/special rendering just because the normal final
drawing section is skipped.

There are still2,312 original instructions,6,850 IL bytes,61 locals and225
original callsites; only two instructions move and two branch targets change.
No game method/type/field/reference is added. No lighting-quality change,
simulation/protocol/save change or46B scratch-lifetime reuse is included.
The stock framework and its label diagnostics remain;48's removal is not folded
in. Native NX_PHASE timing remains unchanged;49's observer is absent.

49 observed the false gate in25,446/30,655 completed sampled solid calls
(83.0077%) in its stationary cohort. That supported investigating this path;
it is **not** a claim about all world tiles, invisible tiles or recoverable FPS.

### Closed source-safety prerequisites and limits

The executable source verifier passed41 checks over184 assemblies/142,821
method bodies. It emitted356 exact IL dumps and reproduced364 evidence files
byte-for-byte; altered Terraria/FNA input hashes were rejected.

- `TileID.Count` is754. The width-adjustment array and `HasOutlines` are each
  independently constructed as fresh754-element Boolean arrays by the concrete
  immutable-size SetFactory. The target initializer contains index711; the
  outline initializer's95 decoded indices span10–733. No pool recycle caller,
  built-in replacement, address-taking or alias escape was found for these arrays.
- A successful retained `HasOutlines[typeCache]` read at0154 dominates every path
  to the moved preparation. The same cached type has one store0025 and no address
  escape. With intact arrays, reaching either gate therefore already proves the
  required index range; invalid UInt16 tile IDs fail earlier in both versions.
  This does not assume every UInt16 value is a valid tile ID.
- Exact pinned Rectangle/Vector2 constructors and Vector2 addition affect only
  local/value state; there are no callbacks or heap allocations. Rectangle's
  initializer is empty; Vector2 initializes four fixed value constants. Prior
  use/initialization and exact type flags were inspected rather than assumed.
- The built-in reflection census found no supported replacement path for either
  tile-set array. No valid built-in gameplay tradeoff was found. Multiplayer and
  mod-loader execution were **not** tested.

The supported domain is **intact initialized built-in tile sets and unhooked
pinned FNA primitives**. Deliberately replacing the width array with null or a
short array can make the old false path throw while50 returns; four host cases
record this difference explicitly, not as equivalence passes. Arbitrary reflection,
unsafe corruption, modified primitive callbacks and asynchronous/resource failures
are outside the guarantee. If such compatibility becomes required, retaining the
array access before the gate is a different transformation requiring discussion
and proof—not a silent fallback or an unqualified compatibility claim.

### Executed host proof and acceptance

- Full serialized original/candidate Single and its actual parent Draw run in
  the proof, with the original TileDrawInfo constructor and copied pinned FNA
  Rectangle/Vector2 constructor/addition/Zero/initializer IL. External game,
  graphics and RNG callees are deterministic boundaries, not an emulated GPU.
- **500 checks across100 scenarios** cover all three gate entries, false/true
  predicates, all five original returns,16 independent geometry oracles, earlier
  side effects and failures, callback argument/byref values, nested scratch and
  array lifetime, and both layer paths. Four faulty branch/visibility/prior-cache
  candidates fail. Host-only preparation markers are absent from the game image.
- Six warmed10,000-call rounds allocate exactly2,560,000 bytes in both versions,
  on both true and false paths. This verifies unchanged host scratch allocation;
  it is not a Switch allocation-size, timing or speedup measurement.
- Inverse relocation reconstructs every original instruction/operand/local/handler.
  All21,041 other methods,32,059 fields,2,957 types and100 managed resources stay
  unchanged; raw target signatures and native PE resources are checked.
- Review found a patch-time supplied-candidate validation gap: unqualified names
  could hide a Rectangle reference redirected from FNA to an existing core-library
  scope when the fixture remapped it. This is fixed **before** remapping by comparing
  637 qualified TypeRef identities,5,734 MemberRef identities and244,608 declaration/
  parameter/local/override/attribute-constructor/body-use rows. Table reordering and
  dedup are allowed; the intentional self-module MVID change is excluded.
- The real CLI now rejects that wrong-scope candidate before producing a probe or
  success receipt, while genuine supplied-candidate proof passes. The reviewer
  closed the finding. Final acceptance repeats deterministically and passes all
  **11 input/output/source/scope rejection guards**. The validator correction did
  not change the accepted game SHA/MVID, so existing AOT bytes were revalidated
  against the stronger acceptance rather than relabeled as rebuilt.

### Actual Switch-native code and package proof

AOT compiled **52,792/52,816 methods**, exactly the baseline counts and24 existing
fallbacks. Six non-game AOT objects and all framework DLLs are reused unchanged.
Replaying the control link reproduces every allocated46A ELF section exactly.

The final22,500-byte native Single body remains at0x48a9310. The original
`cbz w22` gate at0x48aad48 is now **0x48aab74**, before preparation0x48aab78;
false goes directly to the unchanged epilogue0x48aea98. Both shroom skip branches
and shroom Draw fallthrough enter that gate. True reaches the original preparation.

The native inverse comparison verifies all117 static preparation words in the
same order with only required PC-relative adjustments. All6,244 earlier bytes/
76 earlier calls and15,784 suffix bytes are identical. All196 body relocations,
actual method-table binding and DWARF locations are verified. Normal original
caller paths through that preparation contain115 or117 instructions plus two
callee calls; these are **descriptive instruction counts, not measured time saved**.

Complete final verification covers **192,304 native method targets**,15,010
fallback sentinels and all**16,183 embedded files** in each NRO. Only Terraria.exe
and its AOT object differ, plus the displayed NACP title. All15,997 Content files,
six other AOT objects, icon and non-title NACP fields match control. Method tables
remain read-only/non-executable and there is no RWX load segment.

### Published candidate

Under `fna-nx-test/terraria-mono/switch/`:

`mono_nx_fna_terraria_nochroma50_late_draw_gate.nro`

- Title: `Terraria 50 Late Gate`
- Size:950,591,092 bytes
- NRO SHA256: `28f5d9bdd6adbc59ea97da2796f3c4883565773773c42dcd05984c6209c6fb1c`
- Game SHA256: `06b11beb9c83fc14140754eb5a44c7fa0525d1877ea2fd0860a75b91f3d6f4f6`
- Game MVID: `18ab5d9f-3419-30c0-509e-cf5a34c42f0b`

### Fresh matched hardware comparison

Use **A=existing46A/control**, **B=50**. Do not compare50's frame rate directly
against observer-instrumented49. Copy only the NRO; keep SD runtime/ICU/config,
saves and bindings unchanged. Use full application mode, Frame Skip On,
`logging=true`, `runtime_logging=false` and confirm NX_CONFIG in both logs.

1. Use the same existing safe location, player/camera position, zoom and selected
   empty slot or unprefixed item in both runs. Keep inventory/fullscreen map closed;
   avoid capture/spectator modes, construction, combat and day/night lighting
   transitions in the quiet segment. Let world loading settle first.
2. Stand still for60–90 seconds. Keep a later30–60-second movement/visual check
   separate; note any deaths, inventory use, lighting changes, missing black tiles,
   special tiles or shroom caps. User observations are needed for hardware visuals.
3. Exit normally and preserve the **complete** `/mono/log.txt` after each run as
   `log50A.txt` and `log50B.txt` under `fna-nx-test/`, before the next run overwrites
   it. If the difference is small or conditions drift, a repeated control run
   `log50A2.txt` can help distinguish scene drift from a candidate effect.

Both builds retain native NX_PHASE, not NX_PROFILE version49. Compare equivalent
steady segments and actual Draw/Update counts/times; quieter logs alone do not
establish scene equality. **The completed A/B below supports50 as the working
baseline;46A/clean42 is retained as control and rollback.**

### Persistent reproduction and evidence

- Patcher/proof: `fna-nx-test/scripts/patch_draw_gate/`; `run.py` requires pinned
  clean42/FNA and a fresh output directory. It uses existing shared proof/metadata
  utilities, not a runtime dependency. No original framework source was rewritten.
- Build container: cache at `/build`, project at `/work:ro`,
  `recovery46/sdk-pristine` at `/mono-nx:ro`. Invoke the direct retained SDK
  `/build/runtime-source/.dotnet/dotnet`; the `/root/.dotnet/dotnet` wrapper depends
  on an env.sh absent from the pristine SDK mount. Native linking additionally
  mounts `recovery46/native-deps/install` at `/fna-install:ro`.
- Final host acceptance/source manifests: cache `gate50/accepted-scope-final/`.
  Genuine corrected CLI proof: `gate50/final-supplied-proof/`. Review resolution:
  `gate50/review-closure.json`.
- Source safety: `gate50-audit/safety/run.py`, `run.json`, `evidence/verdict.json`
  and exact IL/reference inventories. Native baseline/candidate checkers and
  disassembly: `gate50-audit/native/` and its `candidate/` subdirectory.
- Build recipes: `gate50/build_aot.py`, `build_native.py`, `verify_pair.py`.
  Completed AOT is **`gate50/aot-final/`**; final native files are `gate50/native/`.
  Complete acceptance-to-binary linkage is in `gate50/pair-verification.json`;
  publication identity is `gate50/published-build.json`.
- `gate50/superseded.json` distinguishes initial inspection, incomplete AOT and
  earlier acceptance attempts from packaging inputs. The first AOT check used an
  ambiguous Single-prefix selector that also matched Flames/SlicedBlock; exact
  symbol matching replaced it. That was a verifier-selection failure, not an
  extra compiler fallback or a hardware result. No stale prototype is shipped.

## Build 50 hardware A/B outcome (2026-09-20)

**Decision: retain50 as the working performance baseline.** The measured rendering
improvement is modest but consistent across the selected steady windows and
their two halves. Keep46A/control for rollback. No new NRO, runtime change or
additional optimization was produced by this analysis.

### Capture integrity and observed configuration

The requested filenames assign **A=46A/control**, **B=50**. The log format does
not embed a binary hash, so this is the supplied test-role mapping, not independent
cryptographic identification of the running NRO.

| Capture | Bytes | NX_PHASE records | Final elapsed | Total Draw calls | Exit |
| --- | ---: | ---: | ---: | ---: | --- |
| `log50A.txt` |61,544|81|466.532s|8,966|normal|
| `log50B.txt` |73,643|139|755.519s|25,509|normal|

All cumulative Tick/Update/Draw/Poll/Swap counters reconcile, timestamps increase,
and adjacent window boundaries differ by at most1ms from printed rounding. Each
capture has one final record followed by normal termination. Native AOT entrypoint
resolution, `logging=1`, `runtime_logging=0`, GL4.3Compatibility/glsl120 and reported
1280×720 resolution match. No uncaught exception/assertion/fatal marker is present;
`Error Logging Enabled` is an initialization message. Both still disable audio
after the unavailable `dummy` audio target; this is shared behavior, not a newly
identified50 regression. Neither contains49's per-frame state profiler.

First native Draw timestamps are19.790s and19.759s. B spends much longer before
the common TIMBER event and includes many near60-Draw/s windows. **Do not treat
the whole-run25,509/755.519 versus8,966/466.532 ratio as the optimization's gain.**

### Explicit state/diagnostic markers

Times below bracket messages between adjacent native reports; they are not exact
event timestamps or continuous state tracking.

| Marker | A elapsed window | B elapsed window |
| --- | --- | --- |
| TIMBER achievement |123.411–130.904s|378.179–385.770s|
| Zombie death notification |161.112–166.132s|415.970–421.047s|
| Final item-label burst |166.131–171.193s|441.170–446.172s|
| Later achievement/combat marker |352.309–357.347s:50th Zombie defeated|612.065–617.116s:YOU_CAN_DO_IT|

A contains288 ItemPrefixCombiner label-entry messages; B156. Both have four
descriptor-creation messages. Those calls alone do not prove an open inventory;
selected-item labels can also render during ordinary gameplay. Different death
message wording does not establish a different seed or mod state.

### Primary event-aligned comparison

Use the **end of the TIMBER-bearing native report** as a common reproducible anchor:
A130.904s, B385.770s. Include every complete window contained in **anchor+90..210s**.
This is a transparent post-hoc rule, not a preregistered/randomized experiment.
It starts at least30 seconds after both final label bursts and ends before the
later achievement notifications. No timing-outcome filter removes individual
windows. The included windows happen to contain zero label, descriptor or chat
markers; their entire set is retained.

- A: indices33–55, log lines896–918, actual221.497–337.228s.
- B: indices84–106, log lines683–705, actual476.354–591.937s.
- Each contains23 full windows; summed printed windows are115.732s and115.583s.
- Rates and operation means divide **summed raw counts/times**, not unweighted
  per-window averages.

| Metric | A:46A | B:50 | Observed change |
| --- | ---: | ---: | ---: |
| Draw calls |1,864|1,974|different equal-stage samples|
| Draw calls/sec |16.1062|17.0786|**+6.0378%**|
| Inclusive Draw wall ms/call |38.7043|36.7269|**−1.9774ms / −5.1090%**|
| Update callbacks/sec |59.9921|60.0002|approximately60 in both|
| Inclusive Update wall ms/call |6.0502|5.9705|−1.3179%|
| Poll calls per Draw |1.000|1.000|same|
| Swap wall ms/call |0.3751|0.3794|approximately unchanged|

The23 window-mean Draw values span38.226–39.186ms in A and36.475–37.087ms in B;
these ranges do not overlap. Window observations within a run are correlated,
however, so this is not23 independent trials or a confidence-interval claim.

### Sensitivity checks, not alternative headline selection

Offsets use the same TIMBER-report anchor and include complete windows only:

| Offset range | Windows A/B | Draw-rate change | Draw-wall change | Use |
| --- | ---: | ---: | ---: | --- |
|90–150s|11/11|+6.3714%|−5.3379%|first primary half|
|150–210s|11/11|+5.7399%|−4.9712%|second primary half|
|70–130s|11/11|+6.1610%|−4.9432%|earlier quiet sensitivity|
|210–270s|11/11|+9.9944%|−4.5552%|contains different later achievement/combat markers|
|60–270s|41/41|+6.9245%|−4.6806%|broader descriptive aggregate|

The later cohort's Update mean also differs by−8.17%; its near10% Draw-rate gain
is therefore **not** the headline. The very short pre-death comparison is also
retained in JSON, not used as the main result. The modest Draw-wall improvement
remains present in both primary halves and earlier/broader checks.

### Interpretation and adoption boundary

The practical observed difference is about **one additional Draw call/sec**, not
a transformation to60FPS. **[INFERENCE]** The consistent lower Draw cost, mostly
similar Update/swap behavior and previously verified isolated native change support
keeping50 for continued performance work. They do not uniquely attribute exactly
1.9774ms to the removed preparation in an identical frozen scene.

Both captures contain a death. Their notification/label bursts are outside the
primary windows, but **there is no explicit respawn or current alive/dead/pause
flag**. The selected windows are later than the notifications, not certified
alive ordinary gameplay. Player/camera positions, world time, exact scene content,
effective zoom and Frame Skip value are also unrecorded. Quiet logs do not prove
stationarity, absence of combat, or matching scenes; equal reported resolution
does not close those gaps. No explicit user visual/input-regression report was
provided, so normal shutdown is not exhaustive visual/input compatibility proof.

**Adoption is a working-baseline decision for the supported vanilla port, based
on one consistently favorable pair plus existing source/behavior/native proof.**
Preserve46A/control for rollback. Keep48/47/46B unpromoted;49 remains measurement
only. Further optimization should start from50 without treating its observed6%
as a universal speedup or relabeling old build-time checks as hardware evidence.

### Evidence

- Uploaded/archived A SHA256:
  `f2cb87da282a026d771922708f1ec812a76887b17c576afa22304caddb14fa57`.
- Uploaded/archived B SHA256:
  `37def9ed4160492d25127dcaec0d2204e5055e3adc03b5108806b7a476a8e918`.
- Byte-identical originals: cache `gate50/hardware-captures/log50A.txt` and
  `gate50/hardware-captures/log50B.txt`.
- Full parsed native rows, marker timeline, integrity checks, every weighted
  cohort, formulas, selection rules, adoption scope and limits:
  `gate50/hardware-comparison.json`.
- Compact full-window table: `gate50/hardware-windows.tsv`.
- Current hardware status is updated in `gate50/published-build.json` and
  `fna-nx-test/terraria-mono/build-verification.json`. The NRO itself is unchanged.

## Build 51 focused UI measurement (2026-09-20)

**Measurement-only on adopted50; host/AOT/native/payload and complete hardware
capture verified.50 remains the performance baseline.** No UI optimization, visual change,
input change, lowered quality or simulation throttling is part of51.

Earlier full-frame profiles measured Interface at6.78–6.91ms/frame in the normal
world cohorts and17.06ms with inventory open. Those are older instrumented scenes,
not current51 measurements.51 measures the actual UI breakdown on50 instead of
assuming those old costs still apply or repeating48's logging-only cleanup.

### Source findings and measurement hierarchy

Exact50 source inspection found **43 registered UI layers**. Main.DrawInterface
iterates them through the Boolean GameInterfaceLayer.Draw call, stops on false,
disposes its enumerator, then performs pending mouse-text and debug drawing.
The layer wrapper catches drawing exceptions through the original logger, ends
its SpriteBatch and preserves the Boolean result. These semantics remain intact.

Text layout is partly deferred: `ChatManager.LayoutSnippets` returns an iterator.
Timing its factory alone would miss the real work.51 includes its actual generated
MoveNext/Dispose/enumerator paths, plus the font measurement/wrapping callsites.
Game font drawing also uses DynamicSpriteFontExtensionMethods, not only methods
declared on DynamicSpriteFont itself. Both routes are covered.

Twelve fixed metric IDs:

| ID | Label | Boundary |
| ---: | --- | --- |
|0|`frame_draw`|sampled original Main.Draw body, excluding outer snapshot/report work|
|1|`ui`|complete Main.DrawInterface, including post-layer pending mouse text|
|2|`inventory_layer`|`Vanilla: Inventory` layer|
|3|`hotbar_layer`|`Vanilla: Hotbar` layer|
|4|`mouse_over_layer`|`Vanilla: Mouse Over` layer|
|5|`mouse_text_layer`|`Vanilla: Mouse Text` layer|
|6|`other_layer`|remaining registered layer calls|
|7|`item_slot`|item-slot drawing|
|8|`tooltip`|pending mouse-text/item-tooltip processing|
|9|`item_format`|item naming **and UI-localized formatting**, not exclusively item names|
|10|`text_draw`|game text/shadow helpers and actual external font drawing|
|11|`text_layout`|measurement/wrapping/parsing/layout, including deferred enumeration|

Scope calls nest. `inclusive_ticks` overlap; **never add inclusive stage means**.
`exclusive_ticks` subtract measured direct-child intervals, including nested calls
with the same category. These child-subtracted times still contain observer and
unmeasured nested work; they are not exclusive CPU/GPU measurements. Valid sample
partitions are checked against both frame and UI roots.

### Sampling, state and attribution

- One-in16 eligible frame attempts is selected, independently per cohort.
  Inner scopes outside UI or on unselected frames make no timestamp calls.
  Typed external-font wrappers keep their original argument evaluation/dispatch
  and value/ref return signatures; an inactive fast path avoids timing work.
- Paired state snapshots cover every owned root Draw, not just timed samples.
  Cohorts remain separate: **world**, **inventory**, **inventory_paused**. Menu,
  fullscreen map, dead, ghost, spectator and invalid-state frames are explicitly
  counted and excluded from these costs; paused-without-inventory is excluded.
- State includes raw local-player ID/position/dead/ghost/spectating, camera position,
  reported size, Frame Skip value, GameZoomTarget, used UI scale, world time/day,
  boundary mouse coordinates, and hover/mouse-item type/prefix transitions.
  No player text or item-name strings are written to the report.
- Flag/player/size/zoom/UIscale/skip/day transitions prevent incompatible frame
  donation. Movement and hover changes are recorded, not treated as observer
  failures. Original layer false returns are legitimate stops and have a separate
  `stopped_ui_samples` counter; no-UI samples are also explicit.
- Other-thread roots are ignored/countable; nested/reentrant Draw suspends timing
  and restores its parent. Original scoped exceptions and observer timing failures
  are distinguished. Cookie/depth/overflow/negative/backward-clock problems cannot
  escape into game behavior or be reported as valid measurements.

Snapshot limits remain explicit: player is raw Main.player[myPlayer], camera raw
Main.screenPosition, zoom the raw target rather than a certified effective render
viewport, and mouse coordinates are boundary Main fields rather than necessarily
the coordinate space used inside every UI layer. Capture/spectator render modes
are not comprehensively certified; avoid them during the run. Boundary snapshots
cannot detect an intermediate toggle-and-return or freeze every scene detail.

### Compact reports and strict reader

Reports use `NX_PROFILE version=51 schema=ui_costs`, roughly every5 seconds plus
an explicit final packet after Game.Run returns normally. The fixed46-row packet
contains BEGIN/STATE, then COHORT/SCENE/12 SCOPE rows for each of the three cohorts,
then REPORT_COST/END. Counters and clocks remain raw Int64 values; unused means
are null, never invented zero-cost operations.

Lazy fixed buffers/metric arrays replace per-call allocation. Report clock
validation occurs before END; there is no unreportable post-END clock check.
Report-body cost excludes the footer, and the next periodic cooldown is anchored
on a later valid frame timestamp without moving the original window start.
Failed writes preserve data/failure state; incomplete packets are not repaired.

The reader preserves raw packets/interleaved ordinary log context and keeps
cohorts separate. It validates fields/order/IDs, frame/sample/capture arithmetic,
scene ranges, nested partitions, clocks, lifetime counters and final/continuity
semantics. Complete periodic packets without a final packet can be read but are
explicitly marked incomplete. Any sticky `measurement_invalid` blocks performance
interpretation. Sampling is deterministic; the reader does not multiply costs
by16 and advertise a fabricated FPS saving.

### Verification actually executed

- **134 original game methods** are changed by observer envelopes or font-call
  replacements. There are46 envelopes and230 original font sites across101 callers,
  redirected through8 exact typed wrappers. No FNA/ReLogic assembly is edited.
- Exact inverse/qualified-metadata verification preserves all20,908 other methods,
  32,059 fields,2,957 original types and100 managed resources. It checks286,252
  qualified rows, raw signatures and pinned SDK references before fixture remapping.
  Original50 DrawSingleTile remains its exact2,312-instruction body.
- **1,167 current-source generated-code checks** pass, including595 runtime/state/
  timing/report checks and104 serialized-body receipts. They exercise actual
  Main.Draw/DrawInterface/layer stop/catch/disposal, pending tooltip/color/formatting,
  deferred layout and all eight wrappers. Seven broken control-flow/callback/
  return/scope mutants fail on semantics, not merely InvalidProgram/TypeLoad.
- Five static mutated images and a malformed primitive signature are rejected.
  Final acceptance and probe DLLs repeat deterministically; all11 input/output/
  source rejection guards pass. No accepted Terraria.exe is published before
  both generated behavior proof and actual report-parser execution succeed.
- Generated final reports:53 accepted cases (42 structurally valid but sticky-invalid
  diagnostic cases),24 partial/malformed cases rejected. The final actual-derived
  parser corpus passes57 accepted and1,569 rejected cases, including weighted
  unequal cohort populations and unused/null semantics.
- Actual mapped helper hooks allocate **zero warmed host bytes** over4,096 measured
  frames after4,096 warmup frames with reporting disabled and host tiered compilation
  off. This is not Switch observer timing, GPU fidelity or a performance claim.
- Independent safety review found no supported-state blocker; fixture game/graphics
  boundaries and observer/wrapper stack-frame limitations remain stated limits.

Switch AOT compiled **52,883/52,907 methods**:91 added methods, still24 pre-existing
fallbacks and no new fallback. All seven module metadata consumers validate; six
non-game AOT objects are reused unchanged. Control linking replays every allocated
50 ELF section exactly. Full verification checks192,304 control and**192,395
measurement native targets**,15,010 fallback sentinels, and all**16,183 embedded
files** per NRO. Only game/AOT observer code and the NACP title differ from50;
all15,997 Content files, framework DLLs, icon and non-title NACP fields are preserved.

### Published measurement artifact

`fna-nx-test/terraria-mono/switch/mono_nx_fna_terraria_nochroma51_ui_profile.nro`

- Title: `Terraria 51 UI Profile`
- Size:950,735,476 bytes
- NRO SHA256: `a08f21c40010558a8dbda1418bb5a7ae65465dc55f778cb615cdfa47dcc13661`
- Game SHA256: `edfe776cbb003475e3c4272aac39ae0c1fa6722be01f8dda089b7f839bcde350`
- Game MVID: `75e0ed96-b158-c70c-94d9-f88396175df7`

### Hardware capture procedure

For this step **run51 only; no new A/B is needed**. Copy only the NRO; keep the
current SD runtime/ICU/config/saves/bindings. Use full application mode,
Frame Skip On, `logging=true`, `runtime_logging=false`.

1. Load an existing safe shelter and wait for loading to settle. Be alive; keep
   fullscreen map closed and avoid capture/spectator mode. If you die, respawn
   and establish a fresh stable segment rather than waiting on the death screen.
2. Stay still, inventory closed, for60–90 seconds. Keep the same camera/zoom and
   start with an empty slot or unprefixed item. This measures ordinary UI/hotbar.
3. Open inventory for another45–60 seconds. Include a settled interval without
   moving the cursor, then a separate tooltip check by hovering/selecting one or
   two items, preferably including a prefixed item. Do not mix rapid cursor motion
   throughout the only inventory segment. Existing autopause behavior is recorded
   and kept separate; do not change settings just to improve the numbers.
4. Close inventory and return to a quiet view for about15 seconds, then exit
   normally. Copy the **complete** `/mono/log.txt` as **`fna-nx-test/log51.txt`**.
   Note any visual/input issues; logs alone cannot certify their absence.

Keep50 as the performance baseline.51 can run slower because it observes UI;
its frame rate is not an optimization A/B result. Its output selects the next
actual UI optimization rather than claiming one already exists.

### Persistent tools and evidence

- `fna-nx-test/scripts/patch_ui_profile/`: contracts/schema, runtime, patcher,
  qualified/inverse audit, generated behavior/runtime proof and acceptance runner.
- `fna-nx-test/scripts/analyze_ui_profile.py`:
  `python3 fna-nx-test/scripts/analyze_ui_profile.py fna-nx-test/log51.txt --output /tmp/ui51-analysis.json`.
- Exact source inventory/IL: cache `ui51-audit/base50-final/`; the earlier142-method
  inventory did not include the complete deferred-layout/extension-method coverage.
- Final source-bound acceptance/proofs: `ui51/accepted-final/`; strict final parser
  mutation proof: `ui51/final-analyzer-proof/`; review: `ui51/safety-review.json`.
- Final game AOT: `ui51/aot-final/`; native replay/NRO: `ui51/native/`; complete
  verification: `ui51/artifact-verification.json`; publication: `ui51/published-build.json`.
- `ui51/build_aot.py`, `build_native.py` and `verify_artifact.py` retain the reproducible
  package route. Use the direct SDK with an **explicit container entrypoint override**:
  `--entrypoint /build/runtime-source/.dotnet/dotnet`. Supplying that path after the
  image alone still runs its default env.sh-sourcing entrypoint.
- Final packaging uses only the final accepted source snapshot. Development
  emission/proof snapshots and the failed inspect01 source-change check are not
  release inputs. The completed hardware measurement and its limits are recorded next.

## Build 51 hardware UI measurements (2026-09-20)

**Capture valid; no optimization applied.** Source: `fna-nx-test/log51.txt`,
1,173,092 bytes, SHA256
`29c3c3e4a990e5183dfacb3eefe4a2becb14b951fe748b4e74c91c31d3d6543b`.
The original is archived byte-for-byte under cache `ui51/hardware-captures/`.

### Measurement integrity and state

- The strict reader accepted all**96 complete packets**, including the final
  packet, with no invalidity, capture failure, scope failure, depth overflow,
  imbalance, reentrant frame, nonowner frame or report failure. Maximum scope
  depth observed was10. Native AOT entrypoint and normal shutdown are logged.
- All100 NX_PHASE records reconcile their cumulative counters. Profiler attempts,
  paired captures and completed frames all equal**11,776 native Draw calls**.
  Native elapsed time563.727s; no uncaught exception/assertion marker was found.
- There are**4,963 eligible world frames /311 valid samples** and**1,847 eligible
  inventory frames /116 samples**. Paused inventory has no samples, not zero cost.
  There are4,966 excluded frames, including4,927 menu and39 paused frames.
  Flag counters overlap; inventory flags cover1,849 frames, including transitions.
- No death/ghost/spectator/invalid-player/invalid-scene state was recorded. Unlike
  the native-only50 comparison, these selected cohorts actually record alive,
  unpaused state and stationary boundary positions/cursor.

### Why the whole-capture mean is misleading

The first world sample (packet14) has412.893ms UI time. The first inventory packet
(64) contains two selected samples averaging407.976ms, with a797.597ms maximum.
Those are valid cold-entry observations, **not steady costs**. They inflate the
all-capture UI means to8.360ms world and21.522ms inventory. All raw values remain
in the saved analysis; the steady comparison does not silently discard them.

Steady selection uses the complete contiguous state/cursor segments, not individual
fast samples: world31–63 follows movement28–30 and precedes mixed inventory entry64;
inventory66–83 follows opening/cursor transition64–65 and precedes hover transition84.
Packets85–86 are the short fixed-hover segment before closing/mixed87.

| Segment | Packets | Eligible frames | Valid selected frames | UI ms/selected frame |
| --- | --- | ---: | ---: | ---: |
| Earlier stationary world sensitivity |15–27|1,209|75|7.4430|
| Main stationary world |31–63|3,405|213|**6.8992**|
| Stationary inventory, no hovered item |66–83|1,495|93|**14.1551**|
| Fixed item hover |85–86|151|9|**18.5584**|

The main world/inventory/hover segments share player `(51186.3828125,5558)` and
camera `(50556,5219)`,1280×720, raw zoom target1, UI scale1.1491200923919678,
Frame Skip value1 and daytime. Recorded positions/scales and cursor are constant
within each selected segment. Cursor differs by UI task: `(608,373)` world,
`(61,170)` empty inventory, `(61,61)` hover. Hovered item is type3507/prefix0;
the9-sample hover segment is not a general benchmark of all/prefixed items.
World time still advances and boundary snapshots do not freeze every world detail.

### Child-subtracted UI ranking

These are raw-tick weighted means per **valid selected frame**. Exclusive here
means measured-child-subtracted wall time, still including observer/unmeasured
nested work. Inclusive parent timings overlap and must not be added to this table.

| Category | World ms | Inventory ms | Fixed-hover ms |
| --- | ---: | ---: | ---: |
| Remaining39 UI layers and shared layer setup |2.4385|2.4624|2.4513|
| Text layout/measurement/parsing |1.8448|1.8917|4.3279|
| Text drawing |1.2704|2.3435|3.8741|
| Inventory layer remainder |0.1085|5.5741|5.6767|
| Item-slot remainder |0.1795|1.2342|1.2538|
| Pending mouse-text/tooltip remainder |0.2763|0.2887|0.7100|
| Hotbar layer remainder |0.4571|0.0093|0.0092|
| Instrumented item/localized formatting |not called|not called|0.0030|

Other small layer/root categories complete each UI total. In ordinary world,
text drawing plus layout account for about45.15% of measured UI time and the
aggregated other layers35.34%. The5.574ms inventory remainder matters while the
inventory is open; it is not an ordinary closed-HUD saving. Its source includes
multiple conditional UI/interaction blocks, so no single recipe/equipment/shop
callee is assigned that entire cost. Similarly,39 callbacks and zoom/Begin/End
share `other_layer`; the result does not prove all2.438ms is batch setup.

No instrumented formatting calls occurred in the main steady world/inventory
segments. This rules out that category as the cost in **these** samples, not all
localization or every prefixed-item path. Layout and text drawing, rather than
prefix logging, explain most of the extra cost in the short item-hover segment.

### Important attribution finding: controller instructions

In the main world sample, the `tooltip` category has exactly**213 entries for213
UI samples**, with2.6883ms inclusive time per sample. Main.DrawInterface always
calls DrawPendingMouseText once. **[INFERENCE, source plus entry counts]** The
mandatory wrapper accounts for every category entry here: its additionally scoped
MouseTextInner/item-tooltip helpers did not execute in those selected frames.

Exact `DrawPendingMouseText` source (06000fbf) explains why this category is not
simply an item tooltip: it **always calls DrawGamepadInstructions at IL0000**, then
performs SpriteBatch.End/Begin, and only afterward tests `_mouseTextCache.isValid`
at003e/0043. The2.6883ms is the **whole wrapper**, not a separately measured cost
for controller instructions, their composition, or any one layout call. Its
text/layout descendants are already represented above; do not add them again.

### Next candidate—not yet an approved patch

The pinned source audit identified a concrete within-call redundancy to investigate
in **Main.DrawGamepadInstructions (06000eec)**:

- `GetStringSize` at007a stores a measured size in `V_4`.
- Its only later use,00b2–00c8, subtracts **`V_4 * Vector2(0,0)`** from the draw
  position. For finite size components this contributes zero geometry.
- For glyph-tag instruction strings, this prepass parses/layouts text, and actual
  drawing parses/layouts it again. The draw helper already shares its positioned
  list between shadow and foreground; that existing sharing is not a new target.

**Recommended next source audit:** remove only this geometrically discarded
pre-measurement **if** reachable parser/tag/virtual-call purity, finite metrics and
required initialization/error sequencing can be proved. NaN/infinity multiplied
by zero is not finite zero. Standard text/glyph paths inspected so far support
investigation, not universal safety for every localized/substituted/delegate tag.
Separate Compose/pre-measurement/actual-draw timings would be needed to attribute
the saving; neither broad tooltip time nor IL size is that measurement.

Do **not** cache entire instruction strings across frames or skip instruction
composition merely because no item tooltip exists. Compose reads live input
profiles, controls/items, interaction/build/wire/map/navigation state and localized
substitutions; UI navigation composition can invoke callbacks and change/restore
suggestion state. Preserve its call and subsequent
`AllowExecutionOfGamepadInstructions=false` ordering. The display-disabled return
comes after composition, not before it. Keep hints/shadows/animation visible and
retain the GlyphsScale swap/restore; the pre-measurement and draw use different
scale ordering, so blindly sharing the first layout is not established safe.

This source audit generated121 exact method IL files from59 seeds and34,381
incoming method-reference sites across pinned Terraria/FNA/ReLogic inputs.
Its executable extraction and rerun both succeeded without modifying those inputs.
No optimization, NRO or replacement runtime was produced by the measurement analysis.

### Observer cost and evidence limits

Recorded report bodies total6.274686s versus535.983641s summed report windows:
body/(window+body)=**1.1571%**, excluding footer and inner timing overhead.
Paired snapshots total74.963044ms, **6.3657µs per captured frame**. These are
overhead indicators, not universal subtractions from scope times.51's sampled
frame rate is not a50-versus51 optimization benchmark.

The valid capture and raw stationary/alive state are stronger evidence than
quiet logging alone. They still do not establish random representative sampling,
all effective viewport/capture modes, unobserved intermediate changes, or exhaustive
visual/input/mod compatibility. No separate user regression report was supplied.
**Keep50 as the performance baseline;51 remains measurement only.**

Evidence under cache:
- `ui51/hardware-parsed.json`: complete strict-reader raw packets and aggregate.
- `ui51/hardware-analysis.json`: state validation, steady selections, weighted
  categories, cold samples, overhead limits and next-source decision.
- `ui51/hardware-windows.tsv`: full per-packet UI/cursor/movement table.
- `ui51/hardware-captures/log51.txt`: byte-identical original, hash above.
- `ui51-next-audit/gamepad/findings.json`, `call-data-map.json`, `final/inventory.json`
  and exact IL. Reproduce with `run-extraction.py <fresh-output-name> --build`.
  Earlier direct/helper/closure folders are exploratory; `final/` is authoritative.
- Current hardware state is reflected in `ui51/published-build.json` and the
  build ledger. Existing build-time proofs remain historical proof, not FPS data.

## Controller-hint prepass safety investigation (2026-09-20)

**Investigation complete; no shipping patch or new NRO. Do not delete the prepass
unconditionally.** There is a supported numerical common case, but removing a
measurement call requires more than proving its return value contributes zero.
Main's combined verdict is cache `hint52-investigation/investigation.json`.

### What was investigated

Pinned50 Main.DrawGamepadInstructions06000eec calls GetStringSize at007a and stores
`V_4`. Its only use00b2–00c8 is `position - V_4 * Vector2(0,0)`. Composition and
`AllowExecutionOfGamepadInstructions=false` precede this; GlyphsScale is swapped
only afterward, before actual drawing, then restored **without a finally**.
The investigation left original Terraria/FNA/ReLogic inputs and all NROs unchanged.

Three independent evidence sets were exercised:

- **Root control-flow probe:** complete original root and a probe-only no-prepass
  counterpart, using copied exact FNA vector constructor/multiply/subtract/Zero/
  initializer IL.140 checks across34 cases pass and repeat identically. Composition,
  font access, parser/layout and drawing are explicitly controlled boundaries,
  not a claim of stock tag reachability or GPU execution.
- **Numeric/font proof:** unmodified pinned assemblies run on host .NET9. Five
  packaged fonts are decoded using pinned FNA decompression;54 verification checks
  cover19 font metric cases,89 vector cases,9 just-checking glyph cases,21 plain/
  unknown-tag-fallback string layouts and9 actual typed glyph layouts.
- **Registry/origin/effect audit:**9 ITagHandler implementations,8 registered types,
  13 aliases,5 snippet types and770 exact method bodies. The origin census follows
  62 conservative composition/callback roots and91 materialized direct string
  methods, plus64 label keys across12 cultures (768 values). Four executable cases
  run exact transplanted ItemSnippet.UniqueDraw and Main.LoadItem with an explicit
  test-created asset/repository boundary.

These are source/managed-behavior proofs and bounded counterexamples, not Switch
AOT/GPU tests or a timing/speedup measurement.

### Numeric part: supported, but conditional

The actual MouseText asset is `Content/Fonts/Mouse_Text.xnb`, SHA256
`1258b272b69efddfd043b50a503c9d1309d78bcee7a1e3784e024f5c17c3c5fe`.
NRO-extracted and loose assets match. It has spacing0, line spacing29,159 pages,
38,636 character records and a present `*` fallback glyph. Its kerning and padding
are finite bounded values; negative side bearings are real and not rejected as
invalid data.

Exact stock direct GlyphsScale writes preserve0.85,1,1.75 after initialization
under the closed synchronous-write assumption. GlyphSnippet.UniqueDraw(true)
jumps past texture/style/device/drawing work and reports26×scale dimensions:
22.1,26,45.5 in the executed cases. Root scale is1 and maxWidth−1; the wrapping
shortcut returns its input, not expanded text.

The fixed metrics, finite stock scales and ordinary text/inspected glyph paths
support finite layout dimensions. The root's positions (X12 and screen-height
minus its fixed offset) preserve the zero-product geometry. Proof includes long
strings/multiline/fallback characters and distinguishes standalone MeasureString's
unchecked Int32 newline-height overflow from the root's actual split layout.

This is **not** a general theorem that any finite float configuration is safe:
finite `float.MaxValue` spacing or glyph scale can overflow dimensions; NaN or
infinite measured components produce NaNs when multiplied by zero. Arbitrary
negative-zero positions can also differ bitwise, unlike the root's constructed
positions. Fixed unchanged assets/metrics, initialized lookup/default/culture,
known bounded handlers and normal floating-point semantics are explicit premises.

### Measurement is not universally side-effect-free

The stock registry contains color, item, name, achievement and generic/Xbox/PS/
Switch glyph handlers; PlainTagHandler exists but is not registered by the stock
initializer. ChatManager.Register is public and can replace an existing alias.

The key counterexample is **item tags**: ItemSnippet.UniqueDraw(true) calls
Main.LoadItem **before** its just-checking branch. The executed exact method pair
requests one asset for an unloaded client item, versus zero for loaded assets or
dedicated-server cases; measured size remains24×24. Thus valid general parser
input such as `[i:1]` is not necessarily allocation-only measurement.
This is not proof that default controller hints emit that tag.

Do not invent an ordinary prefix-RNG regression: ItemTagHandler clamps its prefix
option before byte conversion, excluding negative random-roll modes. Prefix can
initialize Main.rand if null, but that is not an observed normal initialized-state
RNG advance.

No ordinary initialized default-registry/bundled-label/stock-binding/stock-name
counterexample was demonstrated. The768 inspected embedded label values contain
no tags. However, the input domain is not universally closed: binding preferences,
external localization/substitutions, public UI events/handler registration, and
raw NPC names can affect text. A housing callback formats NPC.FullName, whose
GivenName setter does not escape markup, and contains housing/audio/input actions.
Therefore composition callbacks must remain; stationary player/cursor state is
not a valid cache key and callbacks are not merely pure string generators.

### Initialization and error order are observable

Asset.Value is a pure backing-field getter—not a lazy-loading/waiting API. Fresh
and loading assets return null. FontAssets' initializer does not fill MouseText;
LoadFonts assigns it later. A fresh language manager also needs active-culture
initialization before ordinary layout; the host proof exercised that failure and
used the public language initialization API before subsequent valid cases.

The first root asset access and prepass occur **before** the GlyphsScale swap;
actual drawing occurs after it. The root probe demonstrates:

- A null asset receiver throws NullReferenceException in both versions, but the
  original leaves scale1.25 while the no-prepass counterpart has already set1.
- A controlled shared parser failure likewise occurs before versus after the swap.
- A stateful controlled prepass/parser changes effects when removed.
- Nonfinite size changes positions even without an exception.

The controlled cases establish dependencies, not normal stock reachability. They
show why retaining only the first getter is insufficient: it does not preserve
every font/culture/layout/handler failure in the removed call. Existing restore is
not finally-protected. Null font behavior is text-path dependent—glyph-only layout
can avoid font dereferencing—so use concrete paths rather than blanket claims.

### Recommended next step

**[INFERENCE] A guarded common-case optimization is worth designing and proving;
the unconditional delete is not approved.** A candidate must:

1. Keep fresh composition/navigation callbacks, the post-compose execution flag,
   and all early display/chat/hint gates in their original order.
2. Retain the original first font-asset access before the scale swap. Skip only
   when the loaded/stable font and culture, bounded metrics, actual safe handler
   identities and confined text/glyph input domain are positively established.
3. Preserve the original prepass and failure sequencing for unready, unknown,
   changed-handler or general-markup cases; do not replace errors with fake results.
4. Keep current-frame drawing, hints, shadows, binding/device/language updates and
   glyph animation. Do not cache the whole instruction string or reuse pre-swap
   layout blindly—the measured and drawn glyph scales can differ.
5. Prove that guard and fallback on the real method and measure net benefit against
   50 before adoption. The earlier2.688ms pending-text parent includes other work
   and is **not** the time this candidate would save.

The investigation itself created no guard or NRO. Its guarded implementation is
now build52 below; any future narrowing of resource-pack/mod/input compatibility
still requires discussion.50 remains the working baseline until hardware results.

### Reproduction and evidence

Cache root: `~/.cache/terraria-switch-build/hint52-investigation/`.

- `root/final/root-proof.json`, matching `root/repeat/`, and `root/verification.json`
  record the140-check probe, exact commands/source hashes and unchanged input pins.
- `geometry/findings.json`, `geometry/il03/inventory.json` and
  `geometry/proof07/{results,glyph-and-lifecycle,typed-layout,verification}.json`
  contain authoritative numeric/lifecycle evidence. Earlier proof06 string g-tags
  were unregistered fallback text, not glyph dispatch; proof07 labels that honestly
  and separately executes real typed GlyphSnippet layout. Do not reuse the old claim.
- `tags/findings.json`, `tags/complete/`, `tags/origins-complete.json` and
  `tags/asset-proof-run.json` contain the registry/effect matrix, exact source,
  localization census and observed repository-request cases.
- `investigation.json` combines the decision, premises, risks and next action.
  All generated probe DLLs are investigation artifacts, not replacement game files.

## Build 52 guarded hint-layout candidate (2026-09-20)

**Host/static/AOT/native/payload verified; stationary hardware retest accepted.52 is the working performance baseline.** See the [retest and adoption](#build-52-stationary-retest-and-adoption) below;50 remains the reference.
The user approved the guarded next step.52 starts from50, not the51 profiler or
other unpromoted experiments. It is deliberately more conservative than removing
the entire GetStringSize call and its parsing effects.

### Exact implementation

Main.DrawGamepadInstructions keeps all76 original instructions,6 locals and
original no-EH shape. Only the call operand at007a changes from the string
GetStringSize overload to a same-signature helper in ChatManager. Initial gates,
fresh ComposeInstructionsForGamepad/navigation callbacks, the subsequent execution
flag, first font-asset access, zero-vector arithmetic, scale swap/restore and actual
per-frame drawing all remain in their original order.

The new helper performs the original `ParseMessage(text, Color.White)` **once**.
It then checks the actual parsed snippets and current font data:

- Safe bounded cases return a finite zero size; that is geometrically neutral in
  the unchanged `position - size*Vector2(0,0)` calculation.
- Other cases call the original IEnumerable<TextSnippet> GetStringSize overload
  using the **same parsed list**. Fallback does not parse twice.
- All parser callbacks—including replaced/unknown tag handlers—therefore still run.
  Decisions use resulting exact snippet types, not a cached assumption that a tag
  spelling or registry entry is permanently safe. Unknown/stateful snippet kinds
  retain original virtual layout/asset behavior.

This keeps parsing allocations/work and removes only the later layout work in
accepted cases. It does not cache composed instructions or layouts across frames,
hide hints, freeze language/bindings/device state, or reuse pre-swap layout for draw.

### Enforced fast-path predicates

- Exact root scale `(1,1)`, maxWidth−1; nonnull exact DynamicSpriteFont; at most256
  parsed snippets and4,096 total UTF-16 text units.
- Every snippet is exactly TextSnippet or the original GlyphSnippet, with nonnull
  bounded text. Subclasses, item/achievement/PlainSnippet types and other unknown
  resulting kinds take fallback. A replacement parser returning an exact safe
  type has already executed its callback; only that known type's layout is skipped.
- Structural eligibility is checked before numeric dependencies. Numeric checking
  follows snippet order: current glyph scale when needed, language readiness/culture
  before plain-text font validation. Glyph-only populations do not touch language.
- GlyphsScale must compare within[0,4]; NaN/infinity/negative/oversized values fail.
- Plain text checks actual current font lookup/default data, spacing in[−1024,1024],
  line spacing[0,1024], used-character kerning components[−1024,1024] and padding
  height[0,1024]. CR/LF follow original lookup-skipping rules. Missing characters
  use the same current default-character fallback. No reflection or cached font
  certificate is used; live mutations are seen on the next check.

The read-only metric checker lives inside the existing ReLogic.DynamicSpriteFont
class so it can legally inspect private character data. This is a **two-assembly
change**: two added ChatManager methods in Terraria, two added font methods in
ReLogic. No existing ReLogic method/field/type/constructor is changed. FNA and the
five unaffected AOT objects remain unchanged. No new assembly reference, native
launcher change, profiler, persistent cache or game-state field is added.

The numeric bounds cover the full accepted population under ordinary binary32,
not merely the executed examples; the conservative derived axes bound is below
2^38. Invalid/unready data takes original fallback rather than receiving a fake
measurement. Original root failures occur before/after the same scale operations.
Pinned primitive/culture initialization ordering was source-reviewed; this is not
an exhaustive cold-start execution proof. Races, native corruption, runtime method
detours and resource exhaustion remain outside the supported contract.

### Verification actually run

- Full static/qualified/raw/SDK/resource/inverse checks recover all**261,278 original
  qualified callsites**. All21,041 other game methods,2,957 original game types,
  32,059 game fields and100 managed resources are unchanged. All1,188 original
  ReLogic methods,248 types and1,594 fields are preserved. Inherited50 Single remains
  exact. Metadata comparison handles existing duplicate method names by owner/order,
  not an ambiguous FullName-only dictionary.
- **640 actual emitted-pair checks** cover161 root cases and45 direct cases with
  118 source-qualified receipts. Original/candidate roots and pinned FNA arithmetic
  run with explicit composition/asset/draw boundaries; helpers/parser/layout/font
  code execute directly in isolated load contexts. Five valid-IL semantic mutants
  are rejected. Unknown handler/snippet fallbacks, live post-success/parser-time
  metric mutations, null manager/culture/font/default, oversized/multiline/Unicode,
  scale edges, outputs/callbacks/errors and same-list fallback are exercised.
- Actual achievement subclass layout runs. The item asset-request fallback uses
  exact current ItemSnippet.UniqueDraw/Main.LoadItem code with an explicit named
  repository boundary. This does not claim actual Item.SetDefaults/GPU execution.
- Independent exact-candidate font proof covers192 cases across five pinned fonts,
  every UTF-16 unit in bounded chunks, and20 numeric witnesses.200,000 warmed font
  guard calls allocate0 bytes. Main proof separately observes0 bytes for100,000
  common-game, font and fallback guard calls each.
- In one controlled1,000-call prepass fixture, parsing retains1,280,000 allocated
  bytes and layout removes288,000 bytes. This is host fixture allocation evidence,
  **not Switch timing, a universal allocation count or a promised FPS saving**.
- Eleven static altered-pair candidates plus a malformed raw primitive signature
  fail validation. Final pair/probe artifacts repeat byte-identically, and all11
  path/input/source runner guards pass. Resolved adjacent FNA/ReLogic/CoreLib bytes
  are pinned as well as separately supplied arguments. Independent safety review
  found no supported-state blocker.

### Switch compilation and package

AOT runs game and ReLogic compilation against the accepted pair in parallel:

| Assembly | Compiled/total | Existing fallbacks |
| --- | ---: | ---: |
|Terraria|52,795/52,819|24|
|ReLogic|20,201/20,205|4|

No new fallbacks. All seven AOT modules' early metadata validates. A control replay
reproduces every allocated50 ELF section. Complete verification covers192,304
control and**192,309 candidate native targets**,15,010 fallback sentinels and all
**16,183 embedded files** per NRO. Only accepted Terraria.exe/ReLogic.dll and their
AOT objects differ, plus displayed NACP title.15,997 Content files, FNA, other five
AOT objects, icon and non-title NACP fields are unchanged. Read-only/non-executable
method tables and no-RWX load-segment invariants remain verified.

Published under `fna-nx-test/terraria-mono/switch/`:

`mono_nx_fna_terraria_nochroma52_guarded_hint_layout.nro`

- Title: `Terraria 52 Hint Guard`
- Size:950,595,700 bytes
- NRO SHA256: `6a34580f9c2f09c030f9687ed4d9f572388b226d539b2a53e8088871bb8b84de`
- Game SHA256: `90b135121829d6650ce0e4f8ddb1395108615aa7d46ae487d279bc3a18698b22`
- Game MVID: `df61a8d1-0622-9555-82c2-a2118b6c9bc4`
- ReLogic SHA256: `856438fe15b91b2bc1972f8ebd710cb3ef0ba2d6d7900e65d33bbf2e3c8d68c8`
- ReLogic MVID: `77a15d83-545c-7620-2bb7-033335c7a666`

### Fresh hardware comparison

The first pair has been analyzed below. This remains the protocol for a retest;
the original uploaded files are archived byte-identically before any replacement.

Use **A=50**, **B=52**, not profiler51. Copy **only the NRO**; the changed managed
pair is embedded. Do not replace SD runtime DLLs. Keep full application mode,
Frame Skip On, `logging=true`, `runtime_logging=false`, resolution/UI scale/zoom,
bindings and world/player setup the same. Keep controller hints visible.

1. In each build, load the same safe shelter, remain alive and let loading settle.
   Stand still with the same selected slot/item and camera, inventory/map closed,
   for60–90 seconds. Do not disable hints to improve the result.
2. Separately exercise inventory/tooltips, item selection and nearby interaction/
   build-mode hints. Check that displayed buttons/text update normally and do not
   disappear or freeze. Keep this activity separate from the quiet timing block.
3. Exit normally and preserve complete `/mono/log.txt` after each run as
   **`log52A.txt`** and **`log52B.txt`** in `fna-nx-test/`, before another run overwrites
   it. If you die, respawn and establish a new stable block. Note visual/input issues.

The runtime guard has its own cost and retains parsing. **No net hardware gain is
established yet;50 remains the performance baseline.** The earlier2.688ms pending
mouse-text category included multiple operations and is not a52 savings estimate.

### Persistent reproduction/evidence

- Tooling: `fna-nx-test/scripts/patch_hint_prepass/`; `run.py` accepts exact50 inputs
  and fresh output only, proves both assemblies and writes readonly accepted files.
- Final source manifest/acceptance and actual proof: cache `hint52/accepted-final/`.
  Exact-candidate font replay: `hint52-guard-font/candidate01/`; development proof
  receipts remain under `hint52-guard-proof/` and `hint52-guard-patcher/`.
- Build recipes and final outputs: `hint52/build_aot.py`, `build_native.py`,
  `verify_artifact.py`, `aot-final/`, `native/`, `artifact-verification.json`.
- Publication and review: `hint52/published-build.json`, `hint52/safety-review.json`.
  Original investigation artifacts are retained as historical evidence, not a
  replacement for the guarded implementation's actual proof.
- Compiler entrypoint remains explicit `/build/runtime-source/.dotnet/dotnet` in
  monobuild with the pristine SDK mounted read-only; native dependency mount and
  other baseline build conventions are unchanged. Source provenance is external
  to runtime helpers; proof-only edits do not change production pair bytes.

## Build 52 hardware comparison

This section records the **first pair**, now superseded by the stationary retest
below. Its death-containing captures and inconclusive decision are preserved,
not pooled with or overwritten by the new measurements.

**Decision: retain50.52 ran and exited normally, but this pair does not establish
a reliable optimization benefit.** No game, runtime or NRO was changed by the
analysis. The original pre-publication receipts remain historical; this section
and the verification ledger supersede their hardware-pending status.

### Capture integrity and observed events

Roles are the requested filenames, **A=50/control, B=52/guarded candidate**;
neither capture contains an NRO hash, build number or MVID proving that assignment.

| Capture | NX_PHASE records | Reported duration | Draw calls | Update calls | Shutdown |
| --- | ---: | ---: | ---: | ---: | --- |
|log52A.txt|77|442.977s|10,274|22,077|normal|
|log52B.txt|117|647.841s|12,613|34,254|normal|

Every window/total count reconciles for Tick, Update, Draw, native Poll and Swap.
Both logs have one final partial report and `Terminating application`; Swap count
equals Draw count, and the final Tick has no Draw in both. Rounded elapsed/window
arithmetic differs by at most1ms. No fatal/unhandled-exception text is logged.

Both report `logging=1 runtime_logging=0`, native Terraria entrypoint, AOT plus
interpreter fallback/full JIT disabled, NV120/Mesa20.1.0-rc3/glsl120 and1280×720.
Both retain the known unavailable dummy-audio warning and disable audio.

They are **not uninterrupted safe-living captures**:

- A line400: `1's depravity was ended by Demon Eye.` It occurs between native
  reports at227.787–232.825s.
- B line416: `1 was ground into sad meat by Zombie.` It occurs at210.728–215.761s;
  B also logs the `YOU_CAN_DO_IT` achievement in that interval.
- TIMBER achievement appears in both earlier entry intervals ending127.005s and
  129.841s. Those are event-containing report ends, not exact world-load timestamps.

No direct camera/player-position, inventory, alive/respawn, enemy/time-of-day,
hint visibility, guard-hit/fallback, or CPU/GPU-clock state is logged. Absence of
extra native polls or chat is **not** a stationary-scene certificate. These captures
cannot visually verify controller hints or input behavior.

### Timing comparison, without whole-run mixing

Use complete non-final windows fully contained in the stated elapsed bounds.
No rows are filtered by favorable FPS, maxima or other timing outcomes. The
160–210s selection is after entry/item-label churn and before both death notices;
it is a **post-hoc descriptive comparison, not a certified matched benchmark**.

| Metric | A / expected50 | B / expected52 | B versus A |
| --- | ---: | ---: | ---: |
|Actual selected elapsed range|162.281–207.606s|160.043–205.692s|9 windows each|
|Draws / window seconds|821 /45.324|738 /45.649|—|
|Draw calls per second|18.1140|16.1668|−10.75%|
|Mean Draw wall time per call|35.3827ms|37.4884ms|+5.95%|
|Mean Update wall time per call|5.7350ms|6.3307ms|+10.39%|
|Update calls per second|59.9903|60.0013|approximately60 in both|

The broader150–210s selection is18.0912→15.9992 Draw/s (−11.56%); the shorter
190–210s selection is18.1119→16.2499 (−10.28%). The unfavorable pre-death result
is therefore not solely the large B Update spike near180.536s. It still does not
identify the guard's cost: Update itself is more expensive although52's intended
change is in hint drawing, and exact scene/system load is unobserved.

**Do not promote52 using its later numbers instead.** Whole windows in260–315s
show15.1115→17.4504 Draw/s (+15.48%) and Draw41.5257→36.6808ms (−11.67%). Those
later windows occur after different deaths and lack a respawn/UI/camera/state
certificate. They are a separate non-equivalent descriptor, not proof of a gain.
The changing sign illustrates why mixing the differently paced capture tails or
selecting a convenient plateau would be misleading.

Formulas: Draw/s=`sum(draw window count)/sum(window seconds)`; mean phase wall
time=`sum(phase ms)/sum(phase call count)`. Source is native `nx_input.c` phase
record/report code (218–288); it resets count/sum/max per report and preserves
cumulative totals. Poll/Swap are inclusive native measurements and may overlap
Tick. No exclusive CPU/GPU cost, per-frame percentile, or layout-only saving is
derived. The earlier2.688ms pending-text profile is not a measured52 saving.

### Result and next measurement

- Hardware execution/normal exit: observed for the labeled pair.
- Hint correctness and guard branch coverage: not established by these logs.
- Reliable net performance benefit: **not established**. A causal slowdown is
  also not established.52 stays an unpromoted experiment;50 remains baseline.
- If testing52 again, use the existing NRO and the same enclosed safe shelter,
  living player, slot, camera and UI settings, with hints visible. Obtain60–90
  settled living seconds per build; separate inventory/hint interaction and keep
  complete logs plus activity notes. After a death, identify a fresh stable segment
  rather than treating the rest of the capture as equivalent. No new build is
  required merely to repeat this measurement.

### Reproduction and preserved evidence

`python3 ~/.cache/terraria-switch-build/hint52/analyze_hardware.py`

This is the frozen first-pair recipe with the original upload hashes. The live
upload filenames now hold the retest and will intentionally fail those old pins;
use the preserved first-pair archives/results for historical evidence. The new
retest command below reads its own pinned archives on subsequent executions.

The actual run validates the pinned raw uploads, count/time arithmetic, shared
logged configuration and normal final records, then writes/verifies identical
archives and computes every reported comparison. It does not invoke a project
test suite or change production files.

- `hint52/hardware-comparison.json`: complete194 parsed phase rows, events,
  whole-window selections, raw sums/counts, deltas, limitations and decision.
- `hint52/hardware-windows.tsv`: every native window with source line and metrics.
- `hint52/hardware-captures/log52A.txt`: SHA256
  `b61f726ed2addea4f205e5c7fec9897b7064315396e1a2080cacda25dc83f23c`.
- `hint52/hardware-captures/log52B.txt`: SHA256
  `f0ffad6350b893f34b2ec295e47af083e700a2b0088c47fef9c924692d9d4a6f`.

Cache paths above are relative to `~/.cache/terraria-switch-build/`.

## Build 52 stationary retest and adoption

**Decision: use52 as the working performance baseline; retain50 as the reference.**
The user explicitly confirmed **A=50, B=52**, tried to use the same position, and
reported not moving. That operator evidence improves on the first pair; it is
not treated as a logged proof of exact camera/world-state equality.

### New captures, kept separate

| Capture | NX_PHASE records | Reported duration | Draw calls | Update calls | Shutdown |
| --- | ---: | ---: | ---: | ---: | --- |
|Retest log52A.txt /50|47|298.469s|5,901|13,283|normal|
|Retest log52B.txt /52|45|285.825s|5,946|12,548|normal|

All92 native records reconcile window and cumulative Tick/Update/Draw/Poll/Swap
counts; elapsed/window rounding differs by at most1ms. Both have one final partial
report, matching Draw/Swap counts, one final Tick without Draw and normal
`Terminating application`. No death or fatal/unhandled-exception message is logged.
Each has exactly one chat event: the TIMBER achievement. The retained dummy-audio
warning appears in both; it is not a new52 failure.

Both retain1280×720, NV120/Mesa20.1.0-rc3/glsl120, `logging=1 runtime_logging=0`,
native Terraria entrypoint and AOT plus interpreter fallback/full JIT disabled.
Build assignment is now explicitly user-confirmed, although not fingerprinted
inside the log. Input/hint appearance and exact guard-hit rate remain uninstrumented.

### Event-aligned results

Anchor at the **end of the report containing TIMBER**:126.652s for A,129.020s
for B. This aligns an observable entry event, not a fabricated precise world-load
timestamp. The primary span is anchor+30–90s, selected before calculating its
aggregate performance. Include **every complete non-final window** contained in
the span—no favorable-FPS, maximum-duration or spike filtering.

| Metric | A /50 | B /52 | Change |
| --- | ---: | ---: | ---: |
|Actual elapsed span|156.928–212.216s|159.168–214.438s|11 windows each|
|Draws / reported window seconds|1,061 /55.288|1,141 /55.271|—|
|Draw calls/second|19.1904|20.6437|+7.57%|
|Mean Draw wall time/call|34.5413ms|32.1900ms|−6.81%,2.3514ms lower|
|Mean Update wall time/call|5.3537ms|5.3100ms|−0.81%|
|Updates/second|59.9949|59.9953|approximately60 in both|

The primary A span contains32 item-label GetValue log entries; B has none there.
Do not silently call this fully chatter-free. The **later anchor+60–110s check**
has zero such entries in either run and still shows:

- A:876 draws/45.234s =19.3660 Draw/s; Draw34.3729ms; Update5.3026ms.
- B:939 draws/45.159s =20.7932 Draw/s; Draw32.0386ms; Update5.2778ms.
- **+7.37% Draw/s,−6.79% Draw wall time,−0.47% Update time**.

Other whole-window checks agree in direction: anchor+30–60s gives+8.09%,
anchor+60–90s+7.19%, broader+20–100s+8.04%, and+40–100s+7.68% Draw/s. Early/late
offset checks each exclude a window straddling their shared boundary; they are
not falsely presented as an exhaustive partition of the11-window primary span.
Both runs' Update spikes remain included, including A's344.939ms maximum in the
window ending207.206s and B's388.376ms maximum ending214.438s.

These are weighted totals, not averages of reciprocals: Draw/s=`sum(draw_count)/
sum(window_seconds)`; mean phase time=`sum(phase_ms)/sum(phase_count)`. Poll/Swap
remain inclusive and are not subtracted to invent exclusive CPU/GPU time. No
per-frame percentile or guard-only timing is inferred from these sparse windows.

### Acceptance and limits

The death-free, operator-reported stationary pair consistently improves rendering
throughput and Draw wall time, including the later label-free check, with Update
cost nearly equal. Together with the existing guarded-path behavior and native
artifact proof, this is sufficient to **adopt52 for continued performance work**.
The earlier first pair stays inconclusive; its unrelated phases are not added to
the retest average, nor relabeled as evidence of a regression.

This remains one ordered A/B retest, not randomized replications or a universal
7.57% guarantee. Exact position/camera, selected-item/UI/hint state, enemy population,
time of day and operating clocks are not logged. No visual/input correctness,
guard-hit percentage, startup/menu improvement or multiplayer/mod compatibility
claim is added by this analysis. The original compatibility discussion gate stays.

Use the **existing** `mono_nx_fna_terraria_nochroma52_guarded_hint_layout.nro`.
No rebuild or SD runtime replacement is needed. Future candidates must retain
the52 Terraria/ReLogic pair and its verified dependencies; do not accidentally
start from50 or the51 profiler.50 remains a useful reference and rollback NRO.

### Reproduction and provenance

Run `python3 ~/.cache/terraria-switch-build/hint52/hardware-retest01/analyze.py`.

The command pins the prior parser/aggregation source, validates all92 new records,
checks the original archives still have their original hashes, and writes/verifies
the separate retest artifacts. Subsequent runs use the pinned retest archives,
not whatever next gets uploaded under the same live filenames. The standalone
results were also checked against independently computed in-kernel totals for
all six interval selections. No project-wide tests or production-code changes.

- Cache `hint52/hardware-retest01/comparison.json`: full parsed rows, event/label
  counts, operator report, all selections, sums/deltas, scope and adoption decision.
- `hint52/hardware-retest01/windows.tsv`: all windows and their source lines.
- `hint52/hardware-retest01/captures/log52A.txt`: SHA256
  `7c6e98ba22dc4589a25f71a646fbb89faea51adfcbb39ace2fdd61837e952bbb`.
- `hint52/hardware-retest01/captures/log52B.txt`: SHA256
  `676da11cf893dd59d27761b5b6a93efa18dc1a5632761ee09b110cf7f111577b`.
- First-pair archives/receipts under `hint52/hardware-captures/` and
  `hint52/hardware-comparison.json` remain unchanged. Pre-publication receipts
  likewise remain historical; current baseline state is in this header and
  `fna-nx-test/terraria-mono/build-verification.json`.

## Build 53 focused rendering measurement

**Host/static/AOT/native/payload and hardware measurement verified. Measurement
only—not a speedup build.52 stays the working performance baseline.** See the hardware cost results below. The user
approved refreshing rendering measurements before selecting another optimization.
The later optimization/UI/audio/world-regression roadmap remains subsequent work;
this build supplies the instrumentation, not invented hardware results.

### Scope and exact baseline

53 starts from52 game SHA `90b135121829d6650ce0e4f8ddb1395108615aa7d46ae487d279bc3a18698b22`
and unchanged52 ReLogic SHA `856438fe15b91b2bc1972f8ebd710cb3ef0ba2d6d7900e65d33bbf2e3c8d68c8`.
It is not a reversion to42/46A, nor a combination of unpromoted47/48/49/51 changes.
All original52 hint guards, parsing/callbacks/fallback behavior and50's late gate
are retained. No FNA, ReLogic, native launcher, SDK or SD runtime change.

A fresh pinned inspection produced exact current52 IL for169 rendering methods
under cache `render53-audit/base52/`. The patch changes exactly seven original
method bodies:

- Main.Draw and Program.RunGame: frame bookkeeping and flush after normal Run return.
- TileDrawing.Draw: solid/nonsolid pass/loop counts, timing and rotating sampling.
- TileDrawing.DrawSingleTile: the existing49 helper callsite timing scheme, now
  applied to the actual52 body including the retained50 gate.
- TileBatch.End: an exception-safe envelope retaining both original Int32 returns.
- TileBatch.RenderBatch and FlushLayered: four call operands replaced with three
  typed wrappers—two concrete SetData<VertexPositionColorTexture> overloads and
  shared DrawIndexedPrimitives. Original receiver/array identity, evaluated arguments,
  options, stride, offsets, counts and callvirt behavior are retained.

No original game type/member list or field changes. New observer bookkeeping is
under nonpublic `Terraria.NXRenderProfile53`; its internal nested structs retain
legal same-assembly access for the pass fields addressed by TileDrawing. There
is no allocating observer static constructor or new assembly reference.

### What the measurements mean

The existing tile sampling scheme remains deterministic rotating**1-in-128**,
independently phased for solid and nonsolid passes. It measures GetColor,
GetTileDrawData, GetTileOutlineInfo, DrawTiles_GetLightOverride and GetFinalLight,
with sampled counts, gross sample time, pass/loop totals and clock-pair diagnostics.
Do not multiply sampled helper averages into a claimed full-frame saving.

Three coarse scopes run on every eligible frame:

| Label | Actual boundary | Interpretation |
| --- | --- | --- |
|`tile_batch_end`|TileBatch.End|Inclusive batch completion: managed preparation plus downstream calls|
|`vertex_upload`|The two SetData<VertexPositionColorTexture> callsites in RenderBatch/FlushLayered|Inclusive upload call, including any driver work/wait|
|`indexed_submit`|Their two DrawIndexedPrimitives callsites|Inclusive indexed submission call, not GPU execution time|

These scopes cover eligible-frame **TileBatch work**, not every graphics call in
the game and not necessarily solid tiles alone. They may overlap pass/loop time
and each other. Child-subtracted time removes timed immediate children but retains
observer/untracked work; it is not pure CPU cost. No GPU query, forced fence,
glFinish or renderer modification is added.

### Validity and reporting

- `NX_PROFILE` version53, schema`render_split`; exact packet contract is
  `scripts/patch_render_profile/render53-schema.json`.
- Complete, stable boundary-state frames donate measurements. Menu, paused,
  inventory, fullscreen map, dead/ghost/spectator and invalid-player/scene states
  are excluded. Raw player/camera/zoom/viewport/frame-skip/day/world-time summaries
  remain available. Snapshot limits remain: toggles between boundaries, capture
  modes, exact scene contents and operating clocks are not fully certified.
- New `BATCH_STATE` plus three `BATCH_METRIC` rows follow the original tile rows.
  Every eligible frame is batch-valid or batch-discarded, including zero-call frames.
  Timings/call counts donate only from valid complete coarse frames. An original
  scope abort discards that frame's coarse metrics without swallowing the game
  exception. Observer faults also set sticky measurement-invalid state.
- Coarse nesting is bounded64; invalid clock/cookie/depth/overflow paths never
  become game fallbacks or fabricated timings. Foreign/reentrant calls retain
  isolation. Counter capacity is preflighted; no saturating/fake measurements.
- Report/capture/clock failures, raw counts, unused-null means and complete/final
  packet status are retained. Parser rejects partial, reordered, duplicate,
  malformed, wrong-version and inconsistent reports; invalid diagnostic timings
  are not presented as usable performance means. Reporting overhead is recorded,
  not magically subtracted from the measurements.

### Proof and defects caught before publication

The exact final emitted game passed**1,396 behavior checks**,77 serialized
source-qualified method receipts, a separate62/62 observer-helper entry replay,
and five serialized semantic mutants rejected by observable assertions. Whole
original/candidate seven-method paths and typed wrappers execute against explicit
external graphics/state/clock/private-batch fixture boundaries. The fixtures do
not implement game geometry or execute a GPU. Warmed active/inactive caller and
runtime rounds allocate0 bytes; inactive coarse calls perform0 clocks.

Final accepted and repeated artifacts each produced35 actual runtime packets
across33 accepted report files. The strict report proof rejected201 malformed
files and checked CLI status/no-output behavior, selection, invalid/final flags,
raw weighted totals and nulls. All eight deterministic game/probe artifacts
repeat byte-identically;12 input/output/source/adjacent-library guards pass.

Static checks preserve2,957 original types,32,059 fields,21,037 untouched methods,
100 managed resources and every inverse of the seven changed bodies.14 static
mutants, receipt tampering, primitive-alias fixtures and eight named-kind mutants
are rejected. Independent raw preservation covers**807,756 signature uses**,
including original definitions/locals, referenced signatures, CLASS/VALUETYPE,
array bounds and required modifiers, before any fixture remapping.

Two review findings were corrected, not accepted as caveats:

1. Late scene/tile observer overflow could occur after coarse donation. Capacity
   preflight now precedes eligible donation, but final coarse commit follows all
   those merges. The coherent VisitedMax+1 reproduction changes from incorrectly
   retaining the second frame to valid1/discarded1/failure1 with no leaked End call.
   The emitted proof covers both tile layers and the scene-merge boundary.
2. Named CLASS/VALUETYPE bits could escape name-only metadata comparison. The
   raw per-use census now preserves them independently of shared Cecil flags;
   changed/generated signatures additionally check pinned TypeDef ancestry.
   Real wrapper/array/MethodSpec/MemberRef/helper-field/local/original-signature
   mutants are rejected. Local/helper fixtures flip the original blob byte,
   preserving tokens/scopes rather than accidentally testing an unresolved type.

Both reviewers rechecked their corrections and reported their findings resolved.
These are bounded host/static proofs: not exhaustive game branches, identical
stack traces, asynchronous interruption/stack exhaustion, arbitrary corrupted
prior state, target GPU output or hardware performance verification.

### Native build and published identity

Game AOT: **52,890/52,914 methods**, unchanged24 fallbacks. Six other52 AOT objects
are retained, including ReLogic. Early metadata validates for all seven modules.
An exact52 control replay matches every allocated ELF section. Complete native
verification covers**192,404 candidate targets and15,010 fallback sentinels**.
All**16,183 embedded files** are checked: only Terraria.exe changes;15,997 Content
files, FNA, ReLogic, the icon and non-title NACP data remain unchanged. Method-table
read-only/non-executable and no-RWX segment invariants remain enforced.

Published in `fna-nx-test/terraria-mono/switch/`:

`mono_nx_fna_terraria_nochroma53_render_profile.nro`

- Title: `Terraria 53 Render Profile`
- Size:950,746,228 bytes
- NRO SHA256: `75e5156d2ab0c6ad07d17d987623c1fa6f1bb0423b196937777fd9969b0529ab`
- Game SHA256: `ba2ef0ed7f0d6e362d2871d5969f143b2d87d3f924907412dbaf6a3f4c03911f`
- Game MVID: `0656bd66-a3fa-8008-36b5-f58dc0a92e7a`
- Frozen source manifest SHA256: `9b9c13fbb590b234d7905ee14f0af3ef635b3bb00e02981103b2d1b653793953`

### Hardware capture: one profiling run

Copy **only the53 NRO**. Keep full application mode, Frame Skip On, existing SD
runtime DLLs/settings, `logging=true` and `runtime_logging=false`.52 remains the
normal-play/performance reference;53 may run slower because it measures work.

1. Load the same safe world and let loading settle. Keep inventory/fullscreen map
   closed and controller hints visible. Remain alive and stationary for about90s.
2. If practical, walk through the nearby terrain for a separate30–60s afterward.
   This is optional; do not sacrifice the safe stationary block. Note any death
   or unusual visual/input behavior—the profiler will exclude invalid/dead frames.
3. Exit normally so the final packet flushes. Preserve the **complete**
   `/mono/log.txt` as **`fna-nx-test/log53.txt`**, including startup and final rows.

This is not another50/52 FPS comparison. The next step is to analyze helper/pass
and batch/upload/submission costs, then select one supported optimization on52.
No such optimization or53 hardware result has yet been claimed.

### Persistent reproduction and evidence

- Source tool: `fna-nx-test/scripts/patch_render_profile/`; strict reader:
  `scripts/analyze_render_profile.py`; adversarial report runner:
  `scripts/patch_render_profile/prove_render_reports.py`.
- Cache `render53/accepted-final/`: frozen sources/commands, acceptance/repeat,
  static/raw/semantic/inverse proofs and exact emitted probes. Both roles have
  `proof/render-parser-proof.json` from the final adversarial report replay.
- Cache `render53/{build_aot.py,build_native.py,verify_artifact.py}` and
  `aot-final/`, `native/`, `artifact-verification.json`, `published-build.json`.
  AOT and artifact verification require the final adversarial report receipts.
- Render inspector: `render53-audit/base52/`. Source-only runtime diagnostics:
  `render53-runtime/`. Earlier inspect01/02 proofs are development receipts;
  final accepted game identity above is authoritative.
- Container commands retain explicit entrypoints (python3 or
  `/build/runtime-source/.dotnet/dotnet`), readonly pristine SDK and existing
  native dependency mounts. The image default entrypoint must not be used with
  the SDK-only `/mono-nx` mount. No commit/push or runtime replacement occurred.

## Remaining-work prerequisites after the53 handoff

Historical triage before the53 upload. The missing-capture prerequisite below is
now resolved by the hardware results that follow; other prerequisites remain.

An additional source/evidence triage was completed while waiting for the physical
53 capture. **No game, runtime, input or audio change was made;52/53 stay frozen.**
These findings narrow subsequent work; they do not mark the remaining fixes done.

- **Rendering optimization:** no uploaded53 capture or version53 hardware packets
  were present in the checked live logs. Selecting and comparing the next fix
  still requires `log53.txt`; host fixtures cannot supply those timings.
- **Inventory/UI:** the retained51 profile of50 has93 stable inventory samples:
  inventory-layer8.4005ms inclusive/5.5741ms child-subtracted per selected frame;
  item-slot3.0558ms inclusive; text-draw4.8815ms and text-layout3.1526ms inclusive.
  These scopes overlap and include observer work—do not sum them or call them
  current52 exclusive CPU costs. Item-format calls were0 in that stable subset,
  so that evidence does not justify another formatting/logging optimization.
  A later source audit should focus the remaining inventory-layer work, not
  silently reuse the already-addressed hint prepass as a new candidate.
- **Audio:** current `native/interpreter/source/main.c:43–46` deliberately forces
  `SDL_AUDIODRIVER=dummy`. `native/interpreter/Makefile:160–164` links real
  `libFAudio.a`; sound is not absent simply because a stub replaced FAudio.
  The recorded reason is the earlier `Audio device already open` failure followed
  by a native FAudio/FACT shutdown Data Abort (original findings204–209).
  Removing the environment override is therefore not a verified audio fix.
  **[INFERENCE]** Device ownership/initialization/failure cleanup is the next
  bounded audio investigation; its cause and sound output need an isolated
  Switch reproduction before enabling audio in the performance baseline.
- **Menu input:** the earlier report was consistent downward menu-entry skipping,
  distinct from the confirmed missed-tap fix. The specific menu, skipped entries,
  D-pad versus stick, and tap versus hold remain unspecified (findings944–950).
  The existing README explicitly defers speculative repeat/navigation/frame-step
  changes. A concrete menu/input reproduction is needed before a new fix.
- **Busy worlds, movement and rendering transitions:** these remain physical
  Switch checks.53's optional movement segment provides timing/state evidence,
  not a visual-output certificate or completion of the wider regression pass.

The active priority remains the53 hardware capture, followed by one measured,
behavior-preserving optimization on52. Audio/menu and wider regression work stay
explicitly queued rather than being represented as completed by source inspection.

## Build 53 hardware cost results

**Measurement valid;52 remains baseline.** Uploaded `log53.txt` was run through
the frozen strict reader, archived byte-identically, and independently reconciled
against its native phase counters. No new optimization is adopted by this analysis.

### Capture validity and selection

-80 complete consecutive packets with a final report;82 native phase records;
  normal shutdown at472.008s.15,892 complete profiler frames equal native Draws.
-2,389 eligible frames;13,503 excluded. No aborted frames, deaths, ghosts,
  spectators, capture failures, report failures or coarse observer failures.
  All2,389 coarse frames are valid; no discarded frames or aborted coarse scopes.
-13,129 frames are marked menu;320 inventory frames and transition/paused work
  remain excluded. The cold mixed-entry packet43 contributes one expensive
  eligible frame and is kept separate from steady measurements.
- Primary selection is the longest consecutive fully eligible, zero-motion group
  with equal reported scene keys: **intervals47–67,2,078 frames**. Raw player and
  camera coordinates are constant there; that does not certify enemy population,
  exact lighting state or all unlogged scene details. Initial movement45–46 and
  earlier spawn-position44 are separate. Inventory69–72 is not folded into costs.

### Stationary measurements

| Measured region | Wall time |
| --- | ---: |
|Solid tile loop|14.5663ms/pass|
|Nonsolid tile loop|1.6869ms/pass|
|TileBatch.End|1.1173ms/valid frame, inclusive|
|Vertex uploads|0.1486ms/valid frame|
|Indexed submissions|0.4102ms/valid frame|

Uploads/submissions are included in batch completion where nested; batch work can
also overlap tile pass timing. **Do not sum these into exclusive CPU/GPU time.**
The measured batch subset is much smaller than the solid loop; this is not proof
that every other graphics operation is cheap or that the GPU is never limiting.

Early stationary47–56 and late57–67 agree: solid-loop14.5773/14.5568ms,
batch-end1.1213/1.1138ms. The short initial-moving45–46 subset has152 eligible
frames and solid-loop14.3536ms; no broad movement-performance conclusion follows.

Stationary solid passes visit4,785 positions/frame and call DrawSingleTile about
2,635.95 times/frame.42,805 selected samples contain one GetColor, GetTileDrawData
and light-override operation each. Their sampled inclusive means are1.1397us,
1.1100us and0.6593us; GetFinalLight occurs7,256 times at1.0070us/call. No solid
outline calls occur in this selection. The clock-pair diagnostic is0.3107us/sample.
These are deterministic rotating samples with observer work—not full-frame savings
obtained by multiplying a sampled mean by every tile.

The coarse stream observes8 End calls,3 uploads and about13.998 indexed submissions
per stationary frame. Recorded report bodies total2.0616s against448.7298s summed
report windows; footer/inner observer costs are not all included or subtracted.

### Source candidates and rejected paths

The apparently redundant GetColor in GetTileDrawData is **not a new discovery**:
`scripts/patch_light_lookup/Program.cs:44–82` already implements the637/638 guard
tested as47. It was not promoted. Current GetTileDrawData is private but has seven
callers, including grass/vine paths; it is not exclusive to DrawSingleTile. That
old experiment is not silently being retried or described as a new fix.

Native inspection of52 shows a2,320-byte DrawSingleTile stack frame and repeated
checks, with real direct calls to Lighting.GetColor. Compiler-source inspection
confirms ordinary optimization defaults are already enabled. Additional controlled
trials were run with the **unchanged52 IL and unchanged compiler/runtime**:

- `--optimize=abcrem`: aborted in `decompose.c:1578`, assertion`!need_sext`.
  The non-LLVM bounds-lowering path lacks that required sign-extension case.
- `--optimize=ssa`: aborted in `mini-arm64.c:6143`, assertion
  `ins->opcode == OP_GSHAREDVT_ARG_REGOFFSET`.

Neither produced a usable candidate. No assertion was bypassed, no unsafe mode
was enabled, and no compiler/runtime replacement was made.

A narrower **private inspection trial** changes only the AggressiveInlining
implementation bit on Lighting.GetColor(Int32,Int32), plus its deterministic MVID.
Every other image byte, all method bodies/signatures/resources and the two earlier
NoOptimization overrides remain unchanged. It compiles with baseline compiler
options at52,795/52,819 methods, unchanged24 fallbacks. Both direct GetColor calls
in DrawSingleTile disappear through inlining; Single native code grows22,500→
23,404 bytes. That is a code-generation change, **not a measured speedup**.

**Compatibility discussion boundary:** signatures and managed logic are unchanged,
but **[INFERENCE]** future runtime mods that detour the standalone GetColor method
may not intercept its newly inlined native callsites. A vanilla-only A/B experiment
may be reasonable while retaining52, but this boundary must be discussed before
publishing/adopting that trial. **No54 NRO has been published or adopted.**

### Reproduction and provenance

`python3 ~/.cache/terraria-switch-build/render53/analyze_hardware.py`

- `render53/hardware-captures/log53.txt`: SHA256
  `208f6ca6b5fb4f21ab51e8c08c554f204dedd839392bff419882c300cd5dd2e5`.
- `render53/hardware-parsed.json` and `render53/hardware-analysis.json`: validated
  packets, native reconciliation, all interval state/cost data and selections.
- `abcrem54/{probe01,ssa01}/`: exact failed compiler commands/assertion logs.
- `lightinline54/{Prepare.cs,Prepare.csproj,candidate01,probe01}`: metadata-only
  inspection trial, byte-difference receipt and baseline/candidate native dumps.
- `lightinline54/investigation.json`: rejected paths, compiled private trial and
  the unapproved/unmeasured compatibility boundary. These files are experiments,
  not replacements for the published52 game or runtime.

## Post53 call-preserving preparation investigation

### User decision: skip GetColor inlining

After discussing the estimate, the user selected **Skip this candidate**. The
sampled1.1397us GetColor inclusive cost corresponds to roughly3ms/frame across
the observed solid direct-call stream, but that is the full lighting query—not
removable call overhead. The earlier0–1ms/frame estimate was explicitly
low-confidence, not a result. The compiled inlining trial is retained only as
evidence; do not promote it or silently retry47's light-lookup shortcut.

The selected direction is a larger per-tile preparation investigation that
preserves the existing GetColor call boundary. Existing simulation, lighting
quality, input buffering, FNA/ReLogic, runtime and assets remain unchanged.

### Address-only scratch-field reuse: bounded proof, no demonstrated gain

An isolated candidate keeps the original TileDrawInfo class and constructor but
caches16 **managed field addresses**, not field values, inside DrawSingleTile.
It replaces213 owner/field-access pairs. Callback writes therefore remain visible;
the owner is still fresh and its original nine-element Vector3 array is retained.

- Actual serialized baseline/candidate Single bodies and the original constructor
  execute under pinned host.NET9 with deterministic external-call fixtures.
  All64 scenarios plus replay controls pass, including callback mutations of16
  fields, early returns, exceptions, identity/allocation checks and zero/one/two
  GetColor paths.12 live interior refs relocate together under forced host GC.
- 19 executed negative mutants are rejected, including stale width across a
  callback, wrong field address and a removed GetColor call. The inverse audit
  aligns all2,312 original instructions, all213 pairs,21,044 method signatures and
  225 ordered qualified call/newobj entries; only Single's body changes.
- Normal game AOT compiles52,795/52,819 methods, unchanged24 fallbacks. The two
  direct GetColor calls remain. Single native code grows22,500→22,912 bytes;
  instruction count5,625→5,728, loads636→737 and cmp-to-zero493→472. Static counts
  are not dynamic timing, but do **not** justify calling this a larger speedup.
- No Switch run, native-GC proof, game/GPU equivalence or performance gain is
  claimed. The candidate is **not published or adopted**.

Evidence under `~/.cache/terraria-switch-build/`:

- `fieldrefs54/{Prepare.cs,candidate01,probe01}`: exact source/preparation,
  compiler receipts and baseline/candidate native dumps.
- Candidate SHA256:
  `8ff7740be2c92cc77676010fdb86cda83ce8a29c7c3e68575b4f2980e532f50e`.
- `fieldrefs54-proof/manifest.json` and `run03/`: frozen replay command, exact
  cloned-method/binding/inverse receipts,64 scenarios and19 negative results.
  Final proof SHA256:
  `a44cd97152811e07031d9ee5c696fe4a653bd478813e31b07f7e52a3c24dcd64`.

### Closed scratch representation: source audit passed

The exact52 typed-use inventory finds DrawSingleTile, seven private TileDrawing
helpers and the public TileDrawInfo constructor; no directly typed owner fields
exist outside TileDrawInfo. The constructor creates a fresh Vector3[9] and calls
Object's constructor. **This typed inventory alone is not an escape proof.**
The array is read in the sliced-block path and crosses lighting-helper byref
boundaries; do not infer that it or the scratch owner may be pooled/reused.

The subsequent actual operand-flow audit follows eight scratch methods plus nine
field-address callees:16,847 instructions and37 byref callsites. Only field
receivers and the closed direct helper arguments are accepted for the raw scratch
reference. Object-erased, identity, null, boxing and escaping consumption are
rejected by the auditor. Actual GetTileDrawData, GetTileOutlineInfo and seven FNA
Color methods are traced, not replaced by fixtures for this source analysis.
This establishes the pinned direct-code closure used by the experiment, **not
compatibility with future reflection/IL hooks or a completed behavior proof**.

### Stack-local candidate03: verified and published as reversible54

The final inspection image appends an internal sequential TileDrawState54 with
the same19 field types. DrawSingleTile uses one104-byte per-call stack local;
seven private helpers receive it by managed reference. Every one of eight helper
call edges is migrated, including both BackRope callers. No pooling or cross-call
reuse is introduced. The fresh zeroed Vector3[9] remains allocated every call.

The guard requires original InitLocals=true, a single entry allocation and no
branch into that allocation prefix. Candidate03 relies on method-entry zeroing;
the redundant explicit initobj from02 was removed. Native code now has one104-byte
state memset, not two. Other local initialization is unchanged.

- Full inverse canonical comparison passes. Raw PE comparison finds **eight
  changed IL bodies and seven signatures**, no added methods or changed existing
  fields. All2,957 original TypeDef,32,059 FieldDef and21,044 MethodDef tokens stay
  fixed. Public TileDrawInfo and its constructor remain; constructor IL/signature
  bytes are identical. The earlier nested-type01 trial shifted716 TypeDefs and
  is superseded by the appended internal-type form.
- Normal pinned AOT compiles52,795/52,819 methods, unchanged24 fallbacks and the
  same baseline WeGame diagnostic. Both direct GetColor calls remain. Single
  native code decreases22,500→19,984 bytes (**11.182%**); all seven helper sizes,
  Draw, GetColor, GetTileDrawData, GetTileOutlineInfo and the public ctor retain
  baseline sizes. Code size is **not** a timing measurement.
- Native AllocSmall(120) plus scratch-ctor call is removed; AllocVector(9)
  remains. **[INFERENCE]** At53's observed solid-call rate this would remove about
  309KiB of owner allocation per solid pass, not that amount of total game memory
  or any established frame-time cost.
- Tradeoffs remain: Single's main stack frame grows2,320→2,416 bytes; decoded
  load/store counts rise1,121→1,133 and695→706. Less native code and fewer heap
  allocations do not rule out zero gain or regression.

**Discussed compatibility boundary, approved for a reversible test only:** removing
the owner allocation changes these private signatures: DrawBasicTile,
DrawSingleTile_Flames, DrawSingleTile_SlicedBlock, DrawXmasTree, DrawTile_BackRope,
DrawTile_MinecartTrack and CacheSpecialDraws_Part2. Existing public TileDrawInfo
API and definition tokens stay fixed, but reflection/IL hooks to those seven
helpers would need adaptation. No general mod/loader compatibility is claimed.

**Physical comparison remains pending.** The finalized production runner,
native/payload checks and publication passed as recorded below. No hardware FPS,
game/GPU equivalence or Switch-GC proof is claimed;52 remains baseline.

Frozen evidence under `~/.cache/terraria-switch-build/scratchstack54/`:

- `candidate03/Terraria.exe`: SHA256
  `9782543073b4f66d629126eec2387b9c8e99fdca03bee143750b756107ea409d`;
  MVID `018aaf69-8e4b-014e-8437-5b97ac20037d`.
- `candidate03/{preparation,initialization,token-stability,raw-change-summary}.json`:
  allowed-change, fresh-state and exact original-definition receipts.
- `audit05/audit.json` and its17 actual method bodies: operand-flow closure.
- `probe03/experiment.json`, compiler log and native dumps: exact normal-compiler
  result; `investigation03.json` summarizes its scope and limitations.
- `../scratchstack54-proof/` is a separate host-proof workspace, not an adopted build.

### Compiler-check inspection: no additional optimization applied

Current Mono emits explicit field-owner checks in method-to-ir.c:10275–10325 via
direct compare/exception pairs or ir-emit.h:893–901. Merely setting
MONO_INST_NONULLCHECK through the no-prefix does not suppress these field paths;
the macro has no ins_flag guard. No suppression-prefix candidate was created.
ABCREM is a post-SSA pass gated in mini.c:3734–3774 and can remove earlier emitted
checks when it runs; IR ordering itself is not the problem. The recorded broad
ABCREM/SSA trials failed compiler assertions. No unsupported flag, local-nullness
compiler fix or runtime replacement has been adopted. Corrected source findings
are in `nullcheck54/investigation.json` under the same cache root.

## Build54 reversible stack-state A/B

### Explicit user authorization

The user selected **Build a reversible A/B** after the seven-private-helper
signature boundary, removed owner allocation, unknown FPS benefit and unknown
loader-adaptation scope were presented. This authorizes finishing verification
and publishing a separate vanilla-only54 test NRO. **52 stays unchanged as the
active performance baseline; no adoption without physical Switch results.**
The record is `stack54/experiment-approval.json` under the build cache.

### Bounded host behavior, allocation and GC proof

Private final run06 and the standalone migrated production API both exit0 against
the exact candidate03 game SHA above. They execute the serialized DrawSingleTile
and all seven changed helpers, plus both original public-constructor clones:
18 clones and23,240 source instructions checked under explicit qualified member,
signature, local and branch remapping. This is not a handwritten replacement
algorithm or just a source-text comparison.

- 79 scenarios and deterministic baseline replays; all seven helpers complete.
  Covered paths include actual chest/dummy/tree scratch writes, slice9/4/low
  paths, rope/track/cage branches, lighting-array replacement, genuine callback
  field mutations, twelve managed-byref output writes, numeric draw/RNG/call
  ordering, early returns and thirteen injected exception boundaries.
- 20 successive calls per side retain fresh zeroed nine-vector arrays and repeat
  scratch/numeric state despite poisoning the previous arrays.
- 24 forced compacting host gen0 collections per side move the texture, original
  scratch array and actual sliced-helper replacement array in all24 cases.
  Each side checks state24 times and216 draw arguments. Observers hold weak
  references and diagnostic integer addresses, not strong roots at collection;
  observing scratch afterward explicitly extends its lifetime for this check.
- Three quiet warmed10,000-call rounds measure **256→136 bytes per invocation**.
  An independent fresh-array-only control also measures136 bytes. The removed
  120 bytes match the native owner allocation; this is not an FPS measurement.
- Five executed negative controls fail meaningful assertions: lost helper
  writeback, wrong height/width field, nonzero initial state, reused array and
  removed GetColor. No success is inferred merely from compiler rejection.

The suite is bounded: not every tile/flame branch is exercised; external
lighting/graphics/assets/RNG/entity dependencies are deterministic fixtures.
There is no OOM, old-generation/concurrent GC, multiplayer, mod-hook, pixel/GPU,
Switch-GC or physical-performance equivalence claim. The owner allocation change
is intentional and was explicitly included in the authorization.

Private evidence: `scratchstack54-proof/manifest.json`, SHA256
`1b7aeb8b7b7ca2795d4ced9ca9319baafe9339e670e0b8c36728981432006fed`,
and `run06/`. The standalone production API migration is separately pinned by
`production-manifest.json`, SHA256
`c49eb3eb22bbe9f233e6c46098bc068ec7b445211a80eb96db6a5519cf630a1b`.
The integrated runner produced its own successful receipts below: changing the
fixture-host assembly identity changes the probe hash, not the target game bytes.

### Finalized tooling, native build and publication

Reproducible tooling is in `fna-nx-test/scripts/patch_tile_stack_state/`.
The complete finalized runner passed acceptance, deterministic repeat and supplied
candidate proof, followed by14 CLI rejection guards. Eight managed/probe artifacts
repeat byte-identically. The14 static mutation checks include raw CLASS/VALUETYPE,
layout, byref, initialization and escape failures. The executable proof again
passed79 scenarios and all five behavioral mutants.

- Frozen source-manifest SHA256:
  `0aadcd0d130dd875622bc3a1b03fc356053b921bb86c903e2856db79cabaa8e3`.
- Final integrated probe SHA256:
  `d70efadad063ea672ea52a03b009a71a73ad562b91d5b88051667363ec0d2f8a`.
- Normal AOT:52,795/52,819 game methods, unchanged24 fallbacks. Early metadata
  validates all seven modules; six non-game AOT objects are retained unchanged.
- Re-linking52 reproduces every allocated ELF section. Candidate verification
  covers **192,309 native method targets and15,010 fallback sentinels**. Method
  tables remain read-only/non-executable; no RWX load segment is introduced.
- All **16,183 embedded files** are checked. Only Terraria.exe changes;15,997
  Content files, FNA/ReLogic, runtime data, icon and non-title NACP bytes match52.
  No SD runtime update is required.

Published file:
`fna-nx-test/terraria-mono/switch/mono_nx_fna_terraria_nochroma54_stack_state.nro`

- Title: `Terraria 54 Stack State`
- Size:950,593,140 bytes
- NRO SHA256:
  `ffae440f0be79bac18a5afed34efb2e515824652de0849628ee4f9322df9f055`
- Published52 SHA256 remains:
  `6a34580f9c2f09c030f9687ed4d9f572388b226d539b2a53e8088871bb8b84de`

Evidence: cache `stack54/{published-build,artifact-verification}.json`,
`accepted-final/runner-results.json`, `accepted-final/{accepted,repeat}/proof/`,
`aot-final/` and `native/`. Recipes are `stack54/build_aot.py`, `build_native.py`
and `verify_artifact.py`. The native command must mount
`recovery46/native-deps/install:/fna-install:ro` in addition to the cache, source
and readonly SDK mounts. All three native archives match52's recorded hashes;
the first missing-mount attempt stopped before linking and is retained separately.
Exact mount details are in `stack54/native-mounts.json`.

### Physical52/54 A/B capture

Copy **only54's NRO** into `/switch/`; keep52 and the existing SD runtime DLLs.
Use full application mode, Frame Skip On, `logging=true`, `runtime_logging=false`,
the same safe character/world and unchanged lighting/display/power conditions.

1. Run52, let loading settle and remain alive/stationary for about90s. Keep
   inventory/map closed and controller hints visible. Exit normally. Save the
   **complete** `/mono/log.txt` as **log54A.txt** before another launch.
2. Run54 and repeat the same location/conditions. Exit normally; save the complete
   log as **log54B.txt**. Compare against52, not the instrumented53 profiler.
3. Note any crash, visual/input difference or death. Optional walking follows
   the stationary block; do not mix it into the main comparison. Return to52
   if54 misbehaves.

No54 performance gain or adoption is established yet. Inventory/UI optimization,
audio/menu polish and broader busy-world/movement/rendering regressions remain
queued; this build does not claim to complete them.

## Post54 checkpoint: host findings and deferred work

After follow-up investigation the user selected **Finish54 comparison first**,
explicitly pausing additional builds until the two captures and visual observations
arrive. No `log54A.txt`/`log54B.txt` exists in the documented workshop or staged SD
log locations at this checkpoint;53 remains the newest uploaded numbered capture.
The four outstanding items are **deferred, not completed or abandoned**:

| Item | State and next requirement |
| --- | --- |
| Compare52/54 | Await the complete52/54 logs under matched stationary conditions |
| Remaining inventory/UI costs | Source audit advanced; isolate recipe-refresh cost before selecting a change |
| Audio/menu polish | Menu skipping not seen recently per user; private audio cleanup fix proven, sound still disabled |
| Busy worlds/movement/render transitions | Await actual Switch gameplay/visual observations; host fixtures are insufficient |

Machine-readable checkpoint: cache `stack54/follow-up-gates.json`. Restore these
items to the active worklist when the user supplies hardware evidence.52 remains
the baseline and54 remains an unadopted reversible test; neither NRO was changed
by this follow-up.

### Inventory/UI: current-source checks, no speculative cache

An executed Cecil inspection of the exact52 and50 inputs covers28 selected
inventory, crafting, chat and ReLogic methods. All28 have equal printed
signatures/locals/instructions across those inputs. This is a targeted source
comparison, **not raw-PE or exhaustive metadata equivalence**, and does not turn
51's measured50 costs into measured52/54 costs. Completed output is
`ui55-audit/base52-complete02/inventory.json`; earlier partial directories are
superseded failed inspector runs.

- The positioned-snippet shadow overloads already call LayoutSnippets once and
  materialize one list before reusing it for shadows and the main pass. A proposed
  duplicate-layout removal would therefore be a false optimization.
- ReLogic.InternalDraw already selects InternalDrawFast for zero rotation,
  no sprite effects and no per-character overrides. Do not claim removing the
  general matrix path would accelerate that already-fast case.
- Actual52 Recipe.UpdateRecipeList (`060005f2`) clears/rebuilds available recipes,
  collects craftable items, scans recipes and evaluates filter/environment/item
  conditions before refocusing/repositioning the list. DrawInventory calls it in
  the crafting path. **[INFERENCE]** This is a sensible next region to measure
  separately; its exact share of the5.574ms child-subtracted inventory scope is
  unknown. Skipping/reordering conditions or caching availability could alter
  current crafting behavior and callbacks; no such change was made.
- The93 stable51 inventory samples record96 item-slot scope calls/frame, about
 248 text-draw and66.7 text-layout scope calls/frame. These are aggregated
  instrumented calls, not independent exclusive costs or unique glyph counts.
  Formatting-call count remains zero in that subset.

Inspection code and outputs remain under cache `ui55-audit/`. No UI optimization
or additional NRO was published.

### Audio: confirmed failed-initialization use-after-free

The private `audio55-investigation/` experiment compiles the actual pinned
FAudio/FACT source with ASan and real SDL2 2.26.5. Holding one real SDL dummy
output open makes FACT's second open fail with **Audio device already open**;
no API mocks or injected failure were needed.

Observed sequence: FACT.c355 creates the engine; failed mastering-voice creation
releases/frees it at385 but leaves `pEngine->audio` nonnull. Subsequent Shutdown or
Release calls FAudio_StopEngine through that stale pointer. Both owned-engine
failure variants abort with ASan heap-use-after-free at FAudio.c970 through
FACT.c453. A separate explicitly refcounted shared-engine case observes an extra
reference release that frees the caller-retained engine prematurely.

The private one-line candidate sets **pEngine->audio = NULL immediately after
that failure-branch release**. It retains the required release rather than
leaking an engine to suppress the crash. All four failure-cleanup variants pass
afterward; shared retained engines are reused successfully and finally freed
exactly once. Owned/shared/supplied-master successful explicit-Shutdown controls
pass before and after.

Important limits and outstanding defects:

- This fixes the reproduced host cleanup path, **not the cause of the Switch
  device already being open or audible sound output**. No FNA initialization
  race or SIMD-after-close cause has been established; earlier such speculation
  is discarded.
- Successful final Release without explicit Shutdown times out both before and
  after. A lock/join deadlock is a source hypothesis, not a debugger-proven fix.
- Separate LeakSanitizer checks report pre-existing80-byte/two-mutex FACT leaks.
  Do not call the complete implementation leak-free or all lifecycle paths fixed.
- External engine/master ownership is not documented by the pinned header.
  The reproduction deliberately reserves a reference for the existing consume-one
  behavior; it does not establish a new borrowed/transfer API contract.

Evidence: `audio55-investigation/report.txt`,
`fact-failed-master-lifetime.patch`, `receipts/run-{before,after}-results.json`,
ASan logs, leak results and artifact provenance. Only a private source copy was
patched; the original192-file source tree/archive were checked unchanged.
No installed library, Mono runtime, game, settings or52/54 artifact was altered.
Audio remains disabled pending separate integration and hardware verification.

## Build54 first hardware comparison

**Both separated native sessions validate and exit normally.54 is not adopted.**
The file association follows the requested52=A/54=B naming convention; neither
new unprofiled session embeds a build ID or scene-state record. Do not represent
that association as a binary identity proven by the log.

### Appended-session recovery and capture validity

`log54B.txt` is two complete sessions, not a54 profiler leak:

- Lines1–2275 are **byte-identical to the already analyzed53 capture**, SHA256
  `208f6ca6b5fb4f21ab51e8c08c554f204dedd839392bff419882c300cd5dd2e5`.
  Its80 profiler packets/final472.008s record were excluded from this comparison.
- Lines2276–2894 are the new619-line unprofiled session. No NX_PROFILE records
  occur in this suffix. Its SHA256 is
  `b8e800fcc08bd59e89fa8869cf779f6e494163bc5f9432080954b9e98ea0cbd5`.
- A is one555-line session, SHA256
  `d9ed52745316ba83fe9113a58e299fb8453f43cc55efd1433eb97834da8eba95`.
  Whole uploaded B SHA256 is
  `d80d901dc3f043e63ac90dbb0441a81f06fff1c830d8affb584ed2ec3729d906`.

Each new session has84 NX_PHASE records. Window/total counters reconcile,
elapsed times increase, first-timestamp fields stay constant, Draw equals Swap,
and only the final Tick lacks a Draw. Maximum timestamp-rounding discrepancy is
0.001s. Both contain the expected quiet runtime logging, native AOT, NV120,
GL4.3 Compatibility/glsl120,1280×720 and disabled-audio startup markers.
No unhandled-exception/native-fault marker occurs in either selected session.

| Native totals | A: reported52 | B suffix: reported54 |
| --- | ---: | ---: |
| Normal shutdown elapsed |479.647s|482.967s|
| Draw calls |8,506|9,124|
| Update calls |24,176|24,334|

These whole-run totals include loading/menus and **are not gameplay FPS**.

### Timings and contamination limits

Windows are selected at fixed elapsed offsets from the end of the native window
containing the TIMBER achievement:130.125s in A,130.945s in B. That is a coarse
event anchor, not proof of identical world entry or stationary scene.

| Window after anchor | Draw/s A→B | Change | Draw wall ms A→B |
| --- | ---: | ---: | ---: |
|5–25s, before A's recorded death|15.516→15.112|−2.608%|42.559→43.268|
|60–120s|16.143→18.692|+15.792%|37.577→34.088|
|120–180s|16.158→18.091|+11.966%|37.548→34.385|
|180–240s|16.758→17.121|+2.165%|35.561→35.065|
|240–300s|13.622→14.310|+5.052%|43.240→41.749|
|60–240s combined|16.358→17.970|+9.854%|36.885→34.488|
|120–240s label-free sensitivity|16.466→17.600|+6.886%|36.532→34.718|

The label-free span covers A250.915–366.730s (1,907 Draws/115.815s) and
B251.636–367.207s (2,034 Draws/115.570s). Its Draw wall change is−4.964%; Update
wall per update also changes6.400→6.237ms. The broader60–240s span contains28/118
item-label logging lines in A/B; the added120–240s check avoids that unequal
logging. Every original window remains in the report; no best-window-only claim.

Critical limits:

- A logs **player death by Demon Eye** at upload line297, bracketed by
 155.310–160.320s. Respawn location, camera, health, inventory, lighting and enemies
  are not logged. Longer post-death windows cannot be certified scene-equivalent.
- B's upload line2858 is **Bradley the Guide dying**, bracketed by
 387.364–392.396s; this is an NPC death, not a player death.
- The short pre-death window is slightly negative, while later windows vary
  from about+2% to+16%. These observations are promising but do not isolate the
  patch from world-state/GC/timing differences or establish a repeatable gain.
- Native timing scopes can overlap; no exclusive GPU time, percentile FPS or
  full visual/GC regression pass is inferred from them.

**Decision: keep52 as baseline and54 as the experimental candidate.** Request a
matched sheltered stationary run without deaths, same settings/location, inventory
and map closed, controller hints visible. Save fresh complete logs as
`log54A2.txt` and `log54B2.txt`; archive/move each SD log aside before the next
launch rather than carrying older sessions forward. Report any visual differences.
No new build or runtime replacement was made for this analysis.

### Reproduction and evidence

Run `python3 ~/.cache/terraria-switch-build/stack54/hardware-review01/analyze.py`.
It pins both whole uploads, the old53 prefix, the extracted candidate suffix and
the existing52 parser implementation; validates every native record; computes
all fixed windows from raw sums; and writes `analysis.json` plus `windows.tsv`.
Raw uploads are retained byte-identically in `uploads/`; the B suffix is retained
in `sessions/log54B.session2.txt`. Final analysis SHA256:
`2cc6b66505121d58536d1330a55fc5488546e1a6466b39b9f28977c69ba03f5a`.

## Build54 retest and parking decision

**Final user choice: Keep52; park54.** The user confirmed matching held item,
cursor, inventory/map state and controller hints, with **No problems noticed**
on54. The retest is stable and modestly faster, but the user chose not to adopt
its seven private-helper signature changes for that gain. This is not a failed
hardware boot or an unsafe-build finding.54's NRO, source and evidence remain
available; no binary was deleted or replaced.

### Control-version clarification

The user reported using an older52 and asked whether a new52 had been supplied.
**No replacement52 was supplied.** The original working52 is the intended control:

- File: `mono_nx_fna_terraria_nochroma52_guarded_hint_layout.nro`
- Title: `Terraria 52 Hint Guard`
- Size:950,595,700 bytes
- NRO SHA256: `6a34580f9c2f09c030f9687ed4d9f572388b226d539b2a53e8088871bb8b84de`

Original `hint52/published-build.json` and54's `stack54/published-build.json`
agree on those bytes. These receipts are not a checksum read from the user's
SD card; the log association still follows the requested A=52/B=54 convention.
No new52 or runtime-DLL installation is needed for normal play.

### Capture validation and matched windows

A2 and B2 each contain one complete unprofiled run—no appended53 session this
time. All73 A2 and52 B2 NX_PHASE records reconcile window/total counters, stable
first timestamps, Draw/Swap equality and the final Tick-without-Draw convention.
Both have expected quiet runtime logging/native-AOT/GL startup markers and normal
shutdown, with no recorded death or unhandled/native fault marker.

| Capture | Whole-run elapsed | Whole-run Draws | Achievement-window anchor |
| --- | ---: | ---: | ---: |
|A2, reported52|423.446s|12,637|233.030s|
|B2, reported54|318.394s|6,828|130.263s|

A2 spent longer before the common TIMBER event. Whole-run totals therefore do
not form a gameplay FPS comparison. A2 additionally records YOU_CAN_DO_IT early
in gameplay; the main comparison starts40s after each TIMBER window ends.

Main selection: fixed40–140s anchor offsets, retaining only whole native windows.
This yields19 windows per capture: A273.294–368.678s (95.384s,1,848 Draws) and
B170.508–265.983s (95.473s,1,912 Draws).

| Main-window metric |52|54|Observed change|
| --- | ---: | ---: | ---: |
|Draw calls/sec|19.374|20.027|+3.367%|
|Draw wall ms/Draw|33.642|32.981|−1.965% /0.661ms saved|
|Update wall ms/Update|5.538|5.391|−2.647%|
|Item-label log lines|0|3,824|Unexplained difference|

All fixed sensitivity windows also favor54 in Draw rate:5–20s +9.962%,30–60s
+4.532%,60–90s +5.039%,90–120s +3.388%,120–140s +0.913%,45–135s +3.672%.
The early/short windows are not the headline result; every selection and its
raw sums are preserved rather than reporting only the best window.

### Logging and interpretation limits

B2 emits exactly **two item-name/prefix log lines per Draw** in the main section;
A2 emits none. The user confirms the same setup in both, so do not invent a
different UI state, blame an old52, or silently remove those lines from timing.
The source of the asymmetry and its cost are not established. No log-adjusted
speedup or lower-bound claim is made.

The observed improvement is modest and repeatably positive across the selected
spans. It is not an exact isolated patch-cost measurement, a confidence interval,
GPU-only timing,1%-low FPS result or exhaustive mod/multiplayer/regression proof.
The earlier first-pair +6.886% label-free number is retained as historical evidence,
not substituted for this retest's +3.367% main result.

After those facts and the private-helper tradeoff were presented, the user
explicitly selected **Keep52; park54**. OptimizationAdopted remains false; the
comparison gate is closed, and another54 retest is not requested. Future larger
cost work starts from52, not a stack of unadopted experiments.

### Reproduction and immutable evidence

Run `python3 ~/.cache/terraria-switch-build/stack54/hardware-retest02/analyze.py`.
It reuses the pinned first-review parser, validates the captures and52 publication
identity, incorporates the user confirmations, and writes all comparisons to
`analysis.json` and `windows.tsv`. No NRO or input log is modified.

- A2 SHA256: `44ad2c4c843e9af6b0544d904c266c8dc965e2d1525ee4e0217a593c8021d18b`
- B2 SHA256: `c9532ab8d55c587c2f27c11841b219a5b6a8d29a6b603c86d1d0c570718c2604`
- Final analysis SHA256: `badcf4139dbc4b32fb5ec2f8398f9e75e8ee10eb91c5f133bfa9962fba425798`
- `captures/` preserves both uploaded files; `user-context.json` records matching
  setup/no-problems answers, `control-identity.json` records the old52 clarification,
  and `decision.json` records the user's explicit park54 choice.

The build ledger and `stack54/follow-up-gates.json` carry the same result and
decision. UI/recipe measurements, audio restoration and wider world regressions
remain separate future work—not completed by this hardware comparison.

## Build55 recipe-cost measurement

**Published and verified on host/static/AOT/native/payload paths; the first Switch
capture has now passed the [hardware analysis](#build55-hardware-recipe-results).
Measurement only—not a speedup build.** The unchanged52 game remains the base.

### Why this measurement and what it covers

The actual52 Recipe.UpdateRecipeList clears/rebuilds recipe availability, gathers
items, tests filter/environment/material conditions and restores visual focus.
51's older inventory profile did not isolate this work. A source scan alone cannot
assign it the5.574ms child-subtracted inventory cost;55 obtains that evidence.

The observer samples one in16 eligible Draw attempts independently for three
cohorts: world, inventory and inventory_paused. It records these11 metrics:

| ID | Metric |
| ---: | --- |
|0|frame_draw|
|1|ui|
|2|inventory|
|3|recipe_refresh|
|4|clear_available|
|5|collect_items|
|6|collect_guide|
|7|refocus|
|8|reposition|
|9|crafting_draw|
|10|item_slot|

Five coarse preparation/finalization methods are timed in their original bodies.
Filter.Accepts, PlayerMeetsEnvironmentConditions, CollectedEnoughItemsToCraft and
AddToAvailableRecipes are **not** instrumented inside the candidate-recipe loop.
Every original call remains in its original order. This limits observer work and
avoids inventing a crafting-availability cache or suppressing callbacks.

Recipe inclusive includes its children. Recipe exclusive subtracts only measured
direct children and still includes unmeasured scan/filter/material work and
observer overhead. It is not pure predicate CPU or GPU time. Unused metrics stay
zero in raw rows and undefined/null in derived means, not a claimed free operation.

Scene capture retains51's raw player/camera/zoom/UI/mouse/hover state and adds raw
guide-item type and available-recipe count ranges. These new ranges are recorded,
not used as extra stability exclusions; available count is not recipes visited.
Snapshots happen at Main.Draw boundaries and cannot certify intermediate state.
Invalid/aborted/ineligible frames and observer errors are counted explicitly.

Source inspection also confirms the alternate route: NewCraftingUI.UpdateContents
calls UpdateRecipeList and is called by NewCraftingUI.Draw. Thus the same recipe
envelope covers its draw-path refresh without replacing that callsite.

### Preservation and implementation

Tooling: `fna-nx-test/scripts/patch_recipe_profile/`; strict analysis:
`fna-nx-test/scripts/analyze_recipe_profile.py`.

Exactly14 existing method bodies receive envelopes or the final flush: Main.Draw,
DrawInterface, DrawInventory; Recipe.UpdateRecipeList and its five coarse callees;
CraftingUI.DrawRecipesList/DrawRecipesGrid; NewCraftingUI.DrawUI; the heavy
ItemSlot.Draw overload; and Program.RunGame's post-Game.Run flush. All existing
signatures, access flags, definition tokens, original callsites, non-target bodies,
resources and earlier compiler/runtime workarounds remain. No wrapper changes
private accessibility or changes method-hook signatures as54 did.

The initial callsite-wrapper design was corrected before publication: some coarse
Recipe methods are private. Enveloping their existing definitions is legal and
retains definition ordering, unlike adding cross-type calls or widening access.
The final build has **zero original callsite replacements and zero typed wrappers**.

Observers use protected owner-thread initialization, fixed arrays, balanced
cookies/stack, monotonic clocks, transactional snapshots, reentry/nonowner
isolation, bounded report storage and final flush. Failures are sticky and discard
invalid timing; they must not suppress original game work or publish partial costs.
No layer-only/stopped-UI machinery from51 is retained.

An inherited unused SourceHash literal initially made game bytes depend on the
tool's source-manifest/path hash. It was removed; provenance remains in the
build-bound tool and external receipts. The final candidate is source-path
independent under the guarded deterministic repeat. Earlier inspection6a819/652836
receipts are not attributed to the finalc9a5 game.

### Final verification

The finalized guarded runner passed acceptance, deterministic repeat, supplied
candidate proof and16 CLI rejection guards on the **final** serialized game.
Four executable/pair artifacts repeat byte-identically;35 source inputs are pinned.

- Original behavior:14 inverse/body audits;50 actual recipe-chain pairs,
  1,027 recipe checks,23 root pairs and8 bounded drawing pairs. Eighteen semantic/
  cleanup mutants were rejected. Fourteen clock faults were actually injected
  across24 attempted offsets; this is not a claim that every attempted offset ran.
- The recipe chain executes the original/candidate UpdateRecipeList and all five
  original enveloped callees, plus actual AddToAvailableRecipes where used.
  Predicate/collection internals and external drawing dependencies remain declared
  deterministic host boundaries; no handwritten replacement algorithm is proof.
- Actual emitted observer:47 helper methods,614 runtime checks covering sampling,
  scopes/cohorts, clock errors, malformed arrays/cookies/depth, overflow, snapshot,
  reporting, reentry/nonowner behavior and cleanup. A warmed4,096-frame host run
  measured zero observer allocations (512 selected frames, no report writes);
  report formatting/first initialization and Switch allocation cost are excluded.
- Actual emitted report fixtures:80 files,59 complete packets;55 structurally
  accepted (11 measurement-valid,44 invalid measurements),25 structurally rejected;
  1,156 derived malformed cases rejected and1,239 CLI runs. Receipt repeat is
  byte-identical across Python hash seeds. Invalid measurement suppresses timing
  interpretation; malformed/partial rows are never silently repaired.
- Normal pinned AOT compiles52,867/52,891 game methods; the prior24 fallbacks remain.
  All seven modules' early metadata validate. Six non-game AOT objects are reused.
  Original52 Single native size remains22,500 bytes; parked54 is not included.
- The52 control re-link reproduces all allocated ELF sections. Candidate checks
  cover192,381 native targets and15,010 fallback sentinels. Tables remain read-only
  and non-executable; no RWX load segment is introduced.
- Every16,183 embedded file is verified. Only Terraria.exe changes;15,997 Content
  files, FNA/ReLogic/runtime data, icon and non-title NACP bytes match52. Both the
  published52 and parked54 NRO hashes remain unchanged after55 publication.

These host/static/native checks do not establish hardware cost, rendered-pixel/GPU,
whole-game/mod/multiplayer behavior or a speedup. The subsequent physical capture
is analyzed separately below; it is not retroactive proof from the host harness.

### Published artifact and reproduction

File: `fna-nx-test/terraria-mono/switch/mono_nx_fna_terraria_nochroma55_recipe_profile.nro`

- Title: `Terraria 55 Recipe Profile`
- Size:950,726,772 bytes
- NRO SHA256: `d64997ef1e1376bdd6cba2465ba51c5eec6d9788e09fd7d5dc736bf329ec9e3c`
- Game SHA256: `c9a5a5a30bcbaa2af5f1a51936567c446c3a03b663b259e50c3d51e4957547b6`
- Game MVID: `a6be1f4a-161e-33fc-51c0-fd8fea997bb5`
- Source-manifest SHA256: `e529332d4095dbfe42248923e2a746d76519459f4fd3b176b0cda348a80bf63a`
- Schema SHA256: `e44278519b2aea531a4b1a946e17818fb5c449665e2d4555d1208d799348996f`
- Analyzer SHA256: `4db89a2a63b74420b4be81df8a1477f0d54c4ca8ff6ae9b9045d49794175666a`

Cache evidence under `~/.cache/terraria-switch-build/recipe55/`:

- `base52-inventory/`, `alternate-inventory/` and `plan.json`: original source/call
  paths, fields and final14-body measurement scope.
- `accepted-final/runner-results.json`, `accepted-final/{accepted,repeat}/proof/`
  and supplied-proof/guard logs: final source-bound executable/negative receipts.
- `aot-final/{build-manifest,runtime-metadata-manifest}.json`: normal compiler and
  dependency metadata; `native/native-build.json`: control replay and candidate link.
- `artifact-verification.json` and `published-build.json`: full target/payload
  checks and publication identity.
- `build_aot.py`, `build_native.py`, `verify_artifact.py`, `native-mounts.json`:
  reproducible recipes and exact readonly SDK/FNA native dependency mounts.

### First hardware capture

Copy **only55's NRO** into `/switch/`; keep52 for normal play and retain the
existing SD runtime DLLs. Use full application mode, Frame Skip On,
`logging=true`, `runtime_logging=false`, and preferably Auto Pause Off.

1. Start a safe sheltered world and let loading settle. Stay stationary with
   inventory/map closed for about30s.
2. Open inventory with its crafting panel visible; stay in the same spot for
   about90s with controller hints visible and cursor away from item tooltips.
   Do not craft, change filters, move or die during this main block.
3. Optional hover/filter/guide interaction follows as a separate segment. Exit
   normally to flush the final report; note any visual/input/crash difference.
4. Save the complete `/mono/log.txt` as **log55.txt**. Preserve/move any previous
   SD log aside before launch to avoid concatenating older captures.

The report header must show `version=55 schema=recipe_costs`.55 may run slower
because it is observing work; its FPS is not an optimization comparison. Use the
reported inventory/recipe/crafting/item-slot scopes to select a real fix on52.
No such fix, cache, condition reordering or performance adoption is included yet.

## Build55 hardware recipe results

**Complete, valid measurement; not an optimization or baseline comparison.**
The user supplied `fna-nx-test/log55.txt`. The pinned strict analyzer and a
reproducible hardware-analysis command both passed.52 remains the working
baseline;54 stays parked. No game, native runtime or NRO was changed here.

### Capture and integrity

- Raw log:1,652,602 bytes/6,359 lines; SHA256
  `1ee41494e5cefc3649eeaf779441bd034e8b34e6d32f08602d57f55d90ef677a`.
- One launch and normal termination. All137 consecutive packets, including
  final137, have `version=55 schema=recipe_costs`; measurement-invalid stays zero.
- All143 native records reconcile:18,104 Tick calls,41,984 Updates and18,103
  Draws/swaps. Profiler frame IDs independently total18,103; the final Tick has no
  Draw. Native elapsed time is778.522s, including startup/menu/shutdown.
-9,619 eligible frames:6,039 world and3,580 inventory. All602 selected frames are
  valid:378 world/224 inventory. No deaths, aborted frames, discarded selected
  frames, snapshot failures, unbalanced scopes, overflows or report failures.
- No inventory-paused or guide-recipe timing samples. This does not invalidate
  the unpaused capture; unused timings are null, not zero-cost measurements.
- `logging=1 runtime_logging=0`. The existing audio failure remains:
  `SDL_INIT_AUDIO failed: Audio target 'dummy' not available`, followed by
  `No audio hardware found. Disabling all audio.` Sound is not restored.
- The log identifies55's schema but does not embed an NRO SHA256/game MVID.
  Publication/native/payload receipts remain separate evidence. No user visual
  or input-equivalence confirmation was supplied with this capture.

### Stationary selections, not whole-capture averages

Selections require consecutive fully eligible single-cohort packets, zero
recorded player/camera/mouse/hover/held-item/day transitions, fixed scene ranges
and matching cross-packet identities. Duration—not measured speed—selects the
main blocks. World-time progression is allowed; guide-item and available-recipe
ranges are monitored, not additional selection exclusions.

| Block | Intervals | Eligible frames | Valid selected frames | Profile window seconds |
| --- | ---: | ---: | ---: | ---: |
| Inventory closed |16–70|5,677|355|280.001|
| Inventory open, raw hover/held type0 |73–90|1,584|99|91.964|
| Inventory open, raw hover type8/held type0 |92–109|1,439|90|92.045|
| Later raw hover/held type5544; retained separately |113|78|5|5.128|

The first three blocks have the same recorded player/camera position,1280x720,
zoom target1, UI scale1.1491200923919678, Frame Skip1 and daytime state.
Both main inventory blocks retain guide type0 and10 available recipes. That is
the output recipe count, not the number scanned. Inventory entry71, settling72,
hover transition91 and later interaction/mixed-state packets are not averaged
into the steady blocks. The full parsed file still retains every packet.
Boundary snapshots do not freeze world/NPC/lighting/text state or certify every
intermediate state. These are sequential observations, not a controlled A/B.

### Measured drawing work

All numbers below are milliseconds **per valid selected Draw frame**. They are
time spent inside named scopes, not the duration of an entire Update+Draw frame.

| Scope | Closed | Open, no hover | Open, hover type8 |
| --- | ---: | ---: | ---: |
| Frame drawing |33.188|39.727|43.469|
| Full UI |6.398|13.515|16.963|
| Inventory method |not sampled|5.731|5.731|
| Recipe refresh, inclusive |not sampled|2.600|2.577|
| Recipe refresh, after measured children |not sampled|1.635|1.614|
| Item collection, inside recipe refresh |not sampled|0.862|0.860|
| Crafting drawing |not sampled|0.418|0.451|
| All measured item-slot drawing |0.621|2.461|2.456|
| UI after its measured direct children |5.778|7.784|11.233|

**These rows overlap; do not add them.** Crafting drawing contains item-slot
work; inventory contains recipe/crafting/item-slot work; UI contains inventory.
Recipe-exclusive1.635ms already subtracts the five coarse children. It includes
remaining recipe setup/scan/filter/environment/material work and observer cost,
not an isolated predicate loop or pure CPU time. Do not subtract the children a
second time.

In the no-hover block, recipe refresh ran once in each of99 selected frames.
Its2.600ms is **45.37% of the5.731ms inventory method**, but only **19.24% of the
13.515ms full UI**. The measured coarse children are item collection0.862ms,
clear0.041ms, refocus0.001ms and reposition0.061ms; guide collection was unused.
No recipe-refresh calls were observed in the355 selected inventory-closed frames.

Pinned52 IL confirms collection clears `_ownedItems`, gathers58 inventory slots,
then handles chest collection, item-group counts and pending crafting requests.
The remaining refresh loop preserves filter, environment and material checks,
stops on an empty output-item type and adds available results in original order.
55 does not isolate these internal collection/predicate costs individually.

The hovered block's UI is **3.448ms higher**, while inventory is effectively
unchanged and recipe refresh is0.023ms lower. The additional measured time is
outside the inventory child. **[INFERENCE]** Tooltip/text/other UI work is a
better explanation than increased recipe rebuilding, but55 does not contain a
separate tooltip/text timer and does not establish a specific causal function.

### Actual Draw rates and the Update budget

The following comes from whole native windows fully contained within each
selected profiler frame-ID range. It is **not** calculated as1/Draw time. Native
windows and1-in16 selected frames are different populations; their means need
not match. Draw counts equal swap counts, not necessarily distinct screen images.

| Block | Native Draws | Window seconds | Draws/s | Draw ms/Draw | Update ms/Draw | Updates/Draw |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Closed |5,552|276.370|20.089|33.719|15.225|2.987|
| Open, no hover |1,541|90.526|17.023|40.176|17.667|3.525|
| Open, hover type8 |1,323|85.556|15.464|43.933|19.850|3.880|

Each logic Update averages5.01–5.12ms in these blocks. Multiple Updates run per
Draw; Update work must be included in the frame budget. None of these55 rates is
a measured speedup against52. The whole-session23.253 Draws/s includes menus and
must not be presented as the gameplay rate.

Observer measurements:36,206 scene snapshots total116.486ms, averaging3.217us
per boundary;137 report bodies total8.963s before their footers, averaging65.421ms
(max147.287ms). Other observer work/perturbation is not fully timed. No overhead
subtraction or log-adjusted FPS claim is made.

### Decision and reproduction

Recipe refresh is a real inventory-only cost, not the missing dominant gameplay
cost. **Keep solid-tile rendering and expensive Update subregions as the main
normal-gameplay priorities.** If returning to UI, the7.784ms no-hover remainder
outside inventory and the hover-associated3.448ms deserve attention too. This
capture does not justify a recipe cache, skipped conditions/callbacks or another
clear/refocus micro-optimization. Compatibility tradeoffs still require discussion.

Evidence under `~/.cache/terraria-switch-build/recipe55/`:
- `hardware-captures/log55.txt`: preserved raw upload.
- `hardware-parsed.json`: every strict packet, raw row, context and full-cohort sum.
- `hardware-analysis.json`: identities, reconciled native records, all stationary
  groups, scope attribution, actual Draw rates, observer costs and limitations.
- `hardware-windows.tsv`: compact stationary-group table.
- `analyze_hardware.py`: pins the capture, original analyzer/schema and existing
  native phase reader; regenerates identical evidence and rejects changed inputs.

Reproduce with `python3 ~/.cache/terraria-switch-build/recipe55/analyze_hardware.py`.
The original strict parser is `fna-nx-test/scripts/analyze_recipe_profile.py`.
No repeat55 capture is needed for these findings; broader-world, audio and
visual/input correctness work is not declared complete by this log analysis.

## Build56 Update-cost measurement

**Verified and published; the first [hardware capture is now analyzed](#build56-hardware-results-nighttime-world-item-growth).
Measurement only—not a speedup build.** The user requested the next target after55.
The chosen target is Update work, using the unchanged52 game; parked54 and the
55 recipe observer are not included.

### Source investigation and why a new measurement is needed

43's normal-world group had10,175 logic Updates for2,715 Draws. Normalizing its
existing timers gives the following **milliseconds per logic Update**, not FPS:

| Older43 region | ms/Update |
| --- | ---: |
| Total Update |5.717|
| Updates in World |4.419|
| Update World (tiles) |1.349|
| Players |0.983|
| NPCs |0.614|
| Items |0.382|
| Projectiles |0.333|

World/entity rows sit inside their enclosing totals. Total Update minus Updates
in World is another1.298ms/Update. These are older43 costs guiding investigation;
55's total Update measurement does not establish the current internal split.

Fresh Mono.Cecil extraction from exact52 pinned SHA
`90b135121829d6650ce0e4f8ddb1395108615aa7d46ae487d279bc3a18698b22`
inspected49 methods; the initial28 extracted records reproduced identically.
It establishes:
- Main.Update calls DoUpdate(ref GameTime), then also handles cinematics,
  queued main-thread actions, base Game.Update and exit logic.
- TotalUpdate times Main.DoUpdate, with multiple original early exits. The
  UpdatesInWorld timer brackets DoUpdateInWorld/Inner and its original finally.
- The old **Update World (tiles)** label actually times both
  `WorldGen.UpdateWorld` and `Main.UpdateInvasion`; client netMode1 bypasses it.
  Original ignoreErrors catch/rethrow behavior is significant.
- WorldGen.UpdateWorld includes wiring, tile entities, lunar activity,
  conditional tile counting/liquids, lightning, rate/housing setup, random
  overground/underground work and falling objects. It is not just a tile loop.
- Random-call ordering, remix-world overground/underground ordering and
  growGrassUnderground writes are load-bearing simulation behavior. None is
  reordered, skipped or cached. Input/navigation, UI state and tile/wall
  animation also run outside the in-world timer and need separate attribution.

Evidence: `update56-investigation/{base52-inventory,world52-inventory,investigation.json}`
under the build cache. The inspector was compiled and run against the pinned
assembly; native sizes or method names alone were not treated as timing proof.

### Measurement scope and units

Root: **Main.Update**, not Main.Draw. One in64 attempts is selected independently
for world, inventory and inventory_paused. The raw protocol says `updates`,
`selected_updates` and `first_update_id`/`last_update_id`; derived means use valid
selected Updates. Reconcile counts against native **update_total**, not draw_total.
Rendered Draws/s still requires native counts divided by elapsed time.

| ID | Scope |
| ---: | --- |
|0|update: Main.Update|
|1|do_update: Main.DoUpdate(ref GameTime)|
|2|in_world: Main.DoUpdateInWorld|
|3|world_tiles: Main.UpdateWorld_WorldGenAndInvasion|
|4|worldgen: WorldGen.UpdateWorld|
|5|wiring: Wiring.UpdateMech|
|6|tile_entities: TileEntity.PerformUpdates|
|7|tile_counts: WorldGen.CountTiles|
|8|liquids: Liquid.UpdateLiquid|
|9|town_housing: UpdatePrioritizedTownNPC and CheckForHousesNearAPlayer|
|10|players|
|11|npcs|
|12|projectiles|
|13|items|
|14|input: Main.DoUpdate_HandleInput|
|15|ui_update: Main.UpdateUIStates|
|16|tile_animation: Main.AnimateTiles|
|17|wall_animation: Main.DoUpdate_AnimateWalls|
|18|main_thread_actions: Main.ConsumeAllMainThreadActions|

Twenty existing methods receive protected envelopes; original Program.RunGame
receives the final flush after Game.Run:21 changed bodies total. Every original
signature/access flag/field/definition token/callsite/non-target body remains.
There are no typed wrappers or per-tile/per-entity/per-predicate clocks.

Inclusive scopes overlap. Exclusive subtracts actual measured direct children
in each dynamic stack, not a global sum of other labels. Worldgen-exclusive
contains remaining random-world work, other unmeasured operations and observers;
it is not isolated tile-loop CPU. Whole-method envelopes also include existing
timer bookkeeping, so their boundaries need not exactly match43's old timers.

Missing do_update with other root work is valid (`no_body_samples`). Repeated or
nested body scopes are allowed; their summed inclusive time may exceed root time
while the exact exclusive partition remains valid.55's UI-only assumptions were
not copied into these rules. Raw netMode/maxTilesX/maxTilesY context is added to
the existing scene snapshots; no stateful simulation getter is invoked. These
fields add observations, not admission exclusions. Boundary snapshots do not
freeze world state or certify intermediate states.

### Final verification and corrected proof gaps

The final full tool accepted **game9e01**, repeated it deterministically, proved
the supplied pair and passed16 CLI rejection guards. Four pair/executable
artifacts repeat byte-identically;36 source files are bound to the final tool.

- Static preservation:21 inverse bodies,20 envelopes,22 unchanged measured
  references,2,957 unchanged types,32,059 fields and21,023 non-target methods;
  original callsite replacements/signature/access changes are zero. Nineteen
  malformed candidate variants and the separate primitive-malformation guard
  were rejected.
- Original behavior:42 actual original/candidate target clones, all21 targets,
  277 scenario records and52 rejected mutants (21 missing-observer,31 semantic).
  Source-qualified deterministic external boundaries are explicit; no target
  algorithm was replaced by a handwritten surrogate. Ref arguments, callbacks,
  RNG ordering, arrays/state and original catch/finally/throw paths are checked
  within the recorded fixture/branch bounds, not as whole-game equivalence.
- Actual emitted observer:47 serialized helper bodies,956 checks and115 actual
  injected fault events (64 clock,29 state,10 scope,3 counter,3 snapshot,6 writer).
  After12,288 warmups,12,288 measured Updates across all cohorts/19 metrics
  allocate zero observer bytes;64 selected Updates per measured cohort.
  Initialization/JIT, reflection/assertions, injected faults and reporting are
  excluded; a fixed clock prevents periodic publication during allocation checks.
- Actual emitted reports:83 fixtures/62 packets;58 structurally accepted
  (13 measurement-valid,45 invalid measurements),25 structurally rejected;
  1,691 derived corruptions rejected and1,780 real CLI invocations per proof.
  No-body, repeated/nested body, all57 metric/cohort pairs and raw signed context
  are exercised. Invalid measurements suppress derived timing; malformed or
  partial packets are rejected, not repaired.
- Normal AOT compiles52,867/52,891 methods with the prior24 fallbacks. All21
  changed original methods and the helper entrypoints are native. Seven module
  metadata sets validate; six non-game AOT objects are reused unchanged.
- Control relinking reproduces52's allocated ELF sections. All192,381 native
  targets and15,010 fallback sentinels verify; tables stay read-only/nonexecutable
  and no RWX segment is added. All16,183 embedded files verify: only Terraria.exe
  changes;15,997 Content files and FNA/ReLogic/runtime data remain identical.
  Icon/non-title NACP bytes match;52,54 and55 published NRO hashes are unchanged.

Two host-proof limitations were fixed before final acceptance:
1. Reflection.Invoke lost ref GameTime copyback when a target threw. Host-only
   direct-call adapters now copy actual ref/out locals back in finally, preserving
   the oracle instead of weakening it. Original game/candidate bodies did not change.
2. Generic state/callback serializers encoded Boolean by type, not value because
   Boolean is not IFormattable. A real candidate-derived DoUpdate mutant removing
   `PartySky.MultipleSkyWorkaroundFix=true` escaped the old generic equality.
   Explicit bool/char value encoding now rejects it by state mismatch. The active
   acceptance attempt was stopped and retained separately, then all proofs were
   rerun from corrected sources. The final mutant count is52, not the earlier51.

Tooling also fixes56's inherited pre-build input-pin collision: independent
role checks reject ReLogic/FNA supplied as the game before compiling. The final
Program gate was already independent. Report proof takes160–175s in observed
runs, so its parent deadline is600s rather than the insufficient120s; no tests or
validation were removed.

The focused emitter's gameab33 and full tool's game9e01 differ in **added helper
declaration/token ordering**: focused compilation listed Timing before State;
the full project lists State before Timing. Complete comparison found286,143
normalized metadata/body/use rows and390 raw signatures identical, unchanged
layouts/bindings/body stacks/resources, and no proof/runtime type leakage. Their
MVIDs differ. **All final acceptance/AOT/native claims use9e01's own rerun proofs**;
ab33 receipts are retained as distinct historical focused evidence.

### Published artifact, evidence and reproduction

File: `fna-nx-test/terraria-mono/switch/mono_nx_fna_terraria_nochroma56_update_profile.nro`

- Title: `Terraria 56 Update Profile`
- Size:950,731,892 bytes
- NRO SHA256: `c1fef7c7086884a3bb70f5c635c849511c6393c4fcccd5cd44a4fd80c6445411`
- Game SHA256: `9e01c870d0b94745fa8e8f51bc850a8c75340070d8d87f3234fbd7a6aa54b313`
- Game MVID: `064133d1-d8ac-4331-0918-1c9b9c9803c9`
- Source SHA256: `3639110181ced76490eac1b53e1ecc8f60e33a55f7d1b7bb294eb54ee9755d1b`
- Schema SHA256: `96d995178fb69729918d203757f395b6f17752a9d243366385d33d27e0953e01`
- Analyzer SHA256: `42b2940efb66488dea22e831ab9d40706e76ed1853c933d3f42c987829f90c0e`

Code: `fna-nx-test/scripts/patch_update_profile/`; strict analyzer:
`fna-nx-test/scripts/analyze_update_profile.py`.
Cache evidence under `~/.cache/terraria-switch-build/update56/`:
- `plan.json` and `../update56-investigation/`: exact targets and source findings.
- `accepted-final/runner-results.json`, `accepted-final/{accepted,repeat}/proof/`
  and supplied-proof/guard outputs: authoritative final9e01 evidence.
- `behavior-focus/bool-gap-repro/`, `compare-emissions/`: reproduced proof gap
  and complete focused/full emission comparison. The stopped pre-fix acceptance
  directory is not final acceptance evidence.
- `aot-final/{build-manifest,runtime-metadata-manifest}.json`,
  `native/native-build.json`, `artifact-verification.json`, `published-build.json`
  and `preserved-artifacts-before.json`: native/package/publication identities.
- `build_aot.py`, `build_native.py`, `verify_artifact.py`, `native-mounts.json`:
  reproducible build/verification recipes preserving52's SDK/native dependencies.

The source-bound runner is `scripts/patch_update_profile/run.py --output <fresh-dir>`
inside the same pinned monobuild/mount setup as55. It runs all proof and negative
gates before accepting a pair. Build recipes refuse unaccepted or mismatched inputs.

### First56 hardware capture

Copy **only56's NRO** to `/switch/`; retain52 for normal play. Do not replace SD
runtime DLLs. Use full application mode, Frame Skip On, Auto Pause Off,
`logging=true` and `runtime_logging=false`.

1. Enter a safe sheltered world and let loading settle. Keep inventory/map closed
   and stay stationary for about120s.
2. Open inventory in the same position for about60s, cursor away from item
   tooltips. Do not craft/change filters during this steady block.
3. Optionally close inventory and walk/play normally for another60s as a separate
   segment. Avoid death and note any visual/input/behavior difference.
4. Exit normally. Save the complete `/mono/log.txt` as **log56.txt**; move any old
   SD log aside before launch so captures are not concatenated.

Expect `version=56 schema=update_costs sample_denominator=64 metric_count=19`.
Counts and means refer to **logic Updates**. This profiler may run slower; it is
not a speedup comparison. Use its hierarchy to select a real improvement on52.
The host/native checks above do not establish hardware timing, rendered-pixel/GPU,
sound restoration, whole-world/mod/multiplayer equivalence or performance adoption.

## Build56 hardware results: nighttime world-item growth

**The capture is valid and narrows the progressive slowdown to world-item Update
work.** The user reported56 was a lot slower. This is a profiling capture, not a
controlled52 comparison; the slowdown is not automatically blamed on either the
profiler or baseline52. No game, native runtime or NRO change was made here.

### Capture integrity and exclusions

- `fna-nx-test/log56.txt`:2,132,826 bytes/9,038 lines, SHA256
  `68b5237e803a19c6c89874a0846dbf1bd1dcb26489be1e71aad1bb3a96a9d792`.
- All127 consecutive56/update_costs packets, including the final packet, pass
  the original pinned analyzer. Measurement-invalid, capture failures, aborted
  Updates, discarded selected samples and observer failures are zero.
-130 native records reconcile38,258 Updates,10,600 Ticks and10,599 Draws/swaps;
  normal termination after714.622s, including loading/menu/shutdown.
-32,258 eligible Updates:24,827 world and7,431 inventory. All505 selected samples
  are valid (388 world/117 inventory); no inventory-paused samples.
- Day becomes night in interval31. A zombie death is logged at line8341;
  dead-state counters cover2,597 Updates in intervals118–127. Main comparisons
  precede that event. Do not describe this as a death-free session or mix entry,
  movement, inventory changes and the death into one gameplay average.
- `logging=1 runtime_logging=0`. The log identifies the56 schema, not an embedded
  NRO hash/MVID. Host/native publication receipts remain separate evidence.

### State-based selections

Same common snapshot policy as55: consecutive fully eligible single-cohort
packets, no recorded player/camera/mouse/hover/held-item/day transitions and
matching fixed scene values. All stationary groups are retained. World time and
raw guide/recipe/network/world-size fields remain observations, not new admission
filters. Primary blocks record single-player netMode0, a6400x1800 world,
1280x720 output, zoom1 and UI scale1.1491200923919678.

| Block | Intervals | Eligible Updates | Selected samples |
| --- | ---: | ---: | ---: |
| Stationary day, inventory closed |18–30|3,998|63|
| Entire stationary night, inventory closed |32–78|14,430|226|
| Early night: first10 packets of that run |32–41|3,071|48|
| Late closed night: last10 packets of that run |69–78|3,074|48|
| Night, inventory open without hover/held item |80–99|6,156|96|

The early/late-night windows use chronological edges, not best/worst timing
selection. Both have the same recorded position/camera/mouse and closed UI.
The night run's average hides a progressive cost increase. Later short stationary
groups104 and106 are retained separately, not promoted over the long blocks.

### What became expensive

Milliseconds **per valid selected logic Update**; parent/child rows overlap:

| Scope | Day closed | Early night closed | Late night closed | Night inventory |
| --- | ---: | ---: | ---: | ---: |
| Main.Update root |5.341|5.775|8.281|9.144|
| World-item updates |0.084|0.302|2.545|3.449|
| Worldgen |1.413|1.420|1.312|1.318|
| Players |0.982|0.980|1.033|0.985|
| NPCs |0.463|0.596|0.891|0.859|
| Input/navigation |0.741|0.706|0.719|0.769|

While position and inventory state remain unchanged, Update rises2.506ms and
the item scope rises2.243ms: **89.49% of the observed Update growth is in items**.
The rise starts before inventory opens. Item cost continues above4ms in the
short post-inventory, pre-death samples; it is not a recipe/UI refresh effect.

These are **loose/world items**, not inventory-slot rendering. Worldgen is still
substantial, but does not grow with the slowdown. Liquid/wiring/tile-entity work
is not the large rising term in this capture. The item submethods were not
individually timed, so the log does not yet identify their internal split.

### Actual native rates and profiler overhead

Only whole native windows contained by each selection's cumulative Update IDs
are used below. Their population differs from1-in64 selected samples.

| Block | Draws/s | Native Draw ms/Draw | Native ms/Update | Update work ms/Draw |
| --- | ---: | ---: | ---: | ---: |
| Day closed |16.446|39.346|5.651|20.622|
| Early night closed |15.882|38.829|6.166|23.290|
| Late night closed |12.629|37.522|8.579|40.820|
| Night inventory |9.012|47.010|9.469|63.027|

The stationary night slowdown occurs even as Draw work per Draw becomes slightly
lower. More Update work and more Updates per rendered Draw dominate that trend.
Draw counts match swap counts, not necessarily distinct visible images. None of
these figures is calculated as1/Draw or1/Update time.

Measured observer costs:76,516 snapshots total242.290ms (3.167us per snapshot);
127 report bodies total11.548s before their footers, averaging90.927ms, max160.771ms.
Together these measured parts are **1.65% of total session elapsed time**. That is
not a complete overhead estimate: other hooks, state aggregation, validation,
report footers and codegen/cache/GC perturbation are not isolated.

Native average Update exceeds the selected root mean by roughly0.30–0.39ms in
the main selections. Different samples/windows and timing boundaries prevent
treating this as an exact correction. The data supports attributing most of the
**progressive measured Update growth** to the item scope, not claiming that all
of56's difference from an earlier run is independent of instrumentation.
There is no scene-matched52 control in this upload.

### Source-supported next target and limits

Fresh pinned52 inspection is retained under `update56/item-investigation/`.
It extracted56 world-item/spawn methods, then58 including stacking predicates;
the original56 metadata/body records reproduced identically.

- Main.UpdateWorld_Items visits all400 Main.item slots and calls
  WorldItem.UpdateItem(int).56 times the outer method, not each individual item.
- Inactive WorldItems return early. Active items can perform stacking, special
  monster pickup, physics/collision, despawn checks and visual effects. The visual
  path can make RNG and lighting calls; it is not automatically safe to skip.
- TryCombiningIntoNearbyItems can scan later slots up to400 for each eligible
  item. It checks air/type/prefix/ownership/shimmer and then Manhattan distance;
  successful merges change stacks, position/velocity, lifetime and network sync.
  This repeated scan is a concrete scaling concern, not a measured inner hotspot.
- Vanilla Item.CanStack is only13 IL instructions comparing type/prefix; it is
  not an expensive reflection callback. Do not blame that comparison alone.
- SpawnFallingObjects contains a dayTime/remixWorld-gated starfall/projectile
  path; item type75 has daytime despawn handling. **[INFERENCE]** Accumulating
  night-spawned items, potentially fallen stars, fits the cost ramp. Active-item
  counts/types were not logged, so that specific explanation is not established.

**Mob-related causes are not ruled out.** In the same early/late closed-night
windows, the NPC-update scope rises0.596→0.891ms/Update, while the item scope
rises0.302→2.545ms. This locates measured work; it does not establish its cause.
WorldItem.UpdateItem also calls guarded monster-pickup routines: special-item
checks and, for eligible coins in expert mode, an NPC scan. Those costs belong
to the item scope, not the NPC-update scope. **[INFERENCE]** Mob-related loot or
eligible item–NPC interactions could contribute to item-side growth; the log
does not measure their occurrence/cost or active NPC/item populations. Falling
stars remain a hypothesis. The next item investigation must include mob context
and distinguish these branches rather than treating items and mobs as exclusive
explanations.

**Secondary follow-up: WorldItem.UpdateItem, its stacking/movement/visual branches and the
active world-item population.** This is narrower than another broad Update
profile. Do not delete items, reduce spawn rates, force despawns, skip physics
or add an invalidation cache from this evidence. Gameplay/RNG/network/lifetime/
mod tradeoffs still require discussion. No optimization or new NRO is claimed.

### Reproduction and retained evidence

Under `~/.cache/terraria-switch-build/update56/`:
- `hardware-captures/log56.txt`: unchanged raw upload.
- `hardware-parsed.json`: all strict packets, raw/context rows and whole-cohort sums.
- `hardware-analysis.json`: complete native reconciliation, all stationary groups,
  chronological splits, item-growth attribution, overhead indicators and limits.
- `hardware-windows.tsv`: per-packet/cohort time evolution; no hidden filtering.
- `item-investigation/`: pinned source inspector, IL and call/field inventories.
- `analyze_hardware.py`: input/analyzer/schema/native-reader pins and reproducible
  analysis; a second hash-seed run reproduced identical evidence.

Run `python3 ~/.cache/terraria-switch-build/update56/analyze_hardware.py`.
52 remains the normal-play baseline.56's capture question is answered; no repeat
is needed for this attribution. Inner item costs/population, broader-world
behavior and an actual behavior-preserving speedup remain unproven.

## Solid-tile priority and realistic30FPS budget

The user selected solid-tile rendering as the main focus because it is a
consistent game cost. This supersedes the earlier plan to lead with the item
investigation. Item/mob-related nighttime growth remains important secondary
work; it is not dismissed, solved or evidence against the tile priority.

The available tile measurements support this choice within their capture:
53's stationary solid loop is14.566ms/Draw, with early/late halves14.577/14.557ms.
This is not proof that tile cost is constant in every location/world.

### Budget calculation, not a speedup prediction

The unprofiled52 control from54's retest (`post_anchor_40_140`, A) records
19.374 Draw/s,33.642ms/Draw,5.538ms/logic Update and0.798ms/Tick outside the
measured Draw/Update regions. Scene stationarity was reported by the user, not
verified by embedded coordinates.53 supplies the separate tile-loop estimate.

Assume normal60 logic Updates/s, fixed per-Draw overhead and a30 Draw/s target:

| Target-frame component | Planning budget |
| --- | ---: |
| Complete frame at30FPS |33.333ms|
| Two logic Updates |11.076ms|
| Other Tick work, assumed unchanged |0.798ms|
| Remaining budget for Draw |21.459ms|

Compared with the control's33.642ms Draw, this requires **12.183ms less Draw work**
if Update and other costs do not improve. The target needs two Updates per Draw,
not the roughly three currently observed near20FPS; no simulation frequency is
being reduced to manufacture a rendering gain.

**[INFERENCE]** Under this simplified model:
- A25% reduction in tile cost alone models about21.7FPS.
- Halving tile cost models about24.6FPS, not30.
- A75% reduction models about28.4FPS.
- Tile-only progress to30 would require roughly84% of that tile cost removed.
  That is too aggressive to assume without an actual demonstrated change.

An illustrative combination—halve tile cost, remove3ms elsewhere in Draw and
reduce each logic Update from5.538 to4.5ms—models about30.2FPS. **No such combined
improvement has been implemented or established.** It shows the scale of work,
not a forecast, commitment or justification for changing gameplay.

Model equation: `Draws/s = (1000 - 60 * ms_per_Update) / (ms_per_Draw + other_ms)`.
Its unchanged case approximates the observed control at19.388FPS. It combines
different captures/scopes, assumes linear/fixed costs and uses main-thread wall
time rather than isolated CPU/GPU execution. Pacing, GPU waits, scene changes and
busy/nighttime Update costs can change the result. Stable30 in all scenes is a
stronger requirement than reaching30 in the normal stationary scene.

### Realistic expectation and next work

**[INFERENCE]** Further improvement is plausible; guaranteed30FPS is not supported
by present evidence. The previous runtime/diagnostic fixes and small tile trials
did not exhaust game-side renderer restructuring, but a large measured region
does not prove that its work is removable. There is no already-proven12ms saving
waiting to be enabled. Ordinary compiler defaults are already enabled; the
ABCREM/SSA trials failed, and54's modest scratch result remains parked.

The earlier user-reported L4T result is about50FPS with Frame Skip enabled. This
suggests the Switch hardware alone is not a proven20FPS ceiling, but execution
mode and matched scene conditions were not confirmed. It is not a throughput
guarantee for this Horizon/mono-nx runtime/driver path.

Primary work: source-supported reduction of repeated solid-tile preparation,
lighting/traversal/queuing work, looking for **several milliseconds**, not treating
minor inlining or the parked54 tweak as a new large solution. Each real candidate
still needs behavior preservation and a controlled hardware comparison. Potential
lighting, visibility, simulation, object-lifetime or mod/protocol tradeoffs must
be discussed before applying them. Secondary Update/item/mob costs still set a
limit, particularly in busy scenes, even after a tile improvement.

Calculation evidence: `~/.cache/terraria-switch-build/render53/30fps-feasibility.json`.
52 remains the working baseline. No new performance result, compiler change,
renderer change or NRO was produced by this assessment.

## Solid-tile follow-up: slice-address trial and allocation gate

Resumed the user-selected solid-tile investigation on exact52. Fresh source
extraction covers168 tile/queue/lighting methods, extended to171 with concrete
lighting-engine lookups; every earlier method record reproduced identically.
TileBatch already has a same-texture/layer fast path, and texture lookup already
caches its last paint key. Neither is a missing cache to advertise as a new win.

### Completed private slice-address experiment—not promoted

Target: DrawSingleTile_SlicedBlock, token0600454f. Each9/4-slice iteration loads
the same Vector3 array element separately for X/Y/Z. Candidate keeps the first
checked address, reuses a managed reference for the next two component reads,
and clears it before the next original call. It does not cache component values,
change floating arithmetic/order, skip lighting/draw calls or reuse arrays.

- Candidate SHA: `f3830f177bd74ea14488dd5047bf8aa3f55e0549667d2b1884dad694c43e5518`.
- Candidate MVID: `43c2c569-06b2-3b0b-ad61-23f457c6bb67`.
- One body changes528→530 IL and19→20 locals;31 original calls remain ordered.
  Serialized inverse restores the original body. Original type/field/method
  metadata, access/signatures, resources and other bodies compare unchanged.
- Actual copied target bodies run with equal typed external fixtures:286
  scenarios,73 exceptions and296 forced-compacting callbacks/body agree;10
  negative-control checks reject wrong element/array identities. Arrays remain
  rooted for comparison, so collection-eligibility/lifetime proof is not claimed.
- Normal unchanged AOT options compile52,795/52,819 methods, with unchanged
  fallbacks. Sliced native code is3,616→3,384 bytes (904→846 instructions), array
  length/index-check sites6→2, compare-zero sites78→70, loads162→154, stores143
  unchanged and native calls36 unchanged. Stack frame stays496 bytes. Other
  watched Single/Basic/GetColor method sizes are unchanged.
- Three fresh processes, CPU4, tiering disabled,20k warmups and31 alternating
  AB/BA pairs of300k invocations per9/4/1-slice mode; every measured allocation
  delta is zero. Pooled candidate/baseline medians are0.99060/1.00174/0.99189;
  means0.99818/1.00850/0.99383. Even the unchanged1-slice branch fluctuates.
  Same-original controls bracket the experiment. **No substantive useful host
  improvement is established; these are not ARM64 timing or Switch FPS results.**

The native reduction is real, but only a subset of the roughly17% of measured
solid tiles reaching basic drawing can enter this sliced path. Do not multiply
nine iterations by every2,636 Single calls or turn code-size reduction into FPS.
No NRO was built or published from this trial.

Evidence: `tile57-investigation/slice-ref/{candidate01,probe01,native-code-summary.json}`
and `slice-ref/proof/candidate01-analysis/{report,source-freeze}.json`. Raw paired
and identical-original control receipts remain beside them. The existing
`probe_aot.py` and proof `run.py` reproduce only private experiments, not acceptance.

### Proposed next experiment requires a contract decision

The full-assembly usage audit at
`tile57-investigation/scratch-usage/baseline/usage.json` confirms:
- TileDrawInfo and its `Vector3[] colorSlices` field are **public**.
- Its public constructor allocates a fresh nine-vector array unconditionally.
- Exactly three field accesses exist in pinned52: that constructor store and
  two SlicedBlock loads (nine- and four-slice lighting). Eight methods use the
  scratch type; no assembly fields retain it and no matching reflection-name
  literals were found. This does not rule out external hooks/reflection/mods.
- Many measured dark/non-basic tiles never reach either array-consuming path.

Proposal, **not implemented/approved**: defer a fresh array allocation until a
9/4-slice path actually needs it. Keep fresh storage when used—no pooling,
cross-tile reuse, skipped RNG/callback/physics or changed quality. This can avoid
unused array allocation/payload initialization, but savings in milliseconds/FPS
are not established. The108-byte vector payload alone times53's approximate
2,636 calls and83% no-basic fraction is about0.24MB/Draw; array headers/alignment
and native allocator timing are separate, unmeasured costs.

Tradeoff: the public field would initially be null instead of a nine-element
array. Vanilla pinned usage supports deferral, but mods/hooks expecting the old
constructor-initialized field could need adaptation. This is an initialization/
object-lifetime compatibility change even though original signatures can remain.
Per the user's mandatory gate, ask before implementing/prototyping that change.
52 remains the working baseline; substantial tile savings and30FPS remain unproven.

### Deferred-array theoretical savings requested before approval

The user asked for a per-frame estimate rather than approving the initialization
tradeoff. The earlier proposal quantified allocation volume, not time. Relevant
older hardware evidence exists:45 measured1.362us per selected solid-tile
allocation/constructor bracket (later-window1.353us). Its patch brackets
`newobj TileDrawInfo`, including owner allocation, nine-vector array allocation/
initialization and some observer-boundary work—not the array alone or later GC.

**[INFERENCE—conditional planning, not a measured saving.]**53's approximate
2,635.95 calls/Draw and no-basic fraction `1 - 7256/42805` give about2,189
potentially omitted array allocations/Draw. Applying45's constructor mean gives
2.982ms of gross bracket time on those calls. Owner allocation remains, and lazy
checks cost something, so this is not the direct saving. Other basic draws may
also leave the array unused, but their frequency is not established.

| Assumed array portion of that bracket | Illustrative direct saving | Modeled rate with the52 planning budget |
| --- | ---: | ---: |
|25%|0.75ms/Draw|19.8FPS|
|50%|1.49ms/Draw|20.3FPS|
|75%|2.24ms/Draw|20.7FPS|
|100%, deliberately optimistic gross scenario|2.98ms/Draw|21.2FPS|

These shares are illustrations, not measured probabilities or a confidence
interval. The unchanged model is about19.4FPS, with60 logic Updates/s and fixed
other costs. A1ms saving requires about0.457us per omitted array;2ms requires
0.914us;3ms requires1.370us—approximately the entire older constructor bracket.
Different captures, probe cost, lazy checks/codegen and unmeasured GC effects
prevent treating3ms as a strict upper bound on all possible effects.

This is a **modest low-single-digit-ms experiment**, not an established major
step to30FPS. Zero net gain remains possible. Do not scale54's0.66ms observed
Draw difference by object/array byte sizes:54 changed more than allocation and
is not an isolated allocator benchmark.

Evidence: `~/.cache/terraria-switch-build/tile57-investigation/deferred-array-savings-model.json`,
the45 hardware operation-cost section/`hardware-build45-analysis.json`, and53's
stationary counters. **Decision: preserve the existing construction contract and
continue elsewhere.** No deferred-array candidate was implemented or approved.

## Build57 lighting value-local comparison

**Verified comparison candidate; hardware result pending, not adopted.** After
the user chose **Preserve contract; continue elsewhere**, TileDrawInfo construction,
its fresh nine-vector colorSlices array and original initialization/lifetime were
left unchanged.57 is a different change; it does not include the unpublished
slice-address trial, deferred allocation,54's helper-signature changes or a profiler.

### What the new investigation established

The171-method tile/lighting source extraction preserves the earlier168 records.
Existing texture/paint and same-texture/layer queue fast paths already exist.
The large GetTileDrawData helper is not necessarily a large executed path:
32 source-qualified host fixtures copy all5,140 original instructions and show
ordinary metadata-confirmed Dirt0/Stone1/Grass2 taking76 instructions (79 for half
blocks). Other tested ordinary grass types take67–69. Ordered calls and ref
outputs match the plain projection, including four injected boundary exceptions.
Calls are Empty, Transparent, GetColor and halfBrick; the target reads tileFrame,
Campfires and tile.type once. A lighting callback mutating Campfires changes later
outputs, so a blanket early return or assumed invariant would violate the contract.
This trace did not establish another substantive common-preparation shortcut.
Evidence: `tile57-investigation/prep-trace/{findings.json,final/}`.

### Exact57 change

Target: `Microsoft.Xna.Framework.Color Terraria.Lighting::GetColor(int,int)`,
token06000d45. The original native code copied the returned12-byte Vector3 three
times to extract its RGB components.57 stores that already-returned value once
in a local and loads X/Y/Z through the local's address.

-65→67 IL instructions;5→6 locals; exactly one original body/local signature changes.
- All four original calls remain ordered: menu White, active engine GetColor,
  GlobalBrightness getter and Color.PackedValue setter. No engine call is skipped
  or devirtualized. Brightness timing, conversions, clamps and packed bits remain.
- No mutable cache, inlining, new helper definition, field/signature/access change,
  constructor/array/lifetime change, visibility/RNG/simulation/input/audio change.
- The original TileDrawInfo constructor and colorSlices behavior stay exact.

This is a **low-risk common-path codegen comparison**, not a demonstrated
multi-millisecond tile win or a30FPS solution. Actual whole-frame benefit is
unknown; a controlled Switch comparison is required before any adoption.

### Verification and performance limits

The source-bound final runner accepts only exact52, reproduces the intended image,
proves the supplied pair against current-source emission and publishes its readonly
pair only after all checks.25 source inputs,20 CLI/dependency/source/artifact/alias
guards and20 deterministic accepted/repeat artifacts are recorded.

- Source-qualified metadata, raw signatures, original definition order, native
  resources and the exact serialized inverse pass. Other21,043 methods,2,957
  types and32,059 fields remain unchanged. Six deliberate static corruptions are
  rejected by qualified/raw/resource checks.
- Actual serialized65/67-instruction bodies execute17,350 cases ×2 boundary lanes
  ×3 repetitions:104,100 paired comparisons per proof. The primary lane uses
  pinned FNA and the actual brightness getter; an explicit equal-boundary stress
  lane tests engine/brightness side effects, call order and exceptions. Raw float
  and packed-color results agree, including edge/nonfinite inputs.
- Five effective semantic controls fail: wrong channel, clamp, coordinates,
  duplicate engine call and reordered brightness.450,000 measured invocations
  allocate zero current-thread bytes in the bounded host check.
- Normal unchanged AOT compiles52,795/52,819 methods, with the same24 fallbacks.
  Seven early module metadata sets validate; six non-game AOT objects are reused.
- ARM64 GetColor code is488→428 bytes (122→107 instructions), stack144→96 bytes,
  loads34→25, stores23→17; native calls remain6. Other watched tile methods retain
  their native sizes. These are static work/codegen facts—not runtime percentages.
- The earlier private paired CoreCLR host benchmark was **inconclusive**:
  candidate elapsed ratios were approximately1.026–1.054 in non-menu workloads,
  while unchanged-replica process medians ranged0.877–1.162. No host speedup,
  ARM64 speedup or frame-rate claim follows from those noisy results.
- Native control relinking reproduces52's allocated sections. Both control and
  candidate have192,309 verified native targets and15,010 fallback sentinels;
  tables are read-only/nonexecutable and no RWX segment is introduced.
- All16,183 embedded files verify. Only Terraria.exe differs;15,997 Content files,
  FNA/ReLogic/runtime dependencies, icon and non-title NACP bytes are preserved.
  Published52/54/55/56 NROs were hashed before/after and remain byte-identical.

No transitive whole-game/mod/multiplayer, actual GPU/pixel or physical input/audio
equivalence is inferred from the bounded host proof. In particular, an estimated
12% smaller native function is not a12% faster frame.

### Published candidate and reproducible evidence

File: `fna-nx-test/terraria-mono/switch/mono_nx_fna_terraria_nochroma57_light_value.nro`
- Title: `Terraria 57 Light Value`
- Size:950,596,212 bytes
- NRO SHA256: `57ecfefc5462d43d2e2fc20f0987f5efae981eeb5ed7bddc5aa7a96ae0150132`
- Game SHA256: `4c02e40e6b0502d311764d1c4cb0eaebe0fa17d194c61976250378a90be7e55f`
- Game MVID: `b6af8788-d1e2-ad83-4d3d-2896548ad830`
- Source SHA256: `2b93495037021f08f6eb69eb1eefb33dcd02cd70f8548f9323680b948795608c`

Workspace tool: `fna-nx-test/scripts/patch_light_value/`, including the migrated
actual-body proof under `proof/`. Root `run.py --output <fresh-dir>` uses the
same pinned monobuild/SDK mounts as56; no observer/parser gate is fabricated for
this instrument-free change. `proof/run.py` retains a separate optional timing path.

Cache `~/.cache/terraria-switch-build/lightvalue57/` contains:
- `accepted-final/runner-results.json`, accepted/repeat/supplied proofs and guards.
- `aot-final/{build-manifest,runtime-metadata-manifest}.json`.
- `native/native-build.json`, `artifact-verification.json`, `published-build.json`.
- `preserved-artifacts-before.json`, `build_aot.py`, `build_native.py`,
  `verify_artifact.py`, `native-mounts.json`.
The original private `tile57-investigation/color-local/` remains separate evidence.

### Controlled52/57 hardware comparison

Copy only57's NRO; **do not replace52 or any SD runtime DLL**. Do not compare57
against the53/55/56 profiling builds. Keep the same application mode, docked/
handheld state, resolution/zoom, Frame Skip On, Auto Pause Off,
`logging=true` and `runtime_logging=false`.

1. Run the original52 NRO. Load the test world, let startup settle and stand in
   the sheltered test position for about90–120s, inventory/map closed and cursor
   away from items. Exit normally; save the complete log as **log57A.txt**.
2. Run57 from the same saved scene/position/settings and repeat the stationary
   block. Exit normally; save the complete log as **log57B.txt**.
3. Preferably repeat52 as **log57A2.txt** to expose order/time drift.

Use comparable daytime conditions and avoid the night-driven item/mob growth
seen in56, deaths, inventory changes or movement during these blocks. Restoring
the same test save between runs is preferable if practical; otherwise report
the different starting conditions rather than calling them matched. Note any
lighting/color/visual/input differences. Archive the previous SD log before each
launch to prevent concatenated captures, and identify the actual NRO used.

These unprofiled builds retain native NX_PHASE counters, not embedded scene
coordinates or a cryptographic build identifier. Filename association and scene
matching still need the operator report. A small/noisy result is not sufficient
for adoption. **52 remains the normal-play baseline;57 is comparison only.**

## L4T matched-clock comparison and runtime-quality findings (2026-09-24)

**Goal clarified by the user: a sustained 30 FPS in handheld mode at stock
Horizon clocks, Frame Skip On (60 Updates/s). No overclocking.** CPU-clock
changes such as sys-clk are therefore out of scope and are not a lever.

### Horizon has no JIT; L4T does

Horizon enforces W^X, and mono-nx's full-JIT profiles aborted
(`mini-arm64.c:1335`, see above). The only native path on Horizon is **static AOT
plus interpreter fallback**. Linux on L4T allows JIT, so stock Mono there compiles
every executed method to native code. Any LLVM work on Horizon must therefore
target the **AOT compiler** (`mono-aot-cross` with an LLVM backend), not a JIT.

### User-measured L4T results

Same Switch, L4T Linux, desktop Mono, OpenGL GLCompatibility. Settings are **all
low plus Trippy lighting, the same settings used for every Horizon capture**. The
FPS values are the user's on-screen readings, not NX_PHASE captures.

| L4T configuration | Frame Skip Off | Frame Skip On |
| --- | ---: | ---: |
| Handheld default CPU clock **1581 MHz**, 3 cores (`taskset -c 0-2`) | 40–45 FPS | 60 Updates/s (Draw rate not recorded) |
| `scaling_max_freq=1020000` (Horizon's CPU clock), 3 cores | 30–33 FPS | **24–26 FPS**, 60 Updates/s |

- Pinning to 3 cores made no visible difference, so the game is limited by a
  single main thread. More parallelism is not the main lever.
- 1581→1020 MHz is a 0.645 clock ratio; Frame Skip Off FPS fell by about
  0.67–0.83. That is better than linear scaling, as expected when part of the
  frame (memory latency, GPU, driver waits) does not scale with CPU clock.
- **L4T at 1581 MHz is not a stock-clock comparison.** Under Horizon, games run
  handheld at 1020 MHz, so the earlier ~50 FPS L4T figure mostly reflected clock.
- `scaling_max_freq` is a live sysfs limit: it resets on reboot, or you can
  restore it by writing back the value read beforehand.

### What the matched comparison shows

Horizon build52 (unprofiled control, Frame Skip On): **19.374 Draw/s**, i.e.
33.642 ms per Draw, 5.538 ms per Update, about 3.10 Updates per Draw, 0.798 ms other.
Against L4T's 24–26 FPS at the same clock, cores and settings, the software gap
is about **1.24–1.34×**, not 2×. **Even L4T's software stack does not reach
30 FPS at 1020 MHz with Frame Skip On.** Matching L4T is necessary but not
sufficient; about another 1.15–1.25× must come from compiler or game-side work.

**[INFERENCE, two-equation model]** On L4T, treat Frame Skip Off as 1 Draw + 1
Update per frame, and Frame Skip On as 1 Draw + 60/FPS Updates. Solving both over
the reported ranges gives **Draw ≈ 23–29 ms (central ≈ 26 ms)** and
**Update ≈ 4–7.6 ms (central ≈ 5.9 ms)**. Horizon measures Draw 33.6 ms and Update
5.5 ms. That suggests most of the gap is in **Draw**, while Update costs are similar.
The ranges are wide, and the model assumes Frame Skip Off runs exactly one Update
per Draw. Confirm with a profile before relying on it.

30 FPS at 60 Updates/s on Horizon requires Draw ≤ **21.46 ms** if Update and other
costs stay unchanged. See [the earlier budget](#solid-tile-priority-and-realistic30fps-budget).

### Trippy lighting explains per-frame tile drawing

Pinned IL: `Lighting.get_UpdateEveryFrame()` returns
`!Main.RenderTargetsRequired && !Lighting.NotRetro`, and `Main.DoDraw` copies it
into `Main.drawToScreen`. Retro and Trippy therefore draw tiles directly to the
screen every frame (`DoDraw_Tiles_Solid` → `DrawTiles`). Color and White instead
use `RenderToTargets`, which redraws the wall/tile targets only when
`renderCount==3` or a target is partially offscreen, and otherwise draws the
cached `tileTarget` texture. This matches build53's stationary cohort:
**2,078 completed solid passes in 2,078 eligible frames** (15.22 ms per pass).
Color lighting is **[INFERENCE]** a way to avoid redrawing tiles every frame, at
the cost of the heavier threaded lighting engine and a visual change. Treat it as
a quality/behavior tradeoff for the user's gate, not as a free win.

### AOT coverage: what runs interpreted

Only seven modules are AOT-compiled: CoreLib, Terraria, FNA, ReLogic,
Newtonsoft.Json, NxCrypto, NxInputDiag. `log38.txt` (runtime logging on) reports
`AOT: image '…' not found` for every other framework assembly, including
System.Linq, System.Collections, System.Collections.Concurrent,
System.ObjectModel, System.ComponentModel.*, System.Runtime.Numerics,
System.Private.Xml and others. Those run **through the interpreter**: Mono loads
their IL from the embedded DLL and interprets any method missing from the AOT
images. There are 241 `interp_in` native→interpreter wrappers and 13,884
`AOT: FOUND method` lines, **none** for System.Linq or System.Collections'
LinkedList/Stack.

IL call scan of Terraria.exe (build42 image, following net40 facade forwards):

| Assembly | Distinct calling methods | Per-frame gameplay users |
| --- | ---: | --- |
| System.Linq | 479 | `Main.DrawPlayers_BehindNPCs`/`_AfterProjectiles` (`Where`), `ChatManager.DrawColorCodedStringWithShadow` (`ToList`), `UILinkPointNavigator.DrawLinks` (`Any`), `StormLightningParticle` (`First`/`Last`); the rest are menus, worldgen, loading, loot and trackers |
| System.Collections | 56 | `LinkedList` in `SkyManager`, `OverlayManager` and `FilterManager` Update/Draw; `Stack` only in `WorldGen.StartRoomCheck` |
| System.ObjectModel | 0 | none |

List, Dictionary, HashSet and Queue live in CoreLib under .NET 9, so they are
already native. No measured hotspot (the solid tile loop, world-item Update, UI)
depends on these assemblies. **[INFERENCE]** AOT-compiling them is low risk, but
the expected saving is under about 1 ms per frame. The SkyManager LinkedList loops are
a possible contributor to the unexplained 3.40 ms "Sun, Moon & Stars" row from
build43.

### Shipped framework is a Debug build

The framework shipped in the NRO and used for AOT comes from the SDK's **Debug**
artifacts: CoreLib is `libnx.arm64.Debug/System.Private.CoreLib.dll`
(`ddcbdd24…`, the same hash as the AOT input), and `System.Linq.dll` is byte-identical
(`0b8bf2e3…`) to `net9.0-libnx-Debug-arm64/System.Linq.dll`.

| Assembly | DebuggableAttribute | `Debug.Assert`/`Fail` call sites |
| --- | --- | ---: |
| System.Private.CoreLib | none | **3,706** |
| System.Linq | `0x107` (DisableOptimizations) | 204 |
| System.Collections | `0x107` | 113 |
| Terraria / FNA / ReLogic / Newtonsoft.Json | `0x2` | 0 |

- `[Conditional("DEBUG")]` asserts are removed from Release IL. Here they remain
  inside CoreLib collection, span, string and array code that Terraria calls
  constantly, including from AOT-compiled game code.
- Mono disables inlining for any assembly whose DebuggableAttribute sets
  DisableOptimizations (`method-to-ir.c:6440` → `is_jit_optimizer_disabled`).
  That affects System.Linq and System.Collections.
- The **native** runtime is not a Debug build in practice. `CMakeCache.txt` says
  `CMAKE_BUILD_TYPE=Debug`, but `interp.c`, `mini-runtime.c` and `sgen-gc.c`
  compile with `-O2 -g`, and every `ENABLE_CHECKED_BUILD*` is OFF (matching the
  earlier DWARF finding).
- **[INFERENCE]** A Release CoreLib/framework is a plausible share of the
  1.24–1.34× gap and the cheapest remaining runtime fix. Its size is unmeasured.
  L4T runs a different Mono (**[INFERENCE]** a distro Mono 6.x class library,
  Release) with JIT, so this is one of several differences, not a proven cause.

### Revised optimization order toward 30 FPS handheld

1. **Release CoreLib/framework.** Rebuild the libnx runtime pack with
   `-c Release`, re-AOT CoreLib and relink. No game IL changes. Risk: the
   runtime-start fixes from builds 36–38 (allocation provenance, EventSource
   capability, trampoline bank) must carry over; verify startup first.
   **Built as [build58](#build58-release-corelibframework-ab-2026-09-24); hardware A/B pending.**
   The runtime fixes carry over: native runtime archive, EventSource capability
   and 65,536-entry trampoline bank are unchanged.
2. **AOT-compile System.Linq/System.Collections** (Release) as two extra
   modules. Small expected gain; it also removes the interpreter entries the
   per-frame paths above hit.
3. **Conditional: profile L4T with `perf`** only if Horizon is still clearly below
   L4T's 24–26 FPS after steps 1–2. This is a decision step, not a speedup.
   `MONO_ENV_OPTIONS=--jitmap`, 1020 MHz, 3 cores, same scene. Compare where the
   time goes, not exact per-method ms: L4T is a different Mono with JIT, and
   Horizon's profilers are instrumentation-based. If C# methods such as
   `DrawSingleTile` (about 5.5 µs per call, about 5,600 cycles on Horizon) are
   much cheaper on L4T, the gap is code generation and LLVM is justified. If they
   cost about the same and L4T spends less time in GL/driver code, the gap is
   switch-mesa/nouveau, where LLVM would not help. If the difference is in
   GC/allocation/runtime code, look at the runtime instead. About an hour of
   profiling guards a multi-week LLVM investment.
4. **Codegen:** check the remaining Terraria inlining restriction (build42), then
   **LLVM AOT** (an LLVM-enabled `mono-aot-cross` for the aarch64 libnx target;
   large effort). Default-compiler `abcrem`/`ssa` crash (see build53 notes).
5. **Game-side work reduction** to cover the remaining gap past L4T parity:
   repeated tile preparation/lighting, the 3.40 ms sky row, UI, and nighttime
   world-item merging. Anything visual/behavioral goes through the discussion gate.

### perf decision table (step 3) and driver-side options

Run the conditional L4T profile only if Horizon is still clearly below L4T's
24–26 FPS after steps 1–2. Read the result as where time is spent, not exact per-method ms:

| Where L4T spends less time than Horizon | Meaning | Response |
| --- | --- | --- |
| Managed methods (`DrawSingleTile`, GetTileDrawData, lighting helpers) are much cheaper per call | Horizon's AOT code generation is the gap | Inlining review, then LLVM AOT (step 4) |
| Managed methods cost about the same; L4T spends less in GL/driver (`libGL`, mesa/nouveau, FNA3D) | Driver path is the gap | Driver-side options below; LLVM would not help |
| Difference is in GC, allocation, casts or other runtime helpers | Mono runtime difference | Runtime/GC settings or runtime changes |

A driver-bound result still leaves game- and port-side work. Driver cost is
mostly **per call** (draws, state changes, binds, uploads). We control Terraria
(IL patches), FNA and FNA3D (built from source); switch-mesa is open source but a
deep project. Options, cheapest measurement first:

1. **Count per-frame GL work** first, following RAL's `RAL_GL_DIAGNOSTICS` idea
   (draw, clear, render-target and effect counts, upload bytes, map vs subdata
   counts). This tells us which of the following matter.
2. **FNA3D buffer uploads:** the RAL fork's `glMapBufferRange` +
   `GL_MAP_UNSYNCHRONIZED_BIT` path (see [RAL techniques](#applicable-techniques-and-limits)).
3. **FNA3D redundant-state filtering:** skip binds/state already set.
4. **Fewer draw calls:** larger SpriteBatch batches or texture-grouped tile
   submission. Draw order changes, so a visual check is needed.
5. **Draw less often:** cache the tile layer instead of redrawing it each frame
   (the Color-lighting RenderToTargets path, or a Trippy-look-preserving cache
   patch). This removes both the managed and the driver cost of those draws.

Measured GL-facing costs so far are small (TileBatch.End 1.117 ms inclusive,
uploads 0.149 ms, indexed submissions 0.410 ms, swap about 0.36 ms). These do not
cover every GL call, so they point toward, but do not prove, a managed-code
rather than driver bottleneck.

### Tools and evidence

- `fna-nx-test/scripts/framework_audit/refscan.py <Terraria.exe> [framework_dir] [out.json]`:
  IL call scan with facade forwarding; results in `refscan.json` next to it.
- `fna-nx-test/scripts/framework_audit/dbgcheck.py <dll>...`: DebuggableAttribute
  modes and surviving assert call sites.
- Both need `dnfile` (`uv run --with dnfile python3 ...`).
- `fna-nx-test/scripts/framework_audit/corelib_parity.py <debug CoreLib> <release CoreLib>`:
  internal-call signature and field-layout parity against what the native runtime expects.
- Runtime flags: `~/.cache/terraria-switch-build/runtime-source/artifacts/obj/mono/libnx.arm64.Debug/{CMakeCache.txt,compile_commands.json}`.
- Per-frame pass count: `~/.cache/terraria-switch-build/render53/hardware-analysis.json`,
  stationary intervals 47–67.

## Build58 Release CoreLib/framework A/B (2026-09-24)

**Step 1 of the revised order. Built and fully verified on host; hardware A/B
pending. 52 remains the working baseline.** One change from exact52: the managed
BCL is **Release** instead of Debug. Game bytes (Terraria.exe `90b13512…`, patched
ReLogic/FNA), `mono-aot-cross` (`c7c41a54…`), AOT options (CoreLib
`full,interp,static,ntrampolines=65536`; others `full,interp,static`) and the native
runtime archive (`runtime-fix/libmonosgen-2.0.a`, `-O2`) are build52's.

Published (**58d**, after three hardware startup failures below):
`fna-nx-test/terraria-mono/switch/mono_nx_fna_terraria_nochroma58d_release_bcl.nro`,
title `Terraria 58 Release BCL`, **921,540,888 bytes**, SHA256
`ae425f0a607e2d6a1555b6dae88ebe7e369c52f548c19b1261a6b2a946442eed`.
58d = 58c's AOT objects (CoreLib `--optimize=-inline`) relinked with the corrected
search path `romfs:/mono/lib_net9.0;romfs:/` and `MONO_NX_FATAL_DIAG`. The crashed
attempts are preserved under `~/.cache/terraria-switch-build/release58/`
`try1-crashed/` (58a, `55b0aad2…`), `try2-crashed/` (58b, `c2f3ae28…`) and
`try3-crashed/` (58c, `3be3c19b…`); each has its hardware log/reports.

### Why the BCL is embedded, and the launcher flag

The SD card's `/mono/lib_net9.0` and `/mono/framework_net9.0` hold the Debug BCL.
Every AOT image hard-binds its dependencies' MVIDs (`aot-runtime.c`), so replacing
those SD files would break 52 and all older NROs. Build58 instead ships its
BCL in RomFS and loads it through a new launcher flag:

- `MONO_NX_EMBEDDED_BCL=1` (Makefile; requires `MONO_NX_USE_ROMFS=1`). In `main.c`,
  after `application_initialize()` has read `/mono/config.ini` and ICU from SD,
  it calls `romfsInit()` **without chdir** and then
  `mono_set_assemblies_path("romfs:/mono/lib_net9.0;romfs:/")` (58d; both
  device-absolute, see the 58c section)
  before `mono_jit_init`. The later RomFS mount is skipped under the flag;
  `chdir("romfs:/")` and the return to `sdmc:/` are unchanged.
- Source basis: `mono_set_dirs` only sets the assembly path; `;` is the libnx
  search separator (`src/mono/CMakeLists.txt:783`); libnx eglib treats `device:/`
  paths as absolute (`gmisc-unix.c:105`), so `mono_path_canonicalize` keeps them;
  `mono_assembly_load_corlib` probes `assemblies_path` in order (`assembly.c:2692`).
  Logged as `NX_RUNTIME embedded BCL: ...`.
- Flag off, the workshop `main.c` compiles to a main.o **byte-identical** to build42's
  (all sections plus disassembly with relocations). The compile recipe was recovered
  from main.o's DW_AT_producer and main.d
  (`scripts/release_bcl/compile_main.sh`).

### Release build and one fork fix

`build.sh --subset mono.corelib+libs.sfx --cross --arch arm64 --os libnx -c Release`
(CoreLib needs `/p:RuntimeConfiguration=Release`; native runtime not rebuilt).
The libnx fork had never been built as Release:
`SocketAsyncEngine.Libnx.cs` called `Console.WriteLine` unconditionally, but
System.Net.Sockets.csproj references System.Console only in Debug. The fix wraps
the logging field/call in `#if DEBUG`
(`native/patches/runtime-release-sockets-console.patch`). Release is behaviorally
identical: the `Logging` flag was never set.

| Check | Result |
| --- | --- |
| Release CoreLib | 4,787,712 bytes (Debug 5,572,096); no DebuggableAttribute; 7 residual `Debug.Assert`/`Fail` sites, all inside `System.Diagnostics.Debug`'s own overloads (Debug had 3,706 call sites) |
| Release framework | 168/168 same file names; System.Linq, System.Collections and TypeConverter now `0x2` (were `0x107` DisableOptimizations) |
| ABI parity vs the native runtime (`corelib_parity.py`) | **PARITY OK**: 369/369 internal-call name+signature pairs identical. Field layouts identical except 83 compiler-generated async/closure types and 7 types whose only difference is `#if DEBUG` diagnostic fields (LowLevelLock/LowLevelMonitor `_ownerThread`, QueueUserWorkItemCallbackBase `_executed`, three packed SearchValues variants, MemoryFailPoint state); none is referenced from `src/mono/mono` |

### AOT results

Fixed in `compile_terraria_aot.py`: ReLogic/Newtonsoft now compile from the
**staged** RomFS copy when one exists (what Mono loads), not the original embedded
resource. Before the fix the generic helper bound ReLogic to the unpatched MVID;
validation caught it.

| Module | 52 (Debug BCL) | 58 (Release BCL) | Fallbacks |
| --- | ---: | ---: | --- |
| CoreLib | 75,230/75,888 | 80,532/81,153 | 658 → 621 |
| Terraria | 52,795/52,819 | 45,315/45,339 | 24 → 24 |
| FNA | 20,275/20,324 | 14,381/14,430 | 49 → 49 |
| ReLogic | 20,201/20,205 | 12,747/12,751 | 4 → 4 |
| Newtonsoft.Json | 16,388/16,399 | 10,375/10,386 | 11 → 11 |
| NxCrypto | 7,409/7,409 | 3,605/3,605 | 0 |

The lower game-module totals are **CoreLib generic instantiations** (Span, Vector,
Enumerator, ...) that no longer need a per-module copy. Terraria.exe.o symbol diff:
**no `Terraria_*` method was lost**. The removed symbols are `System_*` generic
instances, `ut_*` unbox trampolines and PLT entries. All 85 name/MVID bindings
validate.

### Native link and payload verification

- The build52 control replay reproduces every allocated ELF section. The candidate
  swaps only main.o and the seven AOT objects.
- Candidate: **166,966 native method targets and 14,214 fallback sentinels verified**
  (52: 192,309 / 15,010); no RWX segment; read-only method tables.
- RomFS (58b): 16,184 files. Versus 52: **166 root framework DLLs swapped
  Debug→Release** (each equals the Release output), **1 added**
  (`mono/lib_net9.0/System.Private.CoreLib.dll`); **16,017
  unchanged** (Terraria.exe, ReLogic, FNA, all Content, the patched mscorlib facade,
  System.IO.Packaging, ...). Icon unchanged; NACP differs only in title slots.
- Kept as-is on purpose: `mscorlib.dll` (the project's patched facade, type-forwards
  only, no code), and the already-Release System.IO.Packaging/Security.Permissions.

### Risks and what the hardware run must show

- **Startup is the main risk.** This is the first NRO that loads CoreLib from RomFS.
  Acceptance: the log shows `NX_RUNTIME embedded CoreLib: romfs:/mono/lib_net9.0...`
  then `NX_AOT Terraria entrypoint resolved to native code`, then the normal title
  and world.
- **[INFERENCE]** No new speedup is claimed. Removed asserts and optimized IL in
  CoreLib/framework are a plausible share of the 1.24–1.34× gap to L4T. Measure
  against 52 with the README procedure (log58A/log58B, handheld, all low + Trippy,
  Frame Skip On).

Reproduce (container `localhost/monobuild:local`, mounts as in the scripts):
`scripts/release_bcl/build_release_managed.sh`, then the CoreLib Release build,
`build_aot.py`, `build_native.py`, then host `verify_artifact.py`. Evidence:
`~/.cache/terraria-switch-build/release58/{aot-final,native}/`,
`artifact-verification.json`, `{control,candidate}-{native,payload}-verification.json`.

### First hardware run (58a): startup abort and fix

User upload: `fna-nx-test/log58.txt` (3,072 bytes, SHA256 `c2bf7e0e…`) plus Atmosphère
report `crash_reports/01790336755_05446530aca7e000.log` (`6d7a0fa3…`).

- **What worked:** config, ICU and the embedded path were in effect
  (`NX_RUNTIME embedded BCL: ...`); CoreLib loaded from RomFS and the runtime
  initialized; Terraria.exe began loading its AOT image table.
- **Crash signature:** `User Break` (2001-0106) inside hbl, main-thread return address
  `__libnx_exit+28`. Symbolized against the candidate ELF (load base `0xae0e17000`,
  solved from `_EntryWrap+168` on the thread-entry frames): the other two threads
  were idle in `condvarWaitTimeout` (Mono's thread_func and `finalizer_thread`
  `sem_wait`). This is a deliberate **abort()**, not a memory fault.
- **Why the log is cut:** the file log is a plain `fopen("a")` stream with default
  full buffering. `abort()` does not flush stdio, so the last buffered block (with
  Mono's fatal message) was lost; the file ends mid-line at exactly 3×1024 bytes.
  With `runtime_logging=false` eglib's default handler prints a fatal message to
  stdout and aborts; `on_mono_log`/`fatal_error` (error applet) is installed only
  when `runtime_logging=true`.
- **Cause (found statically from the payload):** 58a searched
  `romfs:/mono/framework_net9.0` **before** the RomFS root. Two root files are
  intentional overrides with the same name as framework DLLs: the project's
  **patched mscorlib facade** and the **GOG legacy System.Drawing.dll**. The
  framework directory shadowed both. Terraria.exe.o/ReLogic.dll.o had bound
  System.Drawing and Newtonsoft.Json.dll.o had bound mscorlib to the **root** copies'
  MVIDs (runtime-metadata manifest), so the load resolved an assembly with the wrong
  identity → Mono fatal error → abort. **[INFERENCE]** The exact fatal message was
  lost; the shadowing and MVID mismatch are verified from the payload. The 58a log
  shows the diversion: every framework probe goes to
  `romfs:/mono/framework_net9.0/...`, while 52 probes `/...`.
- **Fix (58b):** only CoreLib is embedded under `mono/lib_net9.0`; the framework
  stays at the RomFS root, and the path is `romfs:/mono/lib_net9.0;/`, so every
  non-CoreLib resolution is identical to 52 (root after `chdir("romfs:/")`).
  `verify_artifact.py` now rejects any other file under `mono/` as a possible shadow.
  Rebuilt from scratch: Terraria.exe.o differs from 58a **only in the 16-byte AOTID**;
  52 control replay exact; 166,966 native targets/14,214 fallbacks, no RWX.
- **Diagnostic lesson:** for a startup failure with a truncated log, rerun once with
  `runtime_logging = true` in `/mono/config.ini`. Fatal Mono errors then reach
  `fatal_error`, which prints and shows the error applet, instead of being lost in
  the stdio buffer. Revert afterwards; verbose tracing distorts timing.

### Second hardware run (58b): AOT inlining assertion and 58c

User upload: `fna-nx-test/log58b.txt` (1,160 lines, `runtime_logging=1`, SHA256
`aa858f60…`), with the error applet shown. The layout fix worked: the log shows
`NX_RUNTIME embedded CoreLib: romfs:/mono/lib_net9.0...`, CoreLib from RomFS, and
Terraria.exe's image table loading from the RomFS root as in 52. It then fails
in Terraria's `LinuxLaunch` AssemblyResolve handler → `AssemblyNameParser` →
`CultureInfo.GetCultureByName`:

```
(null) warning mono_class_from_mono_type_internal: implement me 0x00
(null) error * Assertion: should not be reached at .../mono/metadata/class.c:2324
Fatal error in Mono
```

The runtime was handed a MonoType whose element type is 0 (`class.c` default case).

Evidence, all executed on host with the same `mono-aot-cross` and
`MONO_VERBOSE_METHOD`:

- **IL is not the difference.** A Cecil dump of `CultureInfo.GetCultureByName` and
  `CultureData.GetCultureData(string,bool)` shows the same calls, locals and
  `Dictionary<string,CultureData>` uses in Debug and Release; Debug only adds nops.
- **Inlining is.** Debug CoreLib's unoptimized IL is too large for Mono's inliner:
  `GetCultureData` has **0 inlines**. Release inlines **13 callees** there, including
  `Dictionary<string,CultureData>` `.ctor`/`set_Item` (shared generics) and
  `GlobalizationMode`/`SR` getters. `GetCultureByName` gains an inlined
  `get_InvariantCulture` with a new `generic_class_init` inside its catch handler.
  So Release CoreLib exercises far more of the AOT inliner/rgctx paths than any
  CoreLib that has run on hardware since build38.
- **[INFERENCE]** The fatal type-0 decode comes from one of those newly inlined
  shared-generic/class-init paths. This matches the known weakness of this compiler's
  inliner (build42's `SetDisplayMode` compile crash). The exact native frame was
  not captured: `fatal_error` exits through the error applet, so no crash report.

**58c response, two changes:**

1. CoreLib is compiled with `--optimize=-inline`. `GetCultureData` again has 0
   inlines (verified in IR). CoreLib compiles 74,586/75,207 methods. Release IL
   and removed asserts are kept; CoreLib-internal inlining goes back to what
   actually ran with Debug. Game modules keep default inlining as in 52.
2. `MONO_NX_FATAL_DIAG=1` (new Makefile/main.c flag). Installed after
   `application_configure_mono`, it replaces the SDK log hook: messages are logged
   as before when `runtime_logging=true`; **any fatal Mono message is always
   written, the log is flushed/closed, then `diagAbortWithResult` (2347-0093)**
   makes Atmosphère write a crash report with the native stack. With
   `runtime_logging=false` only error-level messages reach it, so normal timing is
   unaffected. Flags off, main.o is still byte-identical to build42's.

58c verification: 52 control replay exact; **161,020 native targets / 14,310
fallbacks**, no RWX; payload vs 52 = 166 root framework swaps + 1 embedded CoreLib.
Game-module objects are the same size as 58b's. The only byte differences are the
AOTID and the unwritten alignment filler after `ret` (documented at build48), so
game code generation is unchanged.

**Remaining risk.** Game modules still inline Release-CoreLib generics across
assemblies (e.g. Terraria's own `Dictionary` calls). If 58c fails in game code,
the crash report plus log now give the exact frame. The next step would then be
`-inline` for the game modules too, at the cost of build42's game-code inlining.

### Third hardware run (58c): the real root cause, and 58d

User upload: `fna-nx-test/log58c.txt` (`runtime_logging=1`, SHA256 `d09dde25…`) plus
Atmosphère reports. **Progress:** `NX_AOT Terraria entrypoint resolved to native code`
(line 863); GetCultureByName no longer crashes. Then:

```
(null) warning Process terminated.
(null) warning Encountered infinite recursion while looking up resource
  'Arg_NullReferenceException' in System.Private.CoreLib.
```

Chain, read directly from the log:

1. Right after the entrypoint, the launcher returns the cwd to `sdmc:/`. The next
   framework dependency load (`System.Linq` → `System.Runtime`) probes nothing:
   no `Assembly Loader probing location` line appears. The search path was
   `romfs:/mono/lib_net9.0;/`, and a bare `/` now means the **SD card root**, which
   has no DLLs.
2. Mono falls back to the managed resolving events
   (`AssemblyLoadContext:MonoResolveUsingResolvingEvent`) → Terraria's
   `LinuxLaunch` `AssemblyResolve` handler → `AssemblyNameParser`. This happens while
   `CultureInfo..cctor` is still running (`Running class .cctor for
   System.Globalization.CultureInfo` at line 960, reached through
   `GlobalizationMode.Settings` → `Environment.GetEnvironmentVariable`).
3. The handler reaches `NumberFormatInfo.get_InvariantInfo` →
   `CultureInfo.get_InvariantCulture`, which reads `s_InvariantCultureInfo` while that
   cctor is still mid-flight on the same thread (CLI re-entrancy returns early) →
   **null** → NullReferenceException. Formatting that exception needs
   `SR`/`ResourceManager`, which re-enters the same half-initialized globalization
   state → the recursive-resource FailFast.

**Build 52 never enters that handler.** Verbose `log38.txt` shows the same load
resolving by filesystem probe (`probing location: '/mono/framework_net9.0/System.Runtime.dll'`);
0 resolving-event entries. 52's `assembly_dir` comes from the SD config
(`/mono/lib_net9.0;/mono/framework_net9.0;/`), which works with cwd on `sdmc:/`.

**Reinterpreting 58b:** its log also has `MonoResolveUsingResolvingEvent` right after
`ApplicationBase is` (line 939) and fails inside the same `LinuxLaunch` handler, just
earlier. The class.c type-0 assertion was therefore also reached through this path
bug. **[INFERENCE]** The Release inlining difference changed *where* the re-entrant
path broke, not *whether* it broke. CoreLib `-inline` is kept in 58d so this run tests
one variable (the path). Re-enabling CoreLib inlining is a separate, later A/B.

**58d fix:** `mono_set_assemblies_path("romfs:/mono/lib_net9.0;romfs:/")`. Both
entries are device-absolute, so every framework load before and after the cwd
change resolves from RomFS (Release framework plus the patched mscorlib/legacy
System.Drawing overrides at the root). Source check: `mono_path_canonicalize("romfs:/")`
strips the trailing separator and re-appends it (no other `/` in the string), and
`load_in_path` joins with `g_build_filename` → `romfs:/X.dll`. 58d relinks 58c's
verified AOT objects: 52 replay exact; 161,020 native targets / 14,310 fallbacks,
no RWX; payload unchanged from 58c (166 swaps + 1 embedded CoreLib).

**Diagnostic-hook limit found:** `MONO_NX_FATAL_DIAG` did not fire (0 `NX_FATAL_DIAG`
lines). This failure was a managed `Environment.FailFast`, which terminates through
Mono's own path, not through a fatal *log* message. The Atmosphère reports again show
the generic `User Break` in hbl. For managed FailFast the verbose log (as here) is
the useful evidence.

### 58e: CoreLib inlining re-enabled (prepared, test only after 58d boots)

Single-variable companion to 58d. It tests the [INFERENCE] above that 58b's
class.c assertion came from the path bug, not from CoreLib inlining.

- `fna-nx-test/terraria-mono/switch/mono_nx_fna_terraria_nochroma58e_release_bcl_inline.nro`,
  title `Terraria 58e BCL Inline`, 927,312,152 bytes, SHA256
  `99fd2ff36671ae93bea1c27d2d3746b5f83b0c82198bcce6300bc764f9663305`.
- The **only** difference from 58d: CoreLib is compiled with the compiler's default
  inlining (80,532/81,153 methods, as in 58a/58b), instead of `--optimize=-inline`
  (74,586/75,207). Same corrected search path, same `MONO_NX_FATAL_DIAG`, same game
  AOT options. main.o disassembly, relocations and strings are identical to 58d's;
  only DWARF include paths differ.
- Verified: 52 replay exact; **166,966 native targets / 14,214 fallbacks**, no RWX;
  payload = 166 framework swaps + 1 embedded CoreLib.
- Scripts: `R58_VARIANT=v58e R58_CORELIB_INLINE=1 R58_TITLE='Terraria 58e BCL Inline'`
  with `build_aot.py`, `build_native.py` and `verify_artifact.py`. Unset, they build
  58d paths. Outputs are in `~/.cache/terraria-switch-build/release58/v58e/`.
- **Decision rule:** run only if 58d starts. If 58e also starts, measure 52 vs 58d
  vs 58e and keep whichever of 58d/58e is faster. If 58e fails and 58d works, CoreLib
  inlining is a real problem on this compiler, and that feeds the inlining/LLVM review.

### Hardware A/B: 52 vs 58d vs 58e (2026-09-25)

**All three start, reach the world and exit normally.** 58d and 58e log
`NX_RUNTIME embedded BCL: romfs:/mono/lib_net9.0;romfs:/` and the native Terraria
entrypoint. `runtime_logging=0` in all three. Uploads: `log58A.txt` (52, SHA256
`0fa98204…`), `log58B.txt` (58d, `22fbbca3…`), `log58E.txt` (58e, `85a74c5d…`).

**58e booting settles the 58b question:** CoreLib inlining was not the crash
cause. The resolve-handler path bug was.

Selection: whole NX_PHASE windows after the window containing the TIMBER
achievement, up to the first return to ~60 Draw/s (menu/pause) or A's death
(`1 got massacred by Ghost`, excluded with everything after it). The first three
windows in every run have 3.5–5.5 polls/Draw (same kind of activity), so they are
the closest matched block.

| Selection | 52 (A) | 58d (B) | 58e (E) |
| --- | ---: | ---: | ---: |
| Matched first 3 windows (~15 s) Draw/s | 14.32 | **17.33** | **17.51** |
| same, ms/Draw | 43.81 | 38.91 | 38.60 |
| same, ms/Update | 5.87 | 5.20 | 5.17 |
| All pre-menu gameplay windows, Draw/s | 15.21 (40.6 s) | 17.86 (30.2 s) | 21.56 (25.1 s) |

- Matched block: **+21% Draw/s and −11% ms/Draw for both Release builds**; Update is
  ~11% lower too. 58d and 58e are within 1% of each other.
- **[INFERENCE]** Direction and size are consistent with the removed CoreLib
  asserts and optimized framework IL, but this is **not an adoption-grade
  measurement**: about 15 s matched per run, no stationary 90–120 s block, one death
  in A, and scene/position matching unconfirmed. A's gameplay is heavier than 52's
  own earlier stationary capture (43.8 vs 33.6 ms/Draw), so absolute FPS is not
  comparable across sessions. 58e's larger all-gameplay number includes a
  transition window (41 Draw/s) and must not be read as a gain over 58d.
- **Decision:** 58e (full Release: CoreLib with default inlining) becomes the
  candidate to carry forward, since it runs and matches 58d. 52 stays the baseline
  until a proper stationary A/B (52 vs 58e) confirms the gain.

## Build59 System.Linq + System.Collections AOT (2026-09-25)

**Step 2 of the revised order. Original 59 crashed on world selection; use verified
59b below, whose world loading and normal exit are now confirmed.**
59 = exact 58e plus two more AOT modules: the RomFS-root Release
`System.Linq.dll` and `System.Collections.dll`, which previously ran through the
interpreter (see the IL call scan above).

- `fna-nx-test/terraria-mono/switch/mono_nx_fna_terraria_nochroma59_linq_collections_aot.nro`,
  title `Terraria 59 Linq+Collections AOT`, **946,067,736 bytes**, SHA256
  `37eb38ceba1d18165d7f54b59613c41ac15a95880490bdd66680ed02d20ff5a1`.
- AOT (same compiler/options as the game modules, `full,interp,static`, default
  inlining): **System.Linq 24,362/24,382**, **System.Collections 10,112/10,112**.
  CoreLib and the seven 58e modules have unchanged counts. The early-metadata check
  now covers **9 modules**; all name/MVID bindings resolve.
- Registration header: 58e's seven lines plus `mono_aot_module_System_Linq_info` and
  `mono_aot_module_System_Collections_info`. The linker script already places every
  `*.dll.o(.data.rel.ro)` into the read-only method-table section.
- Native verification: 52 replay exact; **201,440 native targets / 14,582 fallback
  sentinels** (58e: 166,966 / 14,214), no RWX. Payload identical to 58e: same RomFS
  bytes, just the two extra native objects in the ELF.
- Tooling changes:
  - `compile_terraria_aot.py --extra-module FILE` (staged RomFS assemblies only).
  - `build_aot.py` `R58_EXTRA_MODULES`.
  - `build_native.py` inserts extra objects into the recorded, name-sorted AOT
    run. The native verifier maps linked method tables to modules by sorted object
    name; the first 59 link appended them out of order and verification failed on the
    module-info check, so that link was discarded (`v59/native-misordered/`). The
    recorded order is sorted and contiguous, so 58d/58e and the 52 replay link exactly
    as before.
- **[INFERENCE]** Expected gain is small (the IL scan found only UI/sky/overlay
  per-frame users; <1 ms/frame estimated). The main risk is new: compiling these two
  assemblies' generics for the first time.
- Reproduce: `R58_VARIANT=v59 R58_CORELIB_INLINE=1 R58_EXTRA_MODULES="System.Linq.dll System.Collections.dll" R58_TITLE='Terraria 59 Linq+Collections AOT'`
  with `build_aot.py` → `build_native.py` → `verify_artifact.py`.

## Build60 LLVM-compiled Terraria (2026-09-25)

**Step 4 (LLVM half), experimental. Original 60 is superseded by 60b below;
60b is host-verified and awaiting hardware.**
60 = exact 58e, except Terraria.exe is AOT-compiled through Mono's LLVM backend
(LLVM 19.1, `opt -O2`). CoreLib, FNA, ReLogic, Newtonsoft and the RomFS payload are 58e's.

- `fna-nx-test/terraria-mono/switch/mono_nx_fna_terraria_nochroma60_llvm_terraria.nro`,
  title `Terraria 60 LLVM Terraria`, **903,219,480 bytes**, SHA256
  `6bb901746486ac75fe0b145f373166af676642fdc2d57d33f25746bcc68ff731`.

### LLVM-enabled cross compiler

- The SDK's `mono-aot-cross` has no LLVM (`ENABLE_LLVM_RUNTIME=OFF`, no LLVM
  symbols). The fork's libnx build supports it: `MonoAOTEnableLLVM` pulls Microsoft's
  `runtime.linux-x64.Microsoft.NETCore.Runtime.Mono.LLVM.{Sdk,Tools}`
  19.1.0-alpha.1.24575.1 (the pinned version is still on the dotnet9 feed).
- Built in a separate worktree `~/.cache/terraria-switch-build/runtime-llvm` (same commit
  289cdaa5d + the same two patches), with the mono-nx writeup's two-step recipe plus
  `/p:MonoAOTEnableLLVM=true` (script: `scripts/release_bcl/build_llvm_cross.sh`).
  LLVM 19's bundled libc++ headers need clang ≥ 19. Image `localhost/monobuild-llvm:local`
  = monobuild + Debian bookworm's clang-19/lld-19; step 2 passes `/p:Compiler=clang-19`.
- Parity: without `--llvm`, the new compiler emits NxCrypto code identical to the SDK
  compiler's (only the AOTID and one alignment-filler word differ). **Any change in 60 is
  from LLVM, not a different Mono compiler.**

### Terraria through LLVM

- `--llvm --aot=full,interp,static,llvm-path=…,llvm-outfile=…,temp-path=…`:
  **45,252/45,276** methods (24 fallbacks, as without LLVM); **44,576** of them are
  LLVM-generated. Compile time ~20–25 min.
- `DrawSingleTile`: **11,296 bytes** of LLVM code vs 22,500 (default compiler).
  `Lighting.GetColor(int,int)`: 332 vs 488 bytes. Code size is not speed; that needs hardware.
- Mono emits two objects: `Terraria.exe.o` (method table + JIT-style parts) and
  `Terraria.exe-llvm.o` (LLVM code **plus** `mono_aot_module_Terraria_info`,
  `mono_aot_file_info` and `image_table`). Every undefined symbol in both resolves
  within the existing Switch `libmonosgen-2.0.a`. **No runtime rebuild; the runtime's AOT
  loader handles `MONO_AOT_FILE_FLAG_WITH_LLVM` unconditionally.**

### Link and tooling changes

- `ld: read-only segment has dynamic relocations`: the LLVM object has **148 absolute
  pointers in `.rodata`** (one `R_AARCH64_ABS64` table). A PIE NRO must relocate them at load.
  When LLVM sidecars are present, `build_native.py` appends a linker-script section
  `.mono_llvm_rodata` in the RW data segment (`*-llvm.o(.data .data.*)` first so ld marks
  it SHF_WRITE, then `*-llvm.o(.rodata .rodata.*)`, `INSERT BEFORE .data.rel.ro`).
  Result: R E / R / RW load segments, no RWX, no TEXTREL. The pattern only matches
  `*-llvm.o`, and the 52 control replay stays exact.
- Method-table reach: LLVM Terraria shrinks `.text` (total ~74.6 MiB). Max CALL26 reach is
  **74.5 MiB** (FNA); Terraria's is 37.2 MiB. The limit is 128 MiB.
- `compile_terraria_aot.py`:
  - `--llvm-module NAME` + `--llvm-compiler-dir`
  - writable `temp-path` (mkdtemp failed on the read-only `/work` cwd)
  - reads the module symbol and `image_table` from the sidecar
  - ELF extended section numbering (the sidecar has >65,279 sections, so `e_shnum=0`)
  - allows `*-llvm.o` in the link directory
- `verify_native_llvm.py` (release58): ab46's verifier plus one rule. Method-table
  entries whose target is undefined in the module object (LLVM methods) resolve through
  the linked ELF's symbol table and must land in `.text`. On build 59 it gives exactly
  ab46's counts; `ab46/verify_pair.py` is untouched.
- Build 60's first full run failed after compiling, in the helper's bookkeeping (stale
  `.pending` symbol paths). The fixed helper finished validation and the header on the
  already-built objects (`completion_note` in the manifest); no object was changed.

### Verification

52 replay exact; **166,903 native targets / 14,214 fallbacks, of which 44,576
Terraria entries land in LLVM code**, no RWX, no TEXTREL; 7 modules' early metadata
(Terraria read from the sidecar; identical name→MVID set to 58e); payload identical
to 58e.

### Risks for the hardware run

- First LLVM code on Horizon. Exception unwinding uses Mono's own LLVM EH frames
  (`-enable-mono-eh-frame`); a managed exception through LLVM code is the most likely
  untested path. Startup/world entry and a normal exit are the first acceptance.
- **[INFERENCE]** LLVM mainly helps tight loops (tile drawing, lighting); expect
  gains in Draw rather than in interpreter-bound or GPU-bound work.
- Reproduce: `R58_VARIANT=v60 R58_CORELIB_INLINE=1 R58_LLVM_MODULES=Terraria R58_TITLE='Terraria 60 LLVM Terraria'`,
  run in `localhost/monobuild-llvm:local`.

## Build59b crash fix and build60b rebuild (2026-09-25)

**59b works on hardware; 60b is ready for its first hardware run.** Both original
59 crash sessions report `Ran out of trampolines of type 2 ... (limit 512)` in
`romfs:/mono/lib_net9.0/System.Private.CoreLib.dll`. `log59 (2).txt` also contains
a later successful session of unconfirmed build identity; do not attribute that
session to 59. The separately uploaded `log59b.txt` is user-identified as 59b.

### Cause and bounded fix

The runtime's `aot-runtime.h` defines type 2 as `MONO_AOT_TRAMP_IMT`.
`mono_aot_get_imt_trampoline` calls `get_numerous_trampoline`, which allocates
from CoreLib's shared bank and aborts when its finite capacity is exhausted.
Build38 increased only the specific bank; the IMT bank was still 512 entries.

59b and 60b rebuild CoreLib with
`R58_CORELIB_AOT_EXTRA='nimt-trampolines=8192,ngsharedvt-trampolines=4096,nunbox-arbitrary-trampolines=2048'`.
Decoded object counts, checked by `rebuild_corelib_aot.inspect_pools`:

| Pool | Before | 59b and 60b |
| --- | ---: | ---: |
| Specific | 65,536 | 65,536 |
| Static RGCTX | 4,096 | 4,096 |
| IMT | 512 | 8,192 |
| GSharedVT argument | 512 | 4,096 |
| Function-pointer argument | 0 | 0 |
| Arbitrary unbox | 256 | 2,048 |

Only IMT exhaustion was observed. The GSharedVT/unbox increases are precautionary
headroom, not fixes for demonstrated failures. 60 had not been hardware-tested;
its rebuild is precautionary because it shares the same undersized CoreLib pools,
not evidence that LLVM itself exhausts them. All limits and failure checks remain
active; no runtime code generation, W^X change, or assertion suppression.

### Artifacts and host verification

- **59b:** `terraria-mono/switch/mono_nx_fna_terraria_nochroma59b_linq_collections_aot.nro`,
  title `Terraria 59b Linq+Collections AOT`, **946,632,984 bytes**, SHA256
  `cdc7def4c2536b5013a4e40a306916bdcbdf25df8305848100fc8bb989541702`.
  201,440 native targets and 14,582 fallback sentinels verified; 9 module bindings.
- **60b:** `terraria-mono/switch/mono_nx_fna_terraria_nochroma60b_llvm_terraria.nro`,
  title `Terraria 60b LLVM Terraria`, **903,788,824 bytes**, SHA256
  `2151bba77c10e77a11ff6df92e3f4561c6e0b113fc83304aa0f6d22609eaff14`.
  166,903 native targets and 14,214 fallback sentinels verified; 44,576 Terraria
  entries target LLVM code. 7 module bindings; no RWX or TEXTREL.
- Both replay build52's allocated ELF sections exactly and pass payload checks:
  the same 166 Release-framework replacements and one embedded CoreLib; all
  16,017 other payload files unchanged from 52. Neither changes game IL/assets
  relative to its existing input. Native runtime and graphics driver unchanged.
- 60b completed the fixed compiler helper end to end; no manual manifest recovery.
- Recipes: `R58_VARIANT=v59b`, `R58_CORELIB_INLINE=1`, the extra-pool setting above,
  `R58_EXTRA_MODULES='System.Linq.dll System.Collections.dll'`, and title
  `Terraria 59b Linq+Collections AOT`; or `R58_VARIANT=v60b`, `R58_CORELIB_INLINE=1`,
  the same extra-pool setting, `R58_LLVM_MODULES=Terraria`, and title
  `Terraria 60b LLVM Terraria`. Run `build_aot.py`, `build_native.py`, then
  `verify_artifact.py`; use the LLVM build image for 60b. The tracked recipe now
  includes the CoreLib option used by these successful builds.

### 59b hardware capture

`fna-nx-test/log59b.txt`, SHA256
`ab59ad00520ebe7a8c629251f8434ca1be07c5c4024f9528b3c3667df3e773a3`:

- One session, `runtime_logging=0`, **296.622 s**, normal application termination.
- **55 NX_PHASE records**; every cumulative delta reconciles with its interval
  counter: 9,758 Ticks, 15,914 Updates, 9,757 Draws/swaps, 10,534 polls.
- No repeat trampoline exhaustion, fatal diagnostic, or unhandled-exception marker.
- Settled timing block, elapsed **155.516–260.898 s**, log lines **295–315**:
  21 intervals / **105.380 s** summed windows / **2,603 Draws** / **6,325 Updates**.
  **24.701 Draw/s**, **60.021 Updates/s**, **27.417 ms/Draw**, **5.088 ms/Update**;
  swap **0.347 ms/Draw**. Maximum Tick in this block is **113.507 ms**.
- This is a timing plateau, not proof of stationary player, matching scene/settings,
  stock clocks, or a measured gain over another build. NX_PHASE has no such state
  fields. No baseline promotion and no sustained-30-FPS claim.
- Machine-readable result:
  `~/.cache/terraria-switch-build/release58/v59b/hardware-analysis.json`.

### Next test and scope boundary

**Test 60b next; 60 is not skipped.** Confirm world entry and normal exit, then
compare 58e and 60b in the same sheltered daytime spot for 90–120 s each, stock
handheld clocks, all low + Trippy, Frame Skip On, Auto Pause Off, no inventory/map,
`runtime_logging=false`. Save `log60b-control.txt` and `log60b.txt`. 60b differs
from 58e in compiler backend and pool capacity; 59b additionally AOT-compiles
Linq/Collections, so 59b versus 60b is not an isolated compiler comparison.

The user requested a next experiment that avoids terms-of-service problems.
The conservative new scope is open-source framework/compiler work, no additional
proprietary-game transformations, DRM/licensing/security bypasses, or public
redistribution of game-containing NROs. FNA's
[Ms-PL license](https://raw.githubusercontent.com/FNA-XNA/FNA/master/licenses/LICENSE)
permits modification/recompilation subject to its conditions; this is not a legal
guarantee about Terraria's EULA, console terms, or jurisdiction-specific rights.
FNA sits above FNA3D/OpenGL/Mesa and the Tegra GPU; optimizing FNA does not replace
the NVIDIA hardware or graphics driver.

## Build61 FNA-only LLVM experiment (2026-09-25)

**Built and host-verified; hardware pending. 60b is still the next test.** This
is a separate 59b-derived experiment, not 60b plus FNA. Only the open-source FNA
framework's AOT backend changes to LLVM. No FPS gain or baseline adoption claimed.

- Local candidate:
  `fna-nx-test/terraria-mono/switch/mono_nx_fna_terraria_nochroma61_llvm_fna.nro`,
  title `Terraria 61 LLVM FNA`, **945,436,952 bytes**, SHA256
  `8b58bd32933dda9340d4da44e03230eaba90daf57da00547b751bd93bc11bd81`.
- Input FNA is unchanged, version 26.3.0.0, SHA256
  `15427b2cb4c7952160f4cc124711f29b428459949304ceb0d16436e68a01882f`.
- All **eight non-FNA AOT objects are reused byte-for-byte** from 59b, including
  Terraria, CoreLib and its enlarged trampoline pools, Linq/Collections, ReLogic
  and the helpers. No new proprietary assembly transformation or AOT compilation.
- All **16,184 RomFS files** match 59b's verified payload hashes and sizes,
  including FNA IL, Terraria and every Content asset. Runtime and OpenGL/Mesa
  driver inputs unchanged. This is framework CPU code generation, not a replacement
  for NVIDIA hardware or OpenGL.
- FNA AOT: **14,391/14,440** methods; **11,614** method-table entries target LLVM
  code. Different generated-wrapper counts from the ordinary compiler do not
  indicate additional game methods or a measured speedup.
- Native verification: **201,450 native targets / 14,582 fallback sentinels**,
  9 modules with resolved name/MVID bindings; build52 allocated-section replay exact.
  FNA's farthest verified method-table branch is **101,882,204 bytes**, below the
  ARM64 CALL26 128 MiB limit.
- Segments are R E / R / RW; method tables are read-only; no RWX or TEXTREL.
  This ELF uses **DT_RELR**, not ordinary RELA entries. The 961 packed RELR words
  expand to **37,663 relocations**, all checked to target writable data. LLVM
  read-only-data inputs remain in the existing writable relocation section.

### Reproduction and hardware acceptance

`fna-nx-test/scripts/release_bcl/build_fna_llvm.py` pins 59b's verified NRO and
FNA input hashes, reuses non-FNA objects and payload, runs the existing LLVM
compiler, validates all early metadata, and invokes the existing native linker.
It completed end to end in `localhost/monobuild-llvm:local` with the same
`/build`, `/work`, `/mono-nx`, `/mono-nx/native` and `/fna-install` mounts as 60b.
Output: `~/.cache/terraria-switch-build/release58/v61/` (fresh directory required).
Then run `verify_artifact.py` with `R58_VARIANT=v61` and
`R58_TITLE='Terraria 61 LLVM FNA'`. Additional experiment-boundary and loader
checks are recorded in `v61/experiment-verification.json`.

After testing **60b**, compare **59b versus 61**, not 60b versus 61. Save
`log61-control.txt` and `log61.txt`, using the same sheltered daytime scene,
stock handheld clocks, low + Trippy, Frame Skip On, Auto Pause Off, inventory/map
closed, `runtime_logging=false`, stationary 90–120 s after settling. World entry,
rendering, controls/audio, and normal exit still need hardware verification;
compiler success and valid relocation tables do not prove LLVM runtime behavior.

### Deferred conditional follow-ups

- **L4T perf profile: not run.** No perf/mprof/L4T capture is present in
  `fna-nx-test/`, and the harness has no configured SSH host for the device.
  The procedure remains in the README for when the L4T card is available.
  59b's 24.70 Draw/s plateau does not establish matched-scene L4T parity.
- **Game-side work reduction: not implemented.** The existing discussion gate
  still requires explicit approval for visual/behavior changes. The current
  open-source-only experiment recompiles FNA; it does not reduce Terraria's
  own game work. Keep this separate from completed compiler/build work.

These conditional items are deferred, not reported as completed experiments.
The next hardware action remains 60b world entry/normal exit and its matched
stationary capture; 61 is a separate subsequent comparison against 59b.


## Build60/61 hardware results (2026-09-25)

User: the first 60 run "looked full speed" with Frame Skip On and ~80% with it
off; 61 then felt slow, and a second 60b run was as slow as 61. Uploads:
`log60.txt` (first run), `log60b.txt` (second run), `log61.txt`. Machine-readable:
`~/.cache/terraria-switch-build/release58/llvm-hardware-analysis-60-61.json`.

- **All three boot, enter the world, save and exit normally**; no errors, no
  trampoline exhaustion. First LLVM code on Horizon works in this path.
- **No build difference between the two 60 runs.** The earlier (512-pool) 60 and
  60b LLVM Terraria objects differ as files, but all 44,577 executable sections
  hash identically. Non-timing log events are identical except heap addresses.
- **Menu rendering is the same in every run** (~15.1–15.4 ms/Draw at 60 Draw/s,
  after the world-save marker), so the device ran equally fast; not throttling.
- Gameplay windows (after world entry, before the save's `CopyFile`, ms/Update ≥2):

| Log | Frame skip | Windows | Draw/s | ms/Draw | ms/Update | Game speed |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 60 (first) | on | 3 | 28.7 | 25.0 | 4.09 | 100% |
| 60 (first) | off | 6 | **44.0** | **17.0** | 4.93 | 73% |
| 60b (second) | on | 3 | 28.3 | 29.2 | 3.01 | 86% |
| 60b (second) | off | 10 | 27.7 | 32.5 | 3.74 | 46% |
| 61 | off | 5 | 23.2 | 36.3 | 5.79 | 39% |
| 59b | on | 26 | 24.6 | 27.5 | 5.05 | 100% |

- **The fast first run is a cheaper scene.** Update cost is similar across runs;
  only in-world Draw cost halves in the first run's frameskip-off block. The log
  records no position, zoom, time of day or on-screen content, so the exact cause
  cannot be identified. The user's ~80% impression matches the measured 73%.
- Frameskip-on Draw/s is higher in both 60 runs (~28.5) than in 59b (24.6), and
  61 is slower than both, but none of these are matched scenes; a few 5 s windows
  each. **No LLVM gain and no 61 regression are established.**
- Next: alternating same-spot capture 58e/60b/58e/60b (optional 59b/61), frame
  skip off, 60–90 s stationary; see the README.

## Alternating same-spot A/B: 58e vs 60b (2026-09-25)

**60b (LLVM-compiled Terraria) is a measured gain: +37% Draw/s over 58e.**
Uploads `logAB2.txt`–`logAB6.txt`; the user deleted `logAB1.txt`, but the Switch
log appends across launches and `logAB2.txt` holds two sessions, the first
being AB1 (58e; heap 1530 MB, matching AB3). Machine-readable:
`~/.cache/terraria-switch-build/release58/ab-analysis-58e-60b.json`.

Method: last 10 NX_PHASE windows (~50 s) before the world-save `MkDir` marker;
frameskip off in every block (Draw == Update each window); all sessions exit
normally with no errors.

| Run | Build | Draw/s | ms/Draw | ms/Update |
| --- | --- | ---: | ---: | ---: |
| AB1 | 58e | 26.81 | 31.02 | 5.53 |
| AB2 | 60b | **35.04** | 23.68 | 4.15 |
| AB3 | 58e | 24.62 | 33.94 | 5.87 |
| AB4 | 60b | **35.38** | 23.43 | 4.11 |
| AB5 | 59b | 20.14 | 43.32 | 5.59 |
| AB6 | 61 | 26.35 | 31.72 | 5.48 |

- 58e mean 25.71, 60b mean 35.21 Draw/s: **+36.9%**. Both Draw (−28%) and Update
  (−28%) got cheaper, consistent with LLVM code in Terraria's tight loops.
  The gap is stable across both alternations; 58e itself drifted 26.8→24.6
  (in-game time advances between runs).
- This is one stationary spot with frameskip off, not the heaviest scenes, and
  not a sustained-30-FPS claim across gameplay.
- 61 (LLVM FNA on 59b) beat 59b by ~31% in adjacent runs, but those were not
  alternated and 59b ran slower than 58e here (unexplained: its Update cost is
  similar, its Draw cost is 43 ms). Promising, not proven.
- AB2 session 2's heap reads 1538/1538 MB, versus 1540/1536 for 60b elsewhere;
  identity follows the user's naming and its timing matches AB4 closely.
- Next: build62 = 60b + LLVM FNA (no Linq/Collections AOT, since 59b looked
  slower), then A/B 60b vs 62.

## Build62 LLVM Terraria + LLVM FNA (2026-09-25)

**Host verified; hardware pending.** 62 = exact 60b plus FNA through LLVM.

- `fna-nx-test/terraria-mono/switch/mono_nx_fna_terraria_nochroma62_llvm_terraria_fna.nro`,
  title `Terraria 62 LLVM Terraria+FNA`, **902,588,696 bytes**, SHA256
  `eedeb4f999886cbe74a5ad5a624598df3c279ff8272c27e61b0f40f53d9e6966`.
- Only the FNA AOT object changed vs 60b; Terraria's object and LLVM sidecar
  and all other objects are reused byte-for-byte; RomFS identical to 60b.
- 166,913 native targets / 14,214 fallbacks; LLVM targets: Terraria 44,576,
  FNA 11,614; farthest branch 74.6 MiB (<128 MiB); 7 module bindings; build52
  replay exact; no RWX/TEXTREL; 37,751 RELR relocations all target writable data.
- Recipe: `scripts/release_bcl/build_fna_llvm.py`, now parameterized
  (`FNA_LLVM_BASE=v60b FNA_LLVM_VARIANT=v62 FNA_LLVM_BASE_NRO_SHA=2151bba7…
  FNA_LLVM_TITLE='Terraria 62 LLVM Terraria+FNA'`); defaults still reproduce 61.
  It reuses existing LLVM sidecars with their owner objects.
- Next: alternating 60b/62/60b/62 same-spot capture (`logC1`–`logC4`).

## Alternating same-spot A/B: 60b vs 62 (2026-09-25)

**62 is ~6% faster than 60b; the gain is Draw-side.** `logC1`–`logC4`, one session
each, all normal exits, no errors or trampoline exhaustion. Machine-readable:
`~/.cache/terraria-switch-build/release58/ab-analysis-60b-62.json`.

Settled block = gameplay windows 4–10 (~30 s after arriving), frameskip off:

| Run | Build | Draw/s | ms/Draw | ms/Update |
| --- | --- | ---: | ---: | ---: |
| C1 | 60b | 34.59 | 24.25 | 3.95 |
| C2 | 62 | **37.32** | 22.12 | 3.96 |
| C3 | 60b | 36.19 | 22.92 | 4.02 |
| C4 | 62 | **37.97** | 21.51 | 4.13 |

- 60b mean 35.39, 62 mean 37.65 Draw/s: **+6.4%**; ms/Draw 23.6 → 21.8,
  ms/Update unchanged (FNA work sits in Draw: SpriteBatch, vertex building).
- Both alternations agree. Last-10-window figures give +12.1%, but C4 steps
  from ~38 to ~41.5 Draw/s mid-run (scene/camera change, unique to that run),
  so the conservative early block is the reported number.
- 60b C1/C3 (34.6/36.2) reproduce the AB block's 35.0/35.4.
- Combined with the earlier A/B: 62 is roughly **+43–46% over 58e** at this
  spot [INFERENCE: chained across sessions, not a direct 58e/62 alternation].
- 62 is the best build. Remaining unknowns: heavier scenes (night, crowds,
  Color lighting), and whether other assemblies (ReLogic, CoreLib) benefit
  from LLVM too.

## Build63 LLVM Cortex-A57 tuning (2026-09-25)

**Host verified; hardware pending.** 63 = 62's inputs with both LLVM stages told
the target CPU. Previously `opt` saw **no target triple** (Mono's ARM64 backend sets
only a data layout; `mini-arm64.h` has no `MONO_ARCH_LLVM_TARGET_TRIPLE`), so its
cost models (inlining, unrolling, vectorization) used the host default, and `llc` got
only `-march=aarch64`. 63 adds, for Terraria and FNA only:
`llvmopts=-mtriple=aarch64-none-elf -mcpu=cortex-a57,llvmllc=-mcpu=cortex-a57`.

- `fna-nx-test/terraria-mono/switch/mono_nx_fna_terraria_nochroma63_llvm_a57.nro`,
  title `Terraria 63 LLVM A57`, **902,986,008 bytes**, SHA256
  `c3ffcc6b3ff917931d031fb2589c0ee9554194ad743d03f77472f678eb0f8b74`.
- Probe first (`scripts/release_bcl/probe_llvm_tuning.sh`, NxCrypto): both options
  parse and reach `opt`/`llc`; LLVM text 874,956 (base) → 874,388 (llc only) →
  871,420 bytes (both). No warnings.
- Full build: Terraria 45,252 / FNA 14,391 methods, the same counts as 62; LLVM text
  Terraria 25,758,208 → 25,798,512 bytes, FNA 2,412,792 → 2,406,252. `-mcpu=cortex-a57`
  appears in all 8 opt/llc invocations. RomFS identical to 62; 166,913 native
  targets / 14,214 fallbacks (44,576 Terraria + 11,614 FNA LLVM); farthest branch
  74.9 MiB; build52 replay exact; no RWX/TEXTREL; 37,736 RELR relocations, all
  in writable data.
- CoreLib is recompiled with 62's exact options. Its executable section is the same
  size; 273 words differ, and 59b vs 60b CoreLib shows the same (284 words). This is
  run-to-run nondeterminism in padding/constant words, not a code change.
- **Tooling fix found on the way.** The first 63 attempt (`v63-failed-shared-temp/`)
  failed to link. With two LLVM modules compiling in parallel, both used the same
  `temp-path`, where Mono writes fixed names (`temp.s/.bc/.opt.bc/.o`), so the main
  objects overwrote each other (both FNA.dll.o and Terraria.exe.o held Terraria code).
  60b/61/62 each had one LLVM module per compile and were unaffected. The helper
  (`compile_terraria_aot.py`) now uses a per-module `tmp/llvm-<file>` directory and
  checks that each LLVM main object defines its own `mono_aot_<name>jit_code_start`.
- Recipe: `R58_VARIANT=v63 R58_CORELIB_INLINE=1 R58_LLVM_MODULES='Terraria FNA'`,
  `R58_LLVM_AOT_EXTRA='llvmopts=-mtriple=aarch64-none-elf -mcpu=cortex-a57,llvmllc=-mcpu=cortex-a57'`,
  59b's `R58_CORELIB_AOT_EXTRA`, title `Terraria 63 LLVM A57`, in
  `localhost/monobuild-llvm:local`; `build_aot.py` → `build_native.py` →
  `verify_artifact.py`. Machine-readable: `release58/v63/experiment-verification.json`.
- **[INFERENCE]** Expected gain is small (a few % at most); code size barely moved.
  Next: alternating 62/63/62/63 same-spot capture (`logD1`–`logD4`).

## Alternating same-spot A/B: 62 vs 63 (2026-09-25)

**No measurable gain from Cortex-A57 tuning; 62 stays the best build.**
`logD1`–`logD4`, one session each, normal exits, no errors. Machine-readable:
`~/.cache/terraria-switch-build/release58/ab-analysis-62-63.json`.

Settled block = gameplay windows 4–10 (~30 s after arriving), frameskip off:

| Run | Build | Draw/s | ms/Draw | ms/Update |
| --- | --- | ---: | ---: | ---: |
| D1 | 62 | 41.29 | 19.78 | 3.76 |
| D2 | 63 | 29.98 | 28.74 | 3.90 |
| D3 | 62 | 38.05 | 21.60 | 3.99 |
| D4 | 63 | 41.31 | 19.82 | 3.70 |

- D4 (63) matches D1 (62) to 0.02 Draw/s, while the two 62 runs differ by 3.2 Draw/s.
  Any tuning effect is well below run-to-run variation.
- **D2 anomaly:** exactly 30.0 Draw/s in every 5 s window for ~200 s, Draw 28.7 ms,
  Update 3.9 ms and menu cost 15.24 ms/Draw (normal in all four runs), CPU busy 100%.
  30.0 is not a natural value next to 36–41 in the other runs; it resembles a frame
  cap or a different in-world state. D4, the same NRO, does not reproduce it, so it is
  not attributed to 63's code. The logs cannot identify the cause.
- This session's spot is lighter than the C session (62: 38–41 vs 37–38 Draw/s).
- Conclusion: CPU-targeted LLVM tuning is a dead end at this measurement resolution.
  The per-module temp-dir fix and ownership check from building 63 are kept.

## Build64 LLVM CoreLib (2026-09-25)

**Host verified; hardware pending.** 64 = exact 62 plus CoreLib compiled through
LLVM. It is the largest remaining compiler lever: every managed module calls into
CoreLib (collections, math, strings, generics). It is also the riskiest, because
CoreLib code runs from startup onward and in every exception path.

- `fna-nx-test/terraria-mono/switch/mono_nx_fna_terraria_nochroma64_llvm_corelib.nro`,
  title `Terraria 64 LLVM CoreLib`, **893,946,136 bytes**, SHA256
  `45231469ebee217132f354c689cda5a24fe1e5dfad163de579d0ba38aedfa0f8`.
- CoreLib keeps 62's AOT options (inlining on, `ntrampolines=65536` plus 59b's
  IMT/GSharedVT/unbox pools), only adding `--llvm`. Compiled **80,517/81,138**
  (62: 80,532/81,153; LLVM mode counts 15 fewer generated methods).
  **74,748** CoreLib method-table entries target LLVM code; the rest stay on
  Mono's own backend (methods LLVM declines). CoreLib LLVM sidecar 111.9 MB; the
  main object shrinks to 39.5 MB.
- Only the CoreLib object changed vs 62; Terraria/FNA objects and LLVM sidecars
  and the RomFS are byte-identical. 166,898 native targets / 14,207 fallbacks;
  7 module bindings; build52 replay exact.
- Farthest method-table branch is now **70.1 MiB** (FNA; 62: 74.6; <128 MiB CALL26).
  No RWX or TEXTREL; 37,897 RELR relocations all in writable data. LLVM read-only
  data needing load-time relocation grows 4.9 → 13.9 MB, so loaded segments go
  from 74.7/23.3/8.4 MB (code/read-only/writable, 62) to 70.2/10.9/17.1 MB.
  **[INFERENCE]** The extra ~9 MB writable is negligible against the ~1.5 GB heap.
- Recipe: generalized `scripts/release_bcl/build_fna_llvm.py` with
  `FNA_LLVM_MODULE=System.Private.CoreLib FNA_LLVM_BASE=v62 FNA_LLVM_VARIANT=v64`,
  `FNA_LLVM_BASE_NRO_SHA=eedeb4f9… FNA_LLVM_TITLE='Terraria 64 LLVM CoreLib'`.
  The CoreLib LLVM compile took ~10 minutes. The builder's new ownership check
  first rejected a good object: Mono names CoreLib's global symbols `mono_aot_corlib…`,
  not after the assembly name; the check now uses that prefix.
- Next: first-boot check, then alternating 62/64/62/64 same-spot capture
  (`logE1`–`logE4`).

## Alternating same-spot A/B: 62 vs 64 (2026-09-25)

**64 (LLVM CoreLib) works on hardware and loads faster; gameplay gain unproven.**
`logE1`–`logE4`, one session each, normal exits, no errors. The user could not
tell a game-speed difference but found 64 faster to load. Machine-readable:
`~/.cache/terraria-switch-build/release58/ab-analysis-62-64.json`.

| Run | Build | Draw/s | ms/Draw | ms/Update | Startup stall | World-load stall |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| E1 | 62 | 36.28 | 22.90 | 3.96 | 10.22 s | 4.29 s |
| E2 | 64 | 29.58 | 29.25 | 3.85 | 7.85 s | 3.69 s |
| E3 | 62 | 35.87 | 23.18 | 3.98 | 10.19 s | 4.26 s |
| E4 | 64 | 38.36 | 21.41 | 3.96 | 7.88 s | 3.73 s |

(Draw/s etc. = gameplay windows 4–10, frameskip off. Stalls = the largest single
Tick before the title screen, and the largest in the following world load.)

- **Loading, confirmed:** the startup stall is 10.2 s in every 62/63 run (C, D, E)
  and 7.85/7.88 s in both 64 runs (−23%); the world-load stall drops 4.28 → 3.71 s
  (−13%). Startup is CoreLib-heavy (type loading, globalization, resources, Json),
  so LLVM CoreLib pays off there first.
- **Gameplay, unproven:** E4 is +6% over the mean of E1/E3, but E2 runs near 30
  (29.4–30.0 per window). Update cost is the same in all four; only Draw differs.
- **Second-run near-30 pattern:** D2 (63) and E2 (64) both sit at ~30 Draw/s with
  Draw ~29 ms and normal Update/menu costs, while the same NROs reach 41 and 38 in
  the other run. Both were the second launch of their set. Not a build effect
  (the same build is fast in its other run). Cause unknown; the logs don't record
  position, zoom, time of day or settings. **[INFERENCE]** Most likely something
  in the scene or game state for that launch, not a code-generation difference.
- First LLVM CoreLib code on Horizon: startup, world load, gameplay, save and exit
  all worked; no errors or crash reports.

## Tie-breaker pair: 62 vs 64 (2026-09-25)

**64 becomes the working build.** `logF1.txt` (62) and `logF2.txt` (64), one session
each, normal exits, no errors. Machine-readable:
`~/.cache/terraria-switch-build/release58/ab-analysis-62-64-F.json`.

| | 62 (F1) | 64 (F2) before step | 64 (F2) after step |
| --- | ---: | ---: | ---: |
| Windows | 14 | 2 | 16 |
| Draw/s | 39.56 | 38.89 | **43.18** |
| ms/Draw | 20.48 | 20.97 | 18.45 |
| ms/Update | 4.10 | 4.06 | 4.04 |

- Loading repeats: startup stall 10.30 → 7.90 s, world-load stall 4.30 → 3.76 s.
  Across E and F, all three 64 runs are 7.85–7.90 s; every 62/63 run is 10.2–10.3 s.
- No near-30 run this time (F2 is the second launch), so the D2/E2 pattern is not a
  fixed second-launch effect.
- F2 steps from ~39 to a steady ~43.2 Draw/s at ~127 s; Update is unchanged across
  the step, so drawing got cheaper. C4 (62) showed a similar mid-run step, so it is
  likely a scene/camera change. Before the step, 64 equals 62 within noise.
- **Conclusion:** gameplay gain from LLVM CoreLib is between ~0 and ~9%, below what
  single alternating runs can isolate. Loading is reliably ~23% faster. All three
  logged 64 runs (E2, E4, F2) start, play, save and exit cleanly, so 64 is
  the working build. Further same-spot A/B at this resolution has diminishing
  returns; the next useful measurement is a heavy scene (night, crowds).

## Build65 Release native Mono runtime (2026-09-27)

**Host verified; hardware pending.** 65 = exact 64 (AOT objects, LLVM sidecars, RomFS)
linked against the native Mono runtime built in **Release** instead of the Debug-config
archive every build since 36 used.

- `fna-nx-test/terraria-mono/switch/mono_nx_fna_terraria_nochroma65_release_runtime.nro`,
  title `Terraria 65 Release runtime`, **893,901,080 bytes**, SHA256
  `bb0cc19cd15f1ce86e60a4165ffce407e1df92478eb89b82d6d0bc93bbf6e4e7`.
- Source: `runtime-source` at fork commit `289cdaa5…` with the build36 allocator
  patch (`native/patches/mono-switch-table-data-memory.patch`) applied, the same tree
  that produced `runtime-fix`'s patched object.
  `scripts/release_bcl/build_runtime_release.sh` (`--subset mono.runtime --configuration
  Release`) refuses to run without the patch.
- **What Debug actually cost.** Both configs compile at effective `-O2` (the libnx
  toolchain file adds it after CMake's `-O3`). The difference is Debug's
  `ENABLE_CHECKED_BUILD` + `ENABLE_CHECKED_BUILD_PRIVATE_TYPES` (`src/mono/CMakeLists.txt`
  812–815): extra `g_assertf` checks on every coop GC-safe/unsafe transition
  (`mono-threads-coop.c`; libnx uses coop suspend), per-lookup class-cache
  re-verification (`class.c`), out-of-line `m_class_*` accessors, and the interpreter's
  computed-goto dispatch disabled (`interp.c:350`). Release adds `-DNDEBUG`.
  **[INFERENCE]** Gain depends on how much time goes to icall/P-Invoke transitions and
  interpreted code; not measurable without hardware.
- Compatibility checks: DWARF layouts of all **384** named structs/unions common to the
  shipped and Release archives are identical (incl. `_MonoClass`, `MonoVTable`,
  `_MonoThreadInfo`, `MonoLMF`, `_MonoInternalThread`, `MonoJitTlsData`,
  `MonoAotFileInfo`); only the two checked-build private wrappers exist solely in Debug
  (`scripts/release_bcl/dwarf_layouts.py`). Same 277 archive members; 96 Debug-only
  global symbols (checked-build and out-of-line accessors), none referenced by
  `main.o`, the launcher objects, AOT objects, components or FNA libraries; runtime
  identity literals present. The four linked components export the same symbols as
  the SDK's. `libz.a` and `libSystem.*.a` stay as before (not per-frame).
- Link: `R58_RUNTIME=release` in `build_native.py` swaps libmonosgen and the four
  components for the **candidate only**; the build52 control replay stays exact. The
  linked ELF has none of the Debug-only symbols (`checked_build_init`,
  `m_class_get_byval_arg`); 64's has both.
- 166,898 native targets / 14,207 fallbacks; no RWX/TEXTREL; 38,569 RELR relocations in
  writable data; AOT manifest and RomFS identical to 64. The Release build added only
  headers to `artifacts/bin`; CoreLib and all 449 existing Release outputs unchanged.
- Next: first-boot check, then 64/65/64/65 same-spot capture (`logH1`–`logH4`).


## Build64 lighting modes: Retro and Color (2026-09-27)

`log64retro.txt` and `log64color.txt`, one launch each on 64, normal exits, no
errors, Frame Skip Off, stationary. Settled gameplay (between world entry and the
exit save; Color's first `MkDir` at 76.7 s is a settings save before entering):

| Lighting | Windows | Draw/s | ms/Draw | ms/Update |
| --- | ---: | ---: | ---: | ---: |
| Trippy (64: E4, F2 after step) | — | 38.4 / 43.2 | 21.4 / 18.5 | 3.96 / 4.04 |
| Retro (77.8–293.2 s) | 44 | 43.3 | 18.7 | 3.66 |
| Color (111.8–302.2 s) | 39 | **48.0** | **15.7** | 4.45 |

- Color is fastest: ~3 ms cheaper Draw (tiles/walls cached in render targets instead of
  redrawn each frame, see the Trippy analysis above), ~0.8 ms dearer Update (colour
  lighting). **[INFERENCE]** Frame Skip On: Color ≈ (1000 − 60×4.45)/15.7 ≈ 47 FPS,
  Retro ≈ 42, at this spot. Color's advantage should shrink while moving, when the
  cached layers must be redrawn.
- Retro ≈ Trippy at this resolution. Separate launches, not within-session switches:
  scene drift of a few Draw/s is possible.

## Build66 GC params and GC/memory stats (2026-09-27)

**Host verified; hardware pending.** 66 = exact 65 (AOT objects, LLVM sidecars,
Release runtime, RomFS) with a new launcher `main.c` from terrabuilder-nx:

- `/mono/gc_params.txt` on SD, one line, is set as `MONO_GC_PARAMS` before
  `mono_jit_init`. Absent = SGen defaults for this build: **concurrent mark-sweep**
  (`HAVE_CONC_GC_AS_DEFAULT` in libnx config.h) and a **4 MB nursery**
  (`SGEN_DEFAULT_NURSERY_SIZE`). `nursery-size` must be a power of two.
- `MONO_NX_GC_STATS=1` (this build): a lowest-priority native thread logs
  `NX_GC elapsed=… minor=… major=… minor_ms=… major_ms=… major_conc_ms=…
  managed_alloc_mb=… malloc_used_mb=… malloc_arena_mb=…` every 5 s and a final line at
  exit. Counts/times are cumulative (`mono_gc_stats`, 100 ns units → ms); managed_alloc
  is SGen's total allocation (`mono_gc_get_heap_size`), malloc is newlib `mallinfo`.
  Only lock-free counters are read, so the thread needs no Mono attachment.
- `fna-nx-test/terraria-mono/switch/mono_nx_fna_terraria_nochroma66_gc_params.nro`,
  title `Terraria 66 GC params`, SHA256
  `7de0d928c020daad6b1d91c951c58f287b829ba266515953dc128be3580b0b23`.
- main.c compiles with `-Wall -Werror` with and without stats; build52 replay exact;
  166,898 native targets / 14,207 fallbacks; no RWX/TEXTREL; RomFS identical to 64.
- Recipe: `R58_VARIANT=v66 R58_RUNTIME=release R58_MAIN_DEFINES=-DMONO_NX_GC_STATS=1`,
  `/work` = terrabuilder-nx (first build from that repo), link-only from v65's AOT set.
- Test plan (README): default / `nursery-size=16m` / default / `nursery-size=32m` /
  `major=marksweep`, `logGC1`–`logGC5`.

## Build65/66 hardware: two runtime bugs, fixed in 67 (2026-09-28)

User: from 65 on, all tests use Color lighting and Frame Skip Off (Trippy until 59,
Retro from 60 to 64). Uploads: `log65.txt` + `crash_reports65/`, `log66.txt` +
`crash_reports66/`, `log64mp.txt` (first multiplayer test).

### 65: gameplay unchanged, abort on exit

- Session 2 of `log65.txt` (session 1 idles for ~24 min at 71 s, no crash):
  gameplay windows 210–491 s, **47.87 Draw/s, 15.74 ms/Draw, 4.47 ms/Update** — the
  same as 64 with Color (48.0 / 15.7 / 4.45). The Release native runtime brings no
  measurable gameplay gain; startup stall 7.85 s = 64.
- After the world save the log ends with `Assertion at mono-threads.c:589, condition
  'info' not met, function:unregister_thread`. Symbolized crash stack:
  `start_wrapper → mono_threads_platform_exit → pthread_exit → libnx threadExit →
  tls_thread_destructor (mono-tls.c:250) → thread_info_key_dtor → unregister_thread`.
- Cause: the fork's HEAD commit `289cdaa` "Restore lost TLS destructor bitmap check"
  makes libnx's emulated TLS run destructors for every key the thread ever set. The
  SDK (rel-3) runtime used by builds ≤ 64 has the old, inverted check (verified by
  disassembling `tls_thread_destructor` in both archives), so destructors effectively
  never ran there. Mono sets `thread_info_key` to NULL when it detaches a thread before
  `pthread_exit`; the new code then calls `thread_info_key_dtor(NULL)`. The Release
  runtime is the first shipped build compiled from 289cdaa, so it's the first to hit it.

### 66: `nursery-size=16m` aborts before launch

- `log66.txt`: `NX_GC params=nursery-size=16m`, then `mono_valloc_aligned: returned
  pointer 0xb01800000 is not aligned to 1000000`; stack `mono_jit_init → mono_gc_base_init
  → sgen_gc_init → alloc_nursery → sgen_alloc_os_memory_aligned → mono_valloc_aligned`.
- Cause: libnx fake mmap converted the byte alignment into a page-index stride from
  `heap_start`. mono-nx's `heap.c` aligns the Mono half to 4 MB only, so requests above
  the heap start's own alignment could fail. The default 4 MB nursery always fit; this
  heap started at `…800000` (8 MB-aligned), so 16 MB failed. Also latent for any other
  large aligned allocation.

### Fixes (dotnet_runtime fork, branch `terrabuilder-nx`)

- `23b25381` mmap: find the first free page whose **address** is aligned.
- `969ed2ab` TLS: the per-thread bitmap tracks only **non-NULL** values (set to NULL
  clears the bit). NULL keys skip their destructor as in POSIX, and a destructor may
  re-set then clear its own key (`thread_info_key_dtor` does).
- Host regression `native/tests/test_libnx_runtime_fixes.py` compiles the real
  `mono-mmap-libnx.c` and libnx `mono-tls.c` with stub headers. Before: `mmap-align`
  aborts with the same g_error as the Switch, `tls-detached-exit` calls the destructor
  with NULL. After: all 9 checks pass (4/8/16/32 MB alignment from an 8 MB-offset heap,
  reuse after free, detached and attached thread exit).
- Rebuilt Release runtime: exactly 2 of 277 archive members differ from 65's
  (`mono-mmap-libnx.c.obj`, `mono-tls.c.obj`).

### Build67

`fna-nx-test/terraria-mono/switch/mono_nx_fna_terraria_nochroma67_runtime_fixes.nro`,
title `Terraria 67 runtime fixes`, SHA256
`aeaae5c82e3e43d3096cbd010262653d946929c3ac60da816173db82a45cd6cc`. = 66 (launcher with
gc_params + NX_GC stats, AOT set, RomFS) + the fixed Release runtime. Build52 replay exact;
verify_artifact PASS; no RWX/TEXTREL; RomFS identical to 64. Hardware pending: exit and
the GC test plan (README) run on 67.

### First multiplayer test (`log64mp.txt`, build 64): join crashes

- Joining a server: `Terraria.Netplay.TcpConnectLoop → TcpClient.Connect` fails, then the
  TCP client thread dies with `System.StackOverflowException` at
  `Interop.Sys.Close ← SafeSocketHandle.CloseHandle ← Socket.ReplaceHandle ←
  ReplaceHandleIfNecessaryAfterFailedConnect`, and the fatal error applet appears
  (126 s; the connect attempt starts at ~121 s).
- `TcpConnectLoop` (IL) retries `Connect` in a tight loop with no delay on non-OSX:
  catch → check `Disconnect`/`gameMenu` → loop.
- **[INFERENCE]** The stack overflow is the interpreter's own stack check
  (`interp.c:4102/4379`, 1 MB interp stack): System.Net.Sockets is not AOT-compiled.
  The trace is shallow, so interpreter stack is most likely not being released on each
  failed connect's exception path, until it runs out. Two problems to separate: (1) why
  the connect fails (address/port, BSD socket non-blocking connect on libnx), and
  (2) the interp stack exhaustion across repeated socket exceptions.
- Not yet fixed. Needs: what was joined (IP/host, same LAN, Steam vs IP), and a
  `runtime_logging=true` repro.

## Build67 GC test results (2026-09-28)

Runs on 67, Color lighting, Frame Skip Off, one launch each, all **exited cleanly**
(no assert/abort): the TLS exit fix holds. No default-params 67 run exists. The
periodic `NX_GC` thread failed to start (`thread failed`), so only the final whole-session
line exists. Machine-readable: `~/.cache/terraria-switch-build/release58/gc-analysis-67.json`.

| Setting | Draw/s | ms/Draw | ms/Update | Median max tick | Minors | Majors (STW ms) | Concurrent mark ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 16m nursery | 26.7 | 32.2 | 4.61 | 51 ms | 243 (7.4 ms avg) | 98 (1,392) | 48,052 |
| 32m nursery | 42.4 | 18.1 | 4.81 | 38 ms | 206 (8.2 ms avg) | 95 (1,495) | 44,730 |
| `major=marksweep` | 26.7 | 31.9 | 4.90 | 52 ms | 852 (3.8 ms avg) | 66 (8,865; **134 ms avg**) | 0 |
| 65/64 defaults (Color) | 47.9 / 48.0 | 15.7 | 4.45–4.47 | — | — | — | — |

- **Non-concurrent `major=marksweep` is harmful**: every major collection stops the world
  for ~134 ms on average (vs ~14–16 ms with concurrent marking).
- **Larger nurseries don't help**: minors get rarer (~40/min vs ~67/min) but each takes
  about twice as long (7.4–8.2 vs 3.8 ms); total minor pause per minute rises.
- **Draw/s differences are not GC.** 16m and marksweep both sit flat at ~27 Draw/s with
  Draw ~32 ms, while their Update cost matches the others; 32m sits at ~43. GC cost is
  <1% of runtime in all three. **[INFERENCE]** Those two runs had a heavier or different
  scene or settings (Draw-only change). Not attributable to the GC setting.
- Decision: keep SGen defaults (concurrent mark-sweep, 4 MB nursery). Build 68 adds the
  missing default-params baseline with working periodic stats.
- Stats-thread cause: `threadCreate(..., prio 0x3F, core -2)`. libnx `thread.h:37`:
  0x3F is the special lowest priority on core 3 only; on cores 0..2 it is 0x3B. Fixed
  (terrabuilder-nx `c25e96c`), and the failure now logs the Result code.

## Multiplayer join: diagnosis so far (2026-09-28)

`log64mpruntimelogging.txt` (64, `runtime_logging=1`, LAN host; the UI showed
"Connecting"): one `SystemNative_Connect` resolution, then `StrError`,
`ConvertErrorPalToPlatform`, a `Win32Exception` constructor (SocketException), `GetSockOpt`,
`FcntlSetIsNonBlocking`, `SetLingerOption`, `ConvertErrorPlatformToPal`, then
`StackOverflowException` on the TCP client thread at `Interop.Sys.Close ← … ←
Socket.ReplaceHandleIfNecessaryAfterFailedConnect ← TcpClient.Connect ← TcpConnectLoop`.

- `connect()` **fails**; the log never shows the errno (Terraria catches and retries).
  "Connecting" is shown before the attempt, so it does not mean the server answered.
- Native symbol lookups are logged once per process, so later retries are invisible:
  the log cannot show how many connects happen before the overflow.
- Rejected hypotheses from the delegated investigation (checked against source):
  - *sockaddr layout mismatch*: libnx `sys/socket.h:289` has `sa_len` then `sa_family`,
    and pal_networking writes through `&sockAddr->sa_family` (`pal_networking.c:721-726`),
    so the field offset is correct; managed code also sets byte 0 to the length
    (`SocketAddressPal.Unix.cs:180-182`). Not the cause.
  - *interp stack leak on catch in the same frame* (`interp.c` resume path): the catching
    frame, `TcpConnectLoop`, is AOT/LLVM code, not interpreted. When the handler is in AOT
    code, EH sets a NULL resume frame (`mini-exceptions.c:2464`) and the interpreter
    returns through `interp_entry`, which resets `context->stack_pointer`
    (`interp.c:2222`). A leak is not established; not excluded either.
- **Build 68 diagnostic**: `NX_NET connect #n fd ip:port len rc errno (text) ms bytes=…`
  for every connect via `--wrap=connect`. It shows the failing errno, the sockaddr bytes
  the kernel got, and how many retries precede the overflow (1 = something on the first
  failure path; many = per-retry exhaustion).

## Build68: GC stats + connect trace (2026-09-28)

`fna-nx-test/terraria-mono/switch/mono_nx_fna_terraria_nochroma68_gcstats_nettrace.nro`,
title `Terraria 68 GC stats + net trace`, SHA256
`9f9c97f101b82f646cf59e37ef7ad7863707188244a68b87052c07b26e169392`. = 67 + launcher
`main.c` from terrabuilder-nx `d62b22b` (stats-thread priority fix; `MONO_NX_NET_TRACE`),
`R58_MAIN_DEFINES="-DMONO_NX_GC_STATS=1 -DMONO_NX_NET_TRACE=1"`,
`R58_EXTRA_LDFLAGS=-Wl,--wrap=connect` (candidate only). `SystemNative_Connect` calls
`__wrap_connect` in the linked ELF. Build52 replay exact; verify_artifact PASS.

## Build68 hardware results (2026-09-28)

Both captures used `runtime_logging=1` (verbose Mono tracing), so timings include
diagnostic overhead and are not a clean speed comparison.

- **Baseline `log68.txt`** (default SGen, Frame Skip Off): clean exit. 47 settled
  `NX_PHASE` windows 76.2-312.3 s: **26.6 Draw/s, 31.9 ms/Draw, 4.93 ms/Update**.
  Phase counters reconcile; GC counters are monotonic. GC over 85.2-305.3 s: 497 minor
  collections, 0 major, 1028 ms stop-the-world = **0.47%**. Draw rate matches the 67
  16m/marksweep runs (26.7), so GC settings did not cause those runs' slowdown.
  Analysis: `~/.cache/terraria-switch-build/release58/analysis68.json`.
- **`managed_alloc_mb` is not allocation throughput.** `mono_gc_get_heap_size()` returns
  SGen's current OS allocation (`sgen-memory-governor.c:455,473` add, `:488` subtracts).
  It held 806-807 MiB; newlib `malloc_used` held 1076-1078 MiB.
- **Multiplayer `log68mp.txt`**: 2,164 connects to `192.168.1.138:7777`, each failing in
  ~0.1 ms with `errno=114 ENETUNREACH`. The sockaddr bytes are correct. Terraria's
  `TcpConnectLoop` retries without a delay; the TCP client thread then dies with
  `StackOverflowException` in `SafeSocketHandle` close. A single later connect from the
  WSL host to that address timed out, so the server's state at capture time is unknown.

### Root cause of the overflow: interpreter stack leak (fixed, host-reproduced)

When interpreted code throws and compiled (AOT) code catches, `interp_throw` jumps
through `mono_restore_context` and skips the interpreter entry's epilogue, so
`context->stack_pointer` and `data_stack` are never unwound. Each caught failure leaks
interpreter stack. Runtime fork `47d1c2190fc` (branch `terrabuilder-nx`) unwinds to the
live boundary: an outer interpreter handler keeps its resume state; otherwise the LMF
walk finds the deepest surviving interpreter frame. A first attempt (`daeace6195b`)
cleared resume state unconditionally and would have dropped exceptions caught by an
outer interpreter frame. Review caught it before any package.

Host reproduction (`~/.cache/terraria-switch-build/multiplayer-host-repro/`, Linux x64
Mono from the same fork, x64 CoreLib AOT, AOT caller with an interpreted
`System.Net.Sockets`, deterministic refused loopback connect):

| Scenario (10,000 iterations) | Before | After |
|---|---|---|
| AOT caller catching failed `TcpClient.Connect` | `StackOverflowException` after 2,000-2,999 | pass |
| Plain / outer-interpreter catch / filter+finally / outer-AOT catch, interpreter-only and mixed | pass | pass |

Proof: `verified-proof.json`. The ARM64 libnx CoreLib cannot be AOT-compiled for x64:
it recurses in `AdvSimd.get_IsSupported`. The host test uses a Linux x64 CoreLib.

### Build 69

`mono_nx_fna_terraria_nochroma69_netfix.nro`, title `Terraria 69 network fixes`, SHA256
`e59f4175afbccd1af8226856a9aeaf8596cf9189a37fba47c3ab220f05ca370b`. = 68's AOT set and
payload + Release runtime from fork `47d1c2190fc` (`libmonosgen` `67223bcc…`) + launcher
with a non-blocking NIFM request (`MONO_NX_NIFM=1`, logs `NX_NET nifm ...`).
**[INFERENCE]** The missing NIFM request is the leading ENETUNREACH hypothesis; hardware
must confirm it. `R58_MAIN_DEFINES="-DMONO_NX_GC_STATS=1 -DMONO_NX_NET_TRACE=1
-DMONO_NX_NIFM=1"`, `R58_EXTRA_LDFLAGS=-Wl,--wrap=connect`. verify_artifact PASS:
166,898 native targets, 14,207 fallback sentinels, no RWX.

## tModLoader zero-mod experiment: tmod02 (2026-09-28)

`tmodloader02_zeromod.nro`, SHA256
`b26a38ece069a91ed71223bfa513fa20a0503be5cca6e062e6467f4d62108bf6` (1,130,351,616 bytes).
tModLoader v2026.07.3.0 (1.4.4 stable; release zip SHA256 `6f51610f…`), net8.0 assemblies
on our net9 libnx BCL, user's GOG 1.4.5.8 `Content/` in RomFS, zero mods.

- **Offline patches (Mono.Cecil)**: strip MonoMod detours in `LoggingHooks` and
  `AssemblyRedirects`; force GOG distribution and skip the install hash check; content
  root `romfs:/Content`, FNA base directory `romfs:/`; Steam init stubbed and `-nosteam`
  passed.
- **Hard blocker for real mods (unchanged)**: MonoMod runtime detours need writable,
  executable memory. Zero mods only works because vanilla tModLoader uses its own
  event hooks, not detours.
- **Launcher**: separate saves in `/switch/tmodloader`, default assembly
  `tModLoader.dll`. It does not touch `/switch/terraria` or `/mono/config.ini`.
- **AOT**: tModLoader 59,918/59,955 methods, FNA 12,172/12,221, plus CoreLib; runtime
  fork `47d1c2190fc`. tmod01 was discarded: its packaged `tModLoader.dll` was patched
  after AOT compilation. MVIDs matched but the method bodies did not. tmod02 compiles
  from byte-identical copies of the packaged assemblies.
- **Verification** (`~/.cache/terraria-switch-build/tmod-final-check/`): 152,622 native
  targets and 13,448 fallback sentinels, each checked; no RWX; read-only method tables;
  AOT objects bound to assembly MVIDs; compiler inputs byte-identical to the payload; all
  32,836 RomFS files match staging; no host logs or saves embedded.
- **Host smoke (limited)**: under .NET 9 in server mode it reaches `Choose World:`. The
  client under host Mono (interpreter, Xvfb, llvmpipe) creates the OpenGL device and
  sets the language, then segfaults in `SystemNative_LowLevelMonitor_TimedWait` from
  CoreCLR 9.0.1's `libSystem.Native.so`. The Switch statically links the fork's own
  System.Native, so **[INFERENCE]** this crash is host-specific. **The main menu has not
  been observed anywhere.**
- Recipe: `terrabuilder-nx/scripts/tmod/build_tmod02_nro.py`; launcher source
  `~/.cache/terraria-switch-build/tmod/launcher_build/main_tmod.c`, a copy of `main.c`
  with only the paths and default assembly changed.

## tmod02 hardware: did not launch; tmod03 uses 1.4.4.9 content (2026-09-28)

The user reported tmod02 did not launch and supplied GOG Terraria **1.4.4.9**
(`gameinfo` v1.4.4.9, build 60321; `fna-nx-test/1.4.4/`). No launch log exists yet,
so tmod02's failure point is **unknown**.

Content comparison (tmod02 shipped 1.4.5.8 `Content/` plus tModLoader's 601-file overlay):
- The overlay replaces 594 of the 595 1.4.5.8-changed textures and sounds with its own
  1.4.4-compatible versions (0 of them byte-identical to 1.4.4.9).
- It does **not** cover the 5 fonts (`Fonts/*.xnb`, 33-70% larger in 1.4.5.8),
  `Sound Bank.xsb`, `Wave Bank.xwb` or `Images/Tiles_650.xnb`; tmod02 shipped the
  1.4.5.8 versions. It also lacked 1.4.4.9's `gore_240`, `projectile_179` and
  `projectile_618` and carried 1,636 files 1.4.4 never uses.
- **[INFERENCE]** 1.4.4 code reading 1.4.5.8 fonts or XACT banks is a plausible launch
  failure; the log will confirm or rule it out.

Ruled out statically: native entry points. Every FNA 23.10 `SDL2`/`FAudio`/`FNA3D`
import that our shim can't resolve (21 Android/iOS/WinRT SDL functions, 19
`stb_vorbis`/`FAPOBase`) is imported by vanilla FNA too, and vanilla runs.
`CHECK_LIB_NAME` is an exact `strcmp`, so tModLoader's path-based `NativeLibrary`
attempts fail over to the plain `SDL2`/`FNA3D`/`FAudio` names the shim registers.
Both tmod02 and build 69 ship an empty icon asset, so that isn't the difference.

**tmod03** (`tmodloader03_144content.nro`, SHA256
`007d4bf84f80c6820c7b0e9ea9c3dfca912b95bcaffe3e6f6aae62a09c7d1cf5`, 1,025,035,836
bytes): tmod02's exact ELF, launcher, assemblies and AOT, with `Content/` = GOG
1.4.4.9 (14,370 files) + tModLoader overlay (14,371 total). Recipe
`terrabuilder-nx/scripts/tmod/build_tmod03_nro.py`. Verified: 152,622 native targets
and 13,448 fallback sentinels, no RWX, MVID binding, compiler inputs byte-identical to
the payload, all 15,207 RomFS files match staging, and `Content/` byte-exact against
the 1.4.4.9 install plus overlay.

## tmod03 hardware crash and tmod04 (2026-09-28)

`fna-nx-test/logT.txt` + Atmosphère report `crash_reports/01790608896_05446530aca7e000.log`
(tmod03, `runtime_logging=1`). Startup reached OpenGL (`NV120`, Mesa 20.1 nouveau, GL 4.3
compat), then `Main.InitTMLContentManager`, and died with a Data Abort: SP 0x17e76ef0 is
below the 1 MB stack region 0x17e77000-0x17f77000, i.e. a native stack overflow.

Reproduced on host Mono (same fork, interpreter-only, cwd without `Content/`), where gdb
showed 11,165 frames of `interp_throw` -> first-chance dispatch -> tModLoader's
`Logging.FirstChanceExceptionHandler` (interpreted; AOT had skipped it) -> throw -> ...
Each nested exception was `System.InvalidProgramException`. **The earlier "host-specific"
crash in `SystemNative_LowLevelMonitor_TimedWait` was this same bug, not a native-library
mismatch.**

Root causes, all in our own offline patching (stock tModLoader is fine):
1. The console patcher replaced `Console.set_Title`/`set_ForegroundColor`/`ResetColor`
   calls with `pop`. `ResetColor()` takes no argument, so its `pop` underflows: 9 sites in
   8 methods became invalid IL, including `FirstChanceExceptionHandler`. The handler threw
   `InvalidProgramException` before its re-entry guard, recursing forever.
2. `AssemblyRedirects..cctor` (a MonoMod `Hook`) was replaced with `newobj Dictionary`2::.ctor()`
   on the open generic type: "containing type is not fully instantiated" when tModLoader's
   force-load thread runs static initializers.
3. The trigger: tModLoader builds `System.IO` paths from the relative content root
   `"Content"` (`TMLContentManager` image cache, `TryGetPath`, `OpenStream`, and the root
   list fed to ReLogic's `XnaDirectContentSource`). The launcher's working directory is
   `sdmc:/` (saves go to SD), so they looked in `sdmc:/Content`. FNA's own loads use
   `TitleLocation` = `romfs:/` and were fine.

A full diff of patched vs stock tModLoader.dll (20 changed methods) found no other invalid
IL. One behaviour change remains as-is: `LoaderManager.AutoLoad` uses `GetExportedTypes()`
instead of `GetTypes()` (non-public loader types are skipped).

**Fix: `terrabuilder-nx/scripts/tmod/nxfix` (Mono.Cecil), applied to tmod03's DLL.**
Stack-depth dataflow turns each underflowing `pop` into `nop` (9, matching the 9 stock
`ResetColor` calls; every other method verifies); the open-generic `newobj` is rebound to
`Dictionary<string, Assembly>`; a new `TMLContentManager.NxRoot` prefixes `romfs:/` to a
device-less root, applied to 5 `RootDirectory` reads and the image-cache argument. The
output gets a deterministic new MVID, so a stale AOT image can no longer bind to changed IL
(how tmod01 shipped mismatched code unnoticed).

Host check (`~/.cache/terraria-switch-build/tmod-host-observe/switchlayout.sh`: cwd =
SD stand-in with a `romfs:` link to the payload, launcher XDG paths, `-nosteam`, Xvfb +
openbox + llvmpipe): startup passes content, audio init and the force-load thread; the
tModLoader UI renders and stays up for 5+ minutes with the background animating. A
host-only "No audio hardware found" dialog (the container has no audio device) blocks the
menu, and scripted clicks don't register, so **the main menu is not yet observed.**

**tmod04** (`tmodloader04_ilfix.nro`, SHA256
`9c284ba2641f08881994adaed718d091542e428809251ad1d1ab1b25a74275a7`, 1,025,052,220 bytes):
tmod03 with the repaired tModLoader.dll, AOT-recompiled from the exact packaged file (same
compiler/options as tmod02); FNA, CoreLib objects and launcher object are tmod02's. Recipe
`scripts/tmod/build_tmod04_nro.py`. Verified: 152,634 native targets, 13,439 fallback
sentinels, no RWX, MVID binding, compiler inputs byte-identical to payload, all 15,207
RomFS files, and 1.4.4.9 content byte-exact.