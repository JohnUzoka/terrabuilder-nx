#!/bin/bash
set -euo pipefail

# package_sd_files.sh
# Assembles the SD card directory after building everything.

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
SD_DIR="$ROOT_DIR/sd_files"

rm -rf "$SD_DIR"
mkdir -p "$SD_DIR/mono" "$SD_DIR/switch/fna_test"

# Copy the custom NRO
NRO="$ROOT_DIR/native/interpreter/mono_nx_fna.nro"
if [ ! -f "$NRO" ]; then
    echo "ERROR: $NRO not found. Build the interpreter first."
    exit 1
fi
cp "$NRO" "$SD_DIR/mono/"

# Copy the managed test DLL
TEST_DLL="$ROOT_DIR/managed/fna_test/bin/Debug/net9.0/fna_test.dll"
if [ ! -f "$TEST_DLL" ]; then
    echo "ERROR: $TEST_DLL not found. Build the C# app first."
    exit 1
fi
cp "$TEST_DLL" "$SD_DIR/switch/fna_test/"

# Copy all FNA managed DLLs (FNA.dll, SDL2-CS.dll, etc.)
FNA_BIN="$ROOT_DIR/managed/fna_test/bin/Debug/net9.0/"
cp "$FNA_BIN"/*.dll "$SD_DIR/switch/fna_test/" 2>/dev/null || true

# Copy mono runtime DLLs from the existing mono-nx sd_files
if [ -d "$MONO_NX_DIR/sd_files/mono" ]; then
    cp -r "$MONO_NX_DIR/sd_files/mono/"* "$SD_DIR/mono/"
elif [ -n "${MONO_NX_ROOT:-}" ]; then
    # If sd_files doesn't exist, copy from the runtime build
    mkdir -p "$SD_DIR/mono/lib_net9.0" "$SD_DIR/mono/framework_net9.0" "$SD_DIR/mono/etc"
    cp "$MONO_NX_ROOT/artifacts/bin/mono/libnx.arm64.Debug/"*.dll "$SD_DIR/mono/lib_net9.0/"
    cp "$MONO_NX_ROOT/artifacts/bin/runtime/net9.0-libnx-Debug-arm64/"*.dll "$SD_DIR/mono/framework_net9.0/"
    cp "$ICU_NX_INSTALL_DIR/share/icu/77.1/icudt77l.dat" "$SD_DIR/mono/etc/" 2>/dev/null || true
fi

# Write config.ini
cat > "$SD_DIR/mono/config.ini" << 'EOF'
[mono]
logging = true
runtime_logging = true
icu = /mono/etc/icudt77l.dat
assembly_dir = "/mono/lib_net9.0;/mono/framework_net9.0;/switch/fna_test"
config_dir = /mono/etc
default_assembly = /switch/fna_test/fna_test.dll

[nx]
file_io_redirect = /mono/log.txt
exit_process_on_end = true
force_full_application = true
EOF

# Create zip
(cd "$SD_DIR" && zip -r -9 "$ROOT_DIR/sd_files_fna_test.zip" .)

echo "=== SD files packaged ==="
echo "  Directory: $SD_DIR"
echo "  Zip: $ROOT_DIR/sd_files_fna_test.zip"
echo ""
echo "Copy to SD card root, then launch mono_nx_fna.nro in hbmenu"
echo "(full application mode / title takeover)."
echo "Check /mono/log.txt on the SD card for errors."
