# -*- coding: utf-8 -*-
"""P2.8: compact tools/list and provider health."""
import os
import sys
import unittest
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
SERVER_DIR = os.path.dirname(HERE)
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)

import server  # noqa: E402


class ToolsModeTests(unittest.TestCase):
    def test_compact_default_has_only_direct_tools(self):
        with mock.patch.dict(os.environ, {}, clear=False):
            os.environ.pop("MCP_TOOLS_MODE", None)
            tools = server._tools_for_list()
        names = [tool["name"] for tool in tools]
        self.assertEqual(7, len(names))
        self.assertIn("tools_search", names)
        self.assertIn("tools_call", names)
        self.assertIn("revit_doc_info", names)
        self.assertNotIn("revit_create_wall", names)
        self.assertNotIn("cmd_revit_doc_info", names)

    def test_full_mode_uses_legacy_list(self):
        with mock.patch.dict(os.environ, {"MCP_TOOLS_MODE": "full"}):
            with mock.patch.object(server, "_dynamic_tools", return_value=[]):
                with mock.patch.object(server, "_child_tools", return_value=[]):
                    tools = server._tools_for_list()
        self.assertEqual(len(server.ALL_TOOLS), len(tools))
        self.assertGreater(len(tools), 61)

    def test_tools_health_returns_provider_records(self):
        class Health:
            def __init__(self, name, ok, detail, count):
                self.name = name
                self.ok = ok
                self.detail = detail
                self.tool_count = count

        class Gateway:
            def health(self):
                return [Health("revit-ws", True, "ok", 126),
                        Health("dwgparser", False, "down", 0)]

        with mock.patch.object(server, "_get_gateway", return_value=Gateway()):
            result = server.tool_call("tools_health", {})

        self.assertEqual(2, len(result))
        self.assertEqual("revit-ws", result[0]["name"])
        self.assertTrue(result[0]["ok"])
        self.assertFalse(result[1]["ok"])

    def test_tools_list_uses_compact_mode(self):
        with mock.patch.dict(os.environ, {}, clear=False):
            os.environ.pop("MCP_TOOLS_MODE", None)
            response = server.handle({"jsonrpc": "2.0", "id": 1,
                                      "method": "tools/list", "params": {}})
        self.assertEqual(7, len(response["tools"]))
        self.assertIn("tools_health", [t["name"] for t in response["tools"]])


if __name__ == "__main__":
    unittest.main(verbosity=2)
