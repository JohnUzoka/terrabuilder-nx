#!/bin/bash
set -euo pipefail

# build_all.sh — runs the full build pipeline inside the Docker container.
# Run from the fna-nx-test directory after entering the mono-nx Docker container.

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

export MONO_NX_DIR="${MONO_NX_DIR:-$(cd "$ROOT_DIR/../.." && pwd)}"
export FNA_NX_INSTALL_DIR="$ROOT_DIR/native/install"

echo "============================================"
echo "  FNA on mono-nx — full build pipeline"
echo "============================================"
echo "  MONO_NX_DIR:       $MONO_NX_DIR"
echo "  FNA_NX_INSTALL_DIR: $FNA_NX_INSTALL_DIR"
echo "============================================"
echo ""

# 1. Patch dl_shim.c to register FNA3D and FAudio libraries
echo "[1/6] Patching dl_shim.c..."
"$SCRIPT_DIR/patch_dl_shim.sh"

# 2. Build FNA3D and FAudio as static libraries for libnx
echo ""
echo "[2/6] Building native dependencies (FNA3D, FAudio)..."
"$SCRIPT_DIR/build_native_deps.sh"

# 3. Generate dl_shim C files from compiled library symbols
echo ""
echo "[3/6] Generating dl_shim entries from compiled symbols..."
"$SCRIPT_DIR/gen_dl_shim.sh"

# 4. Build the custom interpreter NRO
echo ""
echo "[4/6] Building custom interpreter NRO..."
cd "$ROOT_DIR/native/interpreter"
make clean || true
make

# 5. Build the C# FNA test app
echo ""
echo "[5/6] Building C# FNA test app..."
"$SCRIPT_DIR/setup_fna_libs.sh"
cd "$ROOT_DIR/managed/fna_test"
dotnet build

# 6. Package SD card files
echo ""
echo "[6/6] Packaging SD card files..."
"$SCRIPT_DIR/package_sd_files.sh"

echo ""
echo "============================================"
echo "  Build complete!"
echo "============================================"
echo ""
echo "Output files:"
echo "  NRO:  $ROOT_DIR/native/interpreter/mono_nx_fna.nro"
echo "  DLL:  $ROOT_DIR/managed/fna_test/bin/Debug/net9.0/fna_test.dll"
echo "  SD:   $ROOT_DIR/sd_files/"
echo "  ZIP:  $ROOT_DIR/sd_files_fna_test.zip"
echo ""
echo "Copy sd_files/ contents to your SD card root."
echo "Launch mono_nx_fna.nro via hbmenu in FULL APPLICATION MODE."
echo "Check /mono/log.txt for errors."
