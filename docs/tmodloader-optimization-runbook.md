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

- Installed loader: tModLoader `v2026.07.3.0` (tag commit `666f6996`) with the
  user's Terraria 1.4.4.9 or 1.4.5.x content (validated builds used GOG
  1.4.4.9). `prepare_modset.py` rejects mod packages built for
  any other loader version. The local source checkout
  (`/home/john_uzoka/tModLoader-src`, MIT) tracks the newer `1.4.4` branch,
  which does not contain that tag. The tag has been fetched, so read installed
  source with `git show v2026.07.3.0:<path>`.
- tModLoader builds compile their launcher from
  `scripts/tmod/launcher/main_tmod.c`, not `native/interpreter/source/main.c`,
  and link with `scripts/tmod/launcher/link-arguments.txt`.
- Code generation differs by build:

| Build | `tModLoader.dll` | Mod assemblies |
| --- | --- | --- |
| CLI, `--mods none` | LLVM, compiled per build, inline limit 60 | none |
| CLI, `--mods FargowiltasSouls` | LLVM, recompiled per mod set, inline limit 60 | LLVM, inline limit 60, `DisableOptimizations` cleared |
| `tmod27` (engineering build) | LLVM | Mono AOT without LLVM |

- NxFix runs in the CLI's patch stage (`scripts/tmod/base_dlls.py`) on the
  console-patched release `tModLoader.dll`, into `<workdir>/tmod/dlls-<key>/`.
  The stage key covers the `scripts/tmod/nxfix/` sources, so changing them
  rebuilds it and every variant. To run it by hand:

  ```sh
  python3 -c 'from pathlib import Path; from scripts.tmod.base_dlls import build_base_dlls
  W = Path.home() / ".cache/terrabuilder"
  print(build_base_dlls(W / "tmodloader/2026.7.3.0", Path("/tmp/dlls"), dotnet=W / "toolchain/dotnet/dotnet",
                        cecil=W / "toolchain/cecil/Mono.Cecil.dll", tools=W / "tools"))'
  ```

  It runs NxFix on the console-patched original, never on an earlier NxFix
  output (the validated tmod09 RomFS's `tModLoader.dll` was one, with an
  audio-disable stub). The mandatory stock-reference check expects nine invalid
  `ResetColor` pops; an already-patched input has zero and is rejected before
  output is written.

- Known numbers: vanilla reaches 48-53 fps with Color lighting and about 60 fps
  with White lighting (starting area, default handheld clocks). With glthread
  (vanilla build 88) the GPU sits idle for about 45% of each frame, so vanilla
  is CPU-bound. tModLoader with Fargo's Souls ran about 20-24 fps in `tmod26`
  and 15-19 fps in `tmod27`; the engineering notes put the drop down to audio
  being enabled in `tmod27`, unprofiled. The current CLI build has not been
  measured. Source for the earlier builds: `fna-nx-switch-findings.md` on the
  workshop repo's `terraria` branch.

## 0. Measure before changing code

`scripts/tmod/launcher/main_tmod.c` never calls `nx_profiler_start()`, so for
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
6. Profile `--mods none` as well. It compiles `tModLoader.dll` with the same
   LLVM settings as mod builds, so differences from the Fargo's Souls build are
   the mods' cost.

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

### v16 (run 42)

v16 (`tmodcli-aa09ff4822e6`) targets what run 41 left. Shares below are of the
run-41 game thread (375-560 s, 67,478 samples, 21.95 ms average frame).

