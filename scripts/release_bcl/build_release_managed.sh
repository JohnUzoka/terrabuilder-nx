#!/bin/bash
# Build58: Release managed CoreLib + shared framework for libnx-arm64.
# Runs INSIDE localhost/monobuild:local with /build = ~/.cache/terraria-switch-build.
# Native runtime is NOT rebuilt here; the hardware-proven Debug-config (-O2) libmonosgen stays.
set -euo pipefail
cd /build/runtime-source
export ROOTFS_DIR=/opt/devkitpro
export ICU_NX_INSTALL_DIR=/mono-nx/icu/libnx
export NUGET_PACKAGES=/build/runtime-fix/nuget-packages
export SSL_CERT_FILE=/build/runtime-fix/amd-ca-bundle.crt
export CURL_CA_BUNDLE=$SSL_CERT_FILE REQUESTS_CA_BUNDLE=$SSL_CERT_FILE NODE_EXTRA_CA_CERTS=$SSL_CERT_FILE
export DOTNET_CLI_TELEMETRY_OPTOUT=1
./build.sh --subset mono.corelib+libs.sfx --cross --arch arm64 --os libnx \
  --configuration Release /p:RuntimeConfiguration=Debug "$@"
