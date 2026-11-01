# tmod-runtime: the runtime tModLoader builds use

`tmod-runtime` is an optional toolchain component (`terrabuilder_pkg/toolchain.py`
`OPTIONAL_COMPONENTS`). It holds the open-source runtime parts that tModLoader builds were
validated with and that differ from the vanilla toolchain. It contains no Terraria,
tModLoader or Re-Logic code or assets.

`assemble.py` builds the component from the maintainer's engineering cache
(`~/.cache/terraria-switch-build`, "LEGACY" below). It checks every copied file against the
hash the validated builds recorded. `component-files.json` lists each file with its size,
SHA-256, executable bit (or symlink target) and LEGACY source. Assembling twice gives byte-identical output.

```sh
python3 -m scripts.tmod.runtime.assemble --legacy ~/.cache/terraria-switch-build \
  --toolchain ~/.cache/terrabuilder/toolchain --out <dir>/tmod-runtime --work <scratch> \
  --record scripts/tmod/runtime/component-files.json
```

## Layout

| Path | Contents | Replaces (LEGACY path the tModLoader scripts read) |
| --- | --- | --- |
| `lib/libmonosgen-2.0.a` | Release Mono runtime | `runtime-release/libmonosgen-2.0-release.a` |
| `lib/libmono-component-{debugger,diagnostics_tracing-stub,hot_reload-stub,marshal-ilgen}-static.a` | Release runtime components | `runtime-source/artifacts/obj/mono/libnx.arm64.Release/out/lib/` |
| `lib/libSystem.Native.a` | System.Native with the libnx rename/CopyFile fix; on the tModLoader link line (`scripts/tmod/launcher/link-arguments.txt`) in place of the sdk's Debug archive | `tmod/save-exit/files/libSystem.Native.a` |
| `framework/` | All 443 files of the Release shared framework (168 `.dll`, 168 `.pdb`, 107 `.xml`); the hardware-tested RomFS root carries the same files | `runtime-source/artifacts/bin/runtime/net9.0-libnx-Release-arm64/` |
| `corelib/System.Private.CoreLib.dll` | Release CoreLib (RomFS `mono/lib_net9.0/`) | `runtime-source/artifacts/bin/mono/libnx.arm64.Release/` |
| `System.Reflection.Metadata.dll` | `framework/System.Reflection.Metadata.dll` after `scripts/tmod/metadata_io` | `tmod/tmod27/input/System.Reflection.Metadata.dll` |
| `aot-compiler/mono-aot-cross` | LLVM-enabled Mono AOT cross compiler | `runtime-llvm/artifacts/bin/mono/linux.x64.Debug/cross/linux-x64/libnx-arm64/` |
| `aot-compiler/{opt,llc,libc++.so.1,libc++abi.so.1}` | Relative symlinks to the `aot-compiler` component, whose files are byte-identical to the ones in `runtime-llvm` | same directory |
| `corelib-aot/` | LLVM CoreLib objects with enlarged trampoline pools, `manifest.json`, `aot.log` | `tmod/corelib/pools-llvm-v72/` |
| `framework-aot/` | Reused AOT objects for System.Linq (non-LLVM), System.Text.RegularExpressions and System.Collections.Concurrent (LLVM), with their records in `framework-aot.json` and the compile logs | `tmod/tmodcli-ddcb095edfa5/aot/` |
| `THIRD_PARTY_NOTICES.md`, `licenses/` | Notices (copied from this directory) and license texts | — |

How to use the component:

- **Running the compiler.** The compiler's RUNPATH is empty (`:::::::`). Run it as
  `LLVM_DIR=/toolchain/tmod-runtime/aot-compiler` with `LD_LIBRARY_PATH=$LLVM_DIR` and
  `llvm-path=$LLVM_DIR/`, as the scripts already do with the LEGACY directory.
- **Paths in the JSON files.** In `corelib-aot/manifest.json` and
  `framework-aot/framework-aot.json`, object paths are relative to the file's own directory.
  Assembly paths are relative to the toolchain root.

