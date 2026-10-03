# Porting FNA-based games to Switch

This guide captures the approach used by `terrabuilder-nx` for a managed game
that uses FNA. It is a technical workflow, not a promise that another game will
work without game-specific changes.

## 1. Establish inputs and constraints

- Start from a clean, user-owned game installation and identify the exact game,
  FNA, Mono/.NET, and content versions.
- Keep game assets, proprietary assemblies, saves, and user logs out of the
  public source repository.
- Inventory managed assemblies, native libraries, platform services, and
  content-loading paths. Include game-specific launchers, codecs, networking,
  audio, and input libraries rather than checking only the main executable.
- Confirm licenses and redistribution terms for the runtime, native
  dependencies, graphics stack, and any mods before packaging or publishing.

## 2. Isolate desktop assumptions

Find every desktop-specific dependency before porting:

- window creation, display enumeration, window handles, and resize/fullscreen
  handling;
- filesystem roots, relative paths, case sensitivity, and write locations;
- audio device ownership and audio-engine initialization order;
- keyboard, mouse, touch, and controller event assumptions;
- process, environment, thread, socket, memory-mapping, and dynamic-library APIs;
- graphics profile selection, shader versions, extensions, and driver queries.

Keep the game's managed API surface stable where possible. Put platform behavior
in the FNA/native compatibility layer or in a small, recorded offline IL patch
when that is the narrowest reliable adaptation.

## 3. Port native dependencies deliberately

Build native dependencies for the target ABI and connect them through explicit
shims. Avoid accidentally loading host libraries into the target package.
Host-native SDL, FNA3D, or audio libraries may be needed to compile a mod or
tooling step, but they should be clearly separated from Switch outputs.

Use one owner for the physical audio device. If multiple managed audio APIs
need the same backend, make them share a context instead of opening independent
devices. Test both ordinary FNA sound and any XACT/legacy audio path; one
working path does not prove that the other is initialized safely.

For graphics, select a profile supported by both the game and the target driver.
Validate shader compilation, framebuffer formats, texture uploads, and swap
behavior on device. Desktop success or an emulator run is not a substitute for
hardware verification.

## 4. Prepare managed code for a no-JIT runtime

This port uses Mono AOT without a JIT. For each managed module:

1. Determine whether it is loaded or referenced at runtime, including
   reflection, mod loading, and dynamically selected dependencies.
2. Apply only necessary IL changes to a staged copy. Fail the build if expected
   source methods or IL patterns have changed.
3. AOT-compile the staged assembly. Apply LLVM optimization selectively; record
   the module policy and avoid assuming that optimizing every large assembly is
   beneficial.
4. Bind the native AOT object to the exact managed assembly identity and verify
   the MVID before linking.
5. Confirm that the launcher registers every required AOT module and reaches
   its managed entrypoint on hardware.

Test reflection and late-bound paths explicitly. A successful link does not
prove that every dynamically loaded assembly has a native implementation.

## 5. Package content and user data

Keep immutable game content in the read-only game filesystem when possible.
Place writable configuration, saves, logs, and mod state in a documented
writeable location. Test first launch, upgrade, missing-file, and normal
shutdown behavior without overwriting existing user data.

For mod support, prefer a small curated set of pinned source dependencies.
Build each mod against the target game's actual mod API and native host
dependencies; validate generated assets and platform-specific build steps.
Document which assemblies are generated locally and which files the user must
copy to the device.

## 6. Verify incrementally on target hardware

Use a test ladder so failures are attributable:

1. Launcher and managed entrypoint.
2. Main menu, display mode, and controller navigation.
3. Basic rendering, audio, save/load, and normal shutdown.
4. A representative world and sustained gameplay.
5. Multiplayer and modded gameplay, if those are in scope.

Capture the exact NRO hash, staged assembly identities, build receipt, and
device logs for each candidate. Separate instrumented profiling builds from
release candidates, and compare performance only under matched scenes and
settings. Promote a candidate only after the required device tests pass.

## Terraria-specific status

The vanilla port has a hardware-tested candidate for audio, FPS toggle,
multiplayer join, and frame-rate checks at default clocks. tModLoader is
experimental: its source-mod build uses shared-audio FNA and native AOT for
tModLoader. On hardware it loads its mods, reaches a world, and plays audio, but
it is very slow, the D-pad does not navigate the inventory, and multiplayer is
unverified. See [Architecture](architecture.md) and [Testing](testing.md).
