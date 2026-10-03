#!/bin/bash
set -euo pipefail

# build_native_deps.sh
# Builds FNA3D, FAudio, and SDL2# as static libraries for libnx.
# Run inside the mono-nx Docker container.
#
# These build into native/build/ and install headers/libs into
# native/install/ under this repository, unless TERRABUILDER_NATIVE_BUILD_DIR /
# TERRABUILDER_NATIVE_INSTALL_DIR point elsewhere (e.g. scratch space, when this
# repo is mounted read-only -- see scripts/toolchain/build_from_source.py).

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
BUILD_DIR="${TERRABUILDER_NATIVE_BUILD_DIR:-$ROOT_DIR/native/build}"
INSTALL_DIR="${TERRABUILDER_NATIVE_INSTALL_DIR:-$ROOT_DIR/native/install}"

# Pinned to the commits the shipped toolchain's native-deps component was built from
# (see THIRD_PARTY_NOTICES.md). Override by exporting FNA3D_COMMIT/FAUDIO_COMMIT before
# running this script if you intentionally want to track a newer upstream commit.
FNA3D_COMMIT="${FNA3D_COMMIT:-2c616bf8efd8bfbce288cb1fc7ca3677064c51d9}"
FAUDIO_COMMIT="${FAUDIO_COMMIT:-41bfee955aa98c03ddb6f26e62a99afdf3c04237}"

mkdir -p "$BUILD_DIR" "$INSTALL_DIR"

echo "=== Cloning FNA3D ($FNA3D_COMMIT) ==="
if [ ! -d "$BUILD_DIR/FNA3D" ]; then
    git clone https://github.com/FNA-XNA/FNA3D.git "$BUILD_DIR/FNA3D"
fi
git -C "$BUILD_DIR/FNA3D" checkout -q "$FNA3D_COMMIT"
# Pins MojoShader to the commit FNA3D's own superproject index records for $FNA3D_COMMIT
# (ad5dff84830c2863c841f4b1f4e3df78c705b383 at the pin above), matching THIRD_PARTY_NOTICES.md.
git -C "$BUILD_DIR/FNA3D" submodule update --init --recursive

echo "=== Cloning FAudio ($FAUDIO_COMMIT) ==="
if [ ! -d "$BUILD_DIR/FAudio" ]; then
    git clone https://github.com/FNA-XNA/FAudio.git "$BUILD_DIR/FAudio"
fi
git -C "$BUILD_DIR/FAudio" checkout -q "$FAUDIO_COMMIT"
git -C "$BUILD_DIR/FAudio" submodule update --init --recursive

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
# FNA3D's CMakeLists.txt only defines install() targets when BUILD_SHARED_LIBS=ON
# ("Installation (Shared only!)"), so for our static build there is no `install`
# make target -- copy the built static libs and public headers by hand instead.
# (No .pc/cmake-config files exist in this mode either; nothing downstream needs them.)
mkdir -p "$INSTALL_DIR/lib" "$INSTALL_DIR/include"
cp libFNA3D.a libmojoshader.a "$INSTALL_DIR/lib/"
cp ../include/*.h "$INSTALL_DIR/include/"
cp ../MojoShader/mojoshader.h "$INSTALL_DIR/include/"

echo "=== Building FAudio (static) ==="
mkdir -p "$BUILD_DIR/FAudio/build"
cd "$BUILD_DIR/FAudio/build"

cmake .. \
    -DCMAKE_TOOLCHAIN_FILE="$DEVKITPRO/cmake/Switch.cmake" \
    -DBUILD_SHARED_LIBS=OFF \
    -DBUILD_SDL3=OFF \
    -DCMAKE_INSTALL_PREFIX="$INSTALL_DIR" \
    -DCMAKE_BUILD_TYPE=Release

make -j$(nproc)
make install

echo "=== Native deps built ==="
echo "Installed to: $INSTALL_DIR"
ls -la "$INSTALL_DIR/lib/"
