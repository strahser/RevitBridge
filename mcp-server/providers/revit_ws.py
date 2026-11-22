# -*- coding: utf-8 -*-
"""Provider adapter for the live RevitBridge WebSocket command registry.

Pure stdlib.  The two callables injected by the caller keep this module
independent of ``server.py``, Revit and any WebSocket client, so the adapter can
be exercised with fakes in unit tests.

C# ``CommandDescriptor`` is authoritative for tool safety: ``safety`` overrides
the older ``transaction`` field and name heuristics.  The resulting annotations
carry both MCP-style hints and the bridge-style keys consumed by the gateway
policy (``writesDocument``, ``requiresConfirmation``).
"""

import re

try:  # package import, e.g. mcp_server.providers.revit_ws
    from ..models import ProviderHealth, ToolSpec
except ImportError:  # flat import: <mcp-server> directory is on sys.path
    from models import ProviderHealth, ToolSpec  # type: ignore[no-redef]


_DESTRUCTIVE_TOKENS = ("delete", "clear", "purge", "reset", "close")


def command_annotations(command):
    """Translate a C# ``CommandDescriptor`` into gateway annotations.

    Mirrors ``server._dynamic_annotations`` so the legacy dynamic tool surface
    and the new catalog agree on safety, while allowing ``safety`` to win over
    ``transaction`` and the tool name when C# supplies it.
    """

    raw_safety = command.get("safety")
    safety = raw_safety if isinstance(raw_safety, dict) else {}
    name = command.get("name") or ""
    read = command.get("transaction") == "read"
    destructive_default = any(
        token in name.casefold() for token in _DESTRUCTIVE_TOKENS
    )

    annotations = {
        "readOnlyHint": bool(safety.get("readOnly", read)),
        "destructiveHint": bool(safety.get("destructive", destructive_default)),
        "idempotentHint": bool(safety.get("idempotent", read)),
        "openWorldHint": bool(safety.get("openWorld", False)),
    }
    if isinstance(raw_safety, dict):
        # Extra keys are ignored by MCP clients but drive ToolPolicy.
        annotations["writesDocument"] = bool(
            safety.get("writesDocument", not bool(safety.get("readOnly", False)))
        )
        annotations["requiresConfirmation"] = bool(
            safety.get("requiresConfirmation", bool(safety.get("destructive", False)))
        )
        annotations["backupRequired"] = bool(safety.get("backupRequired", False))
    return annotations


def _as_commands(payload):
    """Accept both a bare list and ``{\"commands\": [...]}`` from commands.list."""

    if isinstance(payload, list):
        return [item for item in payload if isinstance(item, dict)]
    if isinstance(payload, dict):
        return [item for item in (payload.get("commands") or [])
                if isinstance(item, dict)]
    return []


class RevitWsProvider:
    """Expose each registered bridge command as one catalog tool."""

    name = "revit-ws"

    def __init__(self, list_commands, call_command):
        self._list_commands = list_commands
        self._call_command = call_command
        self.last_error = ""

    def list_tools(self):
        # Discovery must not break the whole gateway when Revit is closed:
        # an unreachable bridge simply contributes no tools this round.
        try:
            commands = _as_commands(self._list_commands())
        except Exception as exc:
            self.last_error = str(exc)
            return []
        self.last_error = ""
        specs = []
        for command in commands:
            name = command.get("name")
            if not name:
                continue
            specs.append(ToolSpec(
                name=name,
                canonical=name,
                description=command.get("description") or "",
                input_schema=command.get("argsSchema") or {},
                annotations=command_annotations(command),
                provider=self.name,
                domain=name.split(".")[0],
                aliases=("cmd_" + re.sub(r"[^A-Za-z0-9_]", "_", name),),
            ))
        return specs

    def call(self, name, args):
        return self._call_command(name, args)

    def health(self):
        # Probe the callables directly: list_tools() swallows discovery errors
        # on purpose, so health() must not rely on it to report bridge down.
        try:
            commands = _as_commands(self._list_commands())
            return ProviderHealth(self.name, True, "ok", len(commands))
        except Exception as exc:
            return ProviderHealth(self.name, False, str(exc), 0)


__all__ = ["RevitWsProvider", "command_annotations"]
