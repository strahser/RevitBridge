# -*- coding: utf-8 -*-
"""P1.3/P1.5/P1.6: MCP static tool policy, WS migration and annotations.

Pure unit tests: no Revit, no WebSocket, no HTTP. The bridge and HTTP helpers
are monkeypatched so a stray legacy POST fails the test loudly.
"""

import os
import sys
import time
import unittest
from unittest import mock

# test file: <root>/mcp-server/tests/test_static_tool_policy.py
HERE = os.path.dirname(os.path.abspath(__file__))
SERVER_DIR = os.path.dirname(HERE)
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)

import server  # noqa: E402

STATIC_BY_NAME = {tool["name"]: tool for tool in server.TOOLS}
ANNOTATION_KEYS = {
    "readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint",
}


class StaticToolPolicyTests(unittest.TestCase):
    def test_all_static_tools_have_annotations(self):
        self.assertEqual(len(server.TOOLS), 61)
        for tool in server.TOOLS:
            with self.subTest(tool=tool["name"]):
                annotations = tool.get("annotations")
                self.assertIsInstance(annotations, dict)
                self.assertEqual(set(annotations), ANNOTATION_KEYS)
                self.assertIs(annotations["openWorldHint"], False)

    def test_write_tools_are_read_only_false(self):
        writes = [t for t in server.TOOLS if server.is_static_write(t["name"])]
        self.assertTrue(writes)
        for tool in writes:
            with self.subTest(tool=tool["name"]):
                self.assertIs(tool["annotations"]["readOnlyHint"], False)

    def test_annotation_details(self):
        self.assertIs(STATIC_BY_NAME["revit_delete_elements"]["annotations"]["destructiveHint"], True)
        self.assertIs(STATIC_BY_NAME["revit_close_document"]["annotations"]["destructiveHint"], True)
        self.assertIs(STATIC_BY_NAME["revit_ping"]["annotations"]["idempotentHint"], True)
        self.assertIs(STATIC_BY_NAME["revit_ping"]["annotations"]["readOnlyHint"], True)
        self.assertIs(STATIC_BY_NAME["revit_list_rooms"]["annotations"]["idempotentHint"], True)
        self.assertIs(STATIC_BY_NAME["revit_create_wall"]["annotations"]["idempotentHint"], False)
        # The six migrated tools are writes where the command mutates state.
        self.assertIs(STATIC_BY_NAME["revit_open_document"]["annotations"]["readOnlyHint"], False)
        self.assertIs(STATIC_BY_NAME["revit_doc_info"]["annotations"]["readOnlyHint"], True)

    def test_classifier_tokens(self):
        cases = {
            "revit_create_wall": True,
            "revit_set_instance_param": True,
            "revit_delete_elements": True,
            "revit_place_family": True,
            "revit_load_family": True,
            "revit_move_element": True,
            "revit_rotate_element": True,
            "revit_copy_element": True,
            "revit_mirror_element": True,
            "revit_cut_opening": True,
            "revit_backup_document": True,
            "revit_undo_last": True,
            "revit_run_heatloss": True,
            "revit_open_document": True,
            "revit_close_document": True,
            "revit_duplicate_schedule": True,
            "revit_export_schedule": True,
            "revit_modify_schedule": True,
            "revit_refresh_schedule": True,
            "revit_connect_pipes": True,
            "revit_dry_run": True,
            "revit_ping": False,
            "revit_doc_info": False,
            "revit_list_rooms": False,
            "revit_get_bounding_box": False,
            "revit_query_elements": False,
            "revit_measure_distance": False,
            "revit_commands": False,
        }
        for name, expected in cases.items():
            with self.subTest(name=name):
                self.assertEqual(server.is_static_write(name), expected)

    def test_legacy_write_blocked_without_http(self):
        # Any attempt to reach _api (HTTP GET or POST) must fail the test.
        with mock.patch.object(server, "_api",
                               side_effect=AssertionError("_api must not be called")):
            for name, args in (
                ("revit_create_wall", {"type_id": "1", "start_mm": [0, 0]}),
                ("revit_place_family", {"family": "Window"}),
                ("revit_delete_elements", {"ids": [1, 2]}),
                ("revit_set_instance_param", {"instance_id": 1}),
                ("revit_move_element", {"element_id": 1, "delta_mm": [0, 0, 0]}),
            ):
                with self.subTest(name=name):
                    result = server.tool_call(name, args)
                    self.assertEqual(result["error"], "legacy_write_disabled")
                    self.assertEqual(result["tool"], name)
                    self.assertIn("Use WS cmd_* / tools_call", result["hint"])

    def test_residual_post_tools_blocked_without_http(self):
        # These static tools have no WS command; they must be blocked before
        # reaching their legacy HTTP POST endpoints.
        names = (
            "revit_duplicate_schedule",
            "revit_export_schedule",
            "revit_modify_schedule",
            "revit_refresh_schedule",
            "revit_connect_pipes",
            "revit_dry_run",
        )
        with mock.patch.object(server, "_api",
                               side_effect=AssertionError("_api must not be called")):
            for name in names:
                with self.subTest(name=name):
                    result = server.tool_call(name, {})
                    self.assertEqual(result["error"], "legacy_write_disabled")
                    self.assertEqual(result["tool"], name)
                    self.assertIs(
                        STATIC_BY_NAME[name]["annotations"]["readOnlyHint"], False
                    )

    def test_generic_dispatchers_are_not_read_only(self):
        for name in ("bridge_call", "revit_call"):
            with self.subTest(name=name):
                annotations = STATIC_BY_NAME[name]["annotations"]
                self.assertIs(annotations["readOnlyHint"], False)
                self.assertIs(annotations["destructiveHint"], True)
                self.assertIs(annotations["idempotentHint"], False)
                self.assertIs(annotations["openWorldHint"], False)

    def test_handle_marks_legacy_write_as_error(self):
        response = server.handle({
            "id": 7,
            "method": "tools/call",
            "params": {"name": "revit_create_wall", "arguments": {}},
        })
        self.assertTrue(response["isError"])
        self.assertIn("legacy_write_disabled", response["content"][0]["text"])


