# -*- coding: utf-8 -*-
"""P5.2: SQLite catalog store round-trip."""
import os
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SERVER_DIR = os.path.dirname(HERE)
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)

from catalog_store import CatalogStore  # noqa: E402
from models import ToolSpec  # noqa: E402


class CatalogStoreTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="catalog_store_")
        self.store = CatalogStore(os.path.join(self.tmp.name, "catalog.db"))
        self.specs = [
            ToolSpec(name="sheet_list", canonical="sheet.list",
                     description="List sheets", domain="sheet",
                     provider="revit-ws",
                     annotations={"readOnlyHint": True}),
            ToolSpec(name="sheet_create", canonical="sheet.create",
                     description="Create sheet", domain="sheet",
                     provider="revit-ws",
                     annotations={"readOnlyHint": False,
                                  "writesDocument": True}),
            ToolSpec(name="dwg_list_layers", canonical="dwg.list_layers",
                     description="List layers", domain="dwg",
                     provider="dwgparser",
                     annotations={"readOnlyHint": True}),
        ]

    def tearDown(self):
        self.store.close()
        self.tmp.cleanup()

    def test_replace_search_and_describe(self):
        self.assertEqual(3, self.store.replace_tools(
            self.specs, {"pipeline.list_sheets": "sheet.list"}))
        self.assertEqual(2, len(self.store.search("sheet", limit=10)))
        self.assertEqual(3, len(self.store.search("", limit=10)))
        self.assertEqual(2, len(self.store.search("", read_only=True, limit=10)))
        self.assertEqual("sheet.list", self.store.describe("sheet_list")["canonical"])
        self.assertEqual("sheet.list", self.store.describe("pipeline.list_sheets")["canonical"])
        self.assertIsNone(self.store.describe("nope"))

    def test_audit_row(self):
        self.store.replace_tools(self.specs)
        self.store.record_call("sheet.create", "ok", 12, args_hash="abc")
        rows = self.store.db.execute(
            "select tool_name, status, duration_ms from tool_calls").fetchall()
        self.assertEqual(1, len(rows))
        self.assertEqual("sheet.create", rows[0]["tool_name"])
        self.assertEqual("ok", rows[0]["status"])

    def test_duplicate_canonical_is_rejected(self):
        with self.assertRaises(Exception):
            self.store.replace_tools([
                ToolSpec(name="a", canonical="x.y"),
                ToolSpec(name="b", canonical="x.y"),
            ])


if __name__ == "__main__":
    unittest.main(verbosity=2)