- **Chat text (NxFix patch 17, `TextCachePatch.cs`).** Most of the cost was not
  `ParseMessage`: `DrawColorCodedString(TextSnippet[])` runs `Regex.Split`
  itself, per snippet for lines and per line for words, and each of those
  replaces a split whose result is never used. Every `*WithShadow` draw runs it
  five times. That was 4.9-6.3% (hotbar stack counts 3.1%, gamepad hints 1.8%,
  Fargo's music banner 1.0%). The unused splits are gone and the line split is
  a helper that returns the same array. `ParseMessage` (1.4%) memoizes only its
  `Format` regex scan per text (512 slots, texts up to 512 characters); it still
  builds new snippets and calls the tag handlers on every call. Expected: about
  6-7%, 1.3-1.6 ms per frame.
- **Lighting-cycle black (patch 16, `LightingCyclePatch.cs`).** `RenderBlack`
  (2.3%, about 4.7 ms per 195 ms cycle) runs on the idle renderCount 4 frame
  instead of with `RenderTiles` when `RenderPhases > 4` (`gfxQuality` below
  0.8), and blackTarget is drawn at its own capture position. The average frame
  does not change; one spike per cycle shrinks.
- **Section framing (patch 18, `SectionFramingPatch.cs`).** `TileDrawing.Update`
  frames the loaded, unframed section nearest the drawn area (within 100×75
  tiles) for at most 1.5 ms per drawn frame. Run 41's five 88-156 ms
  first-visit stalls become about 60-105 frames of up to 1.5 ms each. Tile and
  wall framing hooks still run, only earlier.
- **Debug-built mods.** Fargo's Souls, Fargowiltas and Luminance ship as Debug
  builds (`DebuggableAttribute` modes `0x107`, which include
  `DisableOptimizations`), so Mono inlined nothing in them, AOT included:
  Fargo's `Alive(Projectile)` alone was 0.97% as an out-of-line call.
  `prepare_modset.py` now runs `mod_tuning` first, which clears the flag (new
  MVID, repacked package).
- **Inline limit 60.** Under LLVM AOT, Mono inlines callees below 30 IL bytes.
  Out-of-line managed callees of 30-59 IL bytes held 4.4% (FNA's
  `Vector2.op_Addition` is 36, `Lighting.Brightness` 46, the `TileLoader` hook
  wrappers 36-57) and 60-99 bytes another 2.3%. The CLI compiles every module
  it builds with `MONO_INLINELIMIT=60`; inlining saves the call overhead, a
  fraction of those shares. In the v16 ELF, 3,675 `tModLoader.dll` methods grew
  and 1,469 shrank (its LLVM code went from 25.7 to 26.6 MB, Fargo's Souls'
  from 9.1 to 9.6 MB), AOT failures are unchanged (28 in `tModLoader.dll`, 49
  in FNA) and the NRO grew by 5 MB.
- **Fargo's "music mod not found" banner.** `NoMusicNotification.Update` held
  its timer at 200 until the banner was clicked, and drawing it cost about 2%
  for as long as it stayed. `mod_tuning` lets the countdown run, so the banner
  dismisses itself after 5 s.

Checked and left alone:

- Vanilla build 52's hint-layout skip: the layout work it could skip is 0.19%,
  and a safe font guard would leave about 0.05%. The hint's cost is its drawing,
  which patch 17 covers.
- Vanilla build 50's draw gate: in tModLoader the preparation it could skip
  costs 0.07%; moving the gate further would skip `GetFinalLight` and
  `TileLoader.DrawEffects`.
- FNA3D `SuppressGCTransition`: tModLoader's FNA already has all 16 vanilla
  entries. The largest remaining transition, `FNA3D_SetVertexBufferData`
  (which can block on a busy buffer), is 0.08%.

Ryujinx run v16a (about 5 minutes in-world, walking both ways): the mods loaded,
the world rendered, Fargo's banner faded in and dismissed itself, chat and
gamepad-hint text drew correctly, and nothing was logged as an exception. Its
game-thread profile shows the new paths running: every `RenderBlack` sample
comes through `NxRenderBlackNow`; nothing in `Regex` runs under
`DrawColorCodedString` or `ParseMessage` (every parse hit the cache); and after
world entry, sections were framed in `NxPreframe` slices (396 samples over
about 100 s) with a single on-demand `SectionTileFrame` sample. Emulator timings
say nothing about hardware speed.

Run 42 (`logt41`/`proft41`, 16.8 minutes of complete in-world NX_FPS windows):
median 54.6 fps (run 41: 45.8); 34-50.5 ms frames 1.70% (9.3%); frames over
50.5 ms 0.07% (0.46%); nine 5 s windows with a frame over 100 ms, worst 876 ms.
The 1603 ms frame in that log straddles world entry (content loads plus a major
GC), not gameplay. Audio was off: see v17.

### v17

- **Audio.** Every build from run 39 to v16 was silent. NxFix had been run on
  `tmod09/romfs/tModLoader.dll`, which is itself an early NxFix output whose
  `SoundEngine.TestAudioSupport` is a stub that logs "Switch port: audio
  disabled" and returns false (`logt38` onward show it; `logt27`-`logt37` show
  `SDL Audio Driver: switch`). NxFix now runs on the console-patched original
  (`tmod02/input/tModLoader.dll`) and requires `--reference`: the stock
  assembly's nine `ResetColor` calls must match the nine pops it repairs, so an
  already-patched input is rejected. The stub is rejected by name too. The same
  mix-up had applied the content-root and touch patches twice; the clean input
  removes that. NVorbis and MP3Sharp are now LLVM-AOT compiled: Fargo's Souls
  ships 38 Ogg and 15 MP3 sound effects, and both decoders ran interpreted.
- **Face buttons (patch 19, `SwitchPadMappingPatch.cs`).** SDL's built-in Switch
  mapping is positional (`a:b1,b:b0,x:b3,y:b2`), so tModLoader swapped A/B and
  X/Y and every prompt named the other button. `PlayerInput.Initialize` now
  installs the vanilla build's label-correct mapping (`a:b0,b:b1,x:b2,y:b3`) and
  logs `NX Switch mapping install result=N`. Stick dead zones and bindings were
  already identical to vanilla; tModLoader keeps separate controller profiles
  under `/switch/tmodloader`.
- **Mod loading (patches 20-21).** In run 42, 73.7 s of the 127.5 s mod load was
  `TmodFile._VerifyHash`: SHA-1 of every `.tmod` in the interpreted
  `System.Security.Cryptography`, read in 4 KB chunks. `TmodHashPatch.cs` adds an
  AOT-compiled SHA-1 with 1 MiB reads; digests and pass/fail decisions are
  unchanged (checked against .NET's SHA1 on block and buffer boundaries, short
  reads, the three real packages and a one-byte corruption). `UnloadGcPatch.cs`
  skips `AssemblyManager.Unload`'s ten forced full collections when no mod was
  ever loaded (2.2 s on first load; reloads keep them). Remaining load costs:
  newlib malloc free-list walks and lock waits (about 14 s), interpreted
  MetadataLoadContext/Deflate/localization (about 10 s), SD reads (8 s) and the
  asset lock (7 s). CPU boost during loading only would cut roughly 40%; it is
  not used.
