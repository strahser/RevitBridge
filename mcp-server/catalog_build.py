# -*- coding: utf-8 -*-
"""Build the SQLite tool catalog from the active gateway (P5.2).

Usage:
    python mcp-server/catalog_build.py --db .opencode/tool_catalog.db
    python mcp-server/catalog_build.py --db ... --verify

The gateway is the same one used by the MCP server; it discovers live WS
commands, the DwgParser child tools and the static tools. The SQLite file is a
cache/audit store and can be deleted/rebuilt at any time.

``verify_store`` is the consistency gate: it compares the stored rows with the
source specs and checks the FTS index. It never repairs anything.
"""
import argparse
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

from catalog_store import CatalogStore


def _spec_field(spec, key, default=None):
    if isinstance(spec, dict):
        return spec.get(key, default)
    return getattr(spec, key, default)


def build_from_gateway(gateway, db_path, specs=None):
    if specs is None:
        specs = gateway.list_tools(profile="revit-write")
    aliases = {}
    for spec in specs:
        for alias in _spec_field(spec, "aliases", ()) or ():
            aliases[alias] = _spec_field(spec, "canonical") or _spec_field(spec, "name")
    store = CatalogStore(db_path)
    try:
        count = store.replace_tools(specs, aliases)
    finally:
        store.close()
    return {"db": os.path.abspath(db_path), "tools": count,
            "aliases": len(aliases)}


def verify_store(store, specs, aliases=None):
    """Return a consistency report between source specs and the SQLite cache."""

    expected = {}
    for spec in specs:
        name = _spec_field(spec, "name")
        canonical = _spec_field(spec, "canonical") or name
        expected[canonical] = name
    rows = [dict(row) for row in store.db.execute(
        "select name, canonical, provider from tools")]
    stored = {row["canonical"]: row["name"] for row in rows}
    counts = {}
    for row in rows:
        counts[row["canonical"]] = counts.get(row["canonical"], 0) + 1
    duplicates = sorted(canonical for canonical, count in counts.items() if count > 1)
    missing = sorted(set(expected) - set(stored))
    extra = sorted(set(stored) - set(expected))
    renamed = sorted(canonical for canonical in set(expected) & set(stored)
                     if expected[canonical] != stored[canonical])
    fts = store.db.execute("select count(*) from tools_fts").fetchone()[0]
    expected_aliases = aliases or {}
    stored_aliases = {row[0] for row in store.db.execute("select alias from aliases")}
    return {
        "tools": len(rows),
        "expected_tools": len(expected),
        "fts": fts,
        "duplicate_canonical": sorted(set(duplicates)),
        "missing": missing,
        "extra": extra,
        "renamed": renamed,
        "missing_aliases": sorted(set(expected_aliases) - stored_aliases),
        "extra_aliases": sorted(stored_aliases - set(expected_aliases)),
        "ok": not (missing or extra or renamed or duplicates or
                   fts != len(rows) or
                   (set(expected_aliases) - stored_aliases) or
                   (stored_aliases - set(expected_aliases))),
    }


def _aliases_of(specs):
    aliases = {}
    for spec in specs:
        for alias in _spec_field(spec, "aliases", ()) or ():
            aliases[alias] = _spec_field(spec, "canonical") or _spec_field(spec, "name")
    return aliases


def main(argv=None):
    parser = argparse.ArgumentParser(description="Build tool catalog SQLite cache")
    parser.add_argument("--db", required=True, help="path to tool_catalog.db")
    parser.add_argument("--verify", action="store_true",
                        help="compare the built cache with gateway specs")
    args = parser.parse_args(argv)

    import server  # noqa: E402  (local import: server builds the gateway)

    specs = server._get_gateway().list_tools(profile="revit-write")
    result = build_from_gateway(server._get_gateway(), args.db, specs)
    if args.verify:
        store = CatalogStore(args.db)
        try:
            report = verify_store(store, specs, _aliases_of(specs))
        finally:
            store.close()
        result["verify"] = report
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0 if report["ok"] else 1
    print(result)
    return 0


if __name__ == "__main__":
    sys.exit(main())
