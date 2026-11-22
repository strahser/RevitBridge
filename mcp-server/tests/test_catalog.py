# -*- coding: utf-8 -*-
"""P2.2: ToolCatalog merge, discovery, dispatch and registration safety."""

import os
import sys
import unittest

# test file: <root>/mcp-server/tests/test_catalog.py
HERE = os.path.dirname(os.path.abspath(__file__))
SERVER_DIR = os.path.dirname(HERE)
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)

from catalog import ToolCatalog  # noqa: E402
from models import PolicyError, ProviderHealth, ToolSpec  # noqa: E402
from policy import ToolPolicy  # noqa: E402
from providers.base import ToolProvider  # noqa: E402


READ = ToolSpec(
    name="list_rooms", canonical="revit.list_rooms",
    description="List rooms and their MEP data",
    annotations={"readOnlyHint": True, "destructiveHint": False},
    provider="revit", domain="model",
)
WRITE = ToolSpec(
    name="create_wall", canonical="revit.create_wall",
    description="Create a new wall",
    annotations={"readOnlyHint": False, "writesDocument": True,
                 "destructiveHint": False},
    provider="revit", domain="model",
    aliases=["revit_wall_create"],
)
DELETE = ToolSpec(
    name="delete_elements", canonical="revit.delete_elements",
    description="Delete elements from the model",
    annotations={"readOnlyHint": False, "writesDocument": True,
                 "destructiveHint": True, "requiresConfirmation": True},
    provider="revit", domain="model",
)
TOOLS = [READ, WRITE, DELETE]


class FakeProvider:
    """Duck-typed provider: three tools, records calls, returns a fixed result."""

    def __init__(self, name="revit", specs=None, error=None, health=None):
        self.name = name
        self.specs = list(TOOLS if specs is None else specs)
        self.calls = []
        self.error = error
        self.health_record = health

    def list_tools(self):
        return list(self.specs)

    def call(self, name, args):
        self.calls.append((name, args))
        if self.error is not None:
            raise self.error
        return {"ok": True, "tool": name, "args": args}

    def health(self):
        return self.health_record or ProviderHealth(
            self.name, True, "ok", len(self.specs)
        )


def write_catalog():
    return ToolCatalog(policy=ToolPolicy(profile="revit-write"))


class RegistrationTests(unittest.TestCase):
    def test_fake_provider_is_structural_tool_provider(self):
        self.assertIsInstance(FakeProvider(), ToolProvider)

    def test_merge_two_providers(self):
        cat = ToolCatalog()
        cat.register(FakeProvider("revit"))
        other = ToolSpec(name="ping", canonical="bridge.ping",
                         description="Ping the bridge", domain="ops",
                         annotations={"readOnlyHint": True})
        cat.register(FakeProvider("bridge", specs=[other]))
        self.assertEqual(len(cat.list_tools()), 2)  # writes filtered by default
        self.assertEqual(len(cat.search(limit=100)), 4)
        self.assertIs(cat.describe("bridge.ping"), other)

    def test_duplicate_canonical_rejected_atomically(self):
        cat = ToolCatalog()
        cat.register(FakeProvider("revit"))
        before = list(cat.list_tools(profile="revit-write"))
        with self.assertRaises(ValueError) as raised:
            cat.register(FakeProvider("other", specs=[
                ToolSpec(name="other_create", canonical=READ.canonical)
            ]))
        self.assertIn(READ.canonical, str(raised.exception))
        self.assertEqual(list(cat.list_tools(profile="revit-write")), before)
        self.assertEqual(cat.health()[0].name, "revit")

    def test_duplicate_alias_rejected(self):
        cat = ToolCatalog()
        cat.register(FakeProvider("revit"))
        with self.assertRaises(ValueError) as raised:
            cat.register(FakeProvider("other", specs=[
                ToolSpec(name="other", canonical="other.tool",
                         aliases=[WRITE.aliases[0]])
            ]))
        self.assertIn("Alias conflict", str(raised.exception))
        self.assertIs(cat.describe(WRITE.aliases[0]), WRITE)

    def test_alias_conflict_with_canonical_of_other_provider(self):
        cat = ToolCatalog()
        cat.register(FakeProvider("revit"))
        with self.assertRaises(ValueError):
            cat.register(FakeProvider("other", specs=[
                ToolSpec(name="revit.list_rooms_alias", canonical="other.tool",
                         aliases=["revit.list_rooms"])
            ]))

    def test_duplicate_provider_name_rejected(self):
        cat = ToolCatalog()
        cat.register(FakeProvider("revit"))
        with self.assertRaises(ValueError):
            cat.register(FakeProvider("revit", specs=[]))


