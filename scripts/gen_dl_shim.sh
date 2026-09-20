#!/bin/bash
set -euo pipefail

# gen_dl_shim.sh
# Generates dl_shim_FNA3D.c and dl_shim_FAudio.c from the EXPORTED SYMBOLS
# of the compiled static libraries — not from header parsing.
#
# This is critical: SYM_RESOLVE declares `extern void *Name()` for each entry.
# If a type name (struct/enum) gets in there, the link fails. Extracting from
# the compiled .a ensures only real symbols appear.
#
# Run after build_native_deps.sh, inside the Docker container.

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
INSTALL_DIR="$ROOT_DIR/native/install"
SHIM_DIR="$ROOT_DIR/native/shared"

PREFIX="${DEVKITPRO:-/opt/devkitpro}/devkitA64/bin/aarch64-none-elf-"

# FNA3D
echo "=== Generating FNA3D dl_shim ==="
FNA3D_LIB=$(find "$INSTALL_DIR" -name 'libFNA3D.a' | head -1)
if [ -z "$FNA3D_LIB" ]; then
    echo "ERROR: libFNA3D.a not found. Run build_native_deps.sh first."
    exit 1
fi

# Extract defined global symbols (functions) from the archive.
# Filter: uppercase T = text section (code), defined and global.
${PREFIX}nm "$FNA3D_LIB" 2>/dev/null | \
    awk '$2=="T" && $3 ~ /^FNA3D_/' | \
    awk '{print $3}' | sort -u > /tmp/fna3d_symbols.txt

echo "  Found $(wc -l < /tmp/fna3d_symbols.txt) FNA3D symbols"

cat > "$SHIM_DIR/dl_shim_FNA3D.c" << EOF
// Auto-generated from libFNA3D.a exported symbols.
// DO NOT EDIT - regenerate with gen_dl_shim.sh after rebuilding FNA3D.
#include "../shared_mono_nx/dl_shim_base.h"

void *getsym_FNA3D(const char *name)
{
EOF

while IFS= read -r sym; do
    printf '\tSYM_RESOLVE(%s);\n' "$sym" >> "$SHIM_DIR/dl_shim_FNA3D.c"
done < /tmp/fna3d_symbols.txt

printf '\treturn NULL;\n}\n' >> "$SHIM_DIR/dl_shim_FNA3D.c"

# FAudio
echo "=== Generating FAudio dl_shim ==="
FAUDIO_LIB=$(find "$INSTALL_DIR" -name 'libFAudio.a' | head -1)
if [ -z "$FAUDIO_LIB" ]; then
    echo "ERROR: libFAudio.a not found. Run build_native_deps.sh first."
    exit 1
fi

${PREFIX}nm "$FAUDIO_LIB" 2>/dev/null | \
    awk '$2=="T" && $3 ~ /^FAudio|^FACT|^FAPOFX|^XNA_|^F3D/' | \
    awk '{print $3}' | sort -u > /tmp/faudio_symbols.txt

echo "  Found $(wc -l < /tmp/faudio_symbols.txt) FAudio family symbols"

cat > "$SHIM_DIR/dl_shim_FAudio.c" << EOF
// Auto-generated from libFAudio.a exported symbols.
// DO NOT EDIT - regenerate with gen_dl_shim.sh after rebuilding FAudio.
#include "../shared_mono_nx/dl_shim_base.h"

// FNA loads all audio functions under the "FAudio" library name.
// FAudio, FAudioFX, FACT, and FAPOFX are all compiled into libFAudio.a.
void *getsym_FNAudio(const char *name)
{
EOF

while IFS= read -r sym; do
    printf '\tSYM_RESOLVE(%s);\n' "$sym" >> "$SHIM_DIR/dl_shim_FAudio.c"
done < /tmp/faudio_symbols.txt

printf '\treturn NULL;\n}\n' >> "$SHIM_DIR/dl_shim_FAudio.c"

echo "=== dl_shim files generated ==="
echo "  $SHIM_DIR/dl_shim_FNA3D.c ($(wc -l < "$SHIM_DIR/dl_shim_FNA3D.c") lines)"
echo "  $SHIM_DIR/dl_shim_FAudio.c ($(wc -l < "$SHIM_DIR/dl_shim_FAudio.c") lines)"
