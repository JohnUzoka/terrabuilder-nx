# terrabuilder-nx

Builds a Nintendo Switch homebrew NRO of desktop Terraria (FNA, .NET) from **your own
copy of the game**, running on Mono via [mono-nx](https://github.com/JohnUzoka/mono-nx):
static AOT (optionally through LLVM), interpreter fallback, no JIT.

**This repository contains no game files.** Terraria binaries and content are supplied
by the user at build time and never committed; `.gitignore` blocks NROs, DLLs, EXEs,
XNB assets and hardware logs.

## Repositories

| Repo | Pinned revision | Role |
| --- | --- | --- |
| `JohnUzoka/dotnet_runtime` (fork of `exelix11/dotnet_runtime`) | branch `terrabuilder-nx` = `289cdaa5` + 4 commits | Mono runtime, CoreLib/framework for libnx. Adds the AOT switch-table allocator fix, the Release-build Sockets fix, absolute-address fake-mmap alignment and POSIX TLS destructor semantics. |
| `JohnUzoka/mono-nx` | branch `fna-support` @ `8be547c` | Launcher host, `native/shared` dl-shims (FNA3D/FAudio/SDL3/stub registrations). Mounted as `/mono-nx/native`. |
| this repo | | FNA launcher overlay (`native/`), managed helpers, Terraria IL patch tooling, AOT/LLVM/link/verify pipeline, docs. |

The native overlay (`native/interpreter`, `native/shared`) builds against mono-nx's
`native/shared` through a `shared_mono_nx` symlink; see `native/interpreter/Makefile`.

## Layout

- `native/` launcher overlay: `interpreter/` (main.c, Makefile, AOT method-table linker
  script), `shared/` (input latch, FNA3D/FAudio/SDL3 shims), `patches/` (runtime source
  patches, also committed on the dotnet_runtime fork), `tests/` (host regressions).
- `managed/` small helper assemblies (`NxInputDiag`, FNA test app).
- `scripts/` build tooling:
  - `compile_terraria_aot.py`: prepare, AOT-compile (optionally `--llvm-module`), verify
    MVID bindings and emit the module registration header.
  - `release_bcl/`: the current pipeline (builds 58-65): `build_release_managed.sh`
    (Release CoreLib/framework), `build_runtime_release.sh` (Release native runtime),
    `build_llvm_cross.sh` (LLVM-enabled AOT cross compiler), `build_aot.py`,
    `build_fna_llvm.py` (recompile one module through LLVM), `build_native.py`
    (link + package, with an exact build-52 replay as control), `verify_artifact.py`.
  - `patch_*`: Mono.Cecil IL patch sets applied to the user's game assembly. The
    `*_profile` sets are measurement builds only (see below).
  - `analyze_*`: parsers for the profiler log formats.
- `docs/findings.md`: the full engineering log (every build, hardware result, dead end).
- `docs/testing.md`: per-build NRO table and hardware test procedures.

## Profiling is opt-in

Default builds carry no profiling:

- **Frame timing** (`NX_PHASE` log lines, SDL_PollEvent / FNA3D_SwapBuffers wrappers):
  `make MONO_NX_PHASE_TIMING=1`. Without it the managed hook entry points stay (patched
  Terraria calls them every frame) but only drive the input latch. The timing-on object
  is instruction-identical to build 42's `nx_input.o`, which builds 58-65 link.
- **Profiler IL patches** (`scripts/patch_*_profile`): separate measurement builds,
  never applied by default. `patch_time_logger` is not a profiler: it is the build-41
  fix that is part of every shipped build.
- **GC/memory stats** (`NX_GC` lines): `MONO_NX_GC_STATS=1`. The optional SD file
  `/mono/gc_params.txt` (SGen `MONO_GC_PARAMS`) is honoured by builds from 66 on.

Note: the `release_bcl` pipeline currently replays build 42's launcher objects, which
were built with frame timing on, so NROs 58-65 all include `NX_PHASE`. The toggle takes
effect when the launcher objects are rebuilt from this source.

## Build environment

Podman images `localhost/monobuild:local` (devkitA64, .NET SDK) and
`localhost/monobuild-llvm:local` (adds clang-19 for the LLVM cross compiler). Host work
directory defaults to `~/.cache/terraria-switch-build` (override with
`TERRABUILDER_CACHE`), mounted as `/build`; this repo is mounted read-only as `/work`.
Exact recipes for every build are in `docs/findings.md`.

## CLI quickstart

The first-release CLI is `./terrabuilder` and uses only Python's standard library
on the host. It builds from the user's own clean GOG Terraria install; game files,
patched assemblies, RomFS trees, icons, and game AOT objects stay local.

```sh
./terrabuilder doctor
./terrabuilder toolchain pack --workdir ~/.cache/terrabuilder
./terrabuilder build --target vanilla \
  --game-dir "$HOME/GOG Games/Terraria1_4_5_8/game" \
  --profile release --workdir ~/.cache/terrabuilder -y
```

Outputs are `~/.cache/terrabuilder/out/Terraria.nro` and a JSON receipt. Use
`--profile profiler` for a profiler-enabled NRO at `Terraria-profiler.nro`.
Heavy container steps are serialized with
`~/.cache/terraria-switch-build/.heavy.lock`; the default workdir is
`~/.cache/terrabuilder` and can be overridden with `--workdir`.

`terrabuilder build` without flags prompts for `vanilla | tmodloader` and the game
directory. tModLoader currently exits cleanly with a "coming in this release"
message while its pipeline is wired separately.

## Status

Current working build: 64 (LLVM Terraria + FNA + CoreLib, Release CoreLib/framework);
65 (plus Release native runtime) is awaiting hardware. See `docs/findings.md`.
