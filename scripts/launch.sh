#!/usr/bin/env bash
# The launchers' shared half, run from a checkout: terrabuilder.sh (Linux) runs it directly,
# terrabuilder.cmd (Windows) through scripts/windows/launch.sh. $1 is the folder holding the
# launcher; the other arguments go to ./terrabuilder (none: the guided build).
#
# The Terraria, tModLoader and Mods folders there are copied into this checkout first (an empty
# one leaves the checkout's copy as it is), and Output/ is copied back afterwards, also when the
# build fails, so its logs reach the launcher's folder.
set -euo pipefail

here=${1:?usage: launch.sh <folder holding the launcher> [terrabuilder arguments]}
shift
repo=$(cd "$(dirname "$0")/.." && pwd)

for name in Terraria tModLoader Mods Output; do
    mkdir -p "$here/$name"
done
for name in Terraria tModLoader Mods; do
    if [ -n "$(ls -A "$here/$name")" ]; then
        echo "Copying $name into $repo/$name"
        # -L copies what links and junctions point to, such as a linked Workshop folder.
        rsync -rtL --delete "$here/$name/" "$repo/$name/" ||
            echo "warning: copying $name was incomplete; the build uses what was copied"
    fi
done

status=0
(cd "$repo" && ./terrabuilder "$@") || status=$?

if [ -d "$repo/Output" ]; then
    echo "Copying Output back to $here/Output"
    rsync -rtL --delete "$repo/Output/" "$here/Output/"
fi
exit "$status"
