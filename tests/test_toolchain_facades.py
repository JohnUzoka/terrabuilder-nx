"""Clean-checkout provisioning tests for the retained build-52 facades."""
import hashlib
import json
import shutil
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from scripts.toolchain import build_from_source

ROOT = Path(__file__).resolve().parents[1]
LOCK = json.loads((ROOT / "toolchain.lock.json").read_text())


class ToolchainFacadeTests(unittest.TestCase):
    def test_checked_in_facades_copy_with_locked_bytes_and_modes(self):
        with tempfile.TemporaryDirectory() as tmp:
            output = Path(tmp)
            result = build_from_source.step_facades(LOCK, output)
            pins = LOCK["retained_artifacts"]["facades"]["files"]
            copied = output / "facades"
            self.assertEqual(set(result["files"]), set(pins))
            self.assertEqual({path.name for path in copied.iterdir()}, set(pins))
            for name, pin in pins.items():
                path = copied / name
                self.assertEqual(path.stat().st_size, pin["bytes"])
                self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(), pin["sha256"])
                self.assertEqual(bool(path.stat().st_mode & 0o111), pin["exec"])

    def test_modified_artifact_is_rejected(self):
        record = LOCK["retained_artifacts"]["facades"]
        with tempfile.TemporaryDirectory() as tmp:
            test_root = Path(tmp) / "repo"
            source = test_root / record["path"]
            source.mkdir(parents=True)
            for name in record["files"]:
                shutil.copy2(ROOT / record["path"] / name, source / name)
            path = source / "mscorlib.dll"
            path.write_bytes(path.read_bytes() + b"tampered")
            with patch.object(build_from_source, "REPO_ROOT", test_root):
                with self.assertRaisesRegex(SystemExit, "does not match its lock pin"):
                    build_from_source.step_facades(LOCK, Path(tmp) / "output")

    def test_unexpected_artifact_is_rejected(self):
        record = LOCK["retained_artifacts"]["facades"]
        with tempfile.TemporaryDirectory() as tmp:
            test_root = Path(tmp) / "repo"
            source = test_root / record["path"]
            source.mkdir(parents=True)
            for name in record["files"]:
                shutil.copy2(ROOT / record["path"] / name, source / name)
            (source / "unlocked.dll").write_bytes(b"not pinned")
            with patch.object(build_from_source, "REPO_ROOT", test_root):
                with self.assertRaisesRegex(SystemExit, "do not match toolchain.lock.json"):
                    build_from_source.step_facades(LOCK, Path(tmp) / "output")


if __name__ == "__main__":
    unittest.main()
