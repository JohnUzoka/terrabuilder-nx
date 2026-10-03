# Architecture

`terrabuilder-nx` builds a native Nintendo Switch homebrew application from a
user-supplied Terraria or tModLoader installation. The repository does not
contain game binaries or content.

## Build and runtime flow

```text
User-owned game files
        |
        v
CLI validation, staging, and supported IL compatibility patches
        |
        +--> Mono AOT objects (optional LLVM optimization per module)
        +--> user-owned game assets and managed assemblies in RomFS
        +--> optional curated mod source builds and SD-card payload
        |
        v
Native launcher + mono-nx + libnx + linked AOT objects
        |
        +--> .NET/Mono runtime and registered managed modules
        +--> FNA -> FNA3D / SDL / OpenGL compatibility layer -> Switch GPU
        +--> FAudio / XACT compatibility layer -> Switch audio
        +--> SDL and libnx input/network services
        |
        v
        Terraria.nro
```

The Python CLI uses the standard library on the host. Podman runs the
cross-compilation steps. The current build consumes validated runtime, SDK,
native dependency, and Mesa artifacts from a retained local cache. The local
manifest command does not yet create a bundle that can be installed and used on
a clean machine, and the runtime/SDK/graphics stack is not rebuilt end-to-end
from a clean checkout.

## Managed code and AOT

The Switch build has no JIT. Managed code must be available as native AOT
objects and registered with the launcher. The build verifies each staged
assembly against its AOT module identity (MVID), preventing a stale native
object from being paired with a different managed assembly.

Vanilla builds AOT the managed game/runtime modules and can LLVM-optimize
selected modules. The experimental source-mod tModLoader path currently uses
native Mono AOT without LLVM optimization for `tModLoader.dll`; FNA and the
curated source-built mods are LLVM-AOT compiled. AOT and LLVM policies are
module-specific because large-module optimization can be very expensive.

Supported IL changes are applied offline to the user's local assemblies.
`scripts/patch_*` and the tModLoader compatibility tools record these changes
as source and verify expected method/IL patterns before rewriting. They do not
patch the user's original installation.

## Native and graphics layers

The launcher is built from this repository's native overlay and the pinned
`mono-nx` sources. Its shims adapt Mono, FNA3D, SDL, OpenGL, audio, storage,
network, and controller calls to the Switch environment. The OpenGL path uses
the Switch Mesa/Nouveau driver stack. Runtime and launcher artifacts are
cross-compiled for AArch64/libnx.

Graphics diagnostics and frame-time wrappers are opt-in. Release builds use
quiet launcher defaults and omit profiling instrumentation. The graphics
settings do not enable a clock overclock; benchmark and stability tests should
record the selected system clock separately.

## Audio, input, and content

FNA and tModLoader's XACT layer must share the same FAudio context and audio
device. Opening a second SDL audio device caused an earlier source-mod build to
fail at startup; the CLI now stages the shared-audio FNA build, and audio works
in the current tModLoader build on hardware.

The native input latch bridges Switch controller state into the SDL/FNA input
path. Vanilla controller navigation and the FPS toggle have been exercised on
hardware. In tModLoader the D-pad moves through the main menu and the Start
menu but not the inventory, and the FPS toggle has not been confirmed.

Game files, managed assemblies, and the .NET class library are embedded in the
NRO's RomFS. Two runtime files stay on the SD card because the launcher reads
them before it mounts RomFS: `sd:/mono/config.ini` (logging, launch, and
full-application-mode settings) and the ICU data file it names,
`sd:/mono/etc/icudt77l.dat`. Every build writes both to its `sdcard/` payload.

tModLoader mods are built from curated, pinned open-source repositories; the
generated mod assemblies are supplied in the SD-card payload, not redistributed
by this repository.
Because those assemblies are also AOT-compiled into the NRO, the SD-card `.tmod`
files must come from the same build: a Workshop copy with the same version
aborts at mod load. User saves and game content remain local to the user's
device.

## Current limits

- Vanilla has a hardware-tested release candidate, including audio, FPS toggle,
  multiplayer join, and default-clock frame-rate checks. Hosting a multiplayer
  game from the Switch is not supported.
- tModLoader remains experimental. The current Fargo's Souls build loads its
  mods, reaches a world, and plays audio on hardware, but in-world performance
  is very slow (an earlier build measured roughly 15-19 fps). The D-pad does
  not navigate the inventory, the Plus/Minus FPS toggle has not been
  confirmed, and tModLoader multiplayer has not been verified.
- A clean-checkout, end-to-end from-source toolchain build remains incomplete.
  See [Building](BUILDING.md) for the component recipes and limitations.