class WsMigrationTests(unittest.TestCase):
    def _assert_ws_call(self, name, args, command, expected_args):
        # Migrated static tools must never use HTTP.
        with mock.patch.object(
            server, "_api", side_effect=AssertionError("_api must not be called")
        ), mock.patch.object(server, "_bridge_call", return_value={"ok": True}) as bridge:
            result = server.tool_call(name, args)
        self.assertEqual(result, {"ok": True})
        self.assertEqual(bridge.call_count, 1)
        positional, keyword = bridge.call_args
        self.assertEqual(positional[0], "call")
        self.assertEqual(positional[1]["command"], command)
        self.assertEqual(positional[1]["args"], expected_args)
        self.assertIn("timeout", keyword)

    def test_revit_ping_migrated(self):
        self._assert_ws_call("revit_ping", {}, "revit.ping", {})

    def test_revit_doc_info_migrated(self):
        self._assert_ws_call("revit_doc_info", {}, "revit.doc_info", {})

    def test_revit_list_levels_migrated(self):
        self._assert_ws_call("revit_list_levels", {}, "revit.list_levels", {})

    def test_revit_list_rooms_maps_limit_offset(self):
        self._assert_ws_call(
            "revit_list_rooms", {"limit": 120, "offset": 40},
            "revit.list_rooms", {"limit": 120, "offset": 40},
        )
        self._assert_ws_call(
            "revit_list_rooms", {"limit": 5000, "offset": -3},
            "revit.list_rooms", {"limit": 500, "offset": 0},
        )

    def test_revit_open_document_maps_detach_and_audit(self):
        self._assert_ws_call(
            "revit_open_document",
            {"path": r"C:\models\a.rvt", "detach_from_central": True, "audit": True},
            "revit.open_document",
            {"path": r"C:\models\a.rvt", "detach": True, "audit": True},
        )

    def test_revit_close_document_maps_save(self):
        self._assert_ws_call(
            "revit_close_document", {"save": True},
            "revit.close_document", {"save": True},
        )