class DiscoveryTests(unittest.TestCase):
    def setUp(self):
        self.cat = ToolCatalog()
        self.provider = FakeProvider()
        self.cat.register(self.provider)

    def test_list_tools_read_only_by_default(self):
        self.assertEqual([s.name for s in self.cat.list_tools()], [READ.name])

    def test_list_tools_profile_write(self):
        names = [s.name for s in self.cat.list_tools(profile="revit-write")]
        self.assertEqual(names, [READ.name, WRITE.name, DELETE.name])

    def test_search_is_case_insensitive_across_fields(self):
        self.assertEqual([s.name for s in self.cat.search("ROOMS")], [READ.name])
        self.assertEqual([s.name for s in self.cat.search("REVIT.CREATE")],
                         [WRITE.name])
        self.assertEqual([s.name for s in self.cat.search("delete elements")],
                         [DELETE.name])
        self.assertEqual([s.name for s in self.cat.search("MODEL")],
                         [READ.name, WRITE.name, DELETE.name])

    def test_search_domain_and_read_only_and_limit(self):
        self.assertEqual([s.name for s in self.cat.search(domain="MODEL")],
                         [READ.name, WRITE.name, DELETE.name])
        self.assertEqual([s.name for s in self.cat.search(domain="missing")], [])
        self.assertEqual([s.name for s in self.cat.search(read_only=True)],
                         [READ.name])
        self.assertEqual([s.name for s in self.cat.search(read_only=False)],
                         [WRITE.name, DELETE.name])
        self.assertEqual([s.name for s in self.cat.search(limit=2)],
                         [READ.name, WRITE.name])
        self.assertEqual(self.cat.search(limit=0), [])

    def test_describe_by_name_canonical_and_alias(self):
        for key in (WRITE.name, WRITE.canonical, WRITE.aliases[0]):
            self.assertIs(self.cat.describe(key), WRITE)
        for key in (DELETE.name, DELETE.canonical):
            self.assertIs(self.cat.describe(key), DELETE)

    def test_describe_unknown_raises_key_error(self):
        with self.assertRaises(KeyError):
            self.cat.describe("no.such.tool")

    def test_health(self):
        self.assertEqual([h.name for h in self.cat.health()], ["revit"])
        health = self.cat.health()[0]
        self.assertTrue(health.ok)
        self.assertEqual(health.tool_count, 3)

    def test_health_probe_failure_is_reported_not_raised(self):
        class Broken(FakeProvider):
            def health(self):
                raise OSError("probe down")

        cat = ToolCatalog()
        cat.register(Broken("broken"))
        record = cat.health()[0]
        self.assertFalse(record.ok)
        self.assertIn("probe down", record.detail)


class DispatchTests(unittest.TestCase):
    def setUp(self):
        self.provider = FakeProvider()
        self.cat = ToolCatalog()
        self.cat.register(self.provider)

    def test_call_read_tool_forwards_args(self):
        args = {"limit": 10, "offset": 2}
        result = self.cat.call("list_rooms", args)
        self.assertTrue(result["ok"])
        self.assertEqual(self.provider.calls[-1], (READ.name, args))

    def test_call_write_blocked_by_read_only_policy(self):
        with self.assertRaises(PolicyError) as raised:
            self.cat.call("create_wall", {})
        self.assertEqual(raised.exception.code, "write_blocked")
        self.assertIn("read-only", raised.exception.message)
        self.assertEqual(self.provider.calls, [])
        self.assertEqual(set(raised.exception.as_dict()),
                         {"code", "message", "hint"})

    def test_write_profile_allows_write_but_not_unconfirmed_delete(self):
        cat = write_catalog()
        provider = FakeProvider()
        cat.register(provider)
        self.assertTrue(cat.call("create_wall", {"type_id": "1"})["ok"])
        with self.assertRaises(PolicyError) as raised:
            cat.call("delete_elements", {"ids": [1, 2]})
        self.assertEqual(raised.exception.code, "confirmation_required")

    def test_alias_resolution_and_confirmed_delete(self):
        cat = write_catalog()
        provider = FakeProvider()
        cat.register(provider)
        args = {"ids": [7]}
        result = cat.call("revit_wall_create", args)
        self.assertEqual(provider.calls[-1], (WRITE.name, args))
        self.assertTrue(cat.call("delete_elements", args, confirmed=True)["ok"])
        self.assertEqual(provider.calls[-1], (DELETE.name, args))

    def test_unknown_alias_call_raises_key_error(self):
        with self.assertRaises(KeyError):
            self.cat.call("missing.alias", {})

    def test_provider_error_wrapped_in_runtime_error(self):
        provider = FakeProvider(error=ValueError("bad args"))
        cat = write_catalog()
        cat.register(provider)
        with self.assertRaises(RuntimeError) as raised:
            cat.call("create_wall", {})
        self.assertIn("revit", str(raised.exception))
        self.assertIsInstance(raised.exception.__cause__, ValueError)


if __name__ == "__main__":
    unittest.main(verbosity=2)
