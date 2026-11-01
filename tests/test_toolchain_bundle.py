"""Prebuilt toolchain bundle: what `toolchain pack` writes is what `toolchain --bundle` installs."""
import contextlib
import io
import shutil
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from terrabuilder_pkg import cli
from terrabuilder_pkg import toolchain as tclib

CROSS = "sdk/" + tclib.COMPONENTS["sdk"][0]


def make_toolchain(root: Path) -> None:
    """A minimal toolchain: every required file, one executable, one empty directory."""
    for component, required in tclib.COMPONENTS.items():
        for rel in required:
            path = root / component / rel
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(f"{component}/{rel}\n".encode())
    (root / CROSS).chmod(0o755)
    (root / "dotnet/metadata-kept-empty").mkdir()
    tclib.write_manifest(root, origin="test", provenance={})


def add_tmod_runtime(root: Path) -> None:
    """The optional tmod-runtime component; its LLVM tools link to the aot-compiler component."""
    base = root / "tmod-runtime"
    for rel in tclib.OPTIONAL_COMPONENTS["tmod-runtime"]:
        path = base / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        if rel.startswith("aot-compiler/") and rel != "aot-compiler/mono-aot-cross":
            path.symlink_to("../../aot-compiler/" + path.name)
        else:
            path.write_bytes(f"tmod-runtime/{rel}\n".encode())
    (base / "aot-compiler/mono-aot-cross").chmod(0o755)


@unittest.skipUnless(shutil.which("xz"), "toolchain pack needs xz")
class ToolchainBundleTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.quiet = contextlib.redirect_stdout(io.StringIO())
        self.quiet.__enter__()
        self.addCleanup(self.quiet.__exit__, None, None, None)
        source = self.tmp / "source" / "toolchain"
        make_toolchain(source)
        # A container run that mounts mono-nx-native inside the sdk mount creates this mount point.
        (source / "sdk/native").mkdir()
        self.archive = cli.pack_toolchain(source, self.tmp / "out")
        pin = {"url": "https://example.invalid/" + self.archive.name, "sha256": cli.sha256(self.archive),
               "bytes": self.archive.stat().st_size}
        pinned = patch.dict(cli.TOOLCHAIN_BUNDLE, pin)
        pinned.start()
        self.addCleanup(pinned.stop)

    def test_installed_bundle_verifies_with_modes_and_empty_directories(self):
        workdir = self.tmp / "work"
        cli.install_toolchain_bundle(workdir, self.archive)
        live = workdir / "toolchain"
        self.assertEqual(tclib.verify(live), [])
        self.assertTrue((live / CROSS).stat().st_mode & 0o100)
        self.assertTrue((live / "dotnet/metadata-kept-empty").is_dir())
        self.assertFalse((live / "sdk/native").exists())
        self.assertFalse((workdir / "toolchain.bundle").exists())

    def test_usable_toolchain_is_kept_unless_replace(self):
        workdir = self.tmp / "work"
        cli.install_toolchain_bundle(workdir, self.archive)
        marker = workdir / "toolchain/notices/local-note.txt"
        marker.write_text("mine")
        cli.install_toolchain_bundle(workdir, self.archive)
        self.assertTrue(marker.is_file())
        self.assertFalse((workdir / "toolchain.bak").exists())
        cli.install_toolchain_bundle(workdir, self.archive, replace=True)
        self.assertFalse(marker.exists())
        self.assertTrue((workdir / "toolchain.bak/notices/local-note.txt").is_file())
        self.assertEqual(tclib.verify(workdir / "toolchain"), [])

    def test_broken_toolchain_is_replaced_without_replace(self):
        workdir = self.tmp / "work"
        cli.install_toolchain_bundle(workdir, self.archive)
        (workdir / "toolchain" / tclib.MANIFEST).unlink()
        cli.install_toolchain_bundle(workdir, self.archive)
        self.assertEqual(tclib.verify(workdir / "toolchain"), [])

    def test_archive_that_is_not_the_pinned_asset_is_refused(self):
        workdir = self.tmp / "work"
        other = self.tmp / "other.tar.xz"
        other.write_bytes(self.archive.read_bytes() + b"\0")
        with self.assertRaises(SystemExit):
            cli.install_toolchain_bundle(workdir, other)
        self.assertFalse((workdir / "toolchain").exists())

    def test_toolchain_that_fails_verification_is_not_packed(self):
        source = self.tmp / "source" / "toolchain"
        (source / CROSS).write_bytes(b"changed\n")
        with self.assertRaises(SystemExit):
            cli.pack_toolchain(source, self.tmp / "out2")
        self.assertFalse((self.tmp / "out2").exists())


class OptionalComponentTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp)
        self.root = self.tmp / "toolchain"
        make_toolchain(self.root)

    def test_toolchain_without_optional_component_loads_and_verifies(self):
        self.assertFalse(tclib.load(self.root).has("tmod-runtime"))
        self.assertTrue(tclib.load(self.root).has("sdk"))
        self.assertEqual(tclib.verify(self.root), [])

    def test_unrecorded_optional_component_is_unexpected(self):
        add_tmod_runtime(self.root)
        with self.assertRaises(tclib.ToolchainError):
            tclib.load(self.root)
        self.assertTrue(any("tmod-runtime" in problem for problem in tclib.verify(self.root)))

    def test_recorded_optional_component_is_verified(self):
        add_tmod_runtime(self.root)
        manifest = tclib.write_manifest(self.root, origin="test", provenance={})
        self.assertIn("tmod-runtime", manifest["components"])
        self.assertEqual(manifest["files"]["tmod-runtime/aot-compiler/opt"], {"symlink": "../../aot-compiler/opt"})
        self.assertTrue(tclib.load(self.root).has("tmod-runtime"))
        self.assertEqual(tclib.verify(self.root), [])
        (self.root / "tmod-runtime/lib/libSystem.Native.a").write_bytes(b"changed\n")
        self.assertEqual(tclib.verify(self.root), ["changed tmod-runtime/lib/libSystem.Native.a"])
        shutil.rmtree(self.root / "tmod-runtime")
        with self.assertRaises(tclib.ToolchainError):
            tclib.load(self.root)
        self.assertIn("missing tmod-runtime/lib/libSystem.Native.a", tclib.verify(self.root))

    def test_incomplete_optional_component_is_refused(self):
        add_tmod_runtime(self.root)
        (self.root / "tmod-runtime/lib/libmonosgen-2.0.a").unlink()
        with self.assertRaises(tclib.ToolchainError):
            tclib.write_manifest(self.root, origin="test", provenance={})

    @unittest.skipUnless(shutil.which("xz"), "toolchain pack needs xz")
    def test_bundle_carries_optional_component_and_its_symlinks(self):
        add_tmod_runtime(self.root)
        tclib.write_manifest(self.root, origin="test", provenance={})
        with contextlib.redirect_stdout(io.StringIO()):
            archive = cli.pack_toolchain(self.root, self.tmp / "out")
            pin = {"url": "https://example.invalid/" + archive.name, "sha256": cli.sha256(archive),
                   "bytes": archive.stat().st_size}
            with patch.dict(cli.TOOLCHAIN_BUNDLE, pin):
                cli.install_toolchain_bundle(self.tmp / "work", archive)
        live = self.tmp / "work/toolchain"
        self.assertEqual(tclib.verify(live), [])
        self.assertTrue(tclib.load(live).has("tmod-runtime"))
        self.assertEqual((live / "tmod-runtime/aot-compiler/llc").read_bytes(), b"aot-compiler/llc\n")


if __name__ == "__main__":
    unittest.main()
