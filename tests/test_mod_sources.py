"""Mod selection rules of the guided build and the tModLoader version gate for .tmod packages."""
import unittest

from scripts.tmod.tmod_file import loader_version_problem
from terrabuilder_pkg.installer import ModChoice


def source(kind, mod_id, *deps, problem=None):
    entry = {"id": mod_id, "kind": kind, "display_name": mod_id, "dependencies": list(deps)}
    if problem:
        entry["problem"] = problem
    return f"{kind}:{mod_id}", entry


class LoaderVersionTests(unittest.TestCase):
    def test_packages_for_this_or_older_144_releases_load(self):
        for version in ("2026.7.3.0", "2026.7.9.9", "2026.6.3.4", "2025.12.3.0", "2023.3.85.0"):
            self.assertIsNone(loader_version_problem(version), version)

    def test_packages_for_newer_releases_are_refused(self):
        self.assertIn("newer", loader_version_problem("2026.8.3.0"))
        self.assertIn("newer", loader_version_problem("2027.1.0.0"))

    def test_packages_from_before_144_stable_are_refused(self):
        for version in ("2023.3.84.9", "2022.9.47.1", "0.11.8.9"):
            self.assertIn("before tModLoader 1.4.4", loader_version_problem(version), version)


class ModChoiceTests(unittest.TestCase):
    def test_choosing_a_mod_adds_its_dependencies_first(self):
        choice = ModChoice(dict([source("catalog", "Lib"), source("catalog", "Base", "Lib"),
                                 source("file", "Top", "Base")]))
        choice.toggle("file:Top", True)
        active, needed_by = choice.resolve()
        self.assertEqual(set(active), {"file:Top", "catalog:Base", "catalog:Lib"})
        self.assertEqual(needed_by["catalog:Base"], ["Top"])
        self.assertEqual(needed_by["catalog:Lib"], ["Base"])
        mods, sources = choice.build_input()
        self.assertEqual(mods, ["Lib", "Base", "Top"])
        self.assertEqual(sources["Top"]["kind"], "file")

    def test_dependency_comes_from_the_source_the_user_chose(self):
        choice = ModChoice(dict([source("catalog", "Core"), source("file", "Core"),
                                 source("catalog", "Addon", "Core")]))
        choice.toggle("file:Core", True)
        choice.toggle("catalog:Addon", True)
        self.assertEqual(choice.build_input()[1]["Core"]["kind"], "file")
        self.assertIsNotNone(choice.toggle("catalog:Core", True))
        mods, sources = choice.build_input()
        self.assertEqual(sources["Core"]["kind"], "catalog")
        self.assertEqual(mods, ["Core", "Addon"])

    def test_without_a_choice_the_open_source_build_satisfies_a_dependency(self):
        choice = ModChoice(dict([source("file", "Core"), source("catalog", "Core"),
                                 source("file", "Addon", "Core")]))
        choice.toggle("file:Addon", True)
        self.assertEqual(choice.build_input()[1]["Core"]["kind"], "catalog")

    def test_mods_whose_dependencies_cannot_be_used_are_blocked(self):
        choice = ModChoice(dict([source("file", "Orphan", "Missing"),
                                 source("file", "Broken", problem="built for a newer tModLoader"),
                                 source("file", "NeedsBroken", "Broken"),
                                 source("file", "NeedsNeedsBroken", "NeedsBroken")]))
        self.assertIn("needs Missing, which is not available", choice.blocked["file:Orphan"])
        self.assertIn("needs Broken, which can't be used", choice.blocked["file:NeedsBroken"])
        self.assertIn("needs NeedsBroken", choice.blocked["file:NeedsNeedsBroken"])


if __name__ == "__main__":
    unittest.main()
