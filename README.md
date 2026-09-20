# fna-nx-test

FNA-on-mono-nx integration test and Terraria Switch-port development workspace.
The original graphics demo remains below; current Terraria work is experimental.

## Current Terraria iteration (2026-09-18)

The port now reaches Terraria menus/worlds on earlier hardware builds. The
performance/input comparison NROs are in `terraria-mono/switch/`:

| File | Purpose |
| --- | --- |
| `mono_nx_fna_terraria_nochroma33.nro` | User confirms missed-button fix; consistent menu-entry skipping deferred |
| `mono_nx_fna_terraria_nochroma34_aot.nro` | Hardware: native AOT entrypoint works, then Mono data-table assertion before graphics |
| `mono_nx_fna_terraria_nochroma35_aot_glcompat.nro` | Hardware: same runtime failure as 34; GLCompatibility not reached |
| `mono_nx_fna_terraria_nochroma36_aot.nro` | Hardware: previous switch-table failure passed; later EventPipe/exception initialization crash |
| `mono_nx_fna_terraria_nochroma37_aot.nro` | Hardware: tracing fix passed; OpenGL/NV120 initialized; CoreLib AOT specific pool exhausted at 4096 |
| `mono_nx_fna_terraria_nochroma38_aot.nro` | Working hardware baseline: near-full-speed menus, user-reported 1–5 FPS gameplay rendering |
| `mono_nx_fna_terraria_nochroma39_aot.nro` | Hardware: quiet logging and ES/glsles3 confirmed; still slow |
| `mono_nx_fna_terraria_nochroma40_aot_glcompat.nro` | Hardware: GL4.3 Compatibility/glsl120 confirmed; user reports slightly faster but still slow |
| `mono_nx_fna_terraria_nochroma41_aot_glcompat.nro` | Prior hardware baseline: ~13.5 sustained/~15.9 later Draw calls/sec, ~60 in menus |
| `mono_nx_fna_terraria_nochroma42_aot_glcompat.nro` | Current hardware baseline: ~17.9 combined/~18.8 early steady world Draw calls/sec, ~60 menus; normal shutdown |
| `mono_nx_fna_terraria_nochroma43_aot_profile.nro` | Hardware-verified profiler:69 complete intervals; solid tiles14.57ms/frame, UI6.78ms normal/17.06ms inventory; not a clean FPS benchmark |
| `mono_nx_fna_terraria_nochroma44_aot_tile_profile.nro` | Hardware-verified:78 reports/9,556 Draws; solid setup/loop/post0.018/13.556/0.468ms across3,930 normal-world frames; no clean FPS claim |
| `mono_nx_fna_terraria_nochroma45_aot_tile_cost_profile.nro` | Hardware-verified:100 reports/12,167 Draws; sampled solid allocation1.362us and lighting/frame2.262us per call; significant observer/log overhead, no speedup claim |

