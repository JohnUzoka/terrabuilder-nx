# tModLoader Switch optimization runbook

This runbook covers the experimental Nintendo Switch tModLoader build. It is
based on static analysis of this repository, the engineering cache's build
outputs, and the tModLoader source at the installed revision. Nothing here has
been profiled or measured on hardware; every performance claim is a hypothesis
until the measurement steps below confirm it.

A second audit re-checked the first version against the code and cached
builds. It:

- withdrew two items that never run in tModLoader builds;
- demoted the hook-dispatch, interpreter, and loader fast-path items;
- found that the LLVM item had already been built once;
- found that the profiler profile did not work for tModLoader builds (now
  patched, untested on hardware).

## Build facts

- Installed loader: tModLoader `v2026.07.3.0` (tag commit `666f6996`) with GOG
  Terraria 1.4.4.9 content. `prepare_modset.py` rejects mod packages built for
  any other loader version. The local source checkout
  (`/home/john_uzoka/tModLoader-src`, MIT) tracks the newer `1.4.4` branch,
  which does not contain that tag. The tag has been fetched, so read installed
  source with `git show v2026.07.3.0:<path>`.
- tModLoader builds compile their launcher from the engineering cache's
  `tmod/launcher_build/main_tmod.c`, not `native/interpreter/source/main.c`.
- Code generation differs by build:

| Build | `tModLoader.dll` | Mod assemblies |
| --- | --- | --- |
| CLI, `--mods none` | LLVM (object reused from `tmod27`) | none |
| CLI, `--mods FargowiltasSouls` | Mono AOT without LLVM, recompiled per mod set | LLVM |
| `tmod27` (engineering build) | LLVM | Mono AOT without LLVM |

- Known numbers: vanilla reaches 48-53 fps with Color lighting and about 60 fps
  with White lighting (starting area, default handheld clocks). With glthread
  (vanilla build 88) the GPU sits idle for about 45% of each frame, so vanilla
  is CPU-bound. tModLoader with Fargo's Souls ran about 20-24 fps in `tmod26`
  and 15-19 fps in `tmod27`; the engineering notes put the drop down to audio
  being enabled in `tmod27`, unprofiled. The current CLI build has not been
  measured. Source for the earlier builds: `fna-nx-switch-findings.md` on the
  workshop repo's `terraria` branch.

## 0. Measure before changing code

The cache's `main_tmod.c` never calls `nx_profiler_start()`, so for
`--profile profiler`, `build_tmod_modset_nro.py` inserts the
`nx_profiler_start()` and `nx_profiler_stop()` calls around `mono_jit_exec`, as
`native/interpreter/source/main.c` does. The launcher starts tModLoader's game
loop on a large-stack managed pthread, so profiler builds also register that
thread; this has not been run on hardware yet.

The debug and profiler profiles collect:

- `NX_FPS` every 5 s: fps, frame-time histogram, `swap_ms`, nouveau
  buffer-object churn, and FNA3D draw calls per frame. `swap_ms` is main-thread time inside
  `SDL_GL_SwapWindow`. Mesa's GL thread is finished before each swap, so this
  includes waiting for that worker as well as for the GPU and presentation.
- `NX_GC` every 5 s: minor and major collection counts and times.
- `--profile profiler` samples the main thread at 400 Hz into `/mono/prof.bin` (format in `native/shared/nx_profiler.c`).
  Mesa's GL worker registers itself; its samples carry flag 8. tModLoader's
  Switch launcher also creates its game thread through a large-stack `pthread`;
  profiler builds register pthread-created threads because the libnx entry
  thread waits on that game thread. The decoder used
  for vanilla builds 71-89 is on the workshop repo's `terraria` branch:
  `fna-nx-test/scripts/analyze_prof.py prof.bin --symbols <nm -n of the ELF>
  --map <linker map> [--thread extra]`, where `extra` selects the GL worker. It
  attributes samples to linked inputs through the map and names functions by
  the nearest `nm` symbol. Non-LLVM methods have local symbols in
  `native/candidate/tmodloader.elf`; LLVM methods have none, so their names
  come out approximate, but the map lists each one as a `.text.<method>`
  section with its address and size.

  For tModLoader that thread choice is wrong both ways: `--thread main` is the
  libnx entry thread, 100% `svcWaitProcessWideKeyAtomic` while it waits for the
  game thread, and `--thread extra` mixes every registered thread because
  `nx_profiler.c` writes no slot bits. Extract the game thread (the first
  registered extra thread) and analyze that file as the main thread:
  `nx-port-research/tools/split_thread.py prof.bin game.bin 1 Game_Tick <syms>
  <map>`. Even then about 90% of game-thread samples lose their middle frames
  (56-frame cap), so the Draw/Update phase split undercounts and inclusive
  times include stale return addresses; compare self times.

  The first Ryujinx capture of the initial profiler NRO was not a game
  profile: 97.2% of its samples were the launcher thread in `Thread.Join`, and
  it had no Draw/Update samples. That exposed the tModLoader thread
  distinction; the corrected variant registers the large-stack Mono pthread
  (slot 0). Its bounded Ryujinx boot confirmed the registration and produced
  launcher/managed-startup samples, but still had no Draw/Update samples:
  Ryujinx's BSD service throws on the game's AF_UNIX socket request and the
  emulator reaches its memory cap before the title creates a frame. This is
  not evidence about Switch graphics cost.

