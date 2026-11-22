# Tool catalog (P5)

## Sources of truth

| Source | What it defines |
|---|---|
| `RevitBridge.Revit/Commands/*` + `BridgeCommandHost.cs` | built-in RevitBridge commands and their `CommandSafety` |
| `*.bridge.manifest.json` (repo plugin manifests) | provider commands (DwgParser, NetworkBuilder, HeatLoss) |
| `DwgParser/tools/kb_admin/db.sqlite3` → `tools_registry.json` | DwgParser donor catalog (439 tools) |
| `mcp-server/models.py` / providers | active gateway projection |

`tool_catalog.db` is a **cache and audit log**, never a manually maintained
source. Delete it and rebuild from the sources.

## Schema

`mcp-server/sql/catalog_schema.sql`:

- `tools` — one row per MCP-safe tool name, canonical bridge name, provider,
  domain, schema JSON, transaction/safety flags, source hash;
- `aliases` — legacy/static names → canonical;
- `tool_calls` — audit log (tool, session, started_at, duration, status, args hash);
- `catalog_meta` — build metadata (source hashes, timestamps);
- `tools_fts` — FTS5 index over name/canonical/description/domain.

## Build flow

```
manifests ─┐
donor DB ──┼─> catalog_build.py ─> tool_catalog.db ─> tools_search/describe/call
live WS ───┘                         (cache/audit)
```

1. Collect tool descriptors from providers.
2. Normalize canonical names and aliases.
3. Reject duplicate canonical/alias conflicts.
4. Replace `tools`/`aliases` and refresh FTS in one transaction.
5. Record source hashes/time in `catalog_meta`.
6. `tools_call` appends an audit row after policy/provider completion.

## Policies

- `MCP_PROFILE=revit-read` (default): only `writesDocument=false` tools.
- `MCP_PROFILE=revit-write` + `MCP_ALLOW_WRITES=1`: writes allowed; destructive
  calls require `confirmed=true`.
- Legacy HTTP is read-only (`GET` only).
- `MCP_TOOLS_MODE=compact` (default): `tools/list` = 7 direct tools; the rest is
  discoverable via `tools_search`.

## Consistency gate

CI must fail when:

- a source has a duplicate canonical name;
- an alias points to an unknown canonical;
- `tool_catalog.db` differs from a fresh build;
- a tool has no input schema (except no-arg commands);
- a write tool has no `writesDocument`/`readOnlyHint` annotation.
