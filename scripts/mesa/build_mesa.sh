#!/bin/bash
# Rebuild devkitPro's switch-mesa 20.1.0-5 (libEGL.a + libglapi.a) with the glthread patch (builds 75–77).
# Host: ./build_mesa.sh <workdir>   (needs podman; ~20 min, ~1.5 GB). STOCK=1 skips the glthread patch
# (for comparing against the installed portlibs library); output then goes to <workdir>/stock-lib/.
# Output: <workdir>/glthread-lib/{libEGL.a,libglapi.a}; link them ahead of portlibs with
#   R58_EXTRA_LDFLAGS="-L/build/gfx/v76-lib ..." (build_native.py, candidate only).
set -euo pipefail
W=$(realpath "$1"); HERE=$(dirname "$(realpath "$0")"); PATCHES=$HERE/../../native/patches
PKG=https://raw.githubusercontent.com/devkitPro/pacman-packages/master/switch/mesa
mkdir -p "$W" && cd "$W"
IMG=localhost/mesabuild-r28:local
podman build -q -t $IMG -f "$HERE/Containerfile" "$HERE"
OUT=glthread-lib; PATCH="$PATCHES/mesa-20.1.0-switch-glthread.patch"
[ "${STOCK:-0}" = 1 ] && { OUT=stock-lib; PATCH=; }
[ -f mesa-20.1.0-rc3.tar.xz ] || curl -sfLO https://archive.mesa3d.org/older-versions/20.x/mesa-20.1.0-rc3.tar.xz
echo "c90b75ea34302ebde9b81b87c5642fa864c40fe9c4ad34ce0793170c1413168d  mesa-20.1.0-rc3.tar.xz" | sha256sum -c -
for f in switch-mesa-20.1.0-5.patch gl_XML.py.patch glX_XML.py.patch; do [ -f $f ] || curl -sfLO $PKG/$f; done
rm -rf mesa-20.1.0-rc3 && tar xf mesa-20.1.0-rc3.tar.xz && cd mesa-20.1.0-rc3
for p in ../switch-mesa-20.1.0-5.patch ../gl_XML.py.patch ../glX_XML.py.patch $PATCH; do patch -p1 -s -i "$p"; done
# PKGBUILD: meson-cross.sh switch (buildtype=plain, devkitPro -O2 flags) -Db_ndebug=true. The current
# newlib declares timespec_get only in toolchains newer than r28; then the switch threads.h shim must be skipped.
podman run --rm -v "$W":/g --entrypoint /bin/bash $IMG -c '
  set -e; cd /g/mesa-20.1.0-rc3
  /opt/devkitpro/meson-toolchain.sh switch > ../crossfile.txt
  if grep -q timespec_get /opt/devkitpro/devkitA64/aarch64-none-elf/include/time.h; then
    sed -i "s/^\(c\|cpp\)_args = \[.-D__SWITCH__./&,'"'"'-DHAVE_TIMESPEC_GET'"'"'/" ../crossfile.txt
    grep -q HAVE_TIMESPEC_GET ../crossfile.txt; fi
  meson setup --buildtype=plain --cross-file=../crossfile.txt --default-library=static \
    --prefix=/opt/devkitpro/portlibs/switch --libdir=lib build -Db_ndebug=true > ../setup.log
  ninja -C build > ../build.log'
mkdir -p ../$OUT && cp build/src/egl/libEGL.a build/src/mapi/shared-glapi/libglapi.a ../$OUT/
sha256sum ../$OUT/*.a
