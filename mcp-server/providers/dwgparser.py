# -*- coding: utf-8 -*-
"""Provider adapter for the proxied DwgParser child MCP server.

DwgParser tools arrive already prefixed (``dwg_list_layers``) from the proxy in
``server.py``.  The catalog uses a canonical dotted name (``dwg.list_layers``)
and keeps the proxy name as the provider-local ``name``, so dispatch can strip
the prefix and hand the bare child tool name back to the proxy.
"""

from collections.abc import Mapping

try:  # package import, e.g. mcp_server.providers.dwgparser
    from ..models import ProviderHealth, ToolSpec
except ImportError:  # flat import: <mcp-server> directory is on sys.path
    from models import ProviderHealth, ToolSpec  # type: ignore[no-redef]


def bare_name(name):
    """Remove the proxy prefix from a DwgParser tool name."""

    if name.startswith(("dwg_", "dwg.")):
        return name[4:]
    return name


def _field(item, key, default=None):
    if isinstance(item, Mapping):
        return item.get(key, default)
    return getattr(item, key, default)


class DwgParserProvider:
    """Wrap the child ``tools/list`` / ``tools/call`` proxy functions."""

    name = "dwgparser"

    def __init__(self, list_tools, call_tool):
        self._list_tools = list_tools
        self._call_tool = call_tool
        self.last_error = ""

    def list_tools(self):
        # A closed/missing child must not prevent other providers from loading.
        try:
            tools = self._list_tools() or []
        except Exception as exc:
            self.last_error = str(exc)
            return []
        self.last_error = ""
        specs = []
        for tool in tools:
            name = _field(tool, "name")
            if not name:
                continue
            specs.append(ToolSpec(
                name=name,
                canonical="dwg." + bare_name(name),
                description=_field(tool, "description") or "",
                input_schema=_field(tool, "inputSchema") or {},
                annotations=dict(_field(tool, "annotations") or {}),
                provider=self.name,
                domain="dwg",
                aliases=(),
            ))
        return specs

    def call(self, name, args):
        return self._call_tool(bare_name(name), args)

    def health(self):
        try:
            tools = self._list_tools() or []
            return ProviderHealth(self.name, True, "ok", len(tools))
        except Exception as exc:
            return ProviderHealth(self.name, False, str(exc), 0)


__all__ = ["DwgParserProvider", "bare_name"]
