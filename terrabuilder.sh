#!/usr/bin/env bash
# terrabuilder for Linux: put this file in an empty folder and run `bash terrabuilder.sh` there.
#
# It installs Podman, git, rsync and Python if they are missing (after asking; this uses sudo),
# keeps a copy of terrabuilder-nx in ~/terrabuilder-nx up to date and runs the guided build there.
# Put your Terraria, tModLoader and .tmod files in the Terraria, tModLoader and Mods folders it
# creates next to this file; finished builds and logs are copied to the Output folder. Arguments
# go to terrabuilder: `bash terrabuilder.sh doctor` checks the machine. TERRABUILDER_REPO and
# TERRABUILDER_BRANCH override the repository and the branch of a new copy.
set -euo pipefail

repo_url=${TERRABUILDER_REPO:-https://github.com/JohnUzoka/terrabuilder-nx}
branch=${TERRABUILDER_BRANCH:-main}
here=$(cd "$(dirname "$0")" && pwd)
checkout=$HOME/terrabuilder-nx

if [ "$(uname -s)" != Linux ]; then
    echo "terrabuilder needs Linux: parts of the build run Linux x86-64 programs outside containers."
    echo "On a Mac, run this inside a Linux virtual machine."
    exit 1
fi
# This file also ships at the root of a checkout: there, nothing needs setting up or copying.
if [ -x "$here/terrabuilder" ] && [ -d "$here/.git" ]; then
    exec "$here/terrabuilder" "$@"
fi

need=()
for tool in git rsync python3; do
    command -v "$tool" >/dev/null || need+=("$tool")
done
# Podman only when neither Podman nor a Docker that works without sudo is there.
if ! command -v podman >/dev/null && ! docker info >/dev/null 2>&1; then
    need+=(podman)
fi
if [ ${#need[@]} -gt 0 ]; then
    if command -v apt-get >/dev/null; then
        install="sudo apt-get update && sudo apt-get install -y ${need[*]}"
    elif command -v dnf >/dev/null; then
        install="sudo dnf install -y ${need[*]}"
    elif command -v pacman >/dev/null; then
        install="sudo pacman -S --needed --noconfirm ${need[*]/python3/python}"
    elif command -v zypper >/dev/null; then
        install="sudo zypper install -y ${need[*]}"
    else
        echo "terrabuilder needs ${need[*]}: install them with your package manager, then run this again."
        exit 1
    fi
    echo "terrabuilder needs ${need[*]}, which this computer doesn't have."
    read -r -p "Install them now with: $install ? [Y/n] " answer || answer=n
    case ${answer:-y} in
        [Yy]*) bash -c "$install" ;;
        *) echo "Install ${need[*]}, then run this again."; exit 1 ;;
    esac
fi

if [ -d "$checkout/.git" ]; then
    git -C "$checkout" pull -q --ff-only || echo "Could not update $checkout, so the copy you have is used."
else
    echo "Downloading terrabuilder-nx into $checkout"
    git clone -q --branch "$branch" "$repo_url" "$checkout"
fi
if [ ! -x "$checkout/scripts/launch.sh" ]; then
    echo "$checkout is older than this launcher: update it (git -C $checkout pull) or move it away."
    exit 1
fi
exec "$checkout/scripts/launch.sh" "$here" "$@"
