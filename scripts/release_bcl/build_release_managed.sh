#!/bin/bash
# Build58: Release managed CoreLib + shared framework for libnx-arm64.
# Runs INSIDE localhost/monobuild:local with /build = ~/.cache/terraria-switch-build.
# Native runtime is NOT rebuilt here; the hardware-proven Debug-config (-O2) libmonosgen stays.
set -euo pipefail
cd /build/runtime-source
export ROOTFS_DIR=/opt/devkitpro
export ICU_NX_INSTALL_DIR=/mono-nx/icu/libnx
export NUGET_PACKAGES=${NUGET_PACKAGES:-/build/nuget-packages}
if [ -n "${TERRABUILDER_CA_BUNDLE:-}" ]; then
  export SSL_CERT_FILE=$TERRABUILDER_CA_BUNDLE
  export CURL_CA_BUNDLE=$SSL_CERT_FILE REQUESTS_CA_BUNDLE=$SSL_CERT_FILE NODE_EXTRA_CA_CERTS=$SSL_CERT_FILE
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
./build.sh --subset mono.corelib+libs.sfx --cross --arch arm64 --os libnx \
  --configuration Release /p:RuntimeConfiguration=Debug "$@"