- **Remaining run-42 hitches (no patch).** Over 100 ms: synchronous XNB/LZX
  content loads (396 ms), settings and achievement saves (370 and 232 ms),
  first-use ambience code going through the interpreter (131 ms), and the
  background world save: `FastParallel` lighting waits plus a newlib
  allocator-lock stall in a buffer upload (876 and 515 ms). Three windows fall
  in profiler sampling gaps. No major GC occurred in any of them. Pylon range
  checks on the map (2.76% of the game thread, draw side) are the largest
  remaining steady cost without a proven safe fix.
- **Optional mods.** The catalog adds Recipe Browser, Boss Checklist, Where's My
  Items, Cheat Sheet, HERO's Mod, Magic Storage with SerousCommonLib, and
  AlchemistNPC Lite, all untested on hardware. Magic Storage needed recorded
  patches: SerousCommonLib's `DynamicMethod` accessors and cloner become
  reflection, its `UnsafeAccessor` Guid reads become `Guid.TryWriteBytes`, the
  stateful IL manipulators become static ones, and the craft buttons accept the
  game's click as well as the raw mouse. The offline tools now key runtime
  detours per registration site, take mod scopes from the inventory, match
  overloaded IL-hook targets by signature, and run IL edits inside
  `ILContext.Invoke` as MonoMod does on PC. That last change re-encodes short and
  long branches in two Fargo-edited methods; nothing else in the Fargo set
  changed. Panels that read `Mouse.GetState()` need touch, and mod text boxes
  cannot receive text (run v17c below).

