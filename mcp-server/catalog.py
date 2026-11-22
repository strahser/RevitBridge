# -*- coding: utf-8 -*-
"""Registry and dispatcher for MCP tools exposed by pluggable providers.

No Revit / C# / ``mcp`` imports: a provider is any object with ``name``,
``list_tools()``, ``call(name, args)`` and ``health()`` (see
:class:`providers.base.ToolProvider`).  Registration is atomic: a duplicate
canonical or alias leaves the catalog unchanged.
"""

from collections.abc import Mapping

try:  # package import, e.g. ``mcp_server.catalog``
    from .models import PolicyError, ProviderHealth, ToolSpec
    from .policy import ToolPolicy, is_write_tool
except ImportError:  # flat import: <mcp-server> directory is on sys.path
    from models import PolicyError, ProviderHealth, ToolSpec  # type: ignore[no-redef]
    from policy import ToolPolicy, is_write_tool  # type: ignore[no-redef]


class ToolCatalog:
    """Merge provider tool surfaces, resolve names and enforce policy."""

    def __init__(self, policy=None, profile=None, allow_writes: bool = False):
        if policy is None:
            policy = ToolPolicy(
                profile=profile or "revit-read", allow_writes=allow_writes
            )
        self._policy = policy
        self._providers = {}       # provider name -> provider object
        self._specs = {}           # canonical -> ToolSpec
        self._owners = {}          # canonical -> provider object
        self._index = {}           # name/canonical/alias -> canonical

    # -- registration ----------------------------------------------------

    def register(self, provider) -> None:
        """Add a provider and all of its tools; reject identity conflicts."""

        name = getattr(provider, "name", None)
        if not isinstance(name, str) or not name:
            raise ValueError("provider.name must be a non-empty string")
        if name in self._providers:
            raise ValueError(f"Duplicate provider name: '{name}'")

        new_specs = {}
        new_index = {}
        for raw in provider.list_tools():
            spec = ToolSpec(**raw) if isinstance(raw, Mapping) else raw
            canonical = spec.canonical or spec.name
            if not canonical:
                raise ValueError(f"Provider '{name}' returned a tool without a name")
            if canonical in self._specs or canonical in new_specs:
                raise ValueError(f"Duplicate canonical tool name: '{canonical}'")

            for key in (spec.name, canonical, *spec.aliases):
                if not key:
                    continue
                owner = new_index.get(key, self._index.get(key))
                if owner is not None and owner != canonical:
                    raise ValueError(
                        f"Alias conflict: '{key}' already maps to '{owner}' "
                        f"(provider '{name}')"
                    )
                new_index[key] = canonical
            new_specs[canonical] = spec

        # Commit only after the whole provider validates.
        self._providers[name] = provider
        self._specs.update(new_specs)
        self._owners.update({canonical: provider for canonical in new_specs})
        self._index.update(new_index)

    # -- lookup ----------------------------------------------------------

    def _resolve(self, name: str) -> str:
        try:
            return self._index[name]
        except (KeyError, TypeError):
            raise KeyError(f"Unknown tool: {name!r}") from None

    def describe(self, name: str) -> ToolSpec:
        """Find a tool by its name, canonical name or alias."""

        return self._specs[self._resolve(name)]

    # -- discovery -------------------------------------------------------

    def _policy_for(self, profile=None) -> ToolPolicy:
        if profile is None or profile == self._policy.profile:
            return self._policy
        return ToolPolicy(profile=profile, allow_writes=self._policy.allow_writes)

    def list_tools(self, profile=None) -> list[ToolSpec]:
        """List tools allowed by ``profile`` (read-only by default).

        Confirmation-required tools are listed when writes are permitted:
        ``confirmed`` is a call-time decision, not a discovery-time one.
        """

        policy = self._policy_for(profile)
        return [
            spec for spec in self._specs.values()
            if policy.check(spec, confirmed=True) is None
        ]

    def search(
        self,
        query: str = "",
        domain: str = "",
        read_only: bool | None = None,
        limit: int = 50,
    ) -> list[ToolSpec]:
        """Case-insensitive substring search over name/canonical/description/domain.

        ``domain`` is an exact (case-insensitive) filter.  ``read_only=True``
        keeps non-writing tools, ``False`` keeps writing tools.  ``search``
        covers all registered tools; use :meth:`list_tools` for policy filtering.
        """

        needle = (query or "").casefold()
        wanted_domain = (domain or "").casefold()
        max_items = max(0, int(limit))
        if max_items == 0:
            return []
        found = []
        for spec in self._specs.values():
            if wanted_domain and (spec.domain or "").casefold() != wanted_domain:
                continue
            if needle and not any(
                needle in (value or "").casefold()
                for value in (spec.name, spec.canonical, spec.description, spec.domain)
            ):
                continue
            if read_only is not None:
                if is_write_tool(spec) == bool(read_only):
                    continue
            found.append(spec)
            if len(found) >= max_items:
                break
        return found

    # -- dispatch --------------------------------------------------------

    def call(self, name: str, args: dict, confirmed: bool = False) -> object:
        """Resolve ``name``, enforce policy, then delegate to its provider."""

        canonical = self._resolve(name)
        spec = self._specs[canonical]
        error = self._policy.check(spec, confirmed=confirmed)
        if error is not None:
            raise error

        provider = self._owners[canonical]
        try:
            # Providers know their own tool name, not a caller-supplied alias.
            return provider.call(spec.name, args)
        except PolicyError:
            raise
        except Exception as exc:
            raise RuntimeError(
                f"Provider '{provider.name}' failed on tool '{canonical}': {exc}"
            ) from exc

    # -- operations ------------------------------------------------------

    def health(self) -> list[ProviderHealth]:
        """Probe every provider; a failing probe becomes an unhealthy record."""

        result = []
        for provider in self._providers.values():
            try:
                record = provider.health()
            except Exception as exc:
                record = ProviderHealth(provider.name, False, str(exc), 0)
            if isinstance(record, Mapping):
                record = ProviderHealth(**record)
            result.append(record)
        return result


__all__ = ["ToolCatalog"]