## Provenance

The fork is `github.com/JohnUzoka/dotnet_runtime`. The public pin is `7f043851`, the
`terrabuilder-nx` branch named in `toolchain.lock.json`.

### Release runtime (`lib/libmonosgen-2.0.a`, component libraries)

**Source revision.** LEGACY `runtime-source` is at local commit `1951b2f` on branch
`aot-load-image-alc`. Its parent is the pin, so the runtime is the pin plus one commit:

- That commit is exported in `patches/runtime-source-0001-mono-aot-alc-resolve.patch`, made
  with `git format-patch 7f043851..1951b2f`.
- Apart from `index` lines, it is identical to `native/patches/mono-aot-alc-resolve.patch`.
- The working tree has no other content changes, only file-mode bits and the type of
  `src/native/external/libunwind/README.md`.

**Build.** The runtime was built with BUILDING.md §3.4 (`build_runtime_release.sh`). The LEGACY
copy of that script differs from `scripts/release_bcl/build_runtime_release.sh` only in its
NuGet, CA-bundle and parallelism environment lines. The timeline shows the build used
`1951b2f`:

| Time (2026-09-30) | Event |
| --- | --- |
| 01:20:35 | Commit `1951b2f` (reflog) |
| 01:25:29 | `logs/runtime-release.log` ends, after a 2 min 45 s build |
| 01:25:29 | Archive mtime |

The tModLoader builds that ran on hardware record the git commit `1951b2f` and SHA-256
`5c0ecd29…` (`tmod/tmod27/input/modset.json`).

**Differences from the vanilla toolchain.** The vanilla toolchain's `runtime/lib` is built from
the pin plus *both* `native/patches/mono-coop-managed-allocator.patch` and
`mono-aot-alc-resolve.patch`. This runtime lacks the coop-managed-allocator change: the
`runtime-source` `sgen-client-mono.h` has no `ENABLE_COOP_SUSPEND` guard. That missing patch is
one reason the archives differ. The other reasons, such as build environment, were not
isolated.

### Release framework and CoreLib (`framework/`, `corelib/`)

**Build.** These came from BUILDING.md §3.3 (`build_release_managed.sh`, build 58).

**Source revision: not recorded.** The files are dated 2026-09-24 23:06–23:15. That is earlier
than the current `runtime-source` clone (2026-09-30 00:51), so they were carried over from an
earlier checkout. The fork's `libnx` branch tip (`289cdaa5`) and `1951b2f` differ in managed
code only in `SocketAsyncEngine.Libnx.cs`, which is the DEBUG guard of
`native/patches/runtime-release-sockets-console.patch`.

- The shipped `System.Net.Sockets.dll` does not contain the string `System.Console`. That is
  consistent with the guard being present [INFERENCE].
- A rebuild from the pin should give equivalent code but different bytes and MVIDs. The vanilla
  toolchain's from-source `runtime/framework` differs from these files in all 168 DLLs.

### System.Native (`lib/libSystem.Native.a`)

**Build.** This is a Debug libnx-arm64 build of System.Native, built 2026-09-29 11:37–11:40 in
`/build/runtime-source`. The DWARF shows GCC 15.2.0 with `-g -O0`.

**Source change.** The change is fork commit `7f043851`, "System.Native (libnx): POSIX
rename-over-existing and ignore fchmod ENOSYS in CopyFile". It was committed that evening and
is part of the pin. It is exported as `patches/system-native-7f043851.patch`.

**Evidence.** The archive's DWARF declaration lines match `7f043851`'s `pal_io.c` and not its
parent's:

| Function | Line in the archive | Line in `7f043851` | Line in its parent |
| --- | --- | --- | --- |
| `SystemNative_Rename` | 1257 | 1257 | 1257 |
| `SystemNative_CopyFile` | 1433 | 1433 | 1372 |
| `SystemNative_FChMod` | 733 | 733 | 733 |

