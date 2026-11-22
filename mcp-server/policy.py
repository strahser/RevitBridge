# -*- coding: utf-8 -*-
"""Annotation-driven tool policy for the MCP gateway.

The policy is intentionally independent of C#, Revit and concrete providers:
it only looks at ``ToolSpec.annotations``.  Both the MCP-style keys
(``readOnlyHint``, ``destructiveHint``) and the bridge-style keys
(``writesDocument``, ``requiresConfirmation``) are understood.

Safety rules:

* read-only profiles reject any tool that writes a document;
* destructive / confirmation-required tools are rejected unless the caller
  passes ``confirmed=True`` (an explicit, out-of-band user approval);
* an unknown profile fails closed (treated as read-only).
"""

try:  # package import, e.g. ``mcp_server.policy``
    from .models import PolicyError
except ImportError:  # flat import: <mcp-server> directory is on sys.path
    from models import PolicyError  # type: ignore[no-redef]


READ_ONLY_PROFILES = frozenset({"revit-read", "read", "read-only", "readonly"})
WRITE_PROFILES = frozenset({
    "revit-write", "write", "read-write", "readwrite", "full", "all", "admin",
})


def _is_true(value: object) -> bool:
    return value is True or (isinstance(value, str) and value.casefold() == "true")


def _is_false(value: object) -> bool:
    return value is False or (isinstance(value, str) and value.casefold() == "false")


def is_write_tool(spec) -> bool:
    """True when annotations mark the tool as mutating a document."""

    annotations = getattr(spec, "annotations", None) or {}
    # ``writesDocument`` is the bridge's explicit signal. It wins over
    # readOnlyHint: exporting a PDF or opening a document is a write action,
    # but it does not mutate the Revit document and is allowed read-only.
    if "writesDocument" in annotations:
        return _is_true(annotations["writesDocument"])
    if "readOnlyHint" in annotations and _is_false(annotations["readOnlyHint"]):
        return True
    # A destructive tool is always a writer, even with a missing/odd hint.
    return is_destructive_tool(spec)


def is_destructive_tool(spec) -> bool:
    """True when annotations ask for explicit confirmation before a call."""

    annotations = getattr(spec, "annotations", None) or {}
    return any(
        _is_true(annotations.get(key))
        for key in ("destructiveHint", "destructive", "requiresConfirmation")
    )


class ToolPolicy:
    """Decide whether a tool may be listed/called under a given profile."""

    def __init__(self, profile: str = "revit-read", allow_writes: bool = False):
        self.profile = profile
        self.allow_writes = bool(allow_writes)

    @property
    def allows_writes(self) -> bool:
        """Effective write permission (explicit flag or a write profile)."""

        return self.allow_writes or self.profile.casefold() in WRITE_PROFILES

    def check(self, spec, confirmed: bool = False) -> PolicyError | None:
        """Return a :class:`PolicyError` when ``spec`` must not be called."""

        if is_write_tool(spec) and not self.allows_writes:
            return PolicyError(
                code="write_blocked",
                message=(
                    f"Tool '{spec.name}' writes documents; profile "
                    f"'{self.profile}' is read-only."
                ),
                hint="Use profile='revit-write' or ToolPolicy(allow_writes=True).",
            )
        if is_destructive_tool(spec) and not confirmed:
            return PolicyError(
                code="confirmation_required",
                message=(
                    f"Tool '{spec.name}' is destructive or requires "
                    "explicit confirmation."
                ),
                hint="Repeat the call with confirmed=True after user approval.",
            )
        return None


__all__ = ["ToolPolicy", "PolicyError", "is_write_tool", "is_destructive_tool"]