Plus+Minus toggles the on-screen FPS counter in tModLoader builds (see
"Withdrawn" below), but read fps from `NX_FPS`, which also has frame times.

Run order:

1. Build the current Fargo's Souls configuration with `--profile profiler`.
   Use one world, location, clock profile, and settings (Lighting, Frame Skip,
   Quality, Background) for every run; record 60 s idle and 60 s in a busy
   scene.
2. Find where frame time goes. If most of each frame is spent in `swap_ms`,
   faster managed code alone will not raise fps; the GL worker's samples show
   whether Mesa's CPU work (driven by draw calls) or the GPU is the limit.
3. Check `NX_GC` for collection frequency and pause time.
4. Check `sd:/tModLoader-Logs/client.log` for exceptions repeated during play.
   tModLoader's first-chance handler builds a full stack trace and writes a
   warning to the SD card for each one that is not on its ignore list.
5. Without rebuilding, compare Lighting = White and Retro against Color, and
   try `/mono/gc_params.txt` with `nursery-size=16m` and `32m`. The startup
   abort once seen with `nursery-size=16m` (build 66) was a runtime alignment
   bug, fixed in the runtime these builds link (`87f725bb`). It is untested
   with tModLoader on hardware, so watch memory.
6. Profile `--mods none` as well. It reuses `tmod27`'s compiled objects, so it
   builds quickly. It also uses the LLVM `tModLoader.dll`, so differences from
   the Fargo's Souls build mix mod cost with code generation.

## 1. LLVM-AOT `tModLoader.dll` in mod builds

Mod builds now compile `tModLoader.dll` with LLVM, as `tmod27` and `--mods none`
builds already did. The CLI used to drop it (`llvm_modules.discard` in
`terrabuilder_pkg/cli.py`, commit `b42e4ca`, commented as needing more than
16 GB and running out of memory next to other jobs). On this 15 GB machine the
step completes when nothing else runs: in `tmodcli-57f1894915cd` (2026-10-23)
LLVM compiled 60,127 of 60,155 methods, `opt` and `llc` took 25.6 minutes and
the whole build 38 minutes, the LLVM object is 124.6 MB, and the NRO shrank
from 1,059,922,182 to 1,034,653,958 bytes. Peak memory was not recorded, so do
not run the emulator or another build alongside it. Hardware fps with LLVM
`tModLoader.dll` has not been measured yet.

Evidence from the cached builds before the change:

- LLVM compiled 60,122 of 60,155 methods. Its code was 29.8 MiB, against
  43.1 MiB for the current non-LLVM object, which was built from a nearly
  identical assembly.
- Non-LLVM AOT of `tModLoader.dll` takes about 2.5 minutes. The LLVM step took
  at least 55 minutes: the log prints -1,003,423 ms, a 32-bit microsecond
  counter that wrapped. Peak memory was not recorded; this machine has 15 GB
  of RAM.
- Branch reach: AOT method tables must be within +/-128 MiB of all code.
  Projected from `tmod27`'s segment sizes, the current Fargo's Souls build uses
  about 110.8 MiB (17 MiB headroom); with LLVM `tModLoader.dll`, about 97.4 MiB
  (31 MiB headroom).
