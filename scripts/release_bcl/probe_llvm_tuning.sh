#!/bin/bash
# Probe: does Mono's LLVM AOT accept Cortex-A57 tuning, and does it change code?
# Compiles NxCrypto (small) three ways with the LLVM cross compiler; no artifact is shipped.
set -euo pipefail
export PATH=/opt/devkitpro/devkitA64/bin:$PATH
L=/build/runtime-llvm/artifacts/bin/mono/linux.x64.Debug/cross/linux-x64/libnx-arm64
V=/build/release58/v62/aot-final
OUT=/build/release58/probe-llvm-tuning
rm -rf "$OUT"; mkdir -p "$OUT"
PATHS=$(python3 -c "import json;m=json.load(open('$V/build-manifest.json'));print(' '.join('--path='+p for p in m['reference_directories']))")
SRC=$V/runtime-romfs/NxCrypto.dll
run() {
  name=$1; extra=$2
  mkdir -p "$OUT/$name/tmp"
  LD_LIBRARY_PATH=$L TMPDIR=$OUT/$name/tmp "$L/mono-aot-cross" --llvm $PATHS \
    "--aot=full,interp,static,outfile=$OUT/$name/NxCrypto.dll.o,llvm-path=$L/,llvm-outfile=$OUT/$name/NxCrypto.dll-llvm.o,temp-path=$OUT/$name/tmp${extra},tool-prefix=aarch64-none-elf-" \
    "$SRC" > "$OUT/$name/log.txt" 2>&1 || { echo "$name FAILED"; tail -5 "$OUT/$name/log.txt"; return; }
  echo "$name: $(grep -o 'Compiled[^\n]*' "$OUT/$name/log.txt" | head -1) text=$(aarch64-none-elf-size -A "$OUT/$name/NxCrypto.dll-llvm.o" | awk '/^\.text/{s+=$2} END{print s}')"
  grep -E 'Executing (opt|llc)' "$OUT/$name/log.txt" | sed 's/^/    /' | cut -c1-260
}
run base ""
run llc_a57 ",llvmllc=-mcpu=cortex-a57"
run both_a57 ",llvmopts=-mtriple=aarch64-none-elf -mcpu=cortex-a57,llvmllc=-mcpu=cortex-a57"
