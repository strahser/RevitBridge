# -*- coding: utf-8 -*-
"""Phase 0 baseline: inventory of the MCP tool surface (read-only).

Imports the audit helper from tools/tool_surface_audit.py (no Revit, no executes).
Validates the four source counts, the presence of counts/duplicates and that the
known static ``revit_*`` vs dynamic ``cmd_revit_*`` duplicates are reported.
"""

import json
import os
import subprocess
import sys
import unittest

# test file: <root>/mcp-server/tests/test_tool_surface_inventory.py
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
TOOLS_DIR = os.path.join(ROOT, "tools")
if TOOLS_DIR not in sys.path:
    sys.path.insert(0, TOOLS_DIR)

import tool_surface_audit as audit  # noqa: E402

KNOWN_DUPLICATES = [
    "revit_open_document",
    "revit_close_document",
    "revit_doc_info",
    "revit_list_levels",
    "revit_list_rooms",
    "revit_ping",
]


class ToolSurfaceInventoryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.report = audit.build_report()

    def test_counts_key_present(self):
        self.assertIn("counts", self.report)
        self.assertIsInstance(self.report["counts"], dict)

    def test_duplicates_key_present(self):
        self.assertIn("duplicates", self.report)
        self.assertIsInstance(self.report["duplicates"], list)

    def test_static_baseline(self):
        self.assertGreaterEqual(self.report["counts"]["static"], 55)

    def test_bridge_baseline(self):
        self.assertEqual(self.report["counts"]["bridge"], 55)

    def test_dwg_baseline(self):
        self.assertGreaterEqual(self.report["counts"]["dwg"], 100)

    def test_catalog_baseline(self):
        self.assertGreaterEqual(self.report["counts"]["catalog"], 430)

    def test_known_duplicates_reported(self):
        duplicates = self.report["duplicates"]
        for name in KNOWN_DUPLICATES:
            with self.subTest(duplicate=name):
                self.assertIn(name, duplicates)

    def test_duplicate_details_have_dynamic_counterpart(self):
        for item in self.report["duplicate_details"]:
            self.assertTrue(item["static"].startswith("revit_"))
            self.assertTrue(item["dynamic_tool"].startswith("cmd_revit_"))
            self.assertEqual(item["static_normalized"], item["dynamic_tool"])

    def test_cli_prints_valid_json(self):
        script = os.path.join(TOOLS_DIR, "tool_surface_audit.py")
        proc = subprocess.run(
            [sys.executable, script],
            cwd=ROOT,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
        self.assertEqual(proc.returncode, 0, proc.stderr)
        payload = json.loads(proc.stdout)
        self.assertIn("counts", payload)
        self.assertIn("duplicates", payload)
        self.assertEqual(payload["counts"]["bridge"], 55)


if __name__ == "__main__":
    unittest.main(verbosity=2)
