#!/bin/bash
set -euo pipefail

# patch_dl_shim.sh
# Patches mono-nx's native/shared/dl_shim.c to register FNA3D and FAudio.
# Run this once before building the interpreter.

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
MONO_NX_DIR="${MONO_NX_DIR:-$(cd "$ROOT_DIR/../.." && pwd)}"
DL_SHIM="$MONO_NX_DIR/native/shared/dl_shim.c"

if [ ! -f "$DL_SHIM" ]; then
    echo "ERROR: $DL_SHIM not found. Set MONO_NX_DIR to your mono-nx checkout."
    exit 1
fi

# Check if already patched
if grep -q 'FNA3D' "$DL_SHIM"; then
    echo "dl_shim.c already patched with FNA3D. Skipping."
    exit 0
fi

# Insert library registrations after the existing OPENAL block
# Find the line "#if defined(DLSHIM_OPENAL)" block and insert after it
awk '
/#if defined\(DLSHIM_OPENAL\)/ { in_openal = 1 }
in_openal && /^#endif/ {
    print
    print ""
    print "// FNA native libraries"
    print "#if defined(DLSHIM_FNA3D)"
    print "REGISTER_LIBRARY(FNA3D, \"FNA3D\", 0x110)"
    print "#endif"
    print ""
    print "#if defined(DLSHIM_FNA)"
    print "REGISTER_LIBRARY(FNAudio, \"FAudio\", 0x111)"
    print "#endif"
    in_openal = 0
    next
}
{ print }
' "$DL_SHIM" > "$DL_SHIM.tmp" && mv "$DL_SHIM.tmp" "$DL_SHIM"

# Add CHECK_LIB_NAME entries to dlshim_loadLibrary
awk '
/#if defined\(DLSHIM_OPENAL\)/ { in_openal = 1 }
in_openal && /^[[:space:]]*#endif/ {
    print
    print ""
    print "\t#if defined(DLSHIM_FNA3D)"
    print "\tCHECK_LIB_NAME(name, FNA3D);"
    print "\t#endif"
    print ""
    print "\t#if defined(DLSHIM_FNA)"
    print "\tCHECK_LIB_NAME(name, FNAudio);"
    print "\t#endif"
    in_openal = 0
    next
}
{ print }
' "$DL_SHIM" > "$DL_SHIM.tmp" && mv "$DL_SHIM.tmp" "$DL_SHIM"

# Add CHECK_LIB_SYMBOL entries to dlshim_getSymbol
awk '
/#if defined\(DLSHIM_OPENAL\)/ { in_openal = 1 }
in_openal && /^[[:space:]]*#endif/ {
    print
    print ""
    print "\t#if defined(DLSHIM_FNA3D)"
    print "\tCHECK_LIB_SYMBOL(FNA3D)"
    print "\t#endif"
    print ""
    print "\t#if defined(DLSHIM_FNA)"
    print "\tCHECK_LIB_SYMBOL(FNAudio)"
    print "\t#endif"
    in_openal = 0
    next
}
{ print }
' "$DL_SHIM" > "$DL_SHIM.tmp" && mv "$DL_SHIM.tmp" "$DL_SHIM"

echo "Patched $DL_SHIM with FNA3D and FAudio library registrations."
