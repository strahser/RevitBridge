# -*- coding: utf-8 -*-
"""DwgParser child tool classification (manifest is the source of truth).

The DwgParser child MCP annotates almost every tool with the MCP default
``readOnlyHint: false``, including pure readers such as ``revit_list_sheets``.
The MCP default is "may modify the environment", so trusting it would block all
DwgParser reads in a read-only profile. Conversely, guessing read-only for a new
tool would silently allow a writer.

This module keeps an explicit manifest (``manifests/dwg_policy.json``):

* ``read_only``       — does not mutate the active Revit document;
* ``writes_document`` — mutates the document or executes arbitrary commands;
* ``destructive``     — a subset of writers that needs explicit confirmation.

A tool that is absent from the manifest is treated as a writer (fail closed).
The manifest is validated against the live child list by
``tools/dwg_policy_audit.py``.
"""

import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_PATH = os.path.join(HERE, "manifests", "dwg_policy.json")


def _names(value):
    return frozenset(str(item) for item in (value or ()))


class DwgToolPolicy:
    """Classify DwgParser tools and apply the result to their annotations."""

    def __init__(self, read_only=(), writes_document=(), destructive=()):
        self.read_only = _names(read_only)
        self.writes_document = _names(writes_document)
        self.destructive = _names(destructive)

    @classmethod
    def load(cls, path=None):
        """Load a manifest file; raises on a missing or malformed file."""

        path = path or os.getenv("DWG_POLICY", "").strip() or DEFAULT_PATH
        with open(path, encoding="utf-8") as handle:
            data = json.load(handle)
        if not isinstance(data, dict):
            raise ValueError("DWG policy must be a JSON object: " + path)
        return cls(
            read_only=data.get("read_only") or (),
            writes_document=data.get("writes_document") or (),
            destructive=data.get("destructive") or (),
        )

    @property
    def known(self):
        return self.read_only | self.writes_document

    def apply(self, canonical, annotations=None):
        """Return annotations with the manifest classification applied.

        Unknown tools fail closed: they are reported as document writers.
        Environment hints such as ``idempotentHint`` are preserved.
        """

        result = dict(annotations or {})
        if canonical in self.destructive:
            result.update(
                readOnlyHint=False,
                writesDocument=True,
                destructiveHint=True,
                requiresConfirmation=True,
            )
        elif canonical in self.writes_document:
            result.update(
                readOnlyHint=False,
                writesDocument=True,
                destructiveHint=False,
            )
        elif canonical in self.read_only:
            result.update(
                readOnlyHint=True,
                writesDocument=False,
                destructiveHint=False,
            )
        else:
            result.update(
                readOnlyHint=False,
                writesDocument=True,
                destructiveHint=False,
            )
        return result

    def validate(self, tool_names):
        """Compare the manifest with the tools the child actually exposes."""

        names = _names(tool_names)
        return {
            "manifest": len(self.known),
            "tools": len(names),
            "missing": sorted(self.known - names),
            "unknown": sorted(names - self.known),
            "overlap": sorted(self.read_only & self.writes_document),
            "destructive_not_writer": sorted(self.destructive - self.writes_document),
        }

    def ok(self, report):
        return not (report["missing"] or report["unknown"] or report["overlap"]
                    or report["destructive_not_writer"])


__all__ = ["DwgToolPolicy", "DEFAULT_PATH"]
