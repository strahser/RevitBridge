# -*- coding: utf-8 -*-
"""Reproducible baseline of the MCP tool surface (Phase 0, read-only).

Reads four sources as plain text/JSON -- it never imports, executes or modifies
them and never talks to Revit:

  * static MCP tools : mcp-server/server.py, block ``TOOLS = [ ... ]``
                       (names captured from ``"name": "..."``)
  * dynamic commands : RevitBridge.Revit/BridgeCommandHost.cs
                       (names captured from ``Register("...")``)
  * DwgParser tools  : <DwgParser>/mcp_server.py
                       (function names following ``@mcp.tool()``)
  * donor catalog    : <DwgParser>/tools/mcp_gen/tools_registry.json
                       (``len(tools)`` + the JSON ``counts`` object)

Only the Python standard library is used. All paths are resolved relative to
the RevitBridge root (derived from this file's location). The DwgParser root may
be overridden with the ``DWGPARSER_ROOT`` environment variable; by default it is
``<RevitBridge>/../DwgParser``.

CLI:
    python tools/tool_surface_audit.py [--output path.json]

Without --output the JSON report is printed to stdout (UTF-8).
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys

# Known static ``revit_*`` tools that are also exposed as dynamic ``cmd_revit_*``
# tools. Listed explicitly so the duplicate contract is visible in the report.
KNOWN_DUPLICATES = [
    "revit_open_document",
    "revit_close_document",
    "revit_doc_info",
    "revit_list_levels",
    "revit_list_rooms",
    "revit_ping",
]

_TOOLS_BLOCK_RE = re.compile(r"TOOLS\s*=\s*\[")
_NAME_RE = re.compile(r'"name"\s*:\s*"([^"]+)"')
_REGISTER_RE = re.compile(r'Register\(\s*"([^"]+)"')
_MCP_TOOL_RE = re.compile(r"@mcp\.tool[^\n]*\n\s*def\s+([A-Za-z_]\w*)\s*\(")


# --------------------------------------------------------------------------- paths
def bridge_root() -> str:
    """RevitBridge root (parent of the ``tools`` directory holding this file)."""
    return os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def dwgparser_root() -> str:
    """DwgParser root: $DWGPARSER_ROOT or <RevitBridge>/../DwgParser."""
    env = os.environ.get("DWGPARSER_ROOT", "").strip()
    if env:
        return os.path.abspath(env)
    return os.path.normpath(os.path.join(bridge_root(), "..", "DwgParser"))


def source_paths(root: str | None = None, dwg_root: str | None = None) -> dict:
    root = root or bridge_root()
    dwg_root = dwg_root or dwgparser_root()
    return {
        "static": os.path.join(root, "mcp-server", "server.py"),
        "bridge": os.path.join(root, "RevitBridge.Revit", "BridgeCommandHost.cs"),
        "dwg": os.path.join(dwg_root, "mcp_server.py"),
        "catalog": os.path.join(dwg_root, "tools", "mcp_gen", "tools_registry.json"),
    }


# --------------------------------------------------------------------------- helpers
def read_text(path: str) -> str:
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        return fh.read()


def _bracket_block(text: str, start: int) -> str:
    """Return text from ``start`` up to the matching ``]``, ignoring quoted strings."""
    # ``start`` is just after the opening ``[`` of the TOOLS list, so we are
    # already one level deep; nested lists (e.g. "required": [...]) add levels.
    depth = 1
    in_str = False
    escaped = False
    for pos in range(start, len(text)):
        ch = text[pos]
        if in_str:
            if escaped:
                escaped = False
            elif ch == "\\":
                escaped = True
            elif ch == '"':
                in_str = False
            continue
        if ch == '"':
            in_str = True
        elif ch == "[":
            depth += 1
        elif ch == "]":
            depth -= 1
            if depth == 0:
                return text[start:pos]
    return text[start:]


def cmd_name(name: str) -> str:
    """Mirror server.py::_tool_name -- dynamic command -> MCP tool name."""
    return "cmd_" + re.sub(r"[^A-Za-z0-9_]", "_", name)


def normalize_static(name: str) -> str:
    """Normalize a static ``revit_x`` tool to the dynamic namespace ``cmd_revit_x``."""
    if name.startswith("revit_"):
        return cmd_name(name)
    return name


# --------------------------------------------------------------------------- extractors
def extract_static(path: str) -> list:
    text = read_text(path)
    match = _TOOLS_BLOCK_RE.search(text)
    if not match:
        return []
    block = _bracket_block(text, match.end())
    return _NAME_RE.findall(block)


def extract_bridge(path: str) -> list:
    return _REGISTER_RE.findall(read_text(path))


def extract_dwg(path: str) -> list:
    return _MCP_TOOL_RE.findall(read_text(path))


def extract_catalog(path: str) -> dict:
    with open(path, "r", encoding="utf-8") as fh:
        data = json.load(fh)
    tools = data.get("tools", [])
    counts = data.get("counts", {})
    return {
        "total": len(tools),
        "counts": {str(k): v for k, v in counts.items()},
        "schema_version": data.get("schema_version"),
        "source_db": data.get("source_db"),
    }


# --------------------------------------------------------------------------- duplicates
def compute_duplicates(static: list, bridge: list) -> tuple:
    """Return (static-form names shared with dynamic tools, detailed mappings)."""
    static_norm = {}
    for name in static:
        static_norm.setdefault(normalize_static(name), name)

    dynamic_norm = {}
    for command in bridge:
        dynamic_norm.setdefault(cmd_name(command), command)

    overlap = sorted(set(static_norm) & set(dynamic_norm))
    duplicates = [static_norm[n] for n in overlap]
    details = [
        {
            "static": static_norm[n],
            "static_normalized": n,
            "dynamic_command": dynamic_norm[n],
            "dynamic_tool": n,
        }
        for n in overlap
    ]
    return duplicates, details


# --------------------------------------------------------------------------- report
def build_report(root: str | None = None, dwg_root: str | None = None) -> dict:
    paths = source_paths(root, dwg_root)

    static = extract_static(paths["static"])
    bridge = extract_bridge(paths["bridge"])
    dwg = extract_dwg(paths["dwg"])
    catalog = extract_catalog(paths["catalog"])
    duplicates, duplicate_details = compute_duplicates(static, bridge)

    return {
        "static": static,
        "bridge": bridge,
        "dwg": dwg,
        "catalog": catalog,
        "duplicates": duplicates,
        "known_duplicates": list(KNOWN_DUPLICATES),
        "duplicate_details": duplicate_details,
        "normalization": {
            "static": "revit_x -> cmd_revit_x",
            "dynamic": "command 'x.y' -> cmd_x_y (server.py::_tool_name)",
        },
        "counts": {
            "static": len(static),
            "bridge": len(bridge),
            "dwg": len(dwg),
            "catalog": catalog["total"],
            "duplicates": len(duplicates),
        },
        "sources": paths,
    }


def main(argv: list | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", help="Write JSON to this UTF-8 file instead of stdout.")
    args = parser.parse_args(argv)

    report = build_report()
    payload = json.dumps(report, ensure_ascii=False, indent=2)

    if args.output:
        out = os.path.abspath(args.output)
        parent = os.path.dirname(out)
        if parent and not os.path.isdir(parent):
            os.makedirs(parent, exist_ok=True)
        with open(out, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(payload + "\n")
        print("Wrote %s" % out, file=sys.stderr)
    else:
        sys.stdout.reconfigure(encoding="utf-8")
        print(payload)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
