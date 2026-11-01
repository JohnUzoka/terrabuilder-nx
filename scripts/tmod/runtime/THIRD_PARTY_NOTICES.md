# tmod-runtime: third-party notices

This optional toolchain component holds the open-source runtime parts that tModLoader
builds link and compile against. Nothing in it contains or is compiled from Terraria,
tModLoader or Re-Logic code or assets. How each file was built is in the terrabuilder-nx
repository: `scripts/tmod/runtime/README.md`, with per-file hashes in
`scripts/tmod/runtime/component-files.json`.

| Files | Project | Origin | License |
| --- | --- | --- | --- |
| `lib/libmonosgen-2.0.a`, `lib/libmono-component-*-static.a` | .NET runtime (Mono), libnx port | `github.com/JohnUzoka/dotnet_runtime` commit `1951b2f6811085f8d7fa5977feb67feb4954c0ab` (pin `7f043851` plus `mono-aot-alc-resolve`), Release libnx-arm64 | MIT (`licenses/dotnet-runtime-LICENSE.txt`, `licenses/dotnet-runtime-THIRD-PARTY-NOTICES.TXT`) |
| `lib/libSystem.Native.a` | .NET runtime System.Native, libnx port | Same fork, Debug libnx-arm64, with the libnx rename/CopyFile fix (fork commit `7f043851`) | MIT |
| `framework/`, `corelib/System.Private.CoreLib.dll` | .NET runtime shared framework and CoreLib, libnx port | Same fork, Release libnx-arm64 managed build of 2026-09-24 (`.dll`, `.pdb`, `.xml`; source revision in the repository README) | MIT |
| `System.Reflection.Metadata.dll` | .NET runtime System.Reflection.Metadata | `framework/System.Reflection.Metadata.dll` rewritten by terrabuilder-nx `scripts/tmod/metadata_io` (memory mapping off) | MIT |
| `aot-compiler/mono-aot-cross` | .NET runtime Mono AOT cross compiler with the LLVM backend | Same fork, branch `libnx` commit `289cdaa5` plus three local patches (repository README), linux-x64 host, libnx-arm64 target | MIT; LLVM parts Apache-2.0 WITH LLVM-exception (`licenses/LLVM-LICENSE.txt`) |
| `aot-compiler/{opt,llc,libc++.so.1,libc++abi.so.1}` | LLVM, libc++, libc++abi | Symlinks to the toolchain's `aot-compiler` component; no copy here | Apache-2.0 WITH LLVM-exception |
| `corelib-aot/`, `framework-aot/` | Mono AOT output for CoreLib, System.Linq, System.Text.RegularExpressions, System.Collections.Concurrent | Compiled from `corelib/` and `framework/`; the LLVM objects with `aot-compiler/mono-aot-cross`, System.Linq's non-LLVM object by an earlier Mono AOT compiler of the same fork | MIT (compiled .NET runtime code); LLVM-generated code under the LLVM exception |
