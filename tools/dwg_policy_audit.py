# -*- coding: utf-8 -*-
"""Consistency gate: DwgParser policy manifest vs the live child tool list.

Usage:
    python tools/dwg_policy_audit.py
    python tools/dwg_policy_audit.py --policy mcp-server/manifests/dwg_policy.json

Exit code 1 when the manifest has missing/unknown tools, an overlap, or a
destructive tool that is not declared as a document writer.
"""
import argparse
import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SERVER_DIR = os.path.join(ROOT, "mcp-server")
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)

from dwg_policy import DwgToolPolicy  # noqa: E402


def live_names():
    import server  # noqa: E402  (builds providers from env)

    gateway = server._get_gateway()
    return sorted(
        spec.canonical for spec in gateway.list_tools(profile="revit-write")
        if getattr(spec, "provider", "") == "dwgparser"
    )


def main(argv=None):
    parser = argparse.ArgumentParser(description="DwgParser policy consistency gate")
    parser.add_argument("--policy", default="", help="manifest path override")
    args = parser.parse_args(argv)

    policy = DwgToolPolicy.load(args.policy or None)
    report = policy.validate(live_names())
    report["ok"] = policy.ok(report)
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if report["ok"] else 1


if __name__ == "__main__":
    sys.exit(main())
