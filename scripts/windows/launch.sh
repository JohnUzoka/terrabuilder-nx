#!/usr/bin/env bash
# Runs inside WSL for terrabuilder.cmd, the Windows launcher. $1 is the Windows folder holding
# terrabuilder.cmd; the other arguments go to ./terrabuilder (none: the guided build).
#
# The Terraria, tModLoader and Mods folders next to terrabuilder.cmd are copied into this
# checkout first (an empty one leaves the checkout's copy as it is), and Output/ is copied back
# afterwards, also when the build fails, so its logs reach Windows.
set -euo pipefail

win_dir=${1:?usage: launch.sh <folder holding terrabuilder.cmd> [terrabuilder arguments]}
shift
shown=${win_dir%\\.}
here=$(wslpath -a "$win_dir")
repo=$(cd "$(dirname "$0")/../.." && pwd)

for name in Terraria tModLoader Mods Output; do
    mkdir -p "$here/$name"
done
for name in Terraria tModLoader Mods; do
    if [ -n "$(ls -A "$here/$name")" ]; then
        echo "Copying $shown\\$name into $repo/$name"
        # -L copies what links and junctions point to, such as a linked Workshop folder.
        rsync -rtL --delete "$here/$name/" "$repo/$name/" ||
            echo "warning: copying $name was incomplete; the build uses what was copied"
    fi
done

status=0
(cd "$repo" && ./terrabuilder "$@") || status=$?

if [ -d "$repo/Output" ]; then
    echo "Copying Output to $shown\\Output"
    rsync -rtL --delete "$repo/Output/" "$here/Output/"
fi
exit "$status"
