-- Tool catalog cache/audit schema (P5).
-- Source of truth: bridge manifests + DwgParser donor registry.
-- This database is disposable: delete it and rebuild from sources.

PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS tools (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL UNIQUE,          -- MCP-safe name (e.g. sheet_create)
    canonical TEXT NOT NULL,            -- bridge identity (e.g. sheet.create)
    provider TEXT NOT NULL,             -- revit-ws, dwgparser, static
    domain TEXT NOT NULL,               -- revit, sheet, spec, model, dwg, ...
    description TEXT NOT NULL DEFAULT '',
    input_schema_json TEXT NOT NULL DEFAULT '{}',
    tx TEXT NOT NULL DEFAULT 'read',
    read_only INTEGER NOT NULL DEFAULT 1,
    destructive INTEGER NOT NULL DEFAULT 0,
    requires_confirmation INTEGER NOT NULL DEFAULT 0,
    backup_required INTEGER NOT NULL DEFAULT 0,
    version TEXT NOT NULL DEFAULT '',
    source_hash TEXT NOT NULL DEFAULT '',
    enabled INTEGER NOT NULL DEFAULT 1
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_tools_canonical ON tools(canonical);
CREATE INDEX IF NOT EXISTS ix_tools_provider_domain ON tools(provider, domain);

CREATE TABLE IF NOT EXISTS aliases (
    alias TEXT PRIMARY KEY,
    canonical TEXT NOT NULL REFERENCES tools(canonical) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS tool_calls (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    tool_name TEXT NOT NULL,
    session_id TEXT NOT NULL DEFAULT '',
    started_at TEXT NOT NULL,
    duration_ms INTEGER NOT NULL DEFAULT 0,
    status TEXT NOT NULL,               -- ok | policy_error | provider_error
    args_hash TEXT NOT NULL DEFAULT '',
    error_code TEXT NOT NULL DEFAULT ''
);

CREATE INDEX IF NOT EXISTS ix_tool_calls_name_time ON tool_calls(tool_name, started_at);
CREATE INDEX IF NOT EXISTS ix_tool_calls_status ON tool_calls(status);

CREATE TABLE IF NOT EXISTS catalog_meta (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE VIRTUAL TABLE IF NOT EXISTS tools_fts USING fts5(
    name,
    canonical,
    description,
    domain,
    content='tools',
    content_rowid='id'
);
