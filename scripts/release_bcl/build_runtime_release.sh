#!/bin/bash
# Build65 step 1: native Mono runtime (libmonosgen + components) in Release for libnx-arm64.
# Same fork commit and source patch as runtime-fix (build36); Release drops ENABLE_CHECKED_BUILD
# (Debug config enables it) and uses -O3 -DNDEBUG. Runs INSIDE localhost/monobuild:local with
# /build = ~/.cache/terraria-switch-build and /mono-nx = recovery46/sdk-pristine (ro).
set -euo pipefail
cd /build/runtime-source
grep -q 'code_allocated' src/mono/mono/mini/mini-runtime.c || { echo 'allocator patch missing (use the terrabuilder-nx fork branch)'; exit 1; }
export ROOTFS_DIR=/opt/devkitpro
export ICU_NX_INSTALL_DIR=/mono-nx/icu/libnx
export NUGET_PACKAGES=/build/runtime-fix/nuget-packages
export SSL_CERT_FILE=/build/runtime-fix/amd-ca-bundle.crt
export CURL_CA_BUNDLE=$SSL_CERT_FILE REQUESTS_CA_BUNDLE=$SSL_CERT_FILE NODE_EXTRA_CA_CERTS=$SSL_CERT_FILE
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export CMAKE_BUILD_PARALLEL_LEVEL=12
./build.sh --subset mono.runtime --cross --arch arm64 --os libnx \
  --configuration Release --keepnativesymbols true /p:MonoVerboseBuild=true
echo RUNTIME_RELEASE_BUILD_DONE