`tmod/save-exit/files/proof.log` and `pal_fault_test.c` are the fault-injection test of
that change; all 5 tests pass.

**Build command: not recorded.** mono-nx builds native libraries with
`ROOTFS_DIR=/opt/devkitpro ./build.sh --subset libs.native --cross -a arm64 --os libnx` in
Debug, its default configuration. That is the likely command [INFERENCE]. All 28 archive
members differ from the sdk's Debug `libSystem.Native.a` (`37c167c1…`), which was built from
`/mono-nx/dotnet_runtime`.

### LLVM AOT cross compiler (`aot-compiler/mono-aot-cross`)

**Source revision.** LEGACY `runtime-llvm` is a detached checkout of `289cdaa5`, the fork's
`libnx` branch tip and an ancestor of the pin, with uncommitted changes. Those changes are
exported as `patches/runtime-llvm-289cdaa5-worktree.diff`. Its hunks equal
`native/patches/runtime-release-sockets-console.patch`, `mono-coop-managed-allocator.patch` and
`mono-switch-table-data-memory.patch`.

**Build.** The compiler was built with BUILDING.md §3.5 (`build_llvm_cross.sh`, in
`localhost/monobuild-llvm:local`). The LEGACY copy of that script differs from
`scripts/release_bcl/build_llvm_cross.sh` only in its environment lines. The binary is dated
2026-09-30 20:10, right after the last source edits at 20:09.

**Differences from the vanilla toolchain.** The vanilla toolchain's
`aot-compiler/mono-aot-cross` (`0c8cb50d…`) is a different build: the pin plus 2 patches, with
RUNPATH `$ORIGIN`. `opt`, `llc`, `libc++.so.1` and `libc++abi.so.1` are byte-identical, so
this component only links to them.

### CoreLib AOT (`corelib-aot/`)

`scripts/tmod/build_tmod_corelib_aot.py` built these objects with `CORELIB_LLVM=1` and
`CORELIB_VARIANT=pools-llvm-v72` (2026-10-02 00:54). The compiler was this
`mono-aot-cross`, and the input was `corelib/System.Private.CoreLib.dll`. The original command
is kept as `legacy_command` in `manifest.json`.

### Framework AOT (`framework-aot/`)

**LLVM objects.** System.Text.RegularExpressions and System.Collections.Concurrent were compiled
with `--llvm` by `build_tmod_modset_nro.py` in `tmod/tmodcli-ddcb095edfa5` (2026-10-22), using
this compiler. The logs record the commands. `tmod/tmod27` has its own copies of these
modules. Their `.o` files are identical; their `-llvm.o` files differ by 24 bytes, a
run-to-run difference [INFERENCE].

**System.Linq.** `System.Linq.dll.o` (2026-09-29) was carried forward by reuse
(tmod13f → tmod25 → tmod27 → ddcb). Its compile log and compiler are lost. The CLI reuses it
for every build.

### System.Reflection.Metadata

`scripts/tmod/metadata_io` turns memory mapping off in `framework/System.Reflection.Metadata.dll`.
Running it with the toolchain's .NET SDK 9.0.102 and Mono.Cecil 0.11.5 reproduces LEGACY's
file byte for byte (`82762d08…`). LEGACY's `release58` Cecil gives the same bytes.

## Differences from LEGACY

- **File bytes are unchanged.** Every file under `lib/`, `framework/`, `corelib/`,
  `aot-compiler/mono-aot-cross` and the AOT objects is byte-identical to its LEGACY source.
- **Modes are normalized.** Files are mode 0644, except `mono-aot-cross` (0755).
- **Some files are new.** `corelib-aot/manifest.json` and `framework-aot/framework-aot.json`
  are generated with relative paths. LEGACY's records use absolute `/build/...` container
  paths.
- **Some files are left out.** The component does not carry `mono-aot-cross.dbg`,
  `mono-aot-cross.v70`, or the CoreLib build's `IL/`, `.pdb`, `.xml` and `include/`. No
  tModLoader script reads them.
