#!/usr/bin/env bash
# Build NaGaa95/mesa-switch OpenGL/EGL/GLES for Switch into <workdir>/ms-lib.
# Usage: scripts/mesa_switch/build.sh <workdir>
set -euo pipefail
W=$(realpath "$1")
HERE=$(dirname "$(realpath "$0")")
SRC="$W/mesa-switch-src"
IMG=localhost/mesa-switch-gl:local
[ -d "$SRC/.git" ] || git clone --depth 1 --branch main https://github.com/NaGaa95/mesa-switch "$SRC"
podman build -q -t "$IMG" -f "$HERE/Containerfile" "$HERE"
podman run --rm -v "$W":/g --entrypoint /bin/bash "$IMG" -c '
  set -euo pipefail
  export PATH=/opt/devkitpro/devkitA64/bin:/opt/devkitpro/tools/bin:$PATH
  cd /g/mesa-switch-src
  BUILD_DIR=/g/mesa-switch-build
  DESTDIR=/g/mesa-switch-install
  rm -rf "$BUILD_DIR" "$DESTDIR"
  meson setup "$BUILD_DIR" \
    --cross-file /g/mesa-switch-src/switch_cross_file.txt \
    --default-library=static \
    --prefix=/opt/devkitpro/portlibs/switch \
    --libdir=lib \
    --buildtype=release \
    -Doptimization=2 \
    -Db_lto=false \
    -Db_ndebug=true \
    -Dvulkan-drivers= \
    -Dgallium-drivers=nouveau \
    -Dgallium-rusticl=false \
    -Dplatforms=switch \
    -Degl-native-platform=switch \
    -Dglx=disabled \
    -Degl=enabled \
    -Dopengl=true \
    -Dgles1=enabled \
    -Dgles2=enabled \
    -Dvideo-codecs= \
    -Dshader-cache=enabled \
    -Dxmlconfig=auto \
    -Dexpat=auto \
    -Dtools=[] \
    -Dllvm=disabled \
    -Dshared-llvm=disabled \
    -Dcpp_rtti=false \
    -Dbuild-tests=false
  ninja -C "$BUILD_DIR" -j${NINJAJOBS:-4}
  meson install -C "$BUILD_DIR" --destdir "$DESTDIR"
'
rm -rf "$W/ms-lib"
mkdir -p "$W/ms-lib"
cp -a "$W/mesa-switch-install/opt/devkitpro/portlibs/switch/lib/"*.a "$W/ms-lib/"
sha256sum "$W/ms-lib/"*.a