Ryujinx run v17a (Fargo's Souls set, `tmodcli-ea9b095d38e0`): FNA logged `SDL
Audio Driver: switch` and the launcher opened the audio device (48 kHz stereo,
1024 frames); there was no "audio disabled" line. `NX Switch mapping install
result=0`, and Switch Y now opens the inventory (before, it was lock-on). Mod
load took 336 s against v16a's 501 s on the same emulator. The harness cannot
capture host audio (Ryujinx's SDL2 backend never opens the disk device). It
also cannot test controller-only UI, because each key also reaches the guest as
a keyboard key, which switches Terraria to keyboard UI mode.

Ryujinx runs v17b and v17c (all 11 mods, `tmodcli-9940735198fd`): mod load
took 524 s and 587 s, and both runs reached the world with no new errors in
`client.log`. The container's memory peaked at 13.96 GB, above its 13 GB limit on
this 15 GB host, so it swapped and frames stalled for up to 16.6 s. tModLoader
leaves mod hotkeys unassigned until they are set in Settings > Controls, so v17c
seeded keyboard bindings into the input profile. With them, the Boss Log, Boss
Checklist (listing Fargo's bosses), Recipe Browser, Where's My Items and Cheat
Sheet's hotbar opened. HERO's Mod's Item Browser opened by touch, and Cheat
Sheet's hotbar also opens from its tab at the bottom of the screen. At 1280x720,
an expanded HERO's Mod toolbar or Cheat Sheet hotbar covers the inventory's
Settings button and Boss Checklist's Boss Log button. Typed letters never
reached a mod text box (Where's My Items, HERO's Mod): devkitPro SDL's Switch
keyboard driver sends key events but no text, and the launcher keeps the system
keyboard closed, so mod searches cannot be typed, with or without a USB
keyboard. Magic Storage and AlchemistNPC Lite loaded but were not exercised in
the world.

Profiler deadlock (fixed in v17e): in runs v17a-v17d (v17d: the Fargo's Souls
set rebuilt before the fix) the sampler stopped right after the audio threads
registered, and those threads never logged `NX_AUDIO
thread ... -> core 2` or ran the mixer loop (no `NX_AUDIO renderer waits`
lines), so those runs mixed no audio. The pthread wrapper registers every new
thread for sampling; `nx_audio`'s mixer and FACT API threads then move
themselves to core 2 with `svcSetThreadCoreMask`. Ryujinx's
`KThread.SetCoreAndAffinityMask` holds the thread's `_activityOperationLock`, a
host lock, while it waits for the thread to reach the new core, and
`SetActivity` takes the same lock. The sampler on core 2 blocked on it without
giving up core 2, so the moving thread never got there. Horizon's lock puts the
waiter to sleep, so hardware should not deadlock (not verified), but the race is
now closed on both: the moving thread calls `nx_profiler_remove_thread`, which
waits out a sample of it in progress, before moving, and re-registers after. In
v17e (Fargo's Souls set, `tmodcli-87c6f4b3d8f9`) and v17f (all 11 mods,
`tmodcli-c6f058401f0c`) both threads logged their move (rc 0), the mixer loop
ran, sampling continued to the end of the run, and the world loaded; mod load
took 309 s and 464 s. Raising the sampler from 0x2B to 0x29 was not the fix; it
stays so the 0x2A mixer does not delay samples. In v17f one sample's
`pause_us` max wrapped to 9.6e17: Ryubing's guest counter
(`TickSource.ElapsedTicks`) updates static fields without synchronization, so it
can step backwards when threads read it at once. Hardware counters cannot.

On hardware, use `tmodcli-4bb0b6c7efbe` (Fargo's Souls set) or
`tmodcli-063dacebf95c` (all mods), built after run 43 (below). `mono/log.txt`
should show `NX_GC params=max-heap-size=...` from the launcher default, then
`NX_AUDIO thread mixer -> core 2` and `api -> core 2`, then `NX_AUDIO renderer
waits` every 2000 waits. Compare against run 42: mod load time, audio on all
three sound paths (XACT music, SoundEffect, Ogg/MP3 mod sounds), and the
frame-time cost of audio, which run 42 could not measure. Against run 43, check
frame times with all 11 mods and that autosaves past 20 minutes complete. With
the optional mods, also check Magic Storage (deposit, withdraw, craft with touch
and gamepad A, save and reload) and AlchemistNPC Lite's shops, which the
emulator runs did not reach.

### Run 43 (v17, all 11 mods)

Run 43 (`logt42`/`proft42`, `tmodcli-c6f058401f0c`): `logt42` also holds two
shorter launches (log lines 1-1842: the Fargo's Souls NRO; 1843-3764: the
all-mods NRO with only the Fargo packages on the SD card). In the full run
(lines 3765-6785), the 11 mods loaded in 103.2 s, both audio threads logged
their move to core 2, and the profiler sampled all 61 minutes.

- **Frame drops.** In-world (220-1430 s): 40.4 fps, 5.7% of frames over 50 ms,
  and a frame over 100 ms in all 241 five-second windows. The same NRO with only
  the Fargo packages (lines 1843-3764) ran 53.4 fps with 0.3% over 50 ms. Magic
  Storage's and SerousCommonLib's runtime detours on `TileLoader.NearbyEffects`,
  which runs for every tile scanned around the player and around pylons, were on
  the game-thread stack in 22% of samples, in 82 runs of 50 ms or more per
  minute (longest 1.68 s). Every one of those samples had interpreter frames:
  the lowered-detour adapter bound each mod's `orig` delegate to the next link's
  `orig.Invoke`, and a delegate over another delegate's `Invoke` runs in Mono's
  interpreter, so each `orig` call entered it. The adapter now binds the mod's
  delegate to a compiled `LinkN.Call` that calls `Orig.Invoke`
  (`RuntimeDetourLowering.cs`). Fargo's Souls' three runtime detours and
  Luminance's one used the same path.
- **Out of memory.** During the second autosave (about 20 minutes in-world),
  "Saving modded world data" threw `OutOfMemoryException`. The managed region
  (`heap_split.c`, 37.5%) was 1,128 MB. SGen held 840 MB after world load and
  971 MB after the first autosave; no major collection ran between world entry
  and the failure, and the two at the failure left 584 MB. Without
  `max-heap-size`, SGen schedules the next major collection a third above what
  survived the last one (`SGEN_DEFAULT_ALLOWANCE_HEAP_SIZE_RATIO`), which after
  world load lies beyond the region. The native side peaked at a 1,496 MB arena
  of 1,886 MB. Unless `/mono/gc_params.txt` exists, the launcher now sets
  `max-heap-size` to the region minus 160 MB (968 MB for run 43's region),
  `soft-heap-limit` 256 MB lower, and synchronous sweeping (checks below). The
  Fargo packages alone reached 810-908 MB (lines 1843-3764), so that set was
  exposed too.
- **Freeze.** After the exception, the game thread unwound into FNA's
  `Game.Dispose` and waited on a mutex in `eglMakeCurrent` for the remaining 37
  minutes; a Mono thread waited on newlib's malloc lock, and the GL worker was
  idle. The samples do not show which thread holds the lock. Open; the heap
  settings remove this trigger.

Ryujinx checks (all 11 mods, Ryujinx's managed region 1,164 MB), heap settings
from `/mono/gc_params.txt` unless noted:

- v17g crashed Ryujinx itself (SIGSEGV, no guest error) during mod loading; v17h
  of the same build reached the world. In v17h, interpreter frames under Magic
  Storage's `NearbyEffects` chain fell from 65% of its samples (v17f) to 3.4%.
- `max-heap-size=720m`: world loading ran out of memory in
  `WorldFile.LoadWorldTiles`; the world's tile arrays do not fit under 720 MB.
- `max-heap-size=880m` (v17j): the first autosave ran out of memory although
  collections 140 and 141 ran at the cap. The heap fell from 840 to 551 MB only
  after the sweep finished, five seconds later, so the allocation that forced
  the collection still failed.
- `max-heap-size=880m,soft-heap-limit=748m,no-lazy-sweep,no-concurrent-sweep`
  (v17k): the same autosave wrote `Ryu.twld` without error.
- Built-in default (v17l, `tmodcli-063dacebf95c`): `NX_GC
  params=max-heap-size=1004m,soft-heap-limit=748m,no-lazy-sweep,no-concurrent-sweep`;
  the autosave completed, with six major collections during it and one in the
  ten minutes of play before it.

Mod load took 497-543 s under every setting, within Ryujinx's run-to-run spread.
v17n (`max-heap-size=880m` with synchronous sweeping and no soft limit) crashed
Ryujinx itself (SIGSEGV) during mod loading, like v17g; it decided nothing.

### Run 44 (v17, all 11 mods)

Run 44 (`logt43`/`proft43`, `tmodcli-063dacebf95c`): one 40-minute session. The
11 mods loaded in 92.6 s; three autosaves and the save on exit completed without
error, so the run 43 out-of-memory fix held.

- **Frame rate.** In-world (222-2354 s): 33.2 fps, 3.6% of frames over 50 ms;
  37.5 fps (1.4%) up to 1,200 s, 29.6 fps (6.0%) after. Draw calls per frame
  (180-1,023 per two-minute window) did not follow the decline.
- **Saves froze the game.** In-world, 42 major collections took 28 s, mostly in
  clusters of 5-12 around the four saves, about 0.67 s each and up to 2.7 s. The
  default soft limit (712 MB, 256 MB below the 968 MB cap) sat below the
  860-910 MB SGen kept allocated in-world. Above the soft limit SGen allows
  16 MB of growth (four nurseries) between majors and runs each one forced, that
  is serially (`sgen-memory-governor.c`, `sgen_need_major_collection`). Outside
  the saves a major ran every few minutes; minor collections took 0.4-0.8% of
  the time.
- **Game thread, self time.** Flat: no function above 3%. Legacy lighting 7.2%;
  FNA graphics, FNA3D and Mesa's command marshalling 6.7% (SpriteBatch alone
  5.5%); font drawing 3.9%, including the glyph dictionary
  (`Dictionary<char, SpriteCharacterData>`, 1.4%) and the card-table barrier
  its struct copies pay (0.9%); runtime transitions and TLS reads around
  P/Invokes 3.5% (`FACTCue_GetState` the largest caller at 0.8%);
  condition-variable waits 2.7% (62% of them Mesa's glthread fence wait); IPC
  2.0% (presentation and GPU ioctls); `Player.IsTileTypeInInteractionRange` 1.4%
  (the minimap's pylon check); `Dust.UpdateDust` 1.1%. Classified by the
  innermost `Main.DoDraw` or `DoUpdate` frame, 46% of samples are Draw, 27%
  Update and 27% neither.
- **What grew late.** Minutes 24-36 against 6-12 (30.1 against 23.6 ms of
  game-thread samples per frame): audio rose from almost nothing to about 0.9 ms
  per frame (`FAudioSourceVoice_GetState` 0.25 ms, `FAudioVoice_SetOutputMatrix`
  0.18 ms, `ActiveSound.Update` 0.12 ms, FAudio's mutexes and GC transitions),
  the pylon check from 0.26 to 0.46 ms, and dust, projectile, tile-collision,
  gore and item-stacking updates by 0.1-0.2 ms each: more of everything in the
  scene rather than one runaway cost.
- **Scanned stacks mislead.** `analyze_prof.py`'s inclusive table put 24.5% under
  Fargo's Souls' `WorldUpdatingSystem.PostUpdateWorld` via
  `ModContent.NPCType<T>`. 99% of those samples had middle frames elided and the
  chain ran on into newlib's `_vsnprintf_r`; their leaves were vanilla world
  updates and lighting. Use self time and the innermost few frames.
- **AOT gaps.** Nine methods failed AOT on access checks (`IgnoresAccessChecksTo`,
  which Mono's runtime honours but its AOT compiler does not): Magic Storage's
  `BaseStorageUI.OnInitialize`, Fargo's Souls' world-icon lookup and seven
  `SerousCommonLib.UI.Layouts.LayoutManager` methods. They run interpreted;
  interpreter self time was 0.7% of the game thread.

### v18

- **Fewer collections at saves (reverted, see runs 45 and 46).** The v18
  builds set the default `soft-heap-limit` 64 MB below the cap instead of
  256 MB: 904 MB with run 44's 968 MB cap, so majors would be about 64 MB of
  promotion apart instead of 16 MB and, below the soft limit, not forced. On
  hardware that let world loading run out of memory; the source default is back
  to 256 MB.
- **Pylon map tint (patch 22, `PylonMapPatch.cs`).** The minimap's
  `IsPlayerNearAPylon` walk (0.46 ms of each frame late in run 44) now reruns at
  most once per 30 updates for the icons' tint. Clicking an icon still runs
  `HandleTeleportRequest`'s own check.
- **Sound setters (patch 23, `SoundSetterPatch.cs`).** `ActiveSound.Update`
  passes Pan, Volume and Pitch to FAudio only when the instance's value
  differs, instead of three P/Invokes per sound per frame. Pan is always sent
  on an ActiveSound's first update, when `Play` has just left FAudio's default
  matrix for a centred sound.
- **Switch keyboard (`nx_input.c`, `MONO_NX_SWKBD`).** Every text box in
  tModLoader, vanilla and the curated mods sets `PlayerInput.WritingText` while
  focused. When an FNA event drain ends and the flag has turned on, the launcher
  shows the full-screen keyboard (`swkbdShow`; the game thread waits), waits for
  the buttons and touch screen to be released, and returns the text as
  `SDL_TEXTINPUT` events, at most as many characters per tick as Terraria's
  10-key buffer (`Main.keyCount`) has room for. One keyboard per focus: the flag
  has to stay off for 20 drains before it can open again. The mod list, mod
  sources, mod browser and achievements screens, whose filter boxes are active
  the whole time they are open, never open it. Cancel types nothing. After a
  long keyboard session FNA catches up at most 0.5 s of updates
  (`Game.MaxElapsedTime`).

Ryujinx run v18a (all 11 mods, `tmodcli-82062d959ebf`, Ryujinx's region 1,164
MB): `NX_GC params=max-heap-size=1004m,soft-heap-limit=940m,...`. After the
major collection at world entry, none ran through the first autosave, which saved
the world and its mod data (v17l, same world: six majors during the save). Where's
My Items focuses its box when its hotkey opens it; the launcher logged
`NX_SWKBD closed after 0.1 s: rc=0x00000000, 7 bytes to type` (headless Ryujinx
answers every keyboard with "Ryujinx") and the box showed that text. The
harness cannot measure the frame-time effect of patches 22 and 23.

Ryujinx run v18b (Fargo's Souls set, `tmodcli-5886048d34a8`): mod load took
286 s; both audio threads moved to core 2. Opening the in-game Achievements
screen, whose search box is always active, logged `NX_SWKBD not opened:
UIAchievementsMenu keeps its filter box active`. Neither run logged a new
exception. Taps in this harness need a press of about a second: a 0.15 s press
moved the cursor but did not click, so the harness's `click` command now holds
1.2 s.

On hardware, the v18 builds are `tmodcli-82062d959ebf` (all mods) and
`tmodcli-5886048d34a8` (Fargo's Souls set). Both need `/mono/gc_params.txt`
with run 44's values (below); builds from the current source default to them.

### Runs 45 and 46 (v18 on hardware)

- **Run 45 (`logt44` first upload, `tModLoader-Logs44`).** Both launches were
  the Fargo's Souls set's NRO (1,136-1,138 MB region). Both builds produce
  `tmodloader.nro`, so one copied over the other. In the second launch the
  other mods were turned on in Manage Mods; each reload failed and tModLoader
  disabled them: Boss Checklist and Recipe Browser with `hook not lowered at
  build time` (`UpdateTime_StartDay`, `AdjTiles`), SerousCommonLib and Magic
  Storage with a `TypeLoadException` on
  `TerrariaHooks.Lowered.On_Terraria_ModLoader_TileLoader`. AlchemistNPC Lite and
  Cheat Sheet loaded interpreted (215 s). After several reloads, world loading
  threw `OutOfMemoryException` with 623 MB in use (cap 976 MB). Manage Mods
  logged `NX_SWKBD not opened: UIMods ...` each time it opened.
- **Run 46 (`logt44` second upload, `tModLoader-Logs44b`).** The all-mods NRO
  (1,128 MB region, `max-heap-size=968m,soft-heap-limit=904m`) started with five
  mods enabled, six were turned on in Manage Mods, and the eleven reloaded in
  187 s. Loading Healthy Ring (the world run 44 loaded with the same mods)
  threw `OutOfMemoryException` in `WorldFile.LoadWorldTiles` with 499 MB in use:
  SGen's allocation went from 620 MB to 917 MB within five seconds of
  "Loading world data", with one (non-forced) major collection. In run 44 the
  same load ran from 672 MB with eight forced majors and peaked at 899 MB. The
  fatal-error screen then crashed in Mesa's GL worker (`nvc0_clear` ->
  `nvc0_state_validate_3d`, crash report `01793390931`).
- **Cause and fix.** Above the soft limit every major is forced and serial,
  which is what kept run 44's world load under the cap; with the 904 MB soft
  limit, garbage from the load burst built up until an allocation reached the
  cap. The Ryujinx checks passed because Ryujinx's region is 36 MB larger
  (1,004 MB cap). The default gap is back to 256 MB. For the v18 NROs,
  `/mono/gc_params.txt` holds `max-heap-size=968m,soft-heap-limit=712m,
  no-lazy-sweep,no-concurrent-sweep`, run 44's values. Delete the file when
  moving to a build from the current source: its cap is fixed to the 1,128 MB
  region.

### Run 47 (v18 with run 44's GC values, all 11 mods, stress test)

Run 47 (`logt44c`/`proft44c`, `tModLoader-Logs44c`, `tmodcli-82062d959ebf`
with `/mono/gc_params.txt`): the 11 mods loaded in 91.2 s, Healthy Ring loaded,
and after 8.5 minutes in-world the game saved and exited normally. From about
460 s to 650 s the player spawned crowds of enemies in a graveyard.

- **Keyboard on hardware.** Three uses, all as designed: 4 bytes typed after
  12.2 s, OK with an empty box after 2.3 s, and Cancel after 2.7 s
  (`rc=0x00005d59`, nothing typed).
- **Frame rate.** 34.9 fps from 240 s to 440 s; 17.6 fps from 460 s to 650 s,
  with 20-55% of frames over 50 ms per 30-second window. Game-thread samples per
  frame went from 25.8 to 51.1 ms. Two major collections (0.67 s each) fell in
  the stress window.
- **What the crowds added (ms per frame).** `Projectile.VanillaAI` +1.7;
  `Dust.NewDust` +1.3, 88% of it called from projectile AI;
  `FAudioSourceVoice_GetState` +1.1, 78% of it from
  `SoundInstanceGarbageCollector.Update`; graveyard wind
  (`AmbientWindSystem.TestAirCloud` and `TrySpawningWind`) +1.0;
  `Projectile.Update` and `Projectile.Damage` +1.1; `Dust.UpdateDust` +0.6;
  tModLoader's global-hook enumeration +0.5; legacy lighting about +1.3; tile
  and liquid collision about +0.9; FAudio mutexes and P/Invoke transitions about
  +1.0. Most of it is vanilla entity work that scales with the number of
  enemies and projectiles; the dust, sound and wind items are fixed costs per
  spawn, per tracked sound and per tile.

### v19

Patches 24-26 target the fixed costs run 47's crowds exposed.

- **Dust slots (patch 24, `DustSlotPatch.cs`).** `Dust.NewDust` walked
  `Main.dust` from slot 0 to the first inactive dust; with thousands alive the
  walk was most of its 1.3 ms per frame. `Dust._nxFirstFree` is now a lower
  bound on the first free slot: NewDust starts there and stores the slot where
  it stops, and every other `Dust.active` write in tModLoader (`UpdateDust`,
  `CloneDust`, `Main.DrawDust`, `Main.DrawCapture`) goes through
  `Dust.NxSetActive`, which lowers the bound when it frees a slot. Mods can
  clear the flag inside `UpdateDust` (AlchemistNPC Lite's `AlchFlask.Update`),
  so `UpdateDust` resets the bound to 0 at each return. NewDust picks the slot
  the full walk would and draws `Main.rand` the same way; a mod that frees a
  dust anywhere else only makes NewDust skip that slot until `UpdateDust` next
  returns.
- **Sound instance collection (patch 25, `SoundCollectorPatch.cs`).**
  `SoundInstanceGarbageCollector.Update`, one FAudio state query per tracked
  `SoundEffectInstance` (0.86 ms per frame in the crowds), runs every eighth
  update. A stopped instance is disposed up to seven updates (about 0.12 s)
  later.
- **Graveyard wind (patch 26, `AmbientWindPatch.cs`).** In a graveyard
  `TestAirCloud` drew `_random.Next(120000)` for each of 3,600 tiles per update
  (0.62 ms per frame). `AmbientWindSystem.NxAirCloudTrial` draws the number of
  calls until the next success (geometric, p = 1/120,000) from the same
  `_random` and counts down: airborne clouds keep their rate, only the tiles
  that get them change.
- **GC defaults (unchanged).** The cap is 160 MB below the managed region and
  the soft limit 256 MB below the cap: run 44's 968/712 MB on run 46's 1,128 MB
  region. Delete `/mono/gc_params.txt` before running v19.

The tModLoader.dll AOT image compiles 60,048 of 60,076 methods in the all-mods
build and 59,954 of 59,982 in the Fargo's Souls set: two more than v18 in each
(`NxSetActive`, `NxAirCloudTrial`) and the same 28 left to the interpreter.

Ryujinx run v19a (all 11 mods, first v19 build `tmodcli-9db41c95baa9`, no
`gc_params.txt`): `NX_GC params=max-heap-size=1004m,soft-heap-limit=748m,...`
for Ryujinx's 1,164 MB region; mod load took 498 s. In the world, Where's My
Items' box got "Ryujinx" from the keyboard (`NX_SWKBD closed after 0.1 s:
rc=0x00000000, 7 bytes to type`), the player walked and jumped, and 90 s later
the world was still running. No new exception was logged, and the game-thread
profile has 283 in-world samples in the patched `Dust.UpdateDust`.

Ryujinx run v19f (Fargo's Souls set, first build `tmodcli-190682ff90b1`): ten
minutes into the world, `Main.DrawDust` (called through Luminance's
`ParticleManager.DrawParticles` hook) threw `MethodAccessException`:
`NxSetActive` was private to `Dust`, and `Main` is another type. Mono's AOT code
throws only when such a call runs, and DrawDust clears a dust only when it is
off-screen or lit pure black, which never happened in v19a. `NxSetActive` is
now internal, and NxFix's `VerifyPatched` rejects a patched method that
references a private member of another type (with the private helper, NxFix
stops at `Main.DrawDust`).

The first Fargo's Souls build's AOT code calls `mono_throw_method_access` from
`Main.__orig_DrawDust` and `Main.DrawCapture` (one call each); the rebuilt
tModLoader images have no PLT entry for that icall. Ryujinx runs with the
rebuilt NROs: v19g (Fargo's Souls set, `tmodcli-f9275a0576fd`), mod load 299 s;
v19h (all 11 mods, `tmodcli-e8615abf5253`), mod load 502 s, keyboard as in
v19a. Neither logged a new exception (only the usual
`PlatformNotSupportedException` from `MemoryTracking.Finish` during mod load).

On hardware, the v19 builds are `tmodcli-e8615abf5253` (all mods) and
`tmodcli-f9275a0576fd` (Fargo's Souls set). Neither harness scene had crowds or
a graveyard, so the frame-time effect of patches 24-26 needs a stress run like
run 47.

### Run 48 (v19, all 11 mods)

Run 48 (`logt45`/`proft45`, `tModLoader-Logs45`): the all-mods v19 NRO. The
profile resolves against `tmodcli-e8615abf5253`'s symbols (libnx waits as
`svcWaitProcessWideKeyAtomic`; v18's symbols turn them into `eventClose`).
`/mono/gc_params.txt` was still on the card; its 968/712 MB are v19's defaults
for the 1,128 MB region. The 11 mods loaded in 90.4 s and Healthy Ring loaded;
after about four minutes in-world the game saved and exited normally.

- **Frame rate.** 40.2 fps from 185 s to 412 s of the `NX_FPS` clock (profile
  time is that plus 11.6 s), 1.9% of frames over 50 ms. Run 47, same world and
  mods, before its stress test (keyboard windows removed): 35.9 fps.
- **Game thread.** 22.61 sampled ms per frame against run 47's 24.88 (the
  sampler catches about 91% of samples, so wall time is about 10% more). About
  0.2 ms of the difference is v19's code (`FAudioSourceVoice_GetState` 0.05 to
  0, the sound collector 0.09 to about 0, `TestAirCloud` down to 0.3x
  `TrySpawningWind`); the rest is the scene: 493 draw calls a frame against 697
  (waits, IPC, FNA and Mesa -1.4 ms), two major GCs in the window against five,
  fewer projectiles, and Cheat Sheet's UI closed. With frame skip on, a drawn
  frame carried 1.5 updates (run 47 crowds: 3.45).
- **GC.** Two major collections in-world (0.66 s each); five during the save.
- **Errors.** None new: the client log has only the known Process, OpenSSL and
  `Libraries/Native/Linux` exceptions. No `MethodAccessException`.
- **White tiles while moving (reported).** Most likely patch 16. Quality = Auto
  had `RenderPhases` at 7-8 (black renders a median 211 ms apart), so 1,851 of
  1,861 `RenderBlack` samples came through `NxRenderBlackIfDue`, one frame after
  `RenderTiles`. On each lighting cycle's tile frame (about 4.7 times a second)
  the new tile and wall layers sit over the previous cycle's black layer: a tile
  or opaque wall that just went dark is no longer drawn and nothing covers it, so
  the background shows for a frame. Against the sky or a lit tunnel mouth that
  is a white flash. Test without a rebuild: Quality = High fixes `RenderPhases`
  at 4, where patch 16 renders black with the tiles. Fixes: render black with
  the tiles again (same 0.52 ms average, a 5 ms longer tile frame 4.7 times a
  second), or double-buffer the tile, tile2 and wall layers and swap them on the
  black frame (no CPU cost, about 22 MB of GPU memory, changes show one frame
  later). Not changed: the user prefers frame rate to fixing the flash. A
  rarer vanilla cause: `ShiftUnProcessedDataOver` skips shifting the light map
  when the camera moves more than 5 tiles between drawn frames.

Crowd levers left after v19, from run 47's crowd window (ms per frame; each ms
is worth about 1 fps in that scene, and v19's three fixes should take it from
about 17.4 to about 21 fps, unmeasured):

| Lever | ms/frame | Player-visible effect | Size |
| --- | --- | --- | --- |
| Sound state: `SoundPlayer.Update`/`ActiveSound.Update` query each sound's state 2-3 times per update and `Play` checks `IsPlaying` on every tracked sound; query once per update | 2.9 (save 1.2-1.8) | none, or a finished sound freed up to 4 updates later | small-medium |
| Projectile vs NPC: tModLoader runs `CanHitNPCWithProj` and `Colliding` hooks for every pair before the rectangle test; pre-test rectangles for types without a `Colliding` override | 2.7 (save 1.5-2) | none unless a mod's `CanHitNPC` has side effects | medium |
| Lightning: in a graveyard or storm `UpdateWeather` calls `Lighting.LightTiles` twice per update while a flash lasts | 1.63 | lightning reaches tile light at the normal cycle; the sky still flashes | tiny |
| Toxic clouds (`aiStyle` 92): every cloud walks all 1,000 projectile slots per update; keep a per-update list | 1.16 | none | small-medium |
| Graveyard floor wind: scan a quarter of the 3,600 tiles per update at 4x the chance | 0.4 | none noticeable | small |
| Dust cap below 30 fps (e.g. 1,500 active) | part of 1.08 | fewer particles | small-medium |

No cheap lever: NPC updates (3.29 ms, Fargo's Souls' global NPC 0.50), tile
collision (2.16), items on the ground (2.13), major GCs. `Projectile.VanillaAI`,
`NPC.VanillaAI_Inner` and `Player.Update` are compiled by Mono's non-LLVM
backend. Settings: Lighting = Retro is likely slower (`Lighting.UpdateEveryFrame`
redraws tiles, walls and black every frame), Quality = Low gains nothing (Auto is
already at 0 in crowds), and Frame Skip Off raises fps but slows the game to
about half speed in that scene.

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

Runtime detours (MonoMod `Hook`s lowered by `--lower-mod-detours`) entered the
interpreter on every `orig` call until run 43 (see above): the adapter passed
each mod a delegate over the next link's `Invoke`. Adapters now pass a delegate
to a compiled `LinkN.Call`. In Ryujinx, interpreter frames under Magic Storage's
`NearbyEffects` chain fell from 65% of its samples (v17f) to 3.4% (v17h). The
interpreter frames still under Luminance's `NPCLoader.SetDefaults` detour are in
tModLoader's generic `SetDefaults<TEntity, TGlobal>` (global instances created
per NPC spawn), below the compiled `orig`.

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
`scripts/tmod/launcher/main_tmod.c`.

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
