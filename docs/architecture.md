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

The Python CLI uses the standard library on the host. Podman or Docker runs the
cross-compilation steps. The regular build consumes the verified toolchain in
the work directory. `toolchain --bundle` installs the prebuilt toolchain, a
SHA-256-pinned release asset that is rehashed against its manifest after
unpacking. `toolchain --from-source` assembles the same layout from lock-pinned
sources plus the three exact build-52 facade assemblies checked into this
repository; it no longer needs the maintainer's local cache, but a full
clean-machine from-source build has not yet been verified end to end.

tModLoader builds add three inputs from the user's machine: the pinned tModLoader
release (downloaded from GitHub or the user's own copy), the `Content/` folder of
the user's Terraria 1.4.4.9 or 1.4.5.x, and `.tmod` packages from `Mods/`. From
them the CLI makes, in its work directory, the patched tModLoader assemblies
(console patchers, NxFix, the shared-audio FNA; `scripts/tmod/base_dlls.py`), a
base stage with the stock RomFS, `flat_libs` and NRO control data
(`scripts/tmod/base_romfs.py`), and then one variant per mod set. The runtime those
builds were validated with (Mono runtime libraries, framework, CoreLib, the LLVM AOT
compiler and precompiled CoreLib/framework objects, all open source) is the
toolchain's optional `tmod-runtime` component; nothing derived from the game or
from tModLoader is in the repository or the toolchain.

## Managed code and AOT

The Switch build has no JIT. Managed code must be available as native AOT
objects and registered with the launcher. The build verifies each staged
assembly against its AOT module identity (MVID), preventing a stale native
object from being paired with a different managed assembly.

Vanilla builds AOT the managed game/runtime modules and can LLVM-optimize
selected modules. The experimental source-mod tModLoader path LLVM-AOT compiles
`tModLoader.dll`, FNA, and the curated source-built mods. AOT and LLVM policies are
module-specific because large-module optimization can be very expensive. Mods
built in Debug carry `DisableOptimizations`, which stops Mono from inlining
anything in them; mod set preparation clears that flag before hook lowering, and
each module a mod build compiles uses an inline limit of 60 IL bytes (Mono's
LLVM AOT default is 30).

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
fail at startup; the CLI stages the shared-audio FNA build. Audio worked on
hardware in tmod27-era builds. From run 39 to v16 NxFix was fed its own earlier
output, whose `SoundEngine.TestAudioSupport` stub disabled sound; v17 patches the
console-patched original again and rejects that stub.

The native input latch bridges Switch controller state into the SDL/FNA input
path. Vanilla controller navigation and the FPS toggle have been exercised on
hardware. tModLoader's FNA reads buttons straight from SDL and never calls the
latch hooks, so tModLoader builds wrap `SDL_GameControllerGetButton` to run the
Plus+Minus toggle; it toggled on hardware in run 39.

Text boxes in tModLoader builds use the system keyboard. Terraria marks a focused
text box with `PlayerInput.WritingText`; the launcher reads that flag after each
FNA event drain (an `SDL_PollEvent` wrapper), opens the full-screen keyboard
(`swkbdShow`) when it turns on, and returns the entered text as `SDL_TEXTINPUT`
events through FNA's normal text path. SDL's own Switch text input stays off: it
stops touch polling while on, and its inline keyboard lets the game act on the
same button presses.

Game files, managed assemblies, and the .NET class library are embedded in the
NRO's RomFS. Two runtime files stay on the SD card because the launcher reads
them before it mounts RomFS: `sd:/mono/config.ini` (logging, launch, and
full-application-mode settings) and the ICU data file it names,
`sd:/mono/etc/icudt77l.dat`. Every build writes both to its `sdcard/` payload.

tModLoader mods come from three sources: curated, pinned source repositories
(each catalog entry records its license, and several publish source without one),
repository commits the user adds in the guided build, and `.tmod` packages the
user puts in `Mods/`, which are checked (tModLoader version, hook conversion,
members missing from this tModLoader) and used as they are. The resulting mod
assemblies are supplied in the SD-card payload of the user's own build, not
redistributed by this repository.
Because those assemblies are also AOT-compiled into the NRO, the SD-card `.tmod`
files must come from the same build: a Workshop copy with the same version
aborts at mod load. User saves and game content remain local to the user's
device.

## Current limits

- Vanilla has a hardware-tested release candidate, including audio, FPS toggle,
  multiplayer join, and default-clock frame-rate checks. Hosting a multiplayer
  game from the Switch is not supported.
- tModLoader remains experimental. The current Fargo's Souls build loads its
  mods and reaches a world on hardware; in-world v16 ran at a median of about
  55 fps (run 42). With all 11 mods, v17 averaged 33 fps over 40 minutes and
  paused for several seconds at each autosave (run 44).
  tModLoader multiplayer has not been verified.
- A clean-checkout, end-to-end from-source toolchain build remains unverified;
  new machines use the prebuilt toolchain. See [Building](BUILDING.md).
