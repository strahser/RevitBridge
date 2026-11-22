# -*- coding: utf-8 -*-
"""P2.4-P2.8: gateway meta tools, provider adapters and policy enforcement.

Pure unit tests: no Revit, WebSocket, HTTP or child process.  ``_get_gateway``
is replaced with a catalog over fake providers so policy decisions are checked
without touching the live bridge.
"""

import os
import sys
import unittest
from unittest import mock

# test file: <root>/mcp-server/tests/test_gateway_tools.py
HERE = os.path.dirname(os.path.abspath(__file__))
SERVER_DIR = os.path.dirname(HERE)
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)

import server  # noqa: E402
from catalog import ToolCatalog  # noqa: E402
from models import ProviderHealth, ToolSpec  # noqa: E402
from providers.dwgparser import DwgParserProvider  # noqa: E402
from providers.revit_ws import RevitWsProvider  # noqa: E402


SCHEMA = {"type": "object", "properties": {}}


class FakeProvider:
    """Three tools covering read, write and destructive write."""

    name = "fake"

    def __init__(self):
        self.calls = []
        self.specs = [
            ToolSpec(
                name="revit.doc_info", canonical="revit.doc_info",
                description="Active document info", provider=self.name,
                domain="revit",
                annotations={"readOnlyHint": True, "writesDocument": False,
                             "destructiveHint": False, "requiresConfirmation": False,
                             "idempotentHint": True, "openWorldHint": False},
            ),
            ToolSpec(
                name="sheet.create", canonical="sheet.create",
                description="Create a sheet", provider=self.name,
                domain="revit",
                annotations={"readOnlyHint": False, "writesDocument": True,
                             "destructiveHint": False, "requiresConfirmation": False},
            ),
            ToolSpec(
                name="model.clear", canonical="model.clear",
                description="Clear the model", provider=self.name,
                domain="revit",
                annotations={"readOnlyHint": False, "writesDocument": True,
                             "destructiveHint": True, "requiresConfirmation": True},
            ),
        ]

    def list_tools(self):
        return list(self.specs)

    def call(self, name, args):
        self.calls.append((name, args))
        return {"ok": True, "tool": name, "args": args}

    def health(self):
        return ProviderHealth(self.name, True, "ok", len(self.specs))


