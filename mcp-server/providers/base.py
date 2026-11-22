# -*- coding: utf-8 -*-
"""Provider contract for the MCP gateway.

A provider is any object exposing a ``name`` plus ``list_tools``, ``call`` and
``health``.  ``ToolProvider`` is a structural (duck) type: adapters do not have
to subclass it, the catalog only relies on those four members.
"""

from typing import Protocol, runtime_checkable

try:  # package import, e.g. ``mcp_server.providers.base``
    from ..models import ProviderHealth, ToolSpec
except ImportError:  # flat import: <mcp-server> directory is on sys.path
    from models import ProviderHealth, ToolSpec  # type: ignore[no-redef]


@runtime_checkable
class ToolProvider(Protocol):
    """Minimal interface consumed by :class:`catalog.ToolCatalog`."""

    name: str

    def list_tools(self) -> list[ToolSpec]:
        ...

    def call(self, name: str, args: dict) -> object:
        ...

    def health(self) -> ProviderHealth:
        ...


__all__ = ["ToolProvider", "ToolSpec", "ProviderHealth"]
