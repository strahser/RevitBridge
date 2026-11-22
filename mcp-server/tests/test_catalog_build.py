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

from catalog_build import build_from_gateway  # noqa: E402
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
            finally:
                store.close()


if __name__ == "__main__":
    unittest.main(verbosity=2)
