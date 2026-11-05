#!/usr/bin/env bash
# Run inside WSL by terrabuilder.cmd: $1 is the Windows folder holding terrabuilder.cmd, the
# rest go to ../launch.sh, which does the work for both launchers. Published copies of
# terrabuilder.cmd call this path, so it stays.
set -euo pipefail

win_dir=${1:?usage: launch.sh <Windows folder holding terrabuilder.cmd> [terrabuilder arguments]}
shift
exec "$(dirname "$0")/../launch.sh" "$(wslpath -a "$win_dir")" "$@"
