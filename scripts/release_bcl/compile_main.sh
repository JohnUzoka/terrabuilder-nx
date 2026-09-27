#!/bin/bash
# Compile launcher main.o the way build42's Makefile did (flags recovered from
# main.o DW_AT_producer + main.d). $1 = main.c, $2 = output .o, rest = extra -D.
# Mounts expected: /build, /mono-nx (SDK, ro), /mono-nx/native (fork clone, ro).
set -euo pipefail
SRC=$1; OUT=$2; shift 2
MONO_INC=/mono-nx/dotnet_runtime/artifacts/bin/mono/libnx.arm64.Debug/include/mono-2.0
exec /opt/devkitpro/devkitA64/bin/aarch64-none-elf-gcc \
  -march=armv8-a+crc+crypto -mtune=cortex-a57 -mtp=soft -fPIE -g -O2 -ffunction-sections \
  -D__SWITCH__ \
  -DMONO_NX_USE_AOT=1 -DMONO_NX_GL_COMPAT=1 -DMONO_NX_USE_ROMFS=1 "$@" \
  -I/build/aot42-windows \
  -I"$(dirname "$SRC")/../../shared" -I/build/nochroma42/native/shared \
  -I/mono-nx/native/shared -I/mono-nx/native/shared/third_party/ini \
  -I"$MONO_INC" \
  -I/opt/devkitpro/libnx/include -I/opt/devkitpro/portlibs/switch/include \
  -c "$SRC" -o "$OUT"