- Vanilla builds LLVM-compile both `Terraria` and `FNA`.

Before the change, an audit checked every performance patch and looked for
anything else worth at least 3 ms per frame in-world (run 38 profile, game
thread). Nothing new qualified. Two patches were removed:

- The `IsPlayerNearAPylon` rewrite built `List<T>::get_Item` with a concrete
  return type instead of `!0`, so the method never compiled. The minimap paid a
  failed interpreter transform every frame (2.5-3.1 ms), and opening the full
  map crashed the game.
- The `SceneMetrics.ScanAndExportToMain` 1-in-4 throttle also gated
  `TeleportPylonsSystem.HandleTeleportRequest` and `Player.ForceUpdateBiomes`,
  so pylon teleports were checked against stale scans. It saved 0.5-0.8 ms.

The largest remaining non-LLVM items are below the bar: `ChatManager`'s Regex
splitting (about 1.6 ms) and the legacy lighting cadence, which is a lighting
setting. Neither terraria-wasm (its one big lever was LLVM-based full AOT) nor
the old-gen console decompilation TerrariaOGC (text caching estimated at
2.5-3.2 ms before LLVM) offered more.

### Frame pacing with LLVM (run 39)

The first LLVM build ran 22 minutes in-world on hardware: median 42.9 fps
(34.1 in run 38), p10 33.5, p90 60. 52.6% of frames finished within 17 ms,
13.5% took 34-50 ms and 1.4% more than 50 ms. Game-thread profile, split as
described in section 0, with stalls located by
`nx-port-research/tools/long_frames.py` (gaps between buffer-swap samples):

- Every legacy lighting cycle (about four a second) stacked its work on one or
  two frames: `LegacyLighting.PreRenderPhase` 15-26 ms, of which the
  `SceneMetrics` scan was 10-19 ms, and `RenderTiles`, `RenderBlack`,
  `RenderTiles2` and `RenderWalls` 19-23 ms together.
- `TileData<T>.get_ptr` was never inlined (static constructor, not
  beforefieldinit): 7.9% of game-thread time, 23% of the `SceneMetrics` scan.
- Stalls of 0.8-1.8 s: achievement saves and player loads ran `NxCrypto` in
  the interpreter (it was not AOT-compiled for mod builds);
  `ChromaEngine.Update` blocked on the RGB worker's lock; opening Fargo's Souls'
  toggle menu; framing a world section on first visit.
- In the menu, Host & Play's password Submit threw on every frame and
  tModLoader's first-chance exception handler formatted each stack trace, which
  thrashed the GC down to 3-5 fps. Hosting is unsupported.

The next build AOT/LLVM-compiles `NxCrypto.dll`, skips the two
`ChromaEngine.Update` calls, marks `get_ptr` `AggressiveInlining`, refreshes
`RenderTiles2` and `RenderWalls` on their Color-lighting frames in every
lighting mode (walls can show lighting one cycle apart from tiles), and runs
the lighting cycle's `SceneMetrics` scan every second cycle (biome effects
update about every 0.5 s; teleports and `ForceUpdateBiomes` still scan every
time). The toggle menu, section framing and dock-change device resets are
unchanged.

### Run 40 (those changes) and threads

Run 40 (about 5 minutes in-world): p10/median/p90 41.9/46.1/58.4 fps; 6.6% of
frames took 34-50 ms and 0.7% more than 50 ms, half of run 39. `get_ptr` and
`ChromaEngine` are gone from the profile and the `SceneMetrics` scan fell from
4.0% to 1.4% of game-thread time. What remained:

- About 140 ms every 60 s: `MemoryTracking.InGameUpdate`'s RAM log reads
  `GC.GetTotalMemory` (patch 6), which walks the whole SGen major heap
  (`major_iterate_objects`). Patch 14 empties the method.
