"""Tests for next_version.py: python3 .github/scripts/test_next_version.py"""
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from next_version import next_version  # noqa: E402

BETA = ["v0.1.0", "v0.3.0-beta.9", "v0.3.0-beta.33", "v0.3.0-beta.3", "v0.2.0-beta.1"]


class NextVersion(unittest.TestCase):
    def test_prerelease_continues_the_highest_line_numerically(self):
        self.assertEqual(next_version(BETA, "prerelease"), "v0.3.0-beta.34")

    def test_stable_bumps_finalize_a_prerelease_line(self):
        self.assertEqual(next_version(BETA, "patch"), "v0.3.0")
        self.assertEqual(next_version(BETA, "minor"), "v0.3.0")
        self.assertEqual(next_version(BETA, "major"), "v1.0.0")
        self.assertEqual(next_version(["v1.0.0-rc.2"], "major"), "v1.0.0")
        self.assertEqual(next_version(["v0.3.1-beta.2"], "minor"), "v0.4.0")

    def test_stable_bumps_from_a_stable_tag(self):
        tags = BETA + ["v0.3.0"]
        self.assertEqual(next_version(tags, "patch"), "v0.3.1")
        self.assertEqual(next_version(tags, "minor"), "v0.4.0")
        self.assertEqual(next_version(tags, "major"), "v1.0.0")

    def test_stable_sorts_above_its_prereleases(self):
        self.assertEqual(next_version(BETA + ["v0.3.0"], "prerelease"), "v0.3.1-beta.1")

    def test_pre_bumps_start_a_new_line_at_one(self):
        self.assertEqual(next_version(BETA, "prepatch"), "v0.3.1-beta.1")
        self.assertEqual(next_version(BETA, "preminor"), "v0.4.0-beta.1")
        self.assertEqual(next_version(BETA, "premajor"), "v1.0.0-beta.1")
        self.assertEqual(next_version(BETA, "preminor", "rc"), "v0.4.0-rc.1")

    def test_a_new_label_on_the_same_core_restarts_numbering(self):
        self.assertEqual(next_version(BETA, "prerelease", "rc"), "v0.3.0-rc.1")
        self.assertEqual(next_version(BETA, "prerelease", "beta"), "v0.3.0-beta.34")

    def test_ignores_tags_that_are_not_versions(self):
        self.assertEqual(next_version(BETA + ["latest", "vnext", "v1.2"], "prerelease"), "v0.3.0-beta.34")

    def test_no_tags_starts_from_zero(self):
        self.assertEqual(next_version([], "prerelease"), "v0.0.1-beta.1")
        self.assertEqual(next_version([], "minor"), "v0.1.0")

    def test_rejects_bad_input(self):
        with self.assertRaises(ValueError):
            next_version(BETA, "huge")
        with self.assertRaises(ValueError):
            next_version(BETA, "prepatch", "bad label!")


if __name__ == "__main__":
    unittest.main()
