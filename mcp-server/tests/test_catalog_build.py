# -*- coding: utf-8 -*-
"""P5.2: build catalog DB from a gateway snapshot."""
import os
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SERVER_DIR = os.path.dirname(HERE)
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)

from catalog_build import (_aliases_of, build_from_gateway,  # noqa: E402
                           verify_store)
from catalog_store import CatalogStore  # noqa: E402
from models import ToolSpec  # noqa: E402


class FakeGateway:
    def list_tools(self, profile=None):
        return [
            ToolSpec(name="sheet_list", canonical="sheet.list",
                     description="List sheets", domain="sheet",
                     provider="revit-ws", aliases=("cmd_sheet_list",),
                     annotations={"readOnlyHint": True}),
            ToolSpec(name="sheet_create", canonical="sheet.create",
                     description="Create sheet", domain="sheet",
                     provider="revit-ws", aliases=(),
                     annotations={"readOnlyHint": False}),
        ]


class CatalogBuildTests(unittest.TestCase):
    def test_build_from_gateway(self):
        with tempfile.TemporaryDirectory(prefix="catalog_build_") as tmp:
            db = os.path.join(tmp, "catalog.db")
            result = build_from_gateway(FakeGateway(), db)
            self.assertEqual(2, result["tools"])
            self.assertEqual(1, result["aliases"])

            store = CatalogStore(db)
            try:
                self.assertEqual("sheet.list", store.describe("cmd_sheet_list")["canonical"])
                self.assertEqual(1, len(store.search("", read_only=True, limit=10)))
                self.assertEqual(2, len(store.search("", limit=10)))

                specs = FakeGateway().list_tools()
                report = verify_store(store, specs, _aliases_of(specs))
                self.assertTrue(report["ok"])
                self.assertEqual(2, report["tools"])
                self.assertEqual(2, report["fts"])
                self.assertEqual([], report["missing"])
                self.assertEqual([], report["extra"])
                self.assertEqual([], report["duplicate_canonical"])
            finally:
                store.close()

    def test_verify_detects_missing_extra_and_alias_drift(self):
        with tempfile.TemporaryDirectory(prefix="catalog_verify_") as tmp:
            db = os.path.join(tmp, "catalog.db")
            build_from_gateway(FakeGateway(), db)
            store = CatalogStore(db)
            try:
                specs = FakeGateway().list_tools()
                report = verify_store(store, specs[1:], {"old_alias": "sheet.list"})
                self.assertFalse(report["ok"])
                self.assertEqual(["sheet.list"], report["extra"])
                self.assertEqual([], report["missing"])
                self.assertEqual(["old_alias"], report["missing_aliases"])
                self.assertEqual(["cmd_sheet_list"], report["extra_aliases"])
            finally:
                store.close()


if __name__ == "__main__":
    unittest.main(verbosity=2)
