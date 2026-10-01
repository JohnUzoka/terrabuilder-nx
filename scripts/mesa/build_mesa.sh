#!/bin/bash
# Rebuild devkitPro's switch-mesa 20.1.0-5 (libEGL.a + libglapi.a) with the glthread patch (build75).
# Host: ./build_mesa.sh <workdir>   (needs podman and localhost/monobuild:local; ~20 min, ~1.5 GB)
# Output: <workdir>/v75-lib/{libEGL.a,libglapi.a}; link them ahead of portlibs with
#   R58_EXTRA_LDFLAGS="-L/build/gfx/v75-lib ..." (build_native.py, candidate only).
set -euo pipefail
W=$(realpath "$1"); HERE=$(dirname "$(realpath "$0")"); PATCHES=$HERE/../../native/patches
PKG=https://raw.githubusercontent.com/devkitPro/pacman-packages/master/switch/mesa
mkdir -p "$W" && cd "$W"
podman build -q -t localhost/mesabuild:local -f "$HERE/Containerfile" "$HERE"
[ -f mesa-20.1.0-rc3.tar.xz ] || curl -sfLO https://archive.mesa3d.org/older-versions/20.x/mesa-20.1.0-rc3.tar.xz
echo "c90b75ea34302ebde9b81b87c5642fa864c40fe9c4ad34ce0793170c1413168d  mesa-20.1.0-rc3.tar.xz" | sha256sum -c -
for f in switch-mesa-20.1.0-5.patch gl_XML.py.patch glX_XML.py.patch; do [ -f $f ] || curl -sfLO $PKG/$f; done
rm -rf mesa-20.1.0-rc3 && tar xf mesa-20.1.0-rc3.tar.xz && cd mesa-20.1.0-rc3
for p in ../switch-mesa-20.1.0-5.patch ../gl_XML.py.patch ../glX_XML.py.patch "$PATCHES/mesa-20.1.0-switch-glthread.patch"; do patch -p1 -s -i "$p"; done
# PKGBUILD: meson-cross.sh switch (buildtype=plain, devkitPro -O2 flags) -Db_ndebug=true. The current
# devkitA64 newlib already declares timespec_get, so the switch threads.h shim must be skipped.
podman run --rm -v "$W":/g --entrypoint /bin/bash localhost/mesabuild:local -c '
  set -e; cd /g/mesa-20.1.0-rc3
  /opt/devkitpro/meson-toolchain.sh switch > ../crossfile.txt
  sed -i "s/^\(c\|cpp\)_args = \[.-D__SWITCH__./&,'"'"'-DHAVE_TIMESPEC_GET'"'"'/" ../crossfile.txt
  grep -q HAVE_TIMESPEC_GET ../crossfile.txt
  meson setup --buildtype=plain --cross-file=../crossfile.txt --default-library=static \
    --prefix=/opt/devkitpro/portlibs/switch --libdir=lib build -Db_ndebug=true > ../setup.log
  ninja -C build > ../build.log'
mkdir -p ../v75-lib && cp build/src/egl/libEGL.a build/src/mapi/shared-glapi/libglapi.a ../v75-lib/
sha256sum ../v75-lib/*.a