class GatewayMetaToolTests(unittest.TestCase):
    def setUp(self):
        self.provider = FakeProvider()
        self.catalog = ToolCatalog(profile="revit-read")
        self.catalog.register(self.provider)
        # Confirmation is a separate gate from the write permission: exercise
        # destructive tools under a write-enabled profile.
        self.write_provider = FakeProvider()
        self.write_catalog = ToolCatalog(profile="revit-write")
        self.write_catalog.register(self.write_provider)
        patcher = mock.patch.object(
            server, "_get_gateway", return_value=self.catalog
        )
        patcher.start()
        self.addCleanup(patcher.stop)

    def test_tools_search_by_query(self):
        result = server.tool_call("tools_search", {"query": "doc"})
        self.assertIsInstance(result, list)
        names = [item["name"] for item in result]
        self.assertIn("revit.doc_info", names)
        self.assertNotIn("sheet.create", names)
        keys = {"name", "canonical", "description", "domain", "provider",
                "annotations", "deprecated", "aliases"}
        for item in result:
            with self.subTest(item=item["name"]):
                self.assertEqual(set(item), keys)

    def test_tools_search_read_only_excludes_writers(self):
        result = server.tool_call("tools_search", {"query": "", "read_only": True})
        self.assertEqual([item["name"] for item in result], ["revit.doc_info"])

    def test_tools_search_filters_domain_and_limit(self):
        result = server.tool_call("tools_search", {
            "query": "", "domain": "revit", "limit": 2,
        })
        self.assertEqual(len(result), 2)

    def test_tools_describe(self):
        detail = server.tool_call("tools_describe", {"name": "revit.doc_info"})
        self.assertEqual(detail["canonical"], "revit.doc_info")
        self.assertEqual(detail["provider"], "fake")
        self.assertIs(detail["annotations"]["readOnlyHint"], True)

    def test_tools_describe_unknown(self):
        result = server.tool_call("tools_describe", {"name": "nope"})
        self.assertEqual(result["error"], "unknown_tool")
        self.assertEqual(result["tool"], "nope")

    def test_tools_call_read_tool(self):
        result = server.tool_call("tools_call", {
            "name": "revit.doc_info", "args": {"x": 1},
        })
        self.assertEqual(result["tool"], "revit.doc_info")
        self.assertEqual(self.provider.calls[-1], ("revit.doc_info", {"x": 1}))

    def test_tools_call_write_blocked_in_read_profile(self):
        result = server.tool_call("tools_call", {"name": "sheet.create", "args": {}})
        self.assertEqual(result["error"], "write_blocked")
        self.assertIn("hint", result)
        self.assertEqual(self.provider.calls, [])

    def test_tools_call_destructive_needs_confirmation(self):
        with mock.patch.object(server, "_get_gateway",
                               return_value=self.write_catalog):
            result = server.tool_call("tools_call", {"name": "model.clear"})
        self.assertEqual(result["error"], "confirmation_required")
        self.assertEqual(self.provider.calls, [])

    def test_tools_call_destructive_with_confirmation(self):
        with mock.patch.object(server, "_get_gateway",
                               return_value=self.write_catalog):
            result = server.tool_call("tools_call", {
                "name": "model.clear", "args": {}, "confirmed": True,
            })
        self.assertTrue(result["ok"])
        self.assertEqual(self.write_provider.calls[-1][0], "model.clear")

    def test_tools_call_unknown(self):
        result = server.tool_call("tools_call", {"name": "does.not.exist"})
        self.assertEqual(result["error"], "unknown_tool")

    def test_tools_call_write_allowed_in_write_profile(self):
        write_catalog = ToolCatalog(profile="revit-write")
        write_catalog.register(FakeProvider())
        with mock.patch.object(server, "_get_gateway", return_value=write_catalog):
            result = server.tool_call("tools_call", {"name": "sheet.create"})
        self.assertTrue(result["ok"])

    def test_bridge_unavailable_is_structured(self):
        with mock.patch.object(
            server, "_get_gateway",
            side_effect=server.RevitDown("bridge down"),
        ):
            result = server.tool_call("tools_search", {"query": "doc"})
        self.assertEqual(result["error"], "bridge_unavailable")
        self.assertIn("message", result)

    def test_handle_marks_policy_errors_as_is_error(self):
        for target, expected in (
            ("sheet.create", "write_blocked"),
            ("model.clear", "confirmation_required"),
            ("nope", "unknown_tool"),
        ):
            catalog = self.write_catalog if target == "model.clear" else self.catalog
            with self.subTest(target=target), \
                 mock.patch.object(server, "_get_gateway", return_value=catalog):
                response = server.handle({
                    "id": 1,
                    "method": "tools/call",
                    "params": {"name": "tools_call",
                               "arguments": {"name": target}},
                })
                self.assertTrue(response["isError"])
                self.assertIn(expected, response["content"][0]["text"])


