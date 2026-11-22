# -*- coding: utf-8 -*-
"""Build the SQLite tool catalog from the active gateway (P5.2).

Usage:
    python mcp-server/catalog_build.py --db .opencode/tool_catalog.db

The gateway is the same one used by the MCP server; it discovers live WS
commands, the DwgParser child tools and the static tools. The SQLite file is a
cache/audit store and can be deleted/rebuilt at any time.
"""
import argparse
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

from catalog_store import CatalogStore


def build_from_gateway(gateway, db_path):
    specs = gateway.list_tools(profile="revit-write")
    aliases = {}
    for spec in specs:
        for alias in spec.aliases or ():
            aliases[alias] = spec.canonical
    store = CatalogStore(db_path)
    try:
        count = store.replace_tools(specs, aliases)
    finally:
        store.close()
    return {"db": os.path.abspath(db_path), "tools": count,
            "aliases": len(aliases)}


def main(argv=None):
    parser = argparse.ArgumentParser(description="Build tool catalog SQLite cache")
    parser.add_argument("--db", required=True, help="path to tool_catalog.db")
    args = parser.parse_args(argv)

    import server  # noqa: E402  (local import: server builds the gateway)

    result = build_from_gateway(server._get_gateway(), args.db)
    print(result)
    return 0


if __name__ == "__main__":
    sys.exit(main())
