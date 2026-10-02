#!/bin/bash
# Experiment: LLVM-enabled libnx-arm64 mono-aot-cross (feasibility for plan step 4).
# Follows mono-nx notes/writeup.md: (1) offsets header with the Switch toolchain,
# (2) linux-x64 cross compiler with the host toolchain -- here plus MonoAOTEnableLLVM.
# Runs INSIDE localhost/monobuild:local, /build = ~/.cache/terraria-switch-build.
set -euo pipefail
cd /build/runtime-llvm
export ICU_NX_INSTALL_DIR=/mono-nx/icu/libnx
export NUGET_PACKAGES=${NUGET_PACKAGES:-/build/nuget-packages}
if [ -n "${TERRABUILDER_CA_BUNDLE:-}" ]; then
  export SSL_CERT_FILE=$TERRABUILDER_CA_BUNDLE
  export CURL_CA_BUNDLE=$SSL_CERT_FILE REQUESTS_CA_BUNDLE=$SSL_CERT_FILE NODE_EXTRA_CA_CERTS=$SSL_CERT_FILE
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
if [ "${SKIP_STEP1:-0}" != 1 ]; then
  echo "== step 1: offsets header (Switch toolchain)"
  ROOTFS_DIR=/opt/devkitpro ./build.sh -s mono.aotcross /p:MonoGenerateOffsetsOSGroups=libnx
fi
echo "== step 2: linux-x64 cross compiler with LLVM (host toolchain)"
# LLVM 19's bundled libc++ headers need clang >= 19 (clang-14 fails in mini-llvm-cpp.cpp);
# image localhost/monobuild-llvm:local adds Debian bookworm's clang-19.
unset ROOTFS_DIR
./build.sh -s mono /p:AotHostArchitecture=x64 /p:AotHostOS=linux /p:MonoCrossAOTTargetOS=libnx \
  /p:SkipMonoCrossJitConfigure=true /p:BuildMonoAOTCrossCompilerOnly=true /p:MonoAOTEnableLLVM=true \
  /p:Compiler=clang-19
echo "== result"
find artifacts/bin/mono -name mono-aot-cross -path '*libnx*' -exec ls -la {} \;
find artifacts -maxdepth 6 -type f \( -name llc -o -name opt \) | head