class RevitWsProviderTests(unittest.TestCase):
    def test_command_mapping_annotations_and_call(self):
        commands = [
            {"name": "revit.doc_info", "description": "info",
             "transaction": "read", "argsSchema": SCHEMA,
             "safety": {"readOnly": True, "writesDocument": False,
                        "destructive": False, "requiresConfirmation": False,
                        "idempotent": True, "openWorld": False}},
            {"name": "pipeline.export_sheet_pdf", "description": "export",
             "transaction": "write", "argsSchema": SCHEMA,
             "safety": {"readOnly": False, "writesDocument": False,
                        "destructive": False, "requiresConfirmation": False}},
        ]
        provider = RevitWsProvider(
            lambda: commands,
            lambda name, args: {"called": name, "args": args},
        )
        specs = {spec.name: spec for spec in provider.list_tools()}
        info = specs["revit.doc_info"]
        self.assertEqual(info.canonical, "revit.doc_info")
        self.assertEqual(info.provider, "revit-ws")
        self.assertEqual(info.domain, "revit")
        self.assertEqual(info.aliases, ("cmd_revit_doc_info",))
        self.assertEqual(info.input_schema, SCHEMA)
        self.assertIs(info.annotations["readOnlyHint"], True)
        self.assertIs(info.annotations["writesDocument"], False)
        self.assertIs(specs["pipeline.export_sheet_pdf"].annotations["writesDocument"],
                      False)
        self.assertEqual(provider.call("revit.doc_info", {"a": 1})["called"],
                         "revit.doc_info")
        self.assertTrue(provider.health().ok)

    def test_commands_dict_payload_and_fallback_annotations(self):
        payload = {"commands": [
            {"name": "x.delete_it", "description": "d", "transaction": "write"},
        ]}
        provider = RevitWsProvider(lambda: payload, lambda name, args: None)
        spec = provider.list_tools()[0]
        self.assertEqual(spec.canonical, "x.delete_it")
        self.assertEqual(spec.aliases, ("cmd_x_delete_it",))
        self.assertIs(spec.annotations["readOnlyHint"], False)
        self.assertIs(spec.annotations["destructiveHint"], True)
        self.assertEqual(provider.health().tool_count, 1)

    def test_health_when_bridge_down(self):
        def boom():
            raise RuntimeError("down")

        provider = RevitWsProvider(boom, lambda name, args: None)
        self.assertEqual(provider.list_tools(), [])
        record = provider.health()
        self.assertFalse(record.ok)
        self.assertEqual(record.name, "revit-ws")
        self.assertIn("down", record.detail)


class DwgParserProviderTests(unittest.TestCase):
    def test_mapping_and_call(self):
        tools = [{
            "name": "dwg_list_layers", "description": "list layers",
            "inputSchema": SCHEMA,
            "annotations": {"readOnlyHint": True},
        }]
        provider = DwgParserProvider(lambda: tools, lambda name, args: (name, args))
        spec = provider.list_tools()[0]
        self.assertEqual(spec.name, "dwg_list_layers")
        self.assertEqual(spec.canonical, "dwg.list_layers")
        self.assertEqual(spec.provider, "dwgparser")
        self.assertEqual(spec.domain, "dwg")
        self.assertEqual(spec.aliases, ())
        self.assertEqual(spec.input_schema, SCHEMA)
        self.assertIs(spec.annotations["readOnlyHint"], True)
        self.assertEqual(provider.call(spec.name, {"x": 1}),
                         ("list_layers", {"x": 1}))
        self.assertTrue(provider.health().ok)

    def test_health_when_child_down(self):
        def boom():
            raise RuntimeError("child down")

        provider = DwgParserProvider(boom, lambda name, args: None)
        self.assertEqual(provider.list_tools(), [])
        record = provider.health()
        self.assertFalse(record.ok)
        self.assertEqual(record.name, "dwgparser")


class StaticProviderAndToolListTests(unittest.TestCase):
    def test_static_provider_excludes_meta_tools(self):
        specs = server.StaticToolProvider().list_tools()
        names = {spec.name for spec in specs}
        self.assertEqual(len(specs), 61)
        self.assertFalse(names & server._META_TOOL_NAMES)
        self.assertIn("revit_doc_info", names)
        self.assertTrue(server.StaticToolProvider().health().ok)

    def test_meta_tools_are_published_with_manual_annotations(self):
        by_name = {tool["name"]: tool for tool in server.META_TOOLS}
        self.assertIs(by_name["tools_search"]["annotations"]["readOnlyHint"], True)
        self.assertIs(by_name["tools_describe"]["annotations"]["readOnlyHint"], True)
        self.assertIs(by_name["tools_call"]["annotations"]["readOnlyHint"], False)
        self.assertIs(by_name["tools_call"]["annotations"]["destructiveHint"], True)
        with mock.patch.object(server, "_dynamic_tools", return_value=[]), \
             mock.patch.object(server, "_child_tools", return_value=[]):
            tools = server.handle({"method": "tools/list"})["tools"]
        names = [tool["name"] for tool in tools]
        for meta in server._META_TOOL_NAMES:
            with self.subTest(meta=meta):
                self.assertIn(meta, names)
        self.assertEqual(sum(name in server._META_TOOL_NAMES for name in names), 3)


if __name__ == "__main__":
    unittest.main(verbosity=2)