- `Tilemap.get_Item(int, int)` (`Main.tile[x, y]`), 3.1%: marked
  `AggressiveInlining`, but Mono's LLVM inliner rejects any method with a
  `throw`, and its bounds check throws. Patch 15 moves the throw to a
  `NoInlining` helper. In the v15 build's linked ELF, five hot callers make
  no `get_Item` calls (v14: 1 to 210 each, counted with objdump over the
  callers' address ranges from `native/.map`); each makes one call to the
  helper instead.
- `RenderTiles` (about 9 ms when it runs, `RenderBlack` inside it about 7 ms)
  still shares a frame each lighting cycle.
- One-off stalls: content loads after entering a world, `Preferences.Save` when
  closing the settings menu (0.7 s), first-visit section framing (0.5-0.7 s),
  saving on exit.

Threads (all registered threads, in-world): the game thread was 98% busy; Mesa's
glthread worker (on core 1, same settings as vanilla: `MESA_GLTHREAD`,
`MESA_SWITCH_GLTHREAD`, `MESA_SWITCH_BUFFERS=4`) 11%; one thread-pool worker
running Terraria's `FastParallel` lighting about 2%; everything else idle.
libnx creates every pthread at priority 0x3B with the whole process core mask,
so Mono's pool and GC threads can already run on cores 1-2; pinning them buys
little. Lighting is 14.3% of the game thread (300-430 s, each sample counted
once): gather (`TileLightScanner`, mods' `ModifyLight`) 5.3%, draw-time
`GetColor` lookups 4.4%, cycle copies 1.9%, biome scan 1.5%, propagation
(`DoColors`) 1.2%. The Xbox 360/PS3 version (TerrariaOGC) runs only the
propagation on a dedicated worker, a frame behind; here that saves about
0.26 ms per frame. Moving the gather too would save about 1.4 ms, but it reads
world tiles and runs mods' `ModifyLight` hooks, which today run on pool threads
only while the game thread waits; on a worker they would overlap world updates.
Not pursued.

### Run 41 (v15)

Run 41 (`logt40`/`proft40`, about 3 minutes in-world) against run 40, both in-world
only: median 45.8 fps (45.9), 34-50 ms frames 9.4% (7.6%), frames over 50 ms
0.3% (0.4%), 5 s windows with a frame over 100 ms 2.3 a minute (3.3, ignoring
world entry), none over 250 ms (one). Per frame, `Tilemap.get_Item` fell from
0.66 ms to 0 and the heap walk from 0.06 ms to 0; the session spent more on text
(font lookups and drawing +0.26 ms) and dust, so the average frame did not
shrink (21.95 ms against 21.25 ms). What is left:

- Five of the seven slow windows are first-visit section framing,
  100-155 ms each: `TileDrawing.GetScreenDrawArea` →
  `WorldGen.SectionTileFrameWithCheck` → `TileFrame`/`Reframe` on the game
  thread when the camera reaches unframed sections.
- `ChatManager.DrawColorCodedString` reparses its text with
  `ChatManager.ParseMessage`'s regexes on every draw: 0.66 ms per frame
  (`System.Text.RegularExpressions` 3.0% of the game thread).

## 2. Hook dispatch (`offline_hooks`): low priority

After its first call, lowered dispatch does not allocate: the `orig` delegate
is cached in a static field, chain links are created once, and the boxed chain
index costs one unbox per link. The dispatch code and cloned original bodies
are in `tModLoader.dll` and the handlers are in the mod assemblies; all are
AOT-compiled. `--interpret-hooks` leaves `TerrariaHooks.dll` interpreted. That
assembly defines the hook delegate types, so check in the profile whether
their `Invoke` calls fall back to the interpreter.

The Fargo's Souls set registers 52 method hooks, 2 IL edits (applied at build
time), and 4 runtime detours (`inventory-2.json`). Hooks that run every frame
or per entity:

- per frame: `Main.DrawNPCs`, `DrawProjectiles`, `DrawDust`,
  `DoDraw_DrawNPCsOverTiles`, `DrawInterface`, `CheckMonoliths`,
  `FilterManager.EndCapture`, `SoundEngine.Update`,
  `LegacyPlayerRenderer.DrawPlayers`;
- per player update: `GrantPrefixBenefits` (once per accessory),
  `CheckDrowning`, `WingFrame`, `DoCommonDashHandle`, `KeyHoldDown`;
- per projectile update: `Projectile.Damage`;
- IL edits: `Main.DoDraw` and `Player.Update`.

Act only if the profile shows dispatch or interpreter time under these methods.

## 3. tModLoader source hot paths: mostly small

Checked at `v2026.07.3.0`:

- NPC and projectile AI: a "no hooks" fast path would save almost nothing.
  `HookPreAI`, `HookAI`, and `HookPostAI` already enumerate per-type
  precomputed lists (`GlobalTypeLookups`, `EntityGlobalsEnumerator`), so an
  entity with no applicable globals costs three empty loops per update.
- Tile drawing: `TileLoader.PreDraw`, `DrawEffects`, and `PostDraw` run for
  each drawn tile. With no global tile hooks, each is an empty delegate-array
  loop plus a bounds check: a few thousand cheap calls per tile redraw, likely
  well under 1 ms per frame. Global tile draw hooks added by a mod would run
  per tile and show in the profile.
- Lighting scan: tModLoader calls `WallLoader.ModifyLight` from the wall-light
  step with no early exit, unlike `TileLoader.ModifyLight`, which returns early
  for tiles that do not emit light. That is up to one call per scanned tile;
  small, but an early return when no wall hooks exist is cheap.
- `FastParallel.For`: tModLoader replaces vanilla's spin-wait with
  `CountdownEvent.Wait(10000)` and reads thread-pool counters on every call.
  tModLoader never sets `ForceTasksOnCallingThread`, and Mono reports 3 CPUs
  on libnx, so lighting work is split onto pool threads unless vanilla code
  forces single-threaded mode. libnx creates every pthread, and so every Mono
  thread, at priority `0x3B` with all three application cores in its mask, and
  its `sched_yield` lets the thread migrate, so the workers can run on cores
  1-2. What stays unmeasured is how long a worker takes to wake. If the profile
  shows main-thread time blocked in `FastParallel`, test vanilla-style spinning.
- `NPCLoader.WriteExtraAI` and `ProjectileLoader.WriteExtraAI` allocate
  streams for extra-AI sync. Multiplayer only; low priority.

## 4. Lessons from other Switch ports

[sa2-nx](https://github.com/ChanseyIsTheBest/sa2-nx),
[usf4-nx](https://github.com/Thorhax/usf4-nx) and
[masseffect-nx](https://github.com/nebadasSwifty/masseffect-nx) are Xbox 360
games recompiled to native ARM64 with ReXGlue. They render through Mesa's
Vulkan driver (NVK) or, in sa2-nx from 1.0.3, deko3d. Their docs record each
optimization with console measurements. What transfers to this port:

### Ready in the next build

- **Draw census.** Debug and profiler builds add `draws/frame avg/max`
  (FNA3D draw calls) to the `NX_FPS` line. For scale, sa2-nx draws about 1,400
  a frame.

GPU clock changes are out of scope for this port, including the official
handheld profile (`0x92220008`, GPU 460.8 MHz) that sa2-nx and masseffect-nx
request. `appletSetCpuBoostMode` is unusable regardless: it throttles the GPU
to 76.8 MHz (masseffect-nx measured 3-4 fps).

### To run once measurements exist

- **SD-card logging.** tModLoader's log4net `FileAppender` keeps the default
  flush after every event, so each log line is a synchronous SD write on the
  thread that logged it. sa2-nx measured 50-150 ms per write while music
  streamed from the same card, and traced its worst stutters, up to a
  1-second frame, to logging. Terraria streams music from the NRO's RomFS on
  that same card. Count `client.log` lines per minute of play; if they are more
  than occasional, buffer the appender through the NxFix patcher and keep an
  immediate flush for errors.
- **GL driver cost: deko3d or Vulkan.** With glthread, Mesa's OpenGL driver
  work runs on core 1, and `swap_ms` includes waiting for it. Vulkan has never
  been tried in this port. The earlier vanilla work (workshop `terraria`
  branch) found:
  - Before glthread, the whole driver path was 15-20% of main-thread time
    (build 71). Moving it to the glthread worker gained about 4 fps (build 88).
  - mesa-switch 26.2.3's OpenGL driver lost to the patched 20.1: FNA3D's
    `BufferSubData` calls waited on the GPU, holding busy scenes at 29 fps
    (build 89m).
  - deko3d's GPU-side swapchain fence wait is already copied into the 20.1
    patch, which removed the earlier 27 fps lock (build 88).

  None of the three ports above measured OpenGL against Vulkan on the console.
  What they measured:
  - Vulkan (NVK) driver work: masseffect-nx about 4-5.5 µs per draw with the
    CPU overclocked to 1785 MHz, so roughly 7-10 µs at the stock 1020 MHz;
    banjo-nx 2.4-2.9 µs per draw and about 3 µs per pipeline bind.
  - deko3d: in sa2-nx's render thread the deko3d calls were 4.1% of the time
    and pipelines 2.2%; the rest was its own renderer. (Its 17.6 to 11.1 ms
    render-thread drop was measured on deko3d both times, from its own
    optimizations, not from leaving NVK.)

  For FNA, Vulkan would go through the SDL_GPU backend already in our FNA3D
  build, which needs SDL3's Vulkan backend built for the Switch and NVK from
  mesa-switch (Mesa 26.2). deko3d needs a new FNA3D backend, with MojoShader's
  GLSL compiled on the console by `uam` and cached on the SD card, as sa2-nx
  does. Either route moves driver work off glthread's core onto the thread
  that issues draws, unless the backend gets its own recording thread. Decide
  from the profile: the GL worker's busy samples (flag 8, 2.5 ms each) give its
  time per window, and dividing by the draws in the same window gives its cost
  per draw. If that worker is not what limits the frame, neither route helps.
- **PGO and LTO for native code.** sa2-nx gained about 15% from GCC PGO
  (`-fprofile-generate -fno-profile-values -fprofile-update=single
  -fprofile-info-section=gcov_info`, counts streamed to the SD card with
  `__gcov_info_to_gcda`, merged with `gcov-tool`, then `-fprofile-use
  -fprofile-correction -fprofile-partial-training`). masseffect-nx measured no
  gain from PGO or LTO, and usf4-nx a large one from LTO. Here it applies only
  to native code (Mesa, FNA3D, SDL, the Mono runtime), so try it only if the
  profile shows that code taking a large share of the frame.
- **Spin-waits on shared cores.** usf4-nx and sa2-nx gained by making GPU waits,
  frame limiters and job joins sleep, and usf4-nx found job workers starving its
  main thread. Look in the profile for time in `SpinWait`/`Thread.Yield`
  (`FastParallel`), Mesa fence waits, and FNA's frame limiter.
- **Probes.** masseffect-nx bounds an idea before building it by removing the
  work outright (for example, dropping every draw) and treating the result as a
  ceiling, never as a frame rate. Lighting = White/Retro is such a probe.

### Horizon facts confirmed there

- Only priority `0x3B` is time-sliced; at any other priority a busy thread keeps
  its core. The audio threads here already avoid this, and Mesa's GL worker
  runs at `0x2C` on core 1 above the `0x3B` Mono threads.
- A thread should set its own core mask: setting another running thread's mask
  can hang.
- Signalling a condition variable is a kernel call even with no waiter;
  newlib's `malloc` has one global lock; `thread_local` is a function call; GPU
  fence queries are system calls.
- Core 3 needs a custom loader descriptor; both projects that looked at it
  rejected it.

## 5. Interpreter optimizations: only if the profile shows interpreter time

Known interpreted code: `TerrariaHooks.dll`, plus methods that failed AOT (28
in `tModLoader.dll` and 49 in FNA; the logs do not name them).
Super-instructions and tiering stay disabled after an earlier Switch crash;
isolate that crash before enabling either. For tModLoader, the options live in
the cache's `main_tmod.c`.

## Withdrawn: vanilla-only input paths

The first version listed removing release frame hooks and caching the Switch
controller mapping. Neither runs in tModLoader builds: the shipped tModLoader
`FNA.dll` has no `NxInputDiag` references and calls
`SDL_GameControllerGetButton` directly. In vanilla they cost four empty
internal calls per frame and roughly tens of microseconds in `get_button`,
which does not justify a change.

For the same reason the vanilla Plus+Minus path in `begin_update` never runs in
tModLoader builds. They compile `nx_input.c` with `MONO_NX_SDL_BUTTON_WRAP` and
link with `--wrap=SDL_GameControllerGetButton`, which runs the same combo on
SDL's button state: while Plus and Minus are both held the game sees neither,
and each combo toggles `Main.showFrameRate`.

## Already present

- LLVM AOT for `tModLoader.dll`, FNA, `NxCrypto.dll` and the curated mod
  assemblies.
- `SuppressGCTransition` on the tModLoader FNA3D interop calls.
- Mesa GL threading, swapchain buffering, and `MESA_NO_ERROR`.
- Configurable libnx/Mono heap split and NV transfer memory.
- Runtime AOT dependency and MVID verification.
