"""Container engine selection shared by the CLI and the build scripts it runs."""
from __future__ import annotations

import functools
import grp
import os
import shutil
import subprocess
from pathlib import Path


def engine() -> str | None:
    for name in ("podman", "docker"):
        if shutil.which(name):
            return name
    return None


def is_docker(eng: str) -> bool:
    return Path(eng).name == "docker"


# Snap packages may not open hidden folders such as ~/.cache, where builds keep the toolchain, so
# the snap's daemon fails or silently mounts an empty folder (canonical/docker-snap #189, #334).
SNAP_DOCKER = ("docker is the snap version, which can't read the builder's folders: replace it with "
               "`sudo snap remove docker && sudo apt install -y docker.io`, then run "
               "`sudo usermod -aG docker $USER` and open a new terminal")


def docker_access_fix() -> str:
    """The commands that let this user run docker without sudo."""
    try:
        grp.getgrnam("docker")
    except KeyError:
        # Without the group the daemon left its socket root-only; it applies the group on restart.
        return "sudo groupadd docker && sudo usermod -aG docker $USER && sudo service docker restart"
    return "sudo usermod -aG docker $USER"


def engine_status(eng: str) -> tuple[str | None, str]:
    """(problem, report) from `<eng> info`: problem is None when the engine answers, else what to
    do about it in plain words; report is the command's output, for logs."""
    snap = is_docker(eng) and (shutil.which(eng) or "").startswith("/snap/")
    try:
        result = subprocess.run([eng, "info"], capture_output=True, text=True, timeout=60)
    except subprocess.TimeoutExpired:
        return f"{eng} did not answer within 60 s: restart it and try again", ""
    except OSError as error:
        return f"{eng} could not run: {error}", ""
    report = (result.stdout + result.stderr).strip()
    if snap:
        return SNAP_DOCKER, report
    if result.returncode == 0:
        return None, report
    text = report.lower()
    if "wsl integration" in text or "could not be found in this wsl" in text:
        problem = ("Docker Desktop's WSL integration is off for this Linux: turn it on in Docker Desktop, "
                   "Settings > Resources > WSL integration")
    elif "permission denied" in text and is_docker(eng):
        problem = (f"docker is installed but this user may not use it: run `{docker_access_fix()}`, "
                   "then close and reopen the terminal")
    elif "cannot connect" in text or "daemon running" in text or "connection refused" in text:
        problem = (f"{eng} is installed but not running: start Docker Desktop (on Windows) or the {eng} "
                   "service, then run this again")
    else:
        first = next((line.strip() for line in report.splitlines() if line.strip()), f"exit code {result.returncode}")
        problem = f"{eng} isn't working: {first[:200]}"
    return problem, report


@functools.cache
def root_is_invoking_user(eng: str) -> bool:
    """Whether files that container root writes to bind mounts belong to the invoking user.

    True for rootless engines: the scripts assume rootless Podman, and rootless Docker maps
    root the same way. Rootful Docker runs containers as real root.
    """
    if not is_docker(eng):
        return True
    info = subprocess.run([eng, "info", "--format", "{{json .SecurityOptions}}"],
                          capture_output=True, text=True)
    return "name=rootless" in info.stdout


def user_args(eng: str, *, keep_id: bool = True) -> list[str]:
    """`run` arguments that keep files written to bind mounts owned by the invoking user."""
    if not is_docker(eng):
        return ["--userns=keep-id"] if keep_id else []
    return [] if root_is_invoking_user(eng) else ["--user", f"{os.getuid()}:{os.getgid()}"]
