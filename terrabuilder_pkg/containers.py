"""Container engine selection shared by the CLI and the build scripts it runs."""
from __future__ import annotations

import functools
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
