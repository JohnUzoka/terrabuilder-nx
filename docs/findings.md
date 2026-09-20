# FNA on Switch via mono-nx — Findings & Nuances

Start with the [build45 hardware operation costs](#build-45-hardware-operation-costs-2026-09-18), [measurement semantics](#build-45-focused-tile-cost-measurements-2026-09-18), and the [optimization escalation gate](#current-decision-performance-first).
45 produced100 complete reports matching12,167 native Draw calls. Across4,964 post-entry normal-world frames, selected solid calls spend1.362us in allocation/init and2.262us in lighting/frame helpers; their measured body is9.563us including observers. These are selected-call costs, not an exact partition of the uninstrumented tile loop.
Current priority: **performance first; multiplayer testing and mod-loader bring-up remain deferred**.45's hardware profiling works, but its overhead is substantial enough to preclude clean FPS conclusions. No scratch-reuse optimization has been applied.
**Mandatory discussion gate:** bring up potential protocol, simulation, gameplay, multiplayer-visible/save-state, vanilla object-lifetime or mod-compatibility impacts before applying a tradeoff. Compatibility is not an absolute veto: the user may accept breakage for significant measured gains or modest adaptations, but not a large mod/loader overhaul for a small gain. Safe measurement may continue.
Earlier b18/b17 mapping conclusions are superseded: **Plus is b10; Minus is b11**.

Date: 2026-09-16; updated 2026-09-18
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
