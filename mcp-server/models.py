# -*- coding: utf-8 -*-
"""Shared value types for the MCP gateway catalog.

Pure stdlib dataclasses: no Revit, no WebSocket, no ``mcp`` dependency.
Used by both :mod:`policy` and :mod:`catalog` (and by provider adapters).
"""

from dataclasses import dataclass, field


@dataclass
class ToolSpec:
    """Description of one tool exposed by a provider.

    ``canonical`` is the globally unique identity inside a catalog; ``name``
    is the provider-local tool name (what ``provider.call`` understands).
    ``aliases`` are additional names accepted by ``ToolCatalog.describe/call``.
    """

    name: str
    canonical: str
    description: str = ""
    input_schema: dict = field(default_factory=dict)
    annotations: dict = field(default_factory=dict)
    provider: str = ""
    domain: str = ""
    deprecated: bool = False
    aliases: tuple = ()

    def __post_init__(self):
        if not isinstance(self.aliases, tuple):
            self.aliases = tuple(self.aliases or ())
        if self.input_schema is None:
            self.input_schema = {}
        if self.annotations is None:
            self.annotations = {}


class LegacyBackendUnavailable(Exception):
    """A tool's legacy backend is down (DwgParser Revit TCP 8098).

    Carries structured fields so the facade can return a stable MCP error code
    instead of a raw socket exception.  ``ToolCatalog.call`` re-raises it
    unchanged.
    """

    def __init__(self, tool, detail=""):
        self.tool = tool
        self.detail = detail
        super().__init__(detail)


@dataclass
class ProviderHealth:
    """Result of a provider health probe."""

    name: str
    ok: bool
    detail: str = ""
    tool_count: int = 0


@dataclass
class PolicyError(Exception):
    """A policy refusal, safe to serialise to an MCP error response.

    Subclasses ``Exception`` so ``ToolCatalog.call`` can raise it directly;
    ``as_dict`` still produces a plain, JSON-friendly payload.
    """

    code: str
    message: str
    hint: str = ""

    def __post_init__(self):
        super().__init__(self.message)

    def as_dict(self) -> dict:
        """Return the error as a JSON-friendly dict."""

        return {"code": self.code, "message": self.message, "hint": self.hint}
