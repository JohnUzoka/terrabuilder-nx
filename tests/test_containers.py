"""Container engine check: a working engine passes, a failing one is reported instead of looking like missing images."""
import shutil
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from terrabuilder_pkg.containers import engine_status


def fake_engine(folder: Path, script: str) -> str:
    path = folder / "docker"
    path.write_text("#!/bin/sh\n" + script + "\n")
    path.chmod(0o755)
    return str(path)


class EngineStatusTests(unittest.TestCase):
    def setUp(self):
        self.folder = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.folder)

    def test_engine_that_answers_with_warnings_is_working(self):
        eng = fake_engine(self.folder, 'echo "Server Version: 28"; echo "WARNING: No swap limit support" >&2')
        problem, report = engine_status(eng)
        self.assertIsNone(problem)
        self.assertIn("Server Version", report)

    def test_daemon_that_is_not_running_is_reported(self):
        eng = fake_engine(self.folder, 'echo "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. '
                                       'Is the docker daemon running?" >&2; exit 1')
        problem, report = engine_status(eng)
        self.assertIn("not running", problem)
        self.assertIn("Cannot connect", report)

    def test_unknown_failure_keeps_the_engine_message(self):
        eng = fake_engine(self.folder, 'echo "error during connect: something new" >&2; exit 1')
        problem, _ = engine_status(eng)
        self.assertIn("something new", problem)

    def test_snap_docker_is_reported_even_when_it_answers(self):
        # The snap may not mount hidden folders such as ~/.cache, so builds would fail later.
        eng = fake_engine(self.folder, 'echo "Server Version: 28"')
        with patch("terrabuilder_pkg.containers.shutil.which", return_value="/snap/bin/docker"):
            problem, _ = engine_status(eng)
        self.assertIn("snap remove docker", problem)

    def test_permission_fix_creates_a_missing_docker_group(self):
        eng = fake_engine(self.folder, 'echo "permission denied while trying to connect to the docker API '
                                       'at unix:///var/run/docker.sock" >&2; exit 1')
        with patch("terrabuilder_pkg.containers.grp.getgrnam", side_effect=KeyError("docker")):
            problem, _ = engine_status(eng)
        self.assertIn("groupadd docker", problem)


if __name__ == "__main__":
    unittest.main()
