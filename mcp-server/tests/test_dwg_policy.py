# -*- coding: utf-8 -*-
"""P1.6: explicit read/write classification of DwgParser tools."""
import os
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SERVER_DIR = os.path.dirname(HERE)
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)

from dwg_policy import DwgToolPolicy  # noqa: E402
from models import ToolSpec  # noqa: E402
from policy import ToolPolicy  # noqa: E402
from providers.dwgparser import DwgParserProvider  # noqa: E402


class DwgPolicyManifestTests(unittest.TestCase):
    def setUp(self):
        self.policy = DwgToolPolicy.load()

    def test_manifest_is_disjoint_and_destructive_is_a_writer(self):
        self.assertTrue(self.policy.read_only)
        self.assertTrue(self.policy.writes_document)
        self.assertFalse(self.policy.read_only & self.policy.writes_document)
        self.assertTrue(self.policy.destructive <= self.policy.writes_document)

    def test_manifest_has_no_duplicates_between_lists(self):
        report = self.policy.validate(self.policy.known)
        self.assertEqual([], report["missing"])
        self.assertEqual([], report["unknown"])
        self.assertEqual([], report["overlap"])
        self.assertEqual([], report["destructive_not_writer"])
        self.assertTrue(self.policy.ok(report))

    def test_unknown_tool_is_a_writer(self):
        annotations = self.policy.apply("dwg.brand_new_tool",
                                        {"idempotentHint": True})
        self.assertFalse(annotations["readOnlyHint"])
        self.assertTrue(annotations["writesDocument"])
        self.assertTrue(annotations["idempotentHint"])

    def test_read_tool_stays_read_only(self):
        annotations = self.policy.apply("dwg.revit_list_sheets",
                                        {"readOnlyHint": False})
        self.assertTrue(annotations["readOnlyHint"])
        self.assertFalse(annotations["writesDocument"])

    def test_writer_requires_a_write_profile(self):
        spec = ToolSpec(name="dwg_revit_create_sheet",
                        canonical="dwg.revit_create_sheet",
                        annotations={"readOnlyHint": False})
        spec.annotations = self.policy.apply(spec.canonical, spec.annotations)
        self.assertEqual("write_blocked",
                         ToolPolicy("revit-read").check(spec).code)
        self.assertIsNone(ToolPolicy("revit-write").check(spec))

    def test_destructive_needs_confirmation_even_with_writes(self):
        spec = ToolSpec(name="dwg_revit_delete_elements",
                        canonical="dwg.revit_delete_elements")
        spec.annotations = self.policy.apply(spec.canonical, {})
        self.assertEqual("confirmation_required",
                         ToolPolicy("revit-write").check(spec).code)
        self.assertIsNone(ToolPolicy("revit-write").check(spec, confirmed=True))

    def test_validate_reports_missing_and_unknown(self):
        report = self.policy.validate(["dwg.revit_list_sheets", "dwg.extra"])
        self.assertEqual(["dwg.extra"], report["unknown"])
        self.assertIn("dwg.revit_create_sheet", report["missing"])
        self.assertFalse(self.policy.ok(report))

    def test_legacy_revit_tools_are_deprecated_with_ws_hints(self):
        self.assertEqual(26, len(self.policy.deprecated))
        self.assertTrue(set(self.policy.deprecated) <= self.policy.read_only)
        self.assertEqual("pipeline.list_sheets",
                         self.policy.replacement("dwg.revit_list_sheets"))
        self.assertIn("pipeline.list_sheets",
                      self.policy.deprecation_hint("dwg.revit_list_sheets"))
        self.assertIn("tools_search",
                      self.policy.deprecation_hint("dwg.revit_list_grids"))
        self.assertEqual("", self.policy.deprecation_hint("dwg.spec_export_pdf"))

    def test_deprecated_section_is_validated(self):
        report = self.policy.validate(self.policy.known)
        self.assertEqual([], report["deprecated_unknown"])
        self.assertEqual([], report["deprecated_not_legacy"])
        self.assertTrue(self.policy.ok(report))
        broken = DwgToolPolicy(deprecated={"dwg.revit_missing": ""})
        self.assertFalse(broken.ok(broken.validate(broken.known)))


class DwgProviderPolicyTests(unittest.TestCase):
    def setUp(self):
        self.policy = DwgToolPolicy.load()
        self.child_tools = [
            {"name": "dwg_revit_list_sheets",
             "description": "List sheets",
             "inputSchema": {},
             "annotations": {"readOnlyHint": False}},
            {"name": "dwg_revit_create_sheet",
             "description": "Create sheet",
             "inputSchema": {},
             "annotations": {"readOnlyHint": False}},
            {"name": "dwg_revit_delete_elements",
             "description": "Delete elements",
             "inputSchema": {},
             "annotations": {"readOnlyHint": False}},
        ]
        self.provider = DwgParserProvider(
            lambda: self.child_tools, lambda name, args: {"ok": True},
            self.policy,
        )

    def test_child_annotations_are_overridden_by_manifest(self):
        specs = {spec.canonical: spec for spec in self.provider.list_tools()}
        self.assertTrue(specs["dwg.revit_list_sheets"].annotations["readOnlyHint"])
        self.assertTrue(specs["dwg.revit_create_sheet"].annotations["writesDocument"])
        self.assertTrue(specs["dwg.revit_delete_elements"].annotations["destructiveHint"])

    def test_read_profile_allows_reads_only(self):
        specs = {spec.canonical: spec for spec in self.provider.list_tools()}
        gate = ToolPolicy("revit-read")
        self.assertIsNone(gate.check(specs["dwg.revit_list_sheets"]))
        self.assertEqual("write_blocked", gate.check(specs["dwg.revit_create_sheet"]).code)

    def test_legacy_tool_is_deprecated_and_carries_hint(self):
        specs = {spec.canonical: spec for spec in self.provider.list_tools()}
        legacy = specs["dwg.revit_list_sheets"]
        self.assertTrue(legacy.deprecated)
        self.assertIn("pipeline.list_sheets", legacy.description)
        self.assertFalse(specs["dwg.revit_create_sheet"].deprecated)


if __name__ == "__main__":
    unittest.main(verbosity=2)