class DynamicAndChildAnnotationTests(unittest.TestCase):
    def test_dynamic_read_command_annotations(self):
        fake = {
            "cmd_x": {
                "name": "x.y",
                "transaction": "read",
                "description": "fake read",
                "argsSchema": {"type": "object", "properties": {}},
            },
        }
        with mock.patch.object(server, "_BRIDGE_TOOLS", fake), \
             mock.patch.object(server, "_BRIDGE_TOOLS_AT", time.time()):
            tools = server._dynamic_tools()
        tool = next(t for t in tools if t["name"] == "cmd_x")
        annotations = tool["annotations"]
        self.assertIs(annotations["readOnlyHint"], True)
        self.assertIs(annotations["idempotentHint"], True)
        self.assertIs(annotations["destructiveHint"], False)
        self.assertIs(annotations["openWorldHint"], False)

    def test_dynamic_write_command_annotations(self):
        fake = {
            "cmd_revit_delete_wall": {
                "name": "revit.delete_wall",
                "transaction": "write",
                "description": "fake write",
                "argsSchema": {"type": "object", "properties": {}},
            },
        }
        with mock.patch.object(server, "_BRIDGE_TOOLS", fake), \
             mock.patch.object(server, "_BRIDGE_TOOLS_AT", time.time()):
            tools = server._dynamic_tools()
        tool = next(t for t in tools if t["name"] == "cmd_revit_delete_wall")
        annotations = tool["annotations"]
        self.assertIs(annotations["readOnlyHint"], False)
        self.assertIs(annotations["destructiveHint"], True)
        self.assertIs(annotations["idempotentHint"], False)

    def test_dynamic_safety_overrides_transaction_and_name(self):
        # safety is authoritative even when it contradicts the transaction and
        # the tool name: transaction=read + name without destructive tokens.
        fake = {
            "cmd_x": {
                "name": "x.y",
                "transaction": "read",
                "description": "fake",
                "argsSchema": {"type": "object", "properties": {}},
                "safety": {
                    "readOnly": False,
                    "writesDocument": True,
                    "destructive": True,
                    "requiresConfirmation": True,
                    "backupRequired": True,
                    "idempotent": False,
                    "openWorld": True,
                },
            },
        }
        with mock.patch.object(server, "_BRIDGE_TOOLS", fake), \
             mock.patch.object(server, "_BRIDGE_TOOLS_AT", time.time()):
            tools = server._dynamic_tools()
        annotations = next(t for t in tools if t["name"] == "cmd_x")["annotations"]
        self.assertIs(annotations["readOnlyHint"], False)
        self.assertIs(annotations["destructiveHint"], True)
        self.assertIs(annotations["idempotentHint"], False)
        self.assertIs(annotations["openWorldHint"], True)
        self.assertEqual(set(annotations), ANNOTATION_KEYS)

    def test_child_annotations_and_schema_preserved(self):
        schema = {"type": "object", "properties": {"pattern": {"type": "string"}}}
        # Raw child tool names get the "dwg_" prefix in _refresh_child_tools.
        fake = {
            "list_layers": {
                "name": "list_layers", "description": "list", "inputSchema": schema,
            },
            "verify_file": {
                "name": "verify_file", "description": "verify",
                "inputSchema": {"type": "object", "properties": {}},
            },
            "delete_layer": {
                "name": "delete_layer", "description": "delete",
                "inputSchema": {"type": "object", "properties": {}},
            },
            "export_dwg": {
                "name": "export_dwg", "description": "export",
                "inputSchema": {"type": "object", "properties": {}},
            },
        }
        # Drive the real refresh path: feed raw child tools through
        # _child_request and let _refresh_child_tools attach annotations.
        raw_tools = [
            {"name": name, "description": value["description"],
             "inputSchema": value["inputSchema"]}
            for name, value in fake.items()
        ]
        with mock.patch.object(server, "_child_request",
                               return_value={"tools": raw_tools}), \
             mock.patch.object(server, "_CHILD_TOOLS", {}), \
             mock.patch.object(server, "_CHILD_TOOL_MAP", {}), \
             mock.patch.object(server, "_CHILD_AT", 0.0):
            tools = {t["name"]: t for t in server._child_tools()}
        self.assertIs(tools["dwg_list_layers"]["annotations"]["readOnlyHint"], True)
        self.assertIs(tools["dwg_verify_file"]["annotations"]["readOnlyHint"], True)
        self.assertIs(tools["dwg_delete_layer"]["annotations"]["readOnlyHint"], False)
        self.assertIs(tools["dwg_delete_layer"]["annotations"]["destructiveHint"], True)
        self.assertIs(tools["dwg_export_dwg"]["annotations"]["readOnlyHint"], False)
        self.assertIs(tools["dwg_list_layers"]["inputSchema"], schema)
        self.assertEqual(set(tools["dwg_list_layers"]["annotations"]), ANNOTATION_KEYS)


if __name__ == "__main__":
    unittest.main(verbosity=2)
