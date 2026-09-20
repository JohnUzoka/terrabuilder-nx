#!/bin/bash
set -euo pipefail

# setup_fna_libs.sh
# Clones FNA and SDL2-CS source trees for the managed build.
# FNA itself is pure C# — it compiles with dotnet build, no native deps needed
# at the C# layer. The native deps (FNA3D, FAudio, SDL2) are linked into the NRO.

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
LIB_DIR="$ROOT_DIR/managed/lib"

mkdir -p "$LIB_DIR"

echo "=== Cloning FNA ==="
if [ ! -d "$LIB_DIR/FNA" ]; then
    git clone --depth=1 --recurse-submodules https://github.com/FNA-XNA/FNA.git "$LIB_DIR/FNA"
fi

echo "=== FNA source ready at $LIB_DIR/FNA ==="
echo ""
echo "Note: FNA's .csproj targets older .NET. You may need to create a"
echo "SDK-style csproj wrapper or adjust FNA.csproj for net9.0."
echo "The FNA.dll.config (DLL map) is NOT used on Switch — the dl_shim"
echo "handles library name resolution. Delete or ignore FNADllMap.cs"
echo "if it causes build issues."
