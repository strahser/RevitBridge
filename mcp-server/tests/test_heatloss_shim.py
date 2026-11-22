# -*- coding: utf-8 -*-
"""P4.5: revit_run_heatloss maps to the canonical WS heatloss.* commands."""
import os
import sys
import unittest
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))
SERVER_DIR = os.path.dirname(HERE)
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)

import server  # noqa: E402


class FakeGateway:
    def __init__(self):
        self.calls = []

    def call(self, name, args, confirmed=False):
        self.calls.append((name, args, confirmed))
        return {"ok": True, "command": name}


class HeatlossShimTests(unittest.TestCase):
    def setUp(self):
        self.gateway = FakeGateway()
        self.patch = mock.patch.object(server, "_get_gateway",
                                       return_value=self.gateway)
        self.patch.start()

    def tearDown(self):
        self.patch.stop()

    def test_default_is_calculations(self):
        result = server.tool_call("tools_call", {"name": "revit_run_heatloss"})
        self.assertTrue(result["ok"])
        self.assertEqual([("heatloss.calculations", {}, False)], self.gateway.calls)

    def test_named_command_and_case(self):
        server.tool_call("tools_call", {
            "name": "revit_run_heatloss",
            "args": {"command": "Modeling"},
            "confirmed": True,
        })
        self.assertEqual([("heatloss.modeling", {}, True)], self.gateway.calls)

    def test_unknown_command_is_rejected_before_dispatch(self):
        result = server.tool_call("tools_call", {
            "name": "revit_run_heatloss",
            "args": {"command": "rm_rf"},
        })
        self.assertEqual("unknown_heatloss_command", result["error"])
        self.assertIn("calculations", result["allowed"])
        self.assertEqual([], self.gateway.calls)


if __name__ == "__main__":
    unittest.main(verbosity=2)
