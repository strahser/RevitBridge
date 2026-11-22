# -*- coding: utf-8 -*-
"""P5.4: gateway uses the SQLite catalog for search/describe and audit."""
import os
import sys
import tempfile
import unittest
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
SERVER_DIR = os.path.dirname(HERE)
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)

import server  # noqa: E402
from catalog_store import CatalogStore  # noqa: E402
from models import PolicyError, ToolSpec  # noqa: E402


class FakeGateway:
    def search(self, **kwargs):
        return [ToolSpec(name="live_tool", canonical="live.tool",
                         description="live", domain="live", provider="fake")]

    def describe(self, name):
        return ToolSpec(name="live_tool", canonical="live.tool",
                        description="live", domain="live", provider="fake")

    def call(self, name, args, confirmed=False):
        if name == "blocked":
            raise PolicyError("write_blocked", "blocked", "read-only")
        return {"ok": True, "name": name}


class CatalogGatewayTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="cat_gateway_")
        self.db = os.path.join(self.tmp.name, "catalog.db")
        self.store = CatalogStore(self.db)
        self.store.replace_tools([
            ToolSpec(name="sheet_list", canonical="sheet.list",
                     description="List sheets", domain="sheet",
                     provider="revit-ws", aliases=("cmd_sheet_list",),
                     annotations={"readOnlyHint": True}),
        ], {"cmd_sheet_list": "sheet.list"})
        self.patches = [
            mock.patch.object(server, "_get_catalog_store", return_value=self.store),
            mock.patch.object(server, "_get_gateway", return_value=FakeGateway()),
        ]
        for patch in self.patches:
            patch.start()

    def tearDown(self):
        for patch in reversed(self.patches):
            patch.stop()
        self.store.close()
        self.tmp.cleanup()

    def test_search_uses_store_first(self):
        result = server.tool_call("tools_search", {"query": "sheet"})
        self.assertEqual(1, len(result))
        self.assertEqual("sheet.list", result[0]["canonical"])
        self.assertTrue(result[0]["annotations"]["readOnlyHint"])
        self.assertEqual(["cmd_sheet_list"], result[0]["aliases"])

    def test_describe_uses_store(self):
        result = server.tool_call("tools_describe", {"name": "cmd_sheet_list"})
        self.assertEqual("sheet.list", result["canonical"])
        self.assertEqual(["cmd_sheet_list"], result["aliases"])

    def test_call_records_audit_success_and_policy(self):
        ok = server.tool_call("tools_call", {"name": "live_tool", "args": {"a": 1}})
        self.assertTrue(ok["ok"])
        blocked = server.tool_call("tools_call", {"name": "blocked"})
        self.assertEqual("write_blocked", blocked["error"])

        rows = self.store.db.execute(
            "select tool_name, status from tool_calls order by id").fetchall()
        self.assertEqual([("live_tool", "ok"), ("blocked", "policy_error")],
                         [(r["tool_name"], r["status"]) for r in rows])

    def test_unknown_tool_audited(self):
        class NoCall:
            def call(self, name, args, confirmed=False):
                raise KeyError(name)
        with mock.patch.object(server, "_get_gateway", return_value=NoCall()):
            result = server.tool_call("tools_call", {"name": "nope"})
        self.assertEqual("unknown_tool", result["error"])
        row = self.store.db.execute(
            "select status from tool_calls order by id desc limit 1").fetchone()
        self.assertEqual("unknown_tool", row["status"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
