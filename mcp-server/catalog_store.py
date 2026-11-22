# -*- coding: utf-8 -*-
"""SQLite cache/audit store for the unified tool catalog (P5.2).

The database is disposable: rebuild it from manifests/providers. It is never a
manually edited source of truth.
"""
import json
import os
import sqlite3
from datetime import datetime, timezone

try:
    from .models import ToolSpec
except ImportError:  # flat import
    from models import ToolSpec  # type: ignore[no-redef]

HERE = os.path.dirname(os.path.abspath(__file__))
SCHEMA = os.path.join(HERE, "sql", "catalog_schema.sql")


def _value(spec, key, default=None):
    if isinstance(spec, dict):
        return spec.get(key, default)
    return getattr(spec, key, default)


class CatalogStore:
    """Store tool descriptors, aliases and call audit rows in SQLite."""

    def __init__(self, path):
        self.path = os.path.abspath(path)
        parent = os.path.dirname(self.path)
        if parent:
            os.makedirs(parent, exist_ok=True)
        self.db = sqlite3.connect(self.path)
        self.db.row_factory = sqlite3.Row
        with open(SCHEMA, encoding="utf-8") as handle:
            self.db.executescript(handle.read())
        self.db.commit()

    def close(self):
        self.db.close()

    def replace_tools(self, specs, aliases=None):
        """Atomically replace tools/aliases and refresh the FTS index."""
        aliases = aliases or {}
        rows = []
        for raw in specs:
            spec = raw if isinstance(raw, ToolSpec) else ToolSpec(**raw)
            annotations = spec.annotations or {}
            rows.append((
                spec.name,
                spec.canonical or spec.name,
                spec.provider,
                spec.domain,
                spec.description,
                json.dumps(spec.input_schema or {}, ensure_ascii=False),
                "read" if annotations.get("readOnlyHint", True) else "write",
                1 if annotations.get("readOnlyHint", True) else 0,
                1 if annotations.get("destructiveHint", False) else 0,
                1 if annotations.get("requiresConfirmation", False) else 0,
                1 if annotations.get("backupRequired", False) else 0,
                "",
                "",
                0 if spec.deprecated else 1,
            ))
        with self.db:
            self.db.execute("delete from aliases")
            self.db.execute("delete from tools")
            self.db.executemany(
                "insert into tools(name, canonical, provider, domain, description, "
                "input_schema_json, tx, read_only, destructive, requires_confirmation, "
                "backup_required, version, source_hash, enabled) "
                "values (?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                rows,
            )
            for alias, canonical in aliases.items():
                self.db.execute(
                    "insert into aliases(alias, canonical) values (?,?)",
                    (alias, canonical),
                )
            self.db.execute("insert into tools_fts(tools_fts) values('rebuild')")
            self.db.execute(
                "insert or replace into catalog_meta(key, value) values (?,?)",
                ("built_at", datetime.now(timezone.utc).isoformat()),
            )
        return len(rows)

    def _attach_aliases(self, rows):
        """Expose aliases on result rows so the cache matches the live gateway."""
        if not rows:
            return rows
        canonicals = sorted({row["canonical"] for row in rows})
        placeholders = ",".join("?" for _ in canonicals)
        mapping = {}
        for alias, canonical in self.db.execute(
            "select alias, canonical from aliases where canonical in (%s)"
            % placeholders,
            canonicals,
        ):
            mapping.setdefault(canonical, []).append(alias)
        for row in rows:
            row["aliases"] = sorted(mapping.get(row["canonical"], []))
        return rows

    def search(self, query="", domain="", read_only=None, limit=50):
        clauses = ["enabled=1"]
        params = []
        if domain:
            clauses.append("domain = ?")
            params.append(domain)
        if read_only is not None:
            clauses.append("read_only = ?")
            params.append(1 if read_only else 0)
        if query:
            clauses.append(
                "(name like ? or canonical like ? or description like ? or domain like ?)"
            )
            needle = "%" + query + "%"
            params.extend([needle, needle, needle, needle])
        params.append(max(0, int(limit)))
        sql = ("select * from tools where " + " and ".join(clauses) +
               " order by canonical limit ?")
        rows = [dict(row) for row in self.db.execute(sql, params)]
        return self._attach_aliases(rows)

    def describe(self, name):
        row = self.db.execute(
            "select t.* from tools t left join aliases a on a.canonical=t.canonical "
            "where t.name=? or t.canonical=? or a.alias=? limit 1",
            (name, name, name),
        ).fetchone()
        if not row:
            return None
        rows = self._attach_aliases([dict(row)])
        return rows[0]

    def record_call(self, tool_name, status, duration_ms=0, session_id="",
                    args_hash="", error_code="", started_at=None):
        started_at = started_at or datetime.now(timezone.utc).isoformat()
        with self.db:
            self.db.execute(
                "insert into tool_calls(tool_name, session_id, started_at, "
                "duration_ms, status, args_hash, error_code) values (?,?,?,?,?,?,?)",
                (tool_name, session_id, started_at, int(duration_ms), status,
                 args_hash, error_code),
            )


__all__ = ["CatalogStore"]
