#!/bin/bash
set -euo pipefail

# build_native_deps.sh
# Builds FNA3D, FAudio, and SDL2# as static libraries for libnx.
# Run inside the mono-nx Docker container.
#
# These build into native/build/ and install headers/libs into
# native/install/ under this repository.

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
BUILD_DIR="$ROOT_DIR/native/build"
INSTALL_DIR="$ROOT_DIR/native/install"

mkdir -p "$BUILD_DIR" "$INSTALL_DIR"

echo "=== Cloning FNA3D ==="
if [ ! -d "$BUILD_DIR/FNA3D" ]; then
    git clone --depth=1 https://github.com/FNA-XNA/FNA3D.git "$BUILD_DIR/FNA3D"
fi

echo "=== Cloning FAudio ==="
if [ ! -d "$BUILD_DIR/FAudio" ]; then
    git clone --depth=1 https://github.com/FNA-XNA/FAudio.git "$BUILD_DIR/FAudio"
fi

echo "=== Building FNA3D (static, OpenGL backend, SDL2) ==="
mkdir -p "$BUILD_DIR/FNA3D/build"
cd "$BUILD_DIR/FNA3D/build"

# FNA3D needs SDL2 and OpenGL headers, both available from devkitPro portlibs.
# We disable SDL3 and force the OpenGL driver which works with switch-mesa.
cmake .. \
    -DCMAKE_TOOLCHAIN_FILE="$DEVKITPRO/cmake/Switch.cmake" \
    -DBUILD_SHARED_LIBS=OFF \
    -DBUILD_SDL3=OFF \
    -DTRAQACING_SUPPORT=OFF \
    -DCMAKE_INSTALL_PREFIX="$INSTALL_DIR" \
    -DCMAKE_BUILD_TYPE=Release

make -j$(nproc)
make install

echo "=== Building FAudio (static) ==="
mkdir -p "$BUILD_DIR/FAudio/build"
cd "$BUILD_DIR/FAudio/build"

cmake .. \
    -DCMAKE_TOOLCHAIN_FILE="$DEVKITPRO/cmake/Switch.cmake" \
    -DBUILD_SHARED_LIBS=OFF \
    -DCMAKE_INSTALL_PREFIX="$INSTALL_DIR" \
    -DCMAKE_BUILD_TYPE=Release

make -j$(nproc)
make install

echo "=== Native deps built ==="
echo "Installed to: $INSTALL_DIR"
ls -la "$INSTALL_DIR/lib/"