41 removes the growing timing-history overhead; 42's narrower compiler
workaround improves measured gameplay again. Rendering now accounts for about
66% of measured steady Tick time and Update33%.43 now identifies solid-tile
drawing and UI as the next source targets; see the [hardware profiler results](../fna-nx-switch-findings.md#build-43-hardware-profiler-results-2026-09-18).
AOT uses the existing mono-nx SDK, not a replacement runtime or full JIT.

**42 is hardware-tested and is the current working baseline.** Keep Frame Skip
On, existing SD runtime DLLs, ICU, saves, bindings and `/mono/config.ini` unchanged.
The observed world Draw-rate increase over41 is about32% across selected live
intervals, not an identical-scene controlled benchmark. Menus remain near60.
**43's profiler works on hardware;42 remains the non-profile performance baseline.**
It matched all9,500 native Draw calls and reported no storage/registry/report
failures. Solid-tile drawing costs14.57ms/frame including0.475ms batch flush;
UI costs6.78ms normally and17.06ms with inventory. Reports add periodic overhead.
**44's tile profiler works on hardware.** Its capture has ample normal-world
data despite a different timing sequence. The solid loop accounts for96.54%
of the three measured tile regions; setup is small. See the
[hardware tile results and next source target](../fna-nx-switch-findings.md#build-44-hardware-tile-results-2026-09-18).
**45's focused profiler works on Switch.** Allocation/init is measurable but
not the whole bottleneck; lighting/frame helpers cost more in the selected
calls. The remainder includes observers, and one report took3.90 seconds.
See [hardware operation costs and the risk-gated next step](../fna-nx-switch-findings.md#build-45-hardware-operation-costs-2026-09-18).
For performance comparisons, keep `runtime_logging=false` and `logging=true`.
Check `NX_CONFIG` actually reports `runtime_logging=0` as it did in39–45.
`NX_PHASE` retains `poll` and `swap` counts/times. They are inclusive and may
overlap: use Tick minus Update minus Draw for the residual, not blind subtraction.
Build 37's EventSource capability is separate from `runtime_logging`; its
startup log should include `NX_RUNTIME EventSource disabled: native EventPipe is unavailable`.
The consistent menu-entry skipping reported in 33 is documented and deferred,
as requested; the confirmed input-buffering fix is retained.

See [the workshop findings](../fna-nx-switch-findings.md) for the L4T baseline,
corrected b10/b11 mapping, compiler/linker fixes, early dependency metadata,
reproduction commands, and hardware test checklist. Exact checksums and the
verification boundary are in `terraria-mono/build-verification.json`.

`scripts/patch_time_logger/run.py` builds and exercises the guarded statistics
patcher against the reviewed staged Terraria/FNA images. It verifies behavior,
preservation, reproducibility and rejection of unsupported/already-patched input.
The findings include exact input/output hashes and fresh-directory commands.
`scripts/patch_aot_inlining/run.py` validates the build-42 metadata-only cutover:
two desktop-window caller flags plus MVID, with every other game byte unchanged.
The resulting game compiles without the assembly-wide no-inline workaround.
`scripts/patch_frame_profile/run.py` builds/verifies43's observer hooks and
target-compatible signatures. Only stage its accepted Terraria.exe, never host
fixture DLLs. Reports separate time metrics from counters and diagnose excluded
partial/repeated/reset-dirty samples; see the findings for interpretation.
`scripts/patch_tile_profile/run.py` accepts only the reviewed43 input and produces
44's accepted observer image. Completed region timings include observer cost;
sampled DrawSingleTile time is not a measured or extrapolated total. Its host
fixtures execute the full original/patched Draw IL;44 now also produces valid
tile metrics on Switch. Sampled call time is not GPU time or a full-call total.
`scripts/patch_tile_cost_profile/run.py` performs the guarded44-to45 patch and
full serialized-method acceptance. `scripts/analyze_tile_profile.py` validates
NX_PROFILE logs and emits cohort/operation summaries without extrapolating
samples. Optional baseline comparisons are descriptive, not automatic speedup
claims; use matched scenes and equivalent instrumentation to judge a change.
The [RAL source/provenance review](../fna-nx-switch-findings.md#ral-source-review-and-provenance-boundary-2026-09-18)
pins actual buffer-upload changes and licenses, credits inherited code, and
records unresolved legacy helper origins. No RAL source is added in44.

The setup below describes the original FNA demo/installer baseline. Do not
use the raw packer to overwrite a validated Terraria RomFS or substitute a
freshly rebuilt FNA.Core assembly. Current persistent tooling is in
`scripts/patch_fna/`, `managed/nx_input_diag/`, and
`scripts/compile_terraria_aot.py`.

The retained input regression check runs without a Switch SDK:

```sh
mkdir -p "$HOME/.cache/terraria-switch-build"
cc -std=c11 -Wall -Wextra -Werror -O2 native/tests/test_nx_input_latch.c \
  -o "$HOME/.cache/terraria-switch-build/test-nx-input-latch"
"$HOME/.cache/terraria-switch-build/test-nx-input-latch"
```

## Current development process

The current, verified development pipeline is iterative rather than a complete
retail-files-to-NRO application:

1. Preserve original game files and previous working builds. Work on separate,
   reviewed staging copies of supported managed assemblies.
2. Apply guarded IL/metadata patches that check expected inputs and method
   shapes, preserving unrelated code/resources. Later patchers consume the
   reviewed, already-patched stages—not arbitrary retail files.
3. Compile the exact accepted managed bytes to ARM64 AOT objects and embed those
   same managed bytes, required dependencies and game assets in RomFS.
4. Link the AOT objects with the native runtime/FNA stack into an NRO. Verify
   hashes, dependency identities and native bindings, then use Switch hardware
   checks and profiling feedback to guide the next change.

**Performance first, with a discussion gate:** multiplayer testing and mod-loader
bring-up are deferred. Raise potential protocol, simulation, gameplay, save-state,
vanilla lifetime or mod-compatibility impacts before applying a tradeoff. Existing
mods need not all work unchanged: significant gains or modest adaptations may
justify breakage, but a large mod/loader overhaul for a small gain is not wanted.
The user decides the concrete tradeoff; potential speed alone is not approval.
See the [optimization escalation gate](../fna-nx-switch-findings.md#current-decision-performance-first).

See [how game edits reach the NRO](../fna-nx-switch-findings.md#how-game-edits-reach-the-nro)
for the detailed process and verification boundary. The raw
`scripts/pack_terraria_romfs.py` recreates staging; it does **not** orchestrate
all current patches or select the current AOT build configuration.

## Provisional bring-your-own-files workflow

This is a proposed destination, **not current end-user software**. Final UX,
the supported-version/storefront matrix and packaging are still being worked
out; current tooling is not a complete end-user app or automatic dependency installer.

- Supply your own supported, legally obtained game files locally; keep originals intact.
- Have the tool validate the version/input files and reject unsupported combinations.
- Apply compatible patches and build personal Switch output locally with the required toolchain.
- Copy that output to the Switch SD card, keeping saves and configuration separate from replaceable build output.
- Project distribution should provide tools, runtime components, patch logic and required third-party notices—not Terraria executables or assets. This does not establish redistribution clearance for every runtime binary.

## SDL2 vs SDL3 (important)

Terraria 1.4.5+ uses SDL3. SDL3 has no Switch homebrew port — it's NDA-gated
for licensed developers only. However, **FNA ships both SDL2 and SDL3 backends
in the same DLL**, selected at runtime via `FNA_PLATFORM_BACKEND=SDL2`.

The interpreter's `main.c` sets this env var before launching managed code,
forcing FNA to use `SDL2_FNAPlatform.cs` and `SDL2#` bindings, which resolve
to devkitPro's SDL2 via the dl_shim. FNA3D is built with `-DBUILD_SDL3=OFF`.

This means Terraria 1.4.5's managed assemblies can run on the SDL2 backend
without modification. The managed code calls FNA, not SDL directly — it does
not know or care which SDL version is underneath.

## What this does

1. Builds FNA3D, FAudio, SDL2#, and FNA itself as static libraries for libnx
2. Generates dl_shim entries from the compiled libraries' exported symbols
3. Builds a custom mono-nx interpreter NRO with FNA's native deps linked in
4. Runs a tiny C# program that clears the screen and draws a moving texture

## Prerequisites

- Your existing mono-nx Docker environment (devkita64 + dotnet 9 SDK)
- The mono-nx SDK extracted in `mono-nx/dotnet_runtime/` and `mono-nx/icu/`
- A modded Switch with hbmenu, tested in **full application mode** (title takeover)

## Build steps

Run these inside the mono-nx Docker container:

```bash
# 1. Build native dependencies (FNA3D, FAudio, SDL2-CS) as static libs
cd /mono-nx/fna-nx-test/scripts
./build_native_deps.sh

# 2. Generate dl_shim entries from compiled symbols
./gen_dl_shim.sh

# 3. Build the custom interpreter NRO with FNA linked in
cd /mono-nx/fna-nx-test/native/interpreter
make

# 4. Build the C# test app
cd /mono-nx/fna-nx-test/managed/fna_test
dotnet build

# 5. Package SD card files
cd /mono-nx/fna-nx-test/scripts
./package_sd_files.sh
```

## One-file Terraria packaging

FAT32 makes copying Terraria's tens of thousands of individual Content files
impractical. `scripts/pack_terraria_romfs.py` embeds the user's GOG payload in
the NRO's RomFS, so the SD card receives one approximately 751 MB NRO instead
of 15,997 asset files.

Run inside the mono-nx devkitA64 container:

```bash
cd /mono-nx/fna-nx-test
python3 scripts/pack_terraria_romfs.py
```

The default source is `~/FNA-Game/Terraria/game` on the host when mounted into
the container. Override it with `--game-dir /path/to/game`. The packer keeps
only `Terraria.exe`, `FNA.dll`, `FNA.dll.config`, and `Content/` by default;
it removes Windows `:Zone.Identifier` sidecars, framework DLLs, Linux `.so`
files, server binaries, and installer metadata. Optional legacy assemblies
can be added with repeated `--include-legacy-dll` flags.

Output:

```text
dist/terraria/mono/mono_nx_fna_terraria.nro
dist/terraria/mono/config.ini
```

Copy only those two files to `SD:/mono/` (rename the NRO if desired). Keep
`lib_net9.0/`, `framework_net9.0/`, `etc/icudt77l.dat`, and `log.txt` outside
the NRO. `config.ini` is deliberately external so logging and launch paths
remain editable during development.

## Output files

After building:

```
fna-nx-test/
├── native/interpreter/
│   └── mono_nx_fna.nro          ← Custom runtime host with FNA linked
├── managed/fna_test/bin/Debug/net9.0/
│   └── fna_test.dll             ← Minimal FNA test program
└── sd_files/
    ├── mono/mono_nx_fna.nro
    └── switch/fna_test/fna_test.dll
```

Copy `sd_files/` contents to your SD card root and launch via hbmenu in
full application mode.

## What success looks like

The screen clears to a color and a small texture moves across it using
controller input. If you get a black screen, check `/mono/log.txt` for
exception traces — the most likely failure is FNA3D's OpenGL backend
failing to initialize on switch-mesa.

## What failure at this stage tells you

If FNA3D cannot create a device on switch-mesa, the entire Terraria
port approach stops here. You would need to investigate whether the
OpenGL driver supports the required GL version and extensions before
investing further.
