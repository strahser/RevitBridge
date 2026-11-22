# -*- coding: utf-8 -*-
"""P2.3: annotation-driven tool policy (no Revit, no C#, no catalog)."""

import os
import sys
import unittest

# test file: <root>/mcp-server/tests/test_policy.py
HERE = os.path.dirname(os.path.abspath(__file__))
SERVER_DIR = os.path.dirname(HERE)
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)

from models import PolicyError, ToolSpec  # noqa: E402
from policy import (  # noqa: E402
    ToolPolicy, is_destructive_tool, is_write_tool,
)


def spec(**annotations):
    return ToolSpec(name="tool", canonical="domain.tool",
                    annotations=annotations)


class ClassificationTests(unittest.TestCase):
    def test_writes_document_and_read_only_hint_false_are_writes(self):
        for annotations in (
            {"writesDocument": True},
            {"readOnlyHint": False},
            {"destructiveHint": True},
        ):
            with self.subTest(annotations=annotations):
                self.assertTrue(is_write_tool(spec(**annotations)))

    def test_read_and_unknown_annotations_are_not_writes(self):
        for annotations in (
            {"readOnlyHint": True},
            {"readOnlyHint": True, "destructiveHint": False},
            {},
        ):
            with self.subTest(annotations=annotations):
                self.assertFalse(is_write_tool(spec(**annotations)))

    def test_destructive_keys(self):
        for key in ("destructiveHint", "destructive", "requiresConfirmation"):
            with self.subTest(key=key):
                tool = spec(readOnlyHint=False, **{key: True})
                self.assertTrue(is_destructive_tool(tool))
        self.assertFalse(is_destructive_tool(spec(destructiveHint=False)))

    def test_string_boolean_annotations(self):
        self.assertTrue(is_write_tool(spec(writesDocument="true")))
        self.assertTrue(is_destructive_tool(spec(requiresConfirmation="TRUE")))


class ToolPolicyTests(unittest.TestCase):
    def test_read_only_blocks_write_and_allows_read(self):
        policy = ToolPolicy()
        self.assertEqual(policy.profile, "revit-read")
        self.assertIsNone(policy.check(spec(readOnlyHint=True)))
        for annotations in ({"writesDocument": True}, {"readOnlyHint": False}):
            with self.subTest(annotations=annotations):
                error = policy.check(spec(**annotations))
                self.assertIsInstance(error, PolicyError)
                self.assertEqual(error.code, "write_blocked")
                self.assertIn("revit-read", error.message)
                self.assertTrue(error.hint)

    def test_allow_writes_allows_write(self):
        policy = ToolPolicy(profile="revit-read", allow_writes=True)
        self.assertTrue(policy.allows_writes)
        self.assertIsNone(policy.check(spec(writesDocument=True)))
        self.assertIsNone(policy.check(spec(readOnlyHint=False)))

    def test_write_profile_allows_write(self):
        for profile in ("revit-write", "write", "full"):
            with self.subTest(profile=profile):
                policy = ToolPolicy(profile=profile)
                self.assertTrue(policy.allows_writes)
                self.assertIsNone(policy.check(spec(writesDocument=True)))

    def test_destructive_requires_confirmation(self):
        policy = ToolPolicy(profile="revit-write")
        tool = spec(readOnlyHint=False, destructiveHint=True,
                    requiresConfirmation=True)
        error = policy.check(tool)
        self.assertIsInstance(error, PolicyError)
        self.assertEqual(error.code, "confirmation_required")
        self.assertIn("confirmation", error.message.lower())
        self.assertIsNone(policy.check(tool, confirmed=True))

    def test_confirmation_alone_requires_confirmation(self):
        policy = ToolPolicy(allow_writes=True)
        tool = spec(writesDocument=True, requiresConfirmation=True)
        self.assertEqual(policy.check(tool).code, "confirmation_required")

    def test_writes_document_false_overrides_read_only_hint(self):
        # export/open: readOnlyHint=false, but writesDocument=false
        policy = ToolPolicy()
        tool = spec(readOnlyHint=False, writesDocument=False)
        self.assertIsNone(policy.check(tool))

    def test_write_block_wins_over_confirmation_in_read_only_profile(self):
        tool = spec(writesDocument=True, destructiveHint=True)
        error = ToolPolicy().check(tool, confirmed=True)
        self.assertEqual(error.code, "write_blocked")

    def test_unknown_profile_fails_closed(self):
        policy = ToolPolicy(profile="typo-profile")
        self.assertFalse(policy.allows_writes)
        self.assertEqual(policy.check(spec(writesDocument=True)).code,
                         "write_blocked")

    def test_policy_error_as_dict(self):
        error = PolicyError("code", "message", "hint")
        self.assertEqual(error.as_dict(),
                         {"code": "code", "message": "message", "hint": "hint"})


if __name__ == "__main__":
    unittest.main(verbosity=2)
