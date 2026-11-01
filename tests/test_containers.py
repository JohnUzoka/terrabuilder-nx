"""Container engine check: a working engine passes, a failing one is reported instead of looking like missing images."""
import shutil
import tempfile
import unittest
from pathlib import Path

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


if __name__ == "__main__":
    unittest.main()
