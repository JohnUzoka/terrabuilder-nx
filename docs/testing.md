# fna-nx-test

FNA-on-mono-nx integration test and Terraria Switch-port development workspace.
The original graphics demo remains below; current Terraria work is experimental.

## Current Terraria iteration (2026-09-20)

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
| `mono_nx_fna_terraria_nochroma42_aot_glcompat.nro` | Retained clean hardware control: historical~17.9 combined/~18.8 early steady world Draw calls/sec, ~60 menus; normal shutdown |
| `mono_nx_fna_terraria_nochroma43_aot_profile.nro` | Hardware-verified profiler:69 complete intervals; solid tiles14.57ms/frame, UI6.78ms normal/17.06ms inventory; not a clean FPS benchmark |
| `mono_nx_fna_terraria_nochroma44_aot_tile_profile.nro` | Hardware-verified:78 reports/9,556 Draws; solid setup/loop/post0.018/13.556/0.468ms across3,930 normal-world frames; no clean FPS claim |
| `mono_nx_fna_terraria_nochroma45_aot_tile_cost_profile.nro` | Hardware-verified:100 reports/12,167 Draws; sampled solid allocation1.362us and lighting/frame2.262us per call; significant observer/log overhead, no speedup claim |
| `mono_nx_fna_terraria_nochroma46A_aot_control.nro` | Clean42-equivalent hardware control and rollback;52 is now the working performance baseline |
| `mono_nx_fna_terraria_nochroma46B_aot_scratch_reuse.nro` | Hardware-tested experiment, **not promoted**: no demonstrated speed benefit; user noticed no visual/update difference |
| `mono_nx_fna_terraria_nochroma47_guarded_light.nro` | Hardware-tested experiment, **not promoted**: no noticed visual difference; matched-label windows show no useful demonstrated gain |
| `mono_nx_fna_terraria_nochroma48_ui_trace_off.nro` | Hardware-tested diagnostic cleanup; entry messages removed and normal shutdown, but no consistent gameplay FPS gain established; **not promoted** |
| `mono_nx_fna_terraria_nochroma49_tile_helpers.nro` | Hardware measurement passed:65 valid packets/7,617 frames; stationary solid loop14.92ms/frame; individual helpers/state captured; **measurement only, not a speedup build** |
| `mono_nx_fna_terraria_nochroma50_late_draw_gate.nro` | Retained reference/rollback: original hardware pair observed16.11→17.08 Draw/s (+6.04%), Draw wall−5.11%; extended by52 |
| `mono_nx_fna_terraria_nochroma51_ui_profile.nro` | Hardware measurement passed:96 valid packets/11,776 frames; steady UI6.90ms closed/14.16ms inventory; no deaths/observer failures; **measurement only, profiled50** |
| `mono_nx_fna_terraria_nochroma52_guarded_hint_layout.nro` | **Working baseline**: stationary50/52 retest19.19→20.64 Draw/s (+7.57%), Draw wall−6.81%; later label-free check+7.37%; no logged deaths, normal exits |
| `mono_nx_fna_terraria_nochroma53_render_profile.nro` | **Hardware measurement passed**:80 valid packets/15892 frames; stationary solid loop14.566ms, batch1.117ms, upload0.149ms, indexed submit0.410ms; no deaths/observer faults;52 stays baseline |
| `mono_nx_fna_terraria_nochroma54_stack_state.nro` | **Hardware tested; parked by user, not adopted**: no-death A2/B2 retest19.37→20.03 Draw/s (+3.37%), Draw wall−1.96%; matching setup/no problems confirmed. Gain is modest and item-label logging differs.52 remains baseline |
| `mono_nx_fna_terraria_nochroma55_recipe_profile.nro` | **Hardware measurement passed; not a speedup**:137 packets/18,103 reconciled frames,602 valid selected frames, no deaths/observer failures. Steady no-hover UI13.515ms, inventory5.731ms, recipe2.600ms; hover adds3.448ms outside inventory.52 stays baseline |
| `mono_nx_fna_terraria_nochroma56_update_profile.nro` | **Hardware measurement valid; not a speedup**:127 packets/38,258 reconciled Updates,505 valid samples. Stationary night item updates0.302→2.545ms/Update explain89.49% of measured Update growth; late death excluded. Worldgen stays near1.3–1.4ms.52 remains baseline |
| `mono_nx_fna_terraria_nochroma57_light_value.nro` | **Unadopted52 comparison candidate; hardware pending**: one Lighting.GetColor value-local rewrite, all calls/initialization preserved. ARM64 stack144→96B/code488→428B; host timing inconclusive, no FPS claim.192,309 native targets/16,183 files verified; no runtime DLL update |
| `mono_nx_fna_terraria_nochroma58d_release_bcl.nro` | **Boots; short A/B +21% Draw/s vs 52 (not adoption-grade)**: exact52 game on a Release CoreLib/framework (optimized IL, no Debug.Assert). CoreLib at `romfs:/mono/lib_net9.0` (no inlining), framework at RomFS root; search path `romfs:/mono/lib_net9.0;romfs:/`. 58a–58c failed at startup (shadowed facades; SD-root `/` sent framework loads into Terraria's resolve handler mid-cctor). **Copy only the NRO, leave SD runtime DLLs** |
| `mono_nx_fna_terraria_nochroma58e_release_bcl_inline.nro` | **Boots; current candidate**: 58d with CoreLib default inlining. Short A/B matches 58d (17.51 vs 17.33 Draw/s; 52: 14.32). 166,966 native targets verified, no RWX |
| `mono_nx_fna_terraria_nochroma59_linq_collections_aot.nro` | **Superseded by 59b**: crashed on world selection after exhausting CoreLib's 512-entry IMT trampoline pool |
| `mono_nx_fna_terraria_nochroma59b_linq_collections_aot.nro` | **World loading and normal exit confirmed**: 59 with larger CoreLib trampoline pools. `log59b.txt`: 296.6 s, no repeat exhaustion; settled 105.4 s timing block 24.70 Draw/s and 60.02 Updates/s. No matched control or scene-state markers; not a measured speedup or baseline adoption |
| `mono_nx_fna_terraria_nochroma60_llvm_terraria.nro` | **Superseded by 60b before hardware testing**: original LLVM-Terraria candidate with the same small trampoline pools as 59 |
| `mono_nx_fna_terraria_nochroma60b_llvm_terraria.nro` | **Measured gain: +37% vs 58e** in an alternating same-spot A/B (frameskip off, stationary): 35.0/35.4 vs 26.8/24.6 Draw/s; Draw 23.5 vs 32.5 ms, Update 4.1 vs 5.7 ms. 58e + LLVM-compiled Terraria + larger CoreLib pools |
| `mono_nx_fna_terraria_nochroma61_llvm_fna.nro` | **Boots; promising, not proven**: 59b + LLVM FNA. 26.4 Draw/s vs 59b's 20.1 in the same session, but not alternated, and 59b itself measured slower than 58e there |
| `mono_nx_fna_terraria_nochroma62_llvm_terraria_fna.nro` | **Best measured build: ~+6% over 60b** (alternating same-spot A/B, ~43% over 58e): 60b + LLVM-compiled FNA. Boots, no errors; Draw cheaper (21.8 vs 23.6 ms), Update unchanged. RomFS identical to 60b; no RWX/TEXTREL |
| `mono_nx_fna_terraria_nochroma63_llvm_a57.nro` | **Tested: no measurable gain over 62** (`logD1`–`logD4`): 62 with LLVM tuned for the Cortex-A57. D4 (63) 41.3 Draw/s = D1 (62) 41.3; the two 62 runs differ by 3 Draw/s. One 63 run (D2) sat at exactly 30.0 Draw/s, not repeated in D4. Keep 62 |
| `mono_nx_fna_terraria_nochroma64_llvm_corelib.nro` | **Boots; loads faster; gameplay unproven** (`logE1`–`logE4`): 62 + LLVM CoreLib (74,748 of 80,517 methods). Startup stall 7.9 vs 10.2 s, world-load stall 3.7 vs 4.3 s, both 64 runs agree. Gameplay: E4 38.4 vs 62's 36.3/35.9 Draw/s, but E2 sat near 30 |
| `mono_nx_fna_terraria_nochroma65_release_runtime.nro` | **Superseded by 67; crashes on exit**: 64 + Release native runtime. Gameplay equals 64 (Color: 47.9 vs 48.0 Draw/s). Exit abort `unregister_thread: info` = libnx TLS destructor bug, fixed in 67 |
| `mono_nx_fna_terraria_nochroma66_gc_params.nro` | **Superseded by 67; `nursery-size=16m` aborts at startup**: 65 + `/mono/gc_params.txt` + `NX_GC` stats. Fake-mmap alignment bug, fixed in 67. (Without a params file it has 65's exit crash) |
| `mono_nx_fna_terraria_nochroma67_runtime_fixes.nro` | **Exit fix confirmed** (3 clean exits in the GC test): 66 with two runtime fixes (fake-mmap absolute alignment, POSIX TLS destructor semantics). GC test: keep SGen defaults |
| `mono_nx_fna_terraria_nochroma68_gcstats_nettrace.nro` | **Tested**: clean exit; settled 26.6 Draw/s, GC stop-the-world 0.47%. Multiplayer: 2,164 instant `ENETUNREACH` connects, then `StackOverflowException` |
| `mono_nx_fna_terraria_nochroma69_netfix.nro` | **Next hardware test; host verified**: 68 + interpreter stack-unwind fix (retry overflow reproduced and fixed on host) + non-blocking NIFM network request. Use for a multiplayer join |
| `tmodloader02_zeromod.nro` | **Superseded**: 1.4.5.8 content; same startup bugs as 03 |
| `tmodloader03_144content.nro` | **Crashed on hardware** (`logT.txt`): native stack overflow after OpenGL init. Cause: invalid IL from our offline patches (see findings). Superseded by 04 |
| `tmodloader04_ilfix.nro` | **Crashed on hardware** (`logT4.txt`): got past 03's crash, then overflowed the 1 MB main-thread stack in `NPCID.Sets..cctor`. Superseded by 05 |
| `tmodloader05_bigstack.nro` | **Crashed on hardware** (`logT5.txt`): stack fix worked; then fatal "Audio device already open" in XACT setup. Superseded by 06 |
| `tmodloader06_noaudio.nro` | **Reached the menu on hardware** (`logT6.txt`); mod loading then failed on `Process.GetCurrentProcess()` in tModLoader's memory diagnostics and left a popup with no controller navigation. Superseded by 07 |
| `tmodloader07_modload.nro` | **Works on hardware to the main menu**: mod loading completes; menus and settings work with the controller. Character creation can't be navigated with the controller. Superseded by 08 |
| `tmodloader08_touch.nro` | **Touch works on hardware**; character creation completes, but saving the player fails: `Algorithm 'Aes' is not supported`. Superseded by 09 |
| `tmodloader09_saves.nro` | **Next tModLoader test; host reaches the main menu**: 08 + player files saved/loaded with the vanilla port's managed AES (`NxCrypto.dll`, byte-identical to .NET AES). Goal: create a character and world, enter the world |

41 removes the growing timing-history overhead; 42's narrower compiler
workaround improves measured gameplay again. Rendering now accounts for about
66% of measured steady Tick time and Update33%.43 now identifies solid-tile
drawing and UI as the next source targets; see the [hardware profiler results](findings.md#build-43-hardware-profiler-results-2026-09-18).
AOT uses the existing mono-nx SDK, not a replacement runtime or full JIT.

**52 is the working baseline;50 is retained for reference and rollback.** Keep Frame Skip
On, existing SD runtime DLLs, ICU, saves, bindings and `/mono/config.ini` unchanged.
The earlier42-versus41 world Draw-rate increase was about32% across selected live
intervals, not an identical-scene controlled benchmark. Menus remain near60.
**43's profiler works on hardware;42 was the non-profile baseline for those measurements.**
It matched all9,500 native Draw calls and reported no storage/registry/report
failures. Solid-tile drawing costs14.57ms/frame including0.475ms batch flush;
UI costs6.78ms normally and17.06ms with inventory. Reports add periodic overhead.
**44's tile profiler works on hardware.** Its capture has ample normal-world
data despite a different timing sequence. The solid loop accounts for96.54%
of the three measured tile regions; setup is small. See the
[hardware tile results and next source target](findings.md#build-44-hardware-tile-results-2026-09-18).
**45's focused profiler works on Switch.** Allocation/init is measurable but
not the whole bottleneck; lighting/frame helpers cost more in the selected
calls. The remainder includes observers, and one report took3.90 seconds.
See [hardware operation costs and the risk-gated next step](findings.md#build-45-hardware-operation-costs-2026-09-18).
**46B is not being promoted.** Both runs completed, but the captures show no
worthwhile reuse gain. Workloads differ, so this is not a precise causal slowdown
measurement. Keep46A/control or clean42 and return to the measured lighting/frame
and UI targets. The experiment/proof is retained, not silently made the baseline.
See [the hardware A/B outcome and limitations](findings.md#build-46-hardware-ab-outcome-2026-09-18).
**47 is not being promoted.** New `logA.txt`/`logB.txt` runs completed normally;
the user noticed no visual difference and moved early in B. With that portion
excluded and label-getter logging matched, short windows give37.134ms Draw in
A versus37.124ms in B. Broader matched-label checks remain below1% difference;
different death/respawn periods and A's much heavier label diagnostics confound
longer comparisons. Keep46A/control or clean42, not an accumulated47 baseline.
The confirmed UI label/reflection diagnostic-formatting path remains the next
isolated candidate; no logging/runtime patch or new NRO was made by this analysis.
See [the47 hardware outcome and limits](findings.md#build-47-hardware-ab-outcome-2026-09-20).
**48 is not promoted as a performance baseline.** The fresh pair is
`log46A1.txt`/`log48B.txt`; both completed normally. A emitted6,462 item-label
GetValue entry messages and B emitted none, so the intended cleanup worked.
B's shelter/death interval is excluded from the long quiet comparison. The first
quiet minute favors B (16.30→17.09 Draws/sec), but the last favors A
(17.73→16.17); whole quiet blocks give17.23 versus16.70 with unequal durations.
World-state drift and higher B Update cost prevent a clean causal speed verdict.
Keep46A/control or clean42; retain48 as an experiment, not an accumulated baseline.
See [the48 hardware outcome and limits](findings.md#build-48-hardware-outcome-2026-09-20).
**49 completed its hardware measurement and informed50 below.** The
`log49.txt` capture has65 valid packets,7,617 completed frames and3,722 eligible
world frames, with normal shutdown and no invalidity/capture/report failures.
Stationary solid-loop cost is14.92ms/frame; GetColor/GetTileDrawData are nearly tied
at1.18/1.16µs per measured operation. This led to the unused-preparation audit
and isolated50 candidate. The late gate was bypassed by83% of sampled solid calls,
not83% of world tiles, and that frequency does not establish recoverable time.
Keep46A/42 as the performance baseline;49's observers can affect frame rate.
See [the49 hardware measurements and next target](findings.md#build-49-hardware-measurements-2026-09-20)
and [measurement contract/proof](findings.md#build-49-focused-tile-helper-measurement-2026-09-20).

**50's original hardware A/B was accepted;52 now extends it.** Only the existing
late false gate moves ahead of unused rectangle/position setup; particles/RNG,
black/special/shroom-cap rendering and scratch allocation remain unchanged.
Host proof passed500 checks/100 scenarios, including all entries/returns and
geometry/effects/lifetime. Native relocation,192,304 method bindings and16,183
embedded files were verified. No extra profiler, lighting-quality reduction,
runtime replacement or accumulated46B/47/48/49 change is included.
The event-aligned steady comparison used23 complete windows per run:16.1062→
17.0786 Draw calls/sec (+6.04%),38.7043→36.7269ms/Draw (−5.11%). Early and late
halves agree. Both runs exit normally; death/item-label bursts are outside the
primary selection. Logs do not certify respawn/alive state, matching positions,
scene contents or visual correctness. This is a modest observed gain, not a
universal FPS claim. Keep46A for rollback; do not use instrumented49 as control.
See [the50 hardware outcome and limits](findings.md#build-50-hardware-ab-outcome-2026-09-20).

**51 completed its UI measurement on50, not the current52 baseline.** It separates
inventory/hotbar/mouse layers, item slots, tooltip/localized formatting, text drawing
and actual deferred layout. Raw player/death/camera/UI state separates world,
inventory and paused-inventory; dead/menu/map/spectator/invalid frames are excluded
from these costs. Inner timings sample1-in16 eligible frames; inclusive scopes
overlap and child-subtracted times still include observer work.

The valid capture has6,810 eligible frames/427 samples and no deaths or observer
failures. After separating cold entries: world31–63 UI6.899ms, inventory66–83
UI14.155ms, short fixed-hover85–86 UI18.558ms (only9 selected frames).
The ordinary `tooltip` category is the mandatory pending mouse-text wrapper,
which includes gamepad instructions and batch setup—not an active item tooltip.
The safety investigation ruled out deleting the full prepass: general tag effects
and pre-swap failures matter. **52 now implements the guarded next step on50**:
parse once, check exact current snippets/font metrics without reflection or cached
certificates, skip only bounded pure layout, otherwise use the same parsed list in
the original layout. Hints/composition/drawing remain. No51 profiler is included.

**52's stationary retest is accepted;52 is now the working performance baseline.**
The user confirmed A=50, B=52, approximately the same position and no movement.
The new logs have47/45 reconciled native windows, no logged deaths and normal exits.
Complete windows30–90s after the TIMBER-containing report show19.1904→20.6437 Draw/s
(+7.57%), Draw34.5413→32.1900ms (−6.81%) and Update5.3537→5.3100ms (−0.81%).
A has item-label chatter in that primary span; a later label-free check still gives
+7.37% Draw/s. Every earlier/later/broader check agrees in direction. This accepts
the measured stationary scene, not a universal FPS, exact guard cost or visual proof.

Use the existing
`mono_nx_fna_terraria_nochroma52_guarded_hint_layout.nro` from `terraria-mono/switch/`;
its changed game/ReLogic pair is embedded, so keep SD runtime DLLs unchanged.
Keep50 as the reference/rollback build. No new build or further identical A/B is
needed merely to accept52. Future optimization candidates must start from its
accepted Terraria/ReLogic pair and retained dependencies, not50 or profiler51.
The first death-containing pair remains archived and inconclusive; it is not
pooled with the retest. Hints/input appearance and exact branch-hit rate are not
verified by logs. All compatibility discussion gates remain in force.
See [the stationary retest and adoption](findings.md#build-52-stationary-retest-and-adoption).

**53's hardware capture is valid.** All80 packets reconcile with15,892 native
Draw frames;2,389 are eligible gameplay frames, with no deaths or observer faults.
The longest stationary group47–67 has2,078 frames: solid loop14.566ms, nonsolid
1.687ms, batch completion1.117ms inclusive, upload0.149ms and indexed submission
0.410ms/frame. These scopes overlap and do not establish GPU-only cost.

The uploaded log is archived under cache `render53/hardware-captures/log53.txt`;
`render53/analyze_hardware.py` reproduces the strict analysis and stationary
selection.52 remains baseline;53 remains a profiler, not a speedup build.

### 55 recipe-cost measurement

This build is **instrumentation, not an optimization**:
`mono_nx_fna_terraria_nochroma55_recipe_profile.nro` (`Terraria 55 Recipe Profile`).
It measures the inventory recipe-refresh path on the exact52 game.54's parked
scratch-state change is not included. Copy only the new NRO; keep52 and the
existing SD runtime DLLs/settings.

The log separates recipe refresh, clearing/collecting items, guide collection,
refocus/reposition, crafting drawing and item-slot drawing under sampled UI frames.
Original recipe filters, environment/material checks, callbacks and call order
are preserved. No timers are inserted inside each recipe-condition iteration.
Inclusive scopes overlap; recipe-exclusive time includes unmeasured scan work
and observer overhead, not pure predicate/GPU time.

**The first hardware capture is complete and valid:**137 packets,18,103 reconciled
Draw/swap frames and602 valid selected frames, with no deaths or observer failures.
In the steady no-hover block, recipe refresh costs2.600ms inside5.731ms inventory
and13.515ms full UI. The hovered block adds3.448ms outside inventory with nearly
unchanged recipe cost. Actual native rates are20.089 Draw/s closed,17.023 open
without hover and15.464 with raw hover type8—not reciprocals of Draw-only timings
or speedups against52. Solid tiles and Update remain the main gameplay targets.
See [55's hardware results and reproduction](findings.md#build55-hardware-recipe-results).
No repeat55 capture is required for these findings; no new NRO was built.

Original capture procedure (completed):

1. Use full application mode, Frame Skip On, `logging=true`,
   `runtime_logging=false`, and preferably Auto Pause Off.
2. Load a safe sheltered world and let loading settle. Keep inventory/map closed
   and remain stationary for about30s.
3. Open inventory with its crafting panel visible. Keep the same position and
   leave the cursor away from item tooltips for about90s; do not craft, move,
   change filters or die during this block. Keep controller hints visible.
4. Optional hovering/filter/guide interaction comes afterward as a separate
   segment. Exit normally so the final report is flushed.
5. Save the **complete** `/mono/log.txt` as **log55.txt**. Preserve/move any old
   SD log aside before launching so captures are not concatenated.

Expect `NX_PROFILE BEGIN ... version=55 schema=recipe_costs`.55 may run slower
because it measures work. Do not compare its FPS with52 as a speedup result;
return to52 for normal play. Report any visual/input/crash difference.
See [55's verified scope and evidence](findings.md#build55-recipe-cost-measurement).

Compiler-wide ABCREM and SSA trials aborted and were rejected. The user skipped
GetColor inlining; address-only scratch-field reuse also remains unpromoted.
**54 is a tested, now-parked stack-state experiment.** It removes one120-byte
scratch-owner allocation per tile while retaining a fresh nine-vector array and
both GetColor calls. Native Single code shrinks11.2%, but its stack frame and
load/store counts increase slightly. The retest found a modest gain, not a large speedup.

The seven private rendering-helper signature changes were discussed and approved
for this vanilla experiment only. Public TileDrawInfo, original definition tokens,
FNA/ReLogic, runtime DLLs, assets, simulation and input handling remain unchanged.
Host proofs execute Single/all seven helpers and check fresh-state, GC relocation,
draw arguments and120 bytes/call allocation reduction. Native/payload checks passed.
See [54's scope and verification](findings.md#build54-reversible-stack-state-ab).

The completed A2/B2 retest shows **19.374→20.027 Draw/s (+3.37%)** and
**33.642→32.981ms Draw wall (0.661ms saved)** over matched95s sections. The user
confirms matching held item/cursor/UI state and no visual/input/stability issues.
B still logs two item-name/prefix lines per Draw while A logs none; no correction
for that unexplained difference was applied. The user selected **Keep52; park54**.
No further54 capture is requested; the NRO and all evidence are retained.

**No replacement52 was issued for these tests.** The original
`mono_nx_fna_terraria_nochroma52_guarded_hint_layout.nro` (`Terraria 52 Hint Guard`)
is the intended control and working baseline; no new52/runtime DLL copy is needed.
See [the retest and parking decision](findings.md#build54-retest-and-parking-decision).

The first uploaded pair has been analyzed. `log54B.txt` contained the exact old53
log followed by a new session starting at line2276; only that suffix was compared.
Both new runs exit normally. A roughly116s label-free span shows **+6.9% Draw/s
and−5.0% Draw wall time**, but the control logged a player death and the short
pre-death span did not improve. This is provisional, not a clean causal speedup.
See [the first54 hardware analysis](findings.md#build54-first-hardware-comparison).

### 56 Update-cost measurement

**56's hardware capture is complete and analyzed;52 remains the working baseline.**
`mono_nx_fna_terraria_nochroma56_update_profile.nro` (`Terraria 56 Update Profile`)
separates Update/worldgen, liquids, wiring, tile entities/counting/housing, entity
updates, input/UI updates, tile/wall animation and queued main-thread work.

The old World-tiles timer also includes other world work; it does not prove that
random tile stepping alone is expensive.56 preserves all original calls, RNG
order, signatures, catches/finally and simulation rates. It is not an optimization.
**Its1-in64 sample unit is Main.Update—not a rendered Draw frame.**

The user reported56 was much slower. The log isolates a progressive nighttime
world-item cost:0.302→2.545ms per Update across stationary closed-inventory
early/late-night blocks, accounting for89.49% of measured Update growth.
Worldgen remains near1.3–1.4ms. Item cost reaches3.449ms in the later inventory
block and exceeds4ms in short later samples. A late death is excluded from the
main comparison; all505 selected samples are valid and no observer errors occur.
Next target is WorldItem.UpdateItem and its stacking/physics/visual paths,
not another broad Update profile. Active-item counts/types and the exact inner
cost split remain unmeasured. Logged snapshots/report bodies account for1.65%
of elapsed time, but total profiler perturbation is not isolated.
See [56's hardware results and source findings](findings.md#build56-hardware-results-nighttime-world-item-growth).
No new NRO or gameplay change was made during analysis; no repeat56 capture needed.

Original capture procedure (completed):

Copy only56's NRO; leave SD runtime DLLs/settings intact. Use full application
mode, Frame Skip On, Auto Pause Off, `logging=true`, `runtime_logging=false`.
After loading settles, capture about120s stationary with inventory/map closed,
then60s inventory-open in the same spot with no item hover/crafting/filter changes.
Optional walking/gameplay follows as a separate60s block. Exit normally and save
the complete `/mono/log.txt` as **log56.txt**, starting from a non-concatenated log.

Expected header: `version=56 schema=update_costs sample_denominator=64 metric_count=19`.
Do not interpret1/Update time as FPS or compare profiler FPS as a speedup over52.
Final source-bound behavior/runtime/report proofs, all native targets and all
embedded files passed;52/54/55 NROs remain unchanged. See
[56's scope, corrected proof gaps and artifact identity](findings.md#build56-update-cost-measurement).

### 57 lighting value-local A/B

**57 is a comparison candidate, not a new working baseline or a proven speedup.**
`mono_nx_fna_terraria_nochroma57_light_value.nro` removes repeated native Vector3
copies in Lighting.GetColor(int,int). All engine/brightness/color calls, their
order, float operations, fields/signatures and TileDrawInfo/colorSlices
construction remain unchanged. It contains no deferred allocation or profiler.

Host behavior/source/AOT/native/payload gates passed. Native code and stack are
smaller, but host timings were inconclusive and Switch frame benefit is unknown.
52/54/55/56 files remain unchanged. Copy only57's NRO; keep SD runtime DLLs.

Compare **original52 → log57A.txt** and **57 → log57B.txt**, preferably repeat52
as log57A2.txt. Same sheltered position, camera/settings and daytime conditions;
Frame Skip On, Auto Pause Off, full application mode, logging=true and
runtime_logging=false. Let startup settle, stay still90–120s with inventory/map
closed, then exit normally and keep each complete non-concatenated log. Do not
compare against53/55/56 or cross into the nighttime workload increase. Use the
same test save state where practical, report any unmatched setup and any visual
or input difference, and say which NRO was actually launched.

See [57's full scope, evidence and comparison procedure](findings.md#build57-lighting-value-local-comparison).

### 58 Release CoreLib/framework A/B

**58 is a comparison candidate, not a new baseline.**
`mono_nx_fna_terraria_nochroma58d_release_bcl.nro` (`Terraria 58 Release BCL`)
runs the exact52 game against a **Release** CoreLib and framework: optimized IL,
Debug.Assert calls stripped. The shipped SD runtime is a Debug build. Game bytes,
compiler, AOT options and the native Mono runtime are build52's.

CoreLib is embedded in the NRO (`romfs:/mono/lib_net9.0`) and loaded through the
new `MONO_NX_EMBEDDED_BCL` launcher flag; the Release framework replaces the Debug
copies at the RomFS root, where 52 already loaded them from. So:
**copy only the NRO; do not touch SD `/mono/lib_net9.0` or `/mono/framework_net9.0`**.
Other NROs keep loading the SD Debug BCL their AOT images were built against.

First-run acceptance in `/mono/log.txt`: `NX_RUNTIME embedded BCL: romfs:/mono/lib_net9.0;romfs:/`,
`NX_RUNTIME fatal diagnostics: ...`, then `NX_AOT Terraria entrypoint resolved to native code`,
then the title screen and world. If it fails at startup, set `runtime_logging = true`
for that one run (managed `FailFast` failures are only visible in the verbose log),
send the complete log plus any new `crash_reports/*` files, then set it back to
`false`. Keep `runtime_logging = false` for the A/B.

Compare **52 → log58A.txt** and **58 → log58B.txt**, preferably repeat52 as
log58A2.txt. Handheld, all low settings plus Trippy lighting, Frame Skip On, Auto
Pause Off, full application mode, logging=true, runtime_logging=false. Same
sheltered daytime position, stationary 90–120s with inventory/map closed, then
exit normally. Report any visual/input difference and which NRO was launched.

**58e boots too** (CoreLib inlining on). The first A/B (`log58A/B/E.txt`) shows
both Release builds ~21% faster than 52 over a matched ~15 s block, but that is too
short to adopt. 58e is the carried-forward candidate.

### 59b result and the next capture: 60b

`mono_nx_fna_terraria_nochroma59b_linq_collections_aot.nro` loads the world and
exits normally. The user's `log59b.txt` contains 55 reconciled NX_PHASE records
over 296.622 s, with no repeat of 59's IMT trampoline exhaustion. Its settled
155.5–260.9 s block averages 24.70 Draw/s and 60.02 Updates/s. NX_PHASE does not
record player position, scene flags or clocks; this is not a matched speedup claim.

**60 is not skipped: use corrected 60b, not the original 60.**
`mono_nx_fna_terraria_nochroma60b_llvm_terraria.nro` (`Terraria 60b LLVM Terraria`)
keeps 58e's framework coverage, LLVM-compiles Terraria, and increases CoreLib's
trampoline pools. Copy only the NRO; leave the SD runtime DLLs unchanged.

1. First confirm 60b reaches the world and exits normally.
2. Compare **58e → log60b-control.txt**, **60b → log60b.txt**, ideally repeat 58e.
3. Handheld at stock clocks, all low + Trippy, Frame Skip On, Auto Pause Off,
   `runtime_logging=false`. Same sheltered daytime spot; after loading settles,
   stand still 90–120 s with inventory/map closed and no deaths, then exit normally.

If 60b crashes, rerun once with `runtime_logging=true` and send the complete log
plus new crash reports. Then restore `runtime_logging=false` for timing runs.
60b differs from 58e in code generation **and pool capacity**. Do not treat a direct
59b/60b comparison as an isolated LLVM test: 59b additionally AOT-compiles Linq and
Collections. 52 remains the adopted performance baseline.

FNA is not an alternative to OpenGL or NVIDIA: the existing stack is
Terraria → FNA → FNA3D → OpenGL/Mesa → Tegra GPU. A separate FNA-only compiler
experiment keeps that driver path and reuses 59b's game code and payload unchanged.

See [59b/60b's crash fix and capture evidence](findings.md#build59b-crash-fix-and-build60b-rebuild-2026-09-25).

### 61 FNA-only LLVM experiment: separate follow-up

`mono_nx_fna_terraria_nochroma61_llvm_fna.nro` (`Terraria 61 LLVM FNA`) is ready,
but **test 60b first**. 61 is 59b with only FNA recompiled through LLVM; it does
not include 60b's LLVM-compiled Terraria. The existing Terraria AOT object,
CoreLib and its trampoline pools, Linq/Collections, other AOT objects and every
RomFS file are reused unchanged. No additional proprietary assembly transformation
or compilation, and no graphics-driver change.

Host checks: 201,450 native targets / 14,582 fallback sentinels; 11,614 LLVM FNA
targets; all 9 module bindings; exact 52 control replay; no RWX/TEXTREL; all
37,663 packed loader relocations land in writable data. Hardware behavior and
performance remain untested. Building smaller code alone proves no FPS gain.

When ready, compare **59b → log61-control.txt** with **61 → log61.txt**, using the
same 90–120 s scene/settings procedure as above. Check world entry, rendering,
controls/audio and normal exit. Do not compare 60b with 61 as a single-change A/B.

Reproducible build recipe: `scripts/release_bcl/build_fna_llvm.py`, using the
verified local 59b inputs and the same LLVM container/mounts as 60b. Details:
[build61 verification](findings.md#build61-fna-only-llvm-experiment-2026-09-25).

### 60/60b/61 first hardware results and the next A/B

All three boot, reach the world and exit normally (`log60.txt`, `log60b.txt`,
`log61.txt`). **The fast first 60 run is a lighter scene, not a better build:**
60 and 60b's LLVM Terraria code is byte-identical, the non-timing log events
match, and menu rendering costs ~15 ms/Draw in every run. Only in-world rendering
differed: first run 17 ms/Draw with frameskip off (44 Draw/s, ~73% game speed),
second 32.5 ms (28 Draw/s, ~46%), 61 36 ms (23 Draw/s, ~39%). Frameskip-on
gameplay: both 60 runs ~28.5 Draw/s vs 59b's 24.6, but in different, unmatched
scenes. **No LLVM gain is proven yet.** Details:
[hardware results](findings.md#build6061-hardware-results-2026-09-25).

Next capture, one session, **the exact same spot** (ideally the first run's
fast spot, if you can find it again; note time of day). Frameskip **off**
(Draw and Update then tick together, clearest signal), everything else as usual,
stand still 60–90 s after loading settles, exit normally, alternate builds:

1. 58e → **logAB1.txt**, 2. 60b → **logAB2.txt**, 3. 58e → **logAB3.txt**,
4. 60b → **logAB4.txt**. Optional: 59b → **logAB5.txt**, 61 → **logAB6.txt**.

Alternating shows whether the spot, not the build, explains a difference.

**Result (`logAB2`–`logAB6`; AB1 was recovered from the start of `logAB2`,
because the log appends across launches):** last 50 s stationary, frameskip off.

| Run | Build | Draw/s | ms/Draw | ms/Update |
| --- | --- | ---: | ---: | ---: |
| AB1 | 58e | 26.8 | 31.0 | 5.53 |
| AB2 | 60b | **35.0** | 23.7 | 4.15 |
| AB3 | 58e | 24.6 | 33.9 | 5.87 |
| AB4 | 60b | **35.4** | 23.4 | 4.11 |
| AB5 | 59b | 20.1 | 43.3 | 5.59 |
| AB6 | 61 | 26.4 | 31.7 | 5.48 |

60b (LLVM Terraria) is **+37%** over 58e, reproduced in both alternations.
One spot, frameskip off; not yet the heaviest scenes. 59b/61 were not alternated.

**Next: 60b vs 62**, same spot and procedure, alternating:
60b → **logC1.txt**, 62 → **logC2.txt**, 60b → **logC3.txt**, 62 → **logC4.txt**.
Delete or rename `/mono/log.txt` between runs (or just tell me; appended
sessions are recoverable by heap size). If 62 crashes, rerun once with
`runtime_logging=true` and send the log plus `crash_reports/*`.

**Result (`logC1`–`logC4`, one session each, all normal exits, no errors):**
settled block ~30 s after arriving, frameskip off.

| Run | Build | Draw/s | ms/Draw | ms/Update |
| --- | --- | ---: | ---: | ---: |
| C1 | 60b | 34.6 | 24.3 | 3.95 |
| C2 | 62 | **37.3** | 22.1 | 3.96 |
| C3 | 60b | 36.2 | 22.9 | 4.02 |
| C4 | 62 | **38.0** | 21.5 | 4.13 |

62 is ~+6% over 60b, both alternations agree; the gain is on the Draw side, as
expected for FNA. C4 later jumps to ~41.5 Draw/s mid-run (a scene/camera change
not seen elsewhere), so its end-of-run figure overstates the gain.

**Next: 62 vs 63**, same spot and procedure, alternating:
62 → **logD1.txt**, 63 → **logD2.txt**, 62 → **logD3.txt**, 63 → **logD4.txt**.
Expect a small difference; if the two alternations disagree, it's noise.

**Result (`logD1`–`logD4`, all normal exits, no errors):** settled block ~30 s
after arriving, frameskip off.

| Run | Build | Draw/s | ms/Draw | ms/Update |
| --- | --- | ---: | ---: | ---: |
| D1 | 62 | 41.3 | 19.8 | 3.76 |
| D2 | 63 | 30.0 | 28.7 | 3.90 |
| D3 | 62 | 38.1 | 21.6 | 3.99 |
| D4 | 63 | 41.3 | 19.8 | 3.70 |

**No measurable gain from the CPU tuning.** D4 (63) equals D1 (62), and the
two 62 runs differ by 3 Draw/s from each other. D2 held **exactly 30.0 Draw/s
for its whole ~200 s** with Draw at 28.7 ms but normal Update and menu costs.
That looks like a cap or a different in-world state, not 63's code; D4 (same
build) doesn't show it. 62 stays the best build.

**Next: 62 vs 64**, same spot and procedure, alternating:
62 → **logE1.txt**, 64 → **logE2.txt**, 62 → **logE3.txt**, 64 → **logE4.txt**.
First check 64 reaches the world and exits normally. If it crashes or hangs, rerun
it once with `runtime_logging=true` and send the log plus `crash_reports/*`.

**Result (`logE1`–`logE4`, all normal exits, no errors):**

| Run | Build | Draw/s | ms/Draw | ms/Update | Startup stall | World-load stall |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| E1 | 62 | 36.3 | 22.9 | 3.96 | 10.2 s | 4.29 s |
| E2 | 64 | 29.6 | 29.3 | 3.85 | **7.8 s** | **3.69 s** |
| E3 | 62 | 35.9 | 23.2 | 3.98 | 10.2 s | 4.26 s |
| E4 | 64 | **38.4** | 21.4 | 3.96 | **7.9 s** | **3.73 s** |

**64 loads faster** (you noticed it): the long startup stall before the title
drops from 10.2 to 7.9 s (−23%) and the world-load stall from 4.3 to 3.7 s (−13%),
in both 64 runs. Gameplay is not settled: E4 is +6% over 62, but E2 ran near 30
Draw/s, like D2 did. Both near-30 runs were the second run of their set.

**Tie-breaker pair (`logF1` 62, `logF2` 64), normal exits, no errors:** loading
gain repeats (startup 10.3 → 7.9 s, world load 4.3 → 3.8 s). No near-30 run this
time. Gameplay: F2 first matches F1 (38.9 vs 39.6 Draw/s), then steps up at ~127 s
to a steady **43.2 Draw/s (+9%)**. A similar mid-run step happened once on 62 (C4),
so the step can't be credited to 64 alone. **64's gameplay gain is between ~0 and
~9%: too small to isolate with single runs. 64 is now the working build**: it loads
faster, is never slower, and all three logged 64 runs were clean.

**Next: 64 vs 65**, same spot, Frame Skip Off, alternating:
64 → **logH1.txt**, 65 → **logH2.txt**, 64 → **logH3.txt**, 65 → **logH4.txt**.
First check 65 boots, reaches the world and exits normally. If it crashes or
hangs, rerun once with `runtime_logging=true` and send the log plus `crash_reports/*`.

**64 lighting modes (`log64retro.txt`, `log64color.txt`; separate launches,
Frame Skip Off, stationary ~200 s each):**

| Lighting | Draw/s | ms/Draw | ms/Update |
| --- | ---: | ---: | ---: |
| Trippy (64, F2/E4 earlier) | 38–43 | 18.5–21.4 | ~4.0 |
| Retro | 43.3 | 18.7 | 3.66 |
| Color | **48.0** | **15.7** | 4.45 |

Color is the fastest: drawing is ~3 ms cheaper (cached tile layers) and updates ~0.8 ms
dearer (colour lighting). Retro ≈ Trippy. Different launches/scene, so ±a few Draw/s.

**GC test (build 67, not 66).** Same spot, Color lighting, Frame Skip Off, one launch per
setting, stand still 60–90 s, then exit normally (also checks the exit fix). The setting is
a text file on the SD card, `/mono/gc_params.txt`, one line:

1. no file (SGen default: concurrent mark-sweep, 4 MB nursery) → **logGC1.txt**
2. `nursery-size=16m` → **logGC2.txt**
3. no file again → **logGC3.txt**
4. `nursery-size=32m` → **logGC4.txt**
5. `major=marksweep` (no concurrent GC worker thread) → **logGC5.txt**

Delete or rename `/mono/log.txt` between runs. The `NX_GC` lines measure GC time
directly, so these compare well even with some scene drift.

### L4T profile (plan step 3), when the L4T SD card is in

Goal: see where L4T spends the frame at Horizon-equivalent limits, then compare
with the Horizon profilers (managed code vs GL/driver vs runtime/GC).

1. Handheld, CPU limited to 1020 MHz and 3 cores, as before:
   `echo 1020000 | sudo tee /sys/devices/system/cpu/cpu*/cpufreq/scaling_max_freq`.
   Terraria: all low + Trippy, Frame Skip On, sheltered daytime spot similar to the Horizon tests.
2. Get `perf` (once): `sudo apt update && sudo apt install linux-tools-common linux-tools-generic`,
   then `PERF=$(ls /usr/lib/linux-tools/*/perf | head -1)` and
   `sudo sysctl kernel.perf_event_paranoid=-1 kernel.kptr_restrict=0`.
   Check it works: `sudo $PERF stat -e task-clock sleep 1`.
3. Start the game with a JIT symbol map:
   `MONO_ENV_OPTIONS=--jitmap taskset -c 0-2 mono Terraria.exe`.
4. Load the world, stand still ~30 s, then from SSH (or a second terminal):
   `PID=$(pgrep -f Terraria.exe)` and
   `sudo $PERF record -F 499 -g -p $PID -o terraria.perf.data -- sleep 60`.
   Keep standing still for those 60 s. Then quit the game normally.
5. Reports:
   - `sudo $PERF report -i terraria.perf.data --no-children --sort dso --stdio > perf-dso.txt`
   - `sudo $PERF report -i terraria.perf.data --no-children --sort symbol --stdio > perf-symbols.txt`
   - `uname -r; mono --version | head -1; cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq > l4t-info.txt`
     (run that while the game is running)
6. Send `perf-dso.txt`, `perf-symbols.txt`, `l4t-info.txt` and your in-game FPS.

If `perf` will not run on the L4T kernel, use Mono's own sampling profiler instead:
`taskset -c 0-2 mono --profile=log:sample,output=terraria.mlpd Terraria.exe`, same
60 s standing, quit, then `mprof-report --reports=sample terraria.mlpd > mprof.txt`
and send `mprof.txt`. Restore the CPU limit afterwards (reboot or write the old value back).

### Archived54 A/B procedure: experiment now parked

Copy **only** `mono_nx_fna_terraria_nochroma54_stack_state.nro` into `/switch/`.
Keep the52 NRO and existing SD runtime DLLs/settings.54 is not adopted; compare
against52, **not the instrumented53 profiler**.

1. Use full application mode, Frame Skip On, `logging=true`,
   `runtime_logging=false`, and the same display/lighting/power conditions.
2. Run52 with the same character and safe world. Let loading settle, then remain
   alive/stationary for about90s with inventory/map closed and controller hints
   visible. Exit normally; preserve the complete `/mono/log.txt` as **log54A2.txt**.
3. Run54 (`Terraria 54 Stack State`) and repeat the same location/conditions.
   Exit normally; preserve the complete log as **log54B2.txt**.
4. Report crashes, visual differences or input issues. An optional short walk
   comes **after** the stationary block. Return to52 if54 misbehaves.
   Use a sheltered spot so neither run dies. After saving each log, move it aside
   on the SD card before the next launch so the next capture starts fresh.

This retest is complete and no additional54 logs are requested. The procedure is
retained for reproducibility;52 remains the normal-play/performance reference.


For performance comparisons, keep `runtime_logging=false` and `logging=true`.
Check `NX_CONFIG` actually reports `runtime_logging=0` as it did in39–53.
`NX_PHASE` retains `poll` and `swap` counts/times. They are inclusive and may
overlap: use Tick minus Update minus Draw for the residual, not blind subtraction.
Build 37's EventSource capability is separate from `runtime_logging`; its
startup log should include `NX_RUNTIME EventSource disabled: native EventPipe is unavailable`.
The user reports that33's menu-entry skipping is **not seen on recent builds**.
No speculative navigation/repeat change was made; the input-buffering fix stays.
The52/54 comparison is complete; future UI/audio work starts from52, with54 parked.

See [the workshop findings](findings.md) for the L4T baseline,
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
`scripts/patch_tile_reuse/run.py` accepts only the clean42 input, preserves
existing game signatures, and verifies full serialized Draw/Single/reset/scope
behavior. Current accepted candidate evidence is in cache `scratch46-final/`.
Build recovery is persistent under cache `recovery46/`; the old `/tmp` SDK must
not be assumed present.46 uses NX_PHASE rather than NX_PROFILE, so the detailed
profile analyzer above is not the right parser for its comparison logs.
`scripts/patch_light_lookup/run.py` accepts only clean42 and verifies the47
guard, all other methods/metadata and repeated output. Its dynamic proof runs
actual serialized entry/glow-tail slices with explicit host dependencies, not
the complete helper or a Switch scene. Source lifecycle/engine audit supports
initialized built-in rendering; corrupted-state failures and stateful custom
lighting hooks are not equivalent. Evidence is in cache `light47/` and
`light47-audit/`. Both46A/47 retain NX_PHASE; use matching steady segments rather
than the detailed NX_PROFILE analyzer or the old, differently active46 runs.
The47 hardware analysis and archived uploads are in cache
`light47/hardware-comparison.json` and `light47/hardware-captures/`. No stationary
markers or exact hover state are logged; low polling alone is not scene matching.
`scripts/patch_property_diagnostics/run.py` performs pinned framework-only48
acceptance, complete serialized-method differential proof and repeated/negative
checks. Actual complete-framework smoke, AOT padding/reference proof and native
payload evidence are under cache `ui48/`; patch-time evidence is under
`ui48-patcher-final/`. The final AOT inputs are `ui48/aot/link-inputs/`, not the
raw-stage objects. No speedup is inferred from host allocation reductions.
The48 hardware result and original uploads are preserved in cache
`ui48/hardware-comparison.json` and `ui48/hardware-captures/`. The old generic
`logA.txt`/`logB.txt` and scratch-reuse `log46B.txt` are not this comparison.
`scripts/patch_tile_helpers/run.py` accepts exact clean42 and proves the four
observer splices, runtime state/timing behavior and version49 reports. Final
evidence is in cache `tile49/patcher-accept03/`, `tile49/aot-final/` and
`tile49/native/`; earlier accept01/02 snapshots are not packaging inputs.
Use `scripts/analyze_tile_helpers.py log49.txt --output /tmp/tile49-analysis.json`
for NX_PROFILE version49/schema=tile_helpers. It retains raw state, null unused
means and invalidity flags; helper wall times/clock-pair costs are not automatic
CPU/GPU/FPS savings. Scene zoom is raw GameZoomTarget, not effective render zoom.
The49 hardware raw packets, weighted stationary/moving cohorts and archived upload
are in cache `tile49/hardware-parsed.json`, `tile49/hardware-analysis.json` and
`tile49/hardware-captures/log49.txt`. No further49 capture or runtime update is
needed for that source audit; no new optimization was applied by the analysis.
`scripts/patch_draw_gate/run.py` performs pinned50 acceptance, exact inverse
relocation, qualified reference-binding checks and full serialized-method proof.
The supplied-candidate path rejects a Rectangle reference redirected out of FNA
before fixture remapping. Final evidence: cache `gate50/accepted-scope-final/`,
`gate50/aot-final/`, `gate50/native/`, `gate50/pair-verification.json` and
`gate50-audit/`. Earlier staging is labeled in `gate50/superseded.json`.
Use the direct retained SDK `/build/runtime-source/.dotnet/dotnet` with the
pristine `/mono-nx` mount; native/runtime binaries on the SD card do not change.
The completed50 comparison is in cache `gate50/hardware-comparison.json`, with
all windows in `gate50/hardware-windows.tsv` and byte-identical uploads under
`gate50/hardware-captures/`. The existing50 NRO is unchanged; no new build is
required to use the adopted baseline.
`scripts/patch_ui_profile/run.py` builds51 from exact50 and requires static,
generated behavior/runtime and actual report-parser proof before acceptance.
Use `python3 scripts/analyze_ui_profile.py log51.txt --output /tmp/ui51-analysis.json`
for version51/schema=ui_costs. It preserves raw state, keeps cohorts separate,
rejects malformed/partial packets and suppresses timing interpretation when invalid.
Final evidence is under cache `ui51/accepted-final/`, `ui51/aot-final/`, `ui51/native/`,
`ui51/final-analyzer-proof/` and `ui51/artifact-verification.json`; source inspection
is `ui51-audit/base50-final/`. Override the container entrypoint explicitly when
using `/build/runtime-source/.dotnet/dotnet` with the pristine `/mono-nx` mount.
Hardware results: cache `ui51/hardware-analysis.json`, complete parsed packets
`ui51/hardware-parsed.json`, per-window table `ui51/hardware-windows.tsv`, and
byte-identical upload `ui51/hardware-captures/log51.txt`. The completed prepass
investigation is `hint52-investigation/investigation.json`, with exact numeric,
tag/origin and root counterfactual evidence under its geometry/tags/root folders.
Original unknown/unready/general-tag behavior must remain unless separately proved;
controller-hint removal and skipped composition callbacks are not approved.
Guarded52 source is `scripts/patch_hint_prepass/`; final proof/runner results are
cache `hint52/accepted-final/`.640 exact-pair checks,11 static mutation rejections,
raw primitive rejection,5 semantic mutants, repeat identities and11 runner guards
pass. Current-data font replay is `hint52-guard-font/candidate01/`. Both Terraria
and ReLogic AOT compile with unchanged24/4 fallbacks; five other objects remain
unchanged. `hint52/artifact-verification.json` verifies192,309 native targets,
15,010 fallback sentinels and16,183 embedded files; only the accepted game/library,
their AOT objects and NACP title differ. `hint52/{build_aot.py,build_native.py,
verify_artifact.py,published-build.json,safety-review.json}` preserves reproduction,
publication and review. No replacement runtime, JIT or unverified speed claim.
First, inconclusive hardware comparison: cache `hint52/{analyze_hardware.py,hardware-comparison.json,
hardware-windows.tsv}` with unchanged raw uploads in `hint52/hardware-captures/`.
The analysis pins input hashes, reconciles every native counter and uses weighted
whole-window sums without favorable-row filtering. It records normal execution,
the two deaths, absent state/hint/guard coverage and the decision to retain50.
Accepted stationary retest: cache `hint52/hardware-retest01/{analyze.py,comparison.json,
windows.tsv,captures/}`. All92 records reconcile, all six interval checks favor52,
and previous archives retain their hashes. Re-running the new script reads its
own pinned archives, so future uploads do not replace this evidence. The user
confirmed build roles and stationary positioning; unlogged scene/clock/visual
state and single ordered-pair limitations are recorded alongside adoption.
53 source/acceptance is `scripts/patch_render_profile/`, with strict reader
`scripts/analyze_render_profile.py`. Cache `render53/accepted-final/` holds
repeat-identical game/probes, static/semantic/raw/inverse proofs and final report
receipts; both accepted/repeat roles passed33 positive files/201 malformed files.
`render53/{build_aot.py,build_native.py,verify_artifact.py}` compiles only the game,
retains six52 AOT objects, replays the exact52 control and checks the full artifact.
`render53/{artifact-verification.json,published-build.json}` records publication.
The new source/reader is not an optimization and no53 hardware gain is claimed.
The [RAL source/provenance review](findings.md#ral-source-review-and-provenance-boundary-2026-09-18)
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
See the [optimization escalation gate](findings.md#current-decision-performance-first).

See [how game edits reach the NRO](findings.md#how-game-edits-reach-the-nro)
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
