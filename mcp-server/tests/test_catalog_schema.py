# -*- coding: utf-8 -*-
"""P5.1: SQLite catalog schema is disposable and self-contained."""
import os
import sqlite3
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SERVER_DIR = os.path.dirname(HERE)
SCHEMA = os.path.join(SERVER_DIR, "sql", "catalog_schema.sql")


class CatalogSchemaTests(unittest.TestCase):
    def setUp(self):
        with open(SCHEMA, encoding="utf-8") as handle:
            self.sql = handle.read()
        self.db = sqlite3.connect(":memory:")
        self.db.executescript(self.sql)

    def tearDown(self):
        self.db.close()

    def test_tables_and_fts_exist(self):
        names = {row[0] for row in self.db.execute(
            "select name from sqlite_master where type in ('table','view')")}
        for expected in ("tools", "aliases", "tool_calls", "catalog_meta", "tools_fts"):
            self.assertIn(expected, names)

    def test_tool_and_alias_round_trip(self):
        self.db.execute(
            "insert into tools(name, canonical, provider, domain, description, "
            "input_schema_json, tx, read_only) values (?,?,?,?,?,?,?,?)",
            ("sheet_create", "sheet.create", "revit-ws", "sheet", "Create sheet",
             "{}", "write", 0),
        )
        self.db.execute(
            "insert into aliases(alias, canonical) values (?,?)",
            ("pipeline.create_sheet", "sheet.create"),
        )
        row = self.db.execute(
            "select t.canonical, a.alias from tools t "
            "join aliases a on a.canonical=t.canonical").fetchone()
        self.assertEqual(("sheet.create", "pipeline.create_sheet"), row)

    def test_duplicate_canonical_rejected(self):
        values = ("a", "sheet.create", "revit-ws", "sheet", "", "{}", "read", 1)
        self.db.execute(
            "insert into tools(name, canonical, provider, domain, description, "
            "input_schema_json, tx, read_only) values (?,?,?,?,?,?,?,?)",
            values,
        )
        with self.assertRaises(sqlite3.IntegrityError):
            self.db.execute(
                "insert into tools(name, canonical, provider, domain, description, "
                "input_schema_json, tx, read_only) values (?,?,?,?,?,?,?,?)",
                ("b", "sheet.create", "revit-ws", "sheet", "", "{}", "read", 1),
            )

    def test_audit_row(self):
        self.db.execute(
            "insert into tool_calls(tool_name, started_at, duration_ms, status) "
            "values (?,?,?,?)",
            ("sheet.create", "2026-11-22T00:00:00", 12, "ok"),
        )
        row = self.db.execute(
            "select tool_name, status from tool_calls").fetchone()
        self.assertEqual(("sheet.create", "ok"), row)


if __name__ == "__main__":
    unittest.main(verbosity=2)
