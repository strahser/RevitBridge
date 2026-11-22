# -*- coding: utf-8 -*-
"""P2.1/P2.2: provider protocol, value models and duck-typed registration."""

import os
import sys
import unittest

# test file: <root>/mcp-server/tests/test_providers.py
HERE = os.path.dirname(os.path.abspath(__file__))
SERVER_DIR = os.path.dirname(HERE)
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)

from catalog import ToolCatalog  # noqa: E402
from models import PolicyError, ProviderHealth, ToolSpec  # noqa: E402
from providers import ToolProvider  # noqa: E402
from providers.base import ToolProvider as BaseToolProvider  # noqa: E402


class SimpleProvider:
    """No inheritance from ToolProvider: the protocol is structural."""

    def __init__(self):
        self.name = "simple"
        self.received = None

    def list_tools(self):
        return [ToolSpec(
            name="read", canonical="simple.read", description="A read tool",
            annotations={"readOnlyHint": True}, provider=self.name,
            domain="ops",
        )]

    def call(self, name, args):
        self.received = (name, args)
        return "called"

    def health(self):
        return ProviderHealth("simple", True, "alive", 1)


class ValueModelTests(unittest.TestCase):
    def test_tool_spec_fields_and_defaults(self):
        tool = ToolSpec(name="n", canonical="c", aliases=["a", "b"])
        self.assertEqual(tool.name, "n")
        self.assertEqual(tool.canonical, "c")
        self.assertEqual(tool.description, "")
        self.assertEqual(tool.input_schema, {})
        self.assertEqual(tool.annotations, {})
        self.assertEqual(tool.provider, "")
        self.assertEqual(tool.domain, "")
        self.assertFalse(tool.deprecated)
        self.assertEqual(tool.aliases, ("a", "b"))
        self.assertIsInstance(tool.aliases, tuple)

    def test_mutable_defaults_are_not_shared(self):
        first = ToolSpec(name="a", canonical="a")
        second = ToolSpec(name="b", canonical="b")
        first.input_schema["x"] = 1
        first.annotations["y"] = 2
        self.assertEqual(second.input_schema, {})
        self.assertEqual(second.annotations, {})

    def test_health_and_policy_error_models(self):
        health = ProviderHealth("p", False, "down", 2)
        self.assertEqual((health.name, health.ok, health.detail, health.tool_count),
                         ("p", False, "down", 2))
        error = PolicyError("code", "message", "hint")
        self.assertEqual(error.as_dict()["code"], "code")


class ProviderProtocolTests(unittest.TestCase):
    def test_protocol_reexport_and_runtime_check(self):
        self.assertIs(ToolProvider, BaseToolProvider)
        provider = SimpleProvider()
        self.assertIsInstance(provider, ToolProvider)

    def test_provider_contract_called_by_catalog(self):
        provider = SimpleProvider()
        cat = ToolCatalog()
        cat.register(provider)
        tools = provider.list_tools()
        self.assertEqual(len(tools), 1)
        self.assertEqual(cat.describe("simple.read").canonical, "simple.read")
        self.assertEqual(cat.list_tools(), tools)
        self.assertEqual(cat.call("simple.read", {"x": 1}), "called")
        self.assertEqual(provider.received, ("read", {"x": 1}))
        self.assertEqual(cat.health(), [ProviderHealth("simple", True, "alive", 1)])


if __name__ == "__main__":
    unittest.main(verbosity=2)
