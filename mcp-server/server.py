# -*- coding: utf-8 -*-
"""Revit MCP server (stdio) for OpenCode.

Bridges OpenCode -> localhost HTTP bridge hosted inside Revit by
RevitBridge.Revit (E:\\ПлагиныРевит\\\\_bim\\RevitBridge\\RevitBridge.Revit).

Stdio JSON-RPC (MCP 2024-11-05), pure stdlib, no external deps
(same transport as DSH opencode-runner, proven with OpenCode).

Tools:
  revit_ping            - check Revit + addin are alive
  revit_open_document   - open an .rvt (optionally launch Revit if down)
  revit_close_document  - close active document
  revit_doc_info        - title/path/counts of active document
  revit_list_rooms      - rooms + MEP spaces (id/number/name/level/area/volume)
  revit_list_levels     - levels (name/elevation)
  revit_commands        - list HeatLoss plugin command keys
  revit_run_heatloss    - run HeatLoss plugin command (calculations/modeling/...)

Usage: python -X utf8 server.py
Env: REVIT_MCP_URL (default http://127.0.0.1:17871), REVIT_EXE (override),
     REVIT_DEFAULT_MODEL (override default .rvt path)
"""
import json
import os
import re
import subprocess
import sys
import time
import urllib.parse
import urllib.request

PROTOCOL_VERSION = "2024-11-05"
SERVER_NAME = "revit-bridge"
SERVER_VERSION = "0.1.0"

BASE_URL = os.environ.get("REVIT_MCP_URL", "http://127.0.0.1:17871").rstrip("/")

REVIT_CANDIDATES = [
    r"C:\Program Files\Autodesk\Revit 2024\Revit.exe",
    r"C:\Program Files\Autodesk\Revit 2025\Revit.exe",
]
if os.environ.get("REVIT_EXE"):
    REVIT_CANDIDATES.insert(0, os.environ["REVIT_EXE"])

DEFAULT_MODEL = os.environ.get(
    "REVIT_DEFAULT_MODEL",
    r"E:\ПлагиныРевит\Данные теплотехнический расчет\newBuilding\HvackFinal.rvt",
)

BRIDGE_URL = os.environ.get("REVIT_BRIDGE_URL", "ws://127.0.0.1:17871/bridge")
BRIDGE_TOKEN = os.environ.get("REVIT_BRIDGE_TOKEN", "")


class RevitDown(RuntimeError):
    pass


def _api(method, path, body=None, timeout=180):
    url = BASE_URL + path
    data = None
    headers = {"Accept": "application/json"}
    if body is not None:
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        headers["Content-Type"] = "application/json; charset=utf-8"
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            payload = json.loads(resp.read().decode("utf-8"))
    except OSError as ex:
        raise RevitDown(
            "Revit bridge unreachable at %s (%s). Start Revit 2024 with the "
            "RevitBridge addin, or call revit_open_document with "
            "launch_if_needed=true." % (BASE_URL, ex)
        )
    if not payload.get("ok"):
        raise RuntimeError("Revit bridge error: %s" % payload.get("error"))
    return payload.get("data")


def _ping_ok():
    try:
        _api("GET", "/ping", timeout=10)
        return True
    except Exception:
        return False


def _launch_revit(path):
    exe = next((c for c in REVIT_CANDIDATES if os.path.isfile(c)), None)
    if exe is None:
        raise RuntimeError("Revit.exe not found (checked: %s)" % "; ".join(REVIT_CANDIDATES))
    if not os.path.isfile(path):
        raise RuntimeError("Model file not found: %s" % path)
    # Non-blocking: Revit is single-instance per version, file opens in it.
    subprocess.Popen([exe, path])


def _bridge_call(msg_type, payload=None, timeout=180):
    """One-shot WebSocket call: hello -> ready -> request -> response."""
    try:
        import websocket  # websocket-client
    except Exception as ex:
        raise RuntimeError("websocket-client is required for the bridge: %s" % ex)

    try:
        ws = websocket.create_connection(BRIDGE_URL, timeout=min(int(timeout), 10))
    except Exception as ex:
        raise RevitDown(
            "RevitBridge WebSocket unreachable at %s (%s). Start Revit 2024 "
            "with the RevitBridge addin installed." % (BRIDGE_URL, ex)
        )

    try:
        ws.settimeout(timeout)
        ws.send(json.dumps({"v": 1, "id": "hello", "type": "hello",
                            "payload": {"token": BRIDGE_TOKEN}}))
        ready = json.loads(ws.recv())
        if ready.get("type") != "ready":
            raise RuntimeError("bridge hello failed: %s" % ready)
        ws.send(json.dumps({"v": 1, "id": "1", "type": msg_type,
                            "payload": payload or {}}))
        response = json.loads(ws.recv())
    finally:
        try:
            ws.close()
        except Exception:
            pass

    if response.get("type") == "call.error":
        err = response.get("payload") or {}
        raise RuntimeError("bridge error [%s]: %s" % (err.get("code"), err.get("message")))
    return response.get("payload", response)


_BRIDGE_TOOLS = {}
_BRIDGE_TOOLS_AT = 0.0


def _tool_name(command):
    return "cmd_" + re.sub(r"[^A-Za-z0-9_]", "_", command)


def _refresh_bridge_tools(force=False):
    """Discover commands registered in RevitBridge and expose them as MCP tools."""
    global _BRIDGE_TOOLS, _BRIDGE_TOOLS_AT
    if not force and _BRIDGE_TOOLS and (time.time() - _BRIDGE_TOOLS_AT) < 10:
        return
    try:
        payload = _bridge_call("commands.list", {}, timeout=5)
        commands = payload if isinstance(payload, list) else (payload or {}).get("commands", [])
        mapping = {}
        for command in commands:
            name = command.get("name")
            if not name:
                continue
            mapping[_tool_name(name)] = command
        _BRIDGE_TOOLS = mapping
        _BRIDGE_TOOLS_AT = time.time()
    except Exception:
        # Revit may be closed; keep the static tools working.
        pass


def _dynamic_tools():
    _refresh_bridge_tools()
    result = []
    for tool_name, command in _BRIDGE_TOOLS.items():
        schema = command.get("argsSchema") or {"type": "object", "properties": {}}
        result.append({
            "name": tool_name,
            "description": "[%s] %s (bridge command: %s)" % (
                command.get("provider") or "plugin",
                command.get("description") or command.get("name"),
                command.get("name")),
            "inputSchema": schema,
            "annotations": _dynamic_annotations(command, tool_name),
        })
    return result


# ---------------------------------------------------------------- DwgParser proxy
# DwgParser has its own stdio MCP server (DWG/DXF/PDF/3D tools). It is proxied as
# a child process so the client sees one MCP, while DwgParser stays its own repo.

DWGPARSER_SERVER = os.environ.get(
    "DWGPARSER_MCP_SERVER",
    r"D:\Projects\01-bim\DwgParser\mcp_server.py",
)
DWGPARSER_ROOT = os.path.dirname(DWGPARSER_SERVER)
_CHILD = None
_CHILD_READY = False
_CHILD_TOOLS = {}
_CHILD_TOOL_MAP = {}
_CHILD_AT = 0.0
_CHILD_ID = 0


def _stop_child():
    global _CHILD, _CHILD_READY
    try:
        if _CHILD is not None and _CHILD.poll() is None:
            _CHILD.terminate()
            _CHILD.wait(timeout=3)
    except Exception:
        pass
    _CHILD = None
    _CHILD_READY = False


def _child_raw(req_id, method, params, timeout=30):
    global _CHILD_ID
    child = _CHILD
    if child is None or child.poll() is not None:
        return None
    child.stdin.write(json.dumps(
        {"jsonrpc": "2.0", "id": req_id, "method": method, "params": params},
        ensure_ascii=False) + "\n")
    child.stdin.flush()
    deadline = time.time() + timeout
    while time.time() < deadline:
        line = child.stdout.readline()
        if not line:
            return None
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except Exception:
            continue
        if msg.get("id") == req_id:
            return msg
    return None


def _ensure_child():
    global _CHILD, _CHILD_READY
    if _CHILD is not None and _CHILD.poll() is None and _CHILD_READY:
        return True
    if not os.path.isfile(DWGPARSER_SERVER):
        return False
    try:
        _CHILD = subprocess.Popen(
            [sys.executable, "-X", "utf8", DWGPARSER_SERVER],
            cwd=DWGPARSER_ROOT,
            stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True, encoding="utf-8", bufsize=1,
        )
        init = _child_raw(0, "initialize", {
            "protocolVersion": "2024-11-05",
            "capabilities": {},
            "clientInfo": {"name": "revit-bridge-proxy", "version": "1"},
        }, timeout=30)
        if not init:
            _stop_child()
            return False
        _CHILD.stdin.write(json.dumps(
            {"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}}) + "\n")
        _CHILD.stdin.flush()
        _CHILD_READY = True
        return True
    except Exception:
        _stop_child()
        return False


def _child_request(method, params, timeout=60):
    global _CHILD_ID
    if not _ensure_child():
        raise RuntimeError("DwgParser MCP is unavailable at " + DWGPARSER_SERVER)
    _CHILD_ID += 1
    response = _child_raw(_CHILD_ID, method, params, timeout)
    if not response:
        _stop_child()
        raise RuntimeError("DwgParser MCP did not respond to " + method)
    if "error" in response:
        raise RuntimeError("DwgParser MCP error: %s" % response["error"])
    return response.get("result")


def _refresh_child_tools(force=False):
    global _CHILD_TOOLS, _CHILD_TOOL_MAP, _CHILD_AT
    if not force and _CHILD_TOOLS and (time.time() - _CHILD_AT) < 30:
        return
    try:
        result = _child_request("tools/list", {}, timeout=60) or {}
        tools = result.get("tools", [])
        mapping = {}
        tool_map = {}
        for tool in tools:
            original = tool.get("name")
            if not original:
                continue
            name = "dwg_" + re.sub(r"[^A-Za-z0-9_]", "_", original)
            mapping[name] = {
                "name": name,
                "description": "[DwgParser] " + (tool.get("description") or original),
                "inputSchema": tool.get("inputSchema") or {"type": "object", "properties": {}},
                "annotations": _child_annotations(name),
            }
            tool_map[name] = original
        _CHILD_TOOLS = mapping
        _CHILD_TOOL_MAP = tool_map
        _CHILD_AT = time.time()
    except Exception:
        _CHILD_AT = time.time()


def _child_tools():
    _refresh_child_tools()
    return list(_CHILD_TOOLS.values())


import atexit
atexit.register(_stop_child)


# ---------------------------------------------------------------- tool policy (P1.3/P1.5/P1.6)
# C# now serves GET only: every HTTP POST is rejected with 403 legacy_write_disabled.
# Static tools must therefore never silently issue a POST. The six static tools that
# have a first-class WS command are migrated to WS; the remaining write tools are
# blocked at the MCP facade with a structured, actionable error.

_WRITE_TOKENS = (
    "create_", "set_", "delete_", "place_", "load_", "move_", "rotate_",
    "copy_", "mirror_", "cut_", "backup_", "undo_", "run_heatloss",
    "open_document", "close_document",
    # Residual static POST endpoints (no WS command yet).
    "duplicate_", "export_", "modify_", "refresh_", "connect_", "dry_run",
)
_READ_TOKENS = (
    "ping", "doc_info", "list_", "get_", "query_", "measure_",
    "get_bounding_box", "commands",
)
_DESTRUCTIVE_TOKENS = ("delete", "clear", "purge", "reset", "close")
_IDEMPOTENT_TOKENS = ("ping", "doc_info", "list_", "get_", "query_", "measure_")


def is_static_write(name):
    """Classify a static tool name as a write (mutation) or a read."""
    if any(token in name for token in _WRITE_TOKENS):
        return True
    if any(token in name for token in _READ_TOKENS):
        return False
    # Generic dispatchers (bridge_call / revit_call / bridge_status) and
    # unclassified helpers stay readable: blocking them would remove the
    # documented WS escape hatch. Residual POST-only outliers are reported.
    return False


def _is_destructive(name):
    return any(token in name for token in _DESTRUCTIVE_TOKENS)


def _is_idempotent(name):
    return any(token in name for token in _IDEMPOTENT_TOKENS)


def _annotations(read_only, destructive=False, idempotent=False, open_world=False):
    return {
        "readOnlyHint": bool(read_only),
        "destructiveHint": bool(destructive),
        "idempotentHint": bool(idempotent),
        "openWorldHint": bool(open_world),
    }


# Generic WS dispatchers can invoke any command, including writes. They must
# stay callable, but must not advertise themselves as read-only.
_GENERIC_DISPATCHERS = frozenset({"bridge_call", "revit_call"})


def _static_annotations(name):
    if name in _GENERIC_DISPATCHERS:
        return _annotations(read_only=False, destructive=True, idempotent=False)
    return _annotations(
        read_only=not is_static_write(name),
        destructive=_is_destructive(name),
        idempotent=_is_idempotent(name),
    )


def _dynamic_annotations(command, tool_name=None):
    # C# CommandDescriptor.safety is authoritative when present; otherwise fall
    # back to transaction + name heuristics.
    safety = command.get("safety")
    safety = safety if isinstance(safety, dict) else {}
    read = command.get("transaction") == "read"
    return _annotations(
        read_only=safety.get("readOnly", read),
        destructive=safety.get(
            "destructive", _is_destructive(tool_name or command.get("name") or "")
        ),
        idempotent=safety.get("idempotent", read),
        open_world=safety.get("openWorld", False),
    )


def _child_annotations(name):
    # Names are stored with the "dwg_" proxy prefix; classify the bare name.
    bare = name[len("dwg_"):] if name.startswith("dwg_") else name
    read = bare.startswith(("list_", "get_", "ping", "verify_", "find_"))
    return _annotations(read_only=read,
                        destructive=_is_destructive(bare),
                        idempotent=read)


def legacy_write_error(name):
    """Structured error returned instead of calling the HTTP POST endpoints."""
    return {
        "error": "legacy_write_disabled",
        "tool": name,
        "hint": "Use WS cmd_* / tools_call; legacy HTTP writes are closed.",
    }


def _map_no_args(args):
    return {}


def _map_open_document(args):
    # Static schema uses ``detach_from_central``; WS command expects ``detach``.
    return {
        "path": args.get("path") or DEFAULT_MODEL,
        "detach": bool(args.get("detach_from_central", args.get("detach", False))),
        "audit": bool(args.get("audit", False)),
    }


def _map_close_document(args):
    return {"save": bool(args.get("save", False))}


def _map_list_rooms(args):
    return {
        "limit": max(1, min(500, int(args.get("limit") or 50))),
        "offset": max(0, int(args.get("offset") or 0)),
    }


# Static name -> (WS command, argument mapper). These never touch HTTP POST.
WS_STATIC_MAP = {
    "revit_ping": ("revit.ping", _map_no_args),
    "revit_open_document": ("revit.open_document", _map_open_document),
    "revit_close_document": ("revit.close_document", _map_close_document),
    "revit_doc_info": ("revit.doc_info", _map_no_args),
    "revit_list_levels": ("revit.list_levels", _map_no_args),
    "revit_list_rooms": ("revit.list_rooms", _map_list_rooms),
}


TOOLS = [
    {"name": "bridge_ping",
     "description": "Ping the RevitBridge WebSocket (protocol v1): returns revitVersion and protocol version.",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "bridge_commands",
     "description": "List commands registered in RevitBridge (name, provider, transaction, argsSchema).",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "bridge_status",
     "description": "Readiness of the live/background session: bridge ping + active document info.",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "bridge_call",
     "description": "Generic bridge call: invoke any registered command by name with JSON args.",
     "inputSchema": {"type": "object", "properties": {
         "command": {"type": "string", "description": "Command name from bridge_commands"},
         "args": {"type": "object", "description": "Command arguments"}},
         "required": ["command"]}},
    {"name": "revit_call",
     "description": "Alias of bridge_call: invoke a RevitBridge command (revit.*, nb.*, dwg.*, heatloss.*).",
     "inputSchema": {"type": "object", "properties": {
         "command": {"type": "string", "description": "Command name from bridge_commands"},
         "args": {"type": "object", "description": "Command arguments"}},
         "required": ["command"]}},
    {"name": "revit_ping",
     "description": "Check that Revit 2024 + RevitBridge addin bridge is alive. Returns Revit version info.",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "revit_open_document",
     "description": ("Open an .rvt model in Revit. Launches Revit 2024 with the file if the bridge "
                     "is down and launch_if_needed=true (waits up to wait_sec). Default path is HvackFinal.rvt."),
     "inputSchema": {"type": "object", "properties": {
         "path": {"type": "string", "description": "Full path to .rvt file"},
         "detach_from_central": {"type": "boolean", "description": "Detach from central (default false)"},
         "audit": {"type": "boolean", "description": "Audit on open (default false)"},
         "launch_if_needed": {"type": "boolean", "description": "Launch Revit if bridge is down (default true)"},
         "wait_sec": {"type": "integer", "description": "Wait for bridge after launch, sec (default 300)"}},
         "required": []}},
    {"name": "revit_close_document",
     "description": "Close the active Revit document (save=false discards changes).",
     "inputSchema": {"type": "object", "properties": {
         "save": {"type": "boolean", "description": "Save before closing (default false)"}}}},
    {"name": "revit_doc_info",
     "description": "Active document info: title, path, isModified, room/space/level counts.",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "revit_list_rooms",
     "description": "List rooms + MEP spaces: id/number/name/level/area_ft2/volume_ft3 (paged).",
     "inputSchema": {"type": "object", "properties": {
         "limit": {"type": "integer", "description": "Page size 1..500 (default 50)"},
         "offset": {"type": "integer", "description": "Offset (default 0)"}}}},
    {"name": "revit_list_levels",
     "description": "List levels: name + elevation_ft.",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "revit_commands",
     "description": "List HeatLoss plugin command keys available to revit_run_heatloss.",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "revit_run_heatloss",
     "description": ("Run a HeatLoss plugin window inside Revit. Keys: settings, modeling, "
                     "calculations (default), main, reports, tools, about, snapshot. "
                     "Opens the plugin UI modelessly; returns Succeeded/Failed + message."),
     "inputSchema": {"type": "object", "properties": {
         "command": {"type": "string", "description": "HeatLoss command key (default calculations)"}}}},
    {"name": "revit_create_level", "description": "Create a level at elevation_mm.",
     "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "elevation_mm": {"type": "number"}}}},
    {"name": "revit_list_wall_types", "description": "List wall types: id/name/width_mm/kind.",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "revit_create_wall_type", "description": "Create a wall type by duplicating an existing one and setting compound structure layers.",
     "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "layers": {"type": "array"}}}},
    {"name": "revit_create_wall", "description": "Create a wall: type_id, base_level, top_level, start_mm [x,y], end_mm [x,y], flip.",
     "inputSchema": {"type": "object", "properties": {"type_id": {"type": "string"}, "base_level": {"type": "string"}, "top_level": {"type": "string"}, "start_mm": {"type": "array"}, "end_mm": {"type": "array"}, "flip": {"type": "boolean"}}, "required": ["type_id", "base_level", "start_mm", "end_mm"]}},
    {"name": "revit_cut_opening", "description": "Cut a rectangular opening in a wall.",
     "inputSchema": {"type": "object", "properties": {"host_wall_id": {"type": "integer"}, "width_mm": {"type": "number"}, "height_mm": {"type": "number"}, "sill_mm": {"type": "number"}, "position_mm": {"type": "number"}}, "required": ["host_wall_id", "width_mm", "height_mm"]}},
    {"name": "revit_place_family", "description": "Place a family instance (window/door/furniture).",
     "inputSchema": {"type": "object", "properties": {"family": {"type": "string"}, "type": {"type": "string"}, "location_mm": {"type": "array"}, "host_wall_id": {"type": "integer"}, "rotation_deg": {"type": "number"}, "parameters": {"type": "object"}}}},
    {"name": "revit_load_family", "description": "Load a family from .rfa path.",
     "inputSchema": {"type": "object", "properties": {"rfa_path": {"type": "string"}}, "required": ["rfa_path"]}},
    {"name": "revit_get_element", "description": "Get element by id.",
     "inputSchema": {"type": "object", "properties": {"id": {"type": "integer"}}, "required": ["id"]}},
    {"name": "revit_query_elements", "description": "Query elements by category/level/limit.",
     "inputSchema": {"type": "object", "properties": {"category": {"type": "string"}, "level": {"type": "string"}, "limit": {"type": "integer"}}}},
    {"name": "revit_delete_elements", "description": "Delete elements by ids.",
     "inputSchema": {"type": "object", "properties": {"ids": {"type": "array"}}}},
    {"name": "revit_create_room", "description": "Create a room on a level.",
     "inputSchema": {"type": "object", "properties": {"level_id": {"type": "integer"}, "location_mm": {"type": "array"}, "number": {"type": "string"}, "name": {"type": "string"}}, "required": ["level_id", "location_mm"]}},
    {"name": "revit_list_schedules", "description": "List schedules in document.",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "revit_get_schedule", "description": "Get schedule by id (fields + rows).",
     "inputSchema": {"type": "object", "properties": {"id": {"type": "integer"}}, "required": ["id"]}},
    {"name": "revit_duplicate_schedule", "description": "Duplicate a schedule with new name.",
     "inputSchema": {"type": "object", "properties": {"source_id": {"type": "integer"}, "new_name": {"type": "string"}}, "required": ["source_id"]}},
    {"name": "revit_export_schedule", "description": "Export schedule to CSV.",
     "inputSchema": {"type": "object", "properties": {"schedule_id": {"type": "integer"}, "path": {"type": "string"}}, "required": ["schedule_id"]}},
    {"name": "revit_create_grid", "description": "Create a grid line.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}, "start_mm": {"type": "array"}, "end_mm": {"type": "array"}}, "required": ["start_mm", "end_mm"]}},
    {"name": "revit_list_grids", "description": "List grids.", "inputSchema": {"type": "object", "properties": {}}},
    {"name": "revit_create_floor", "description": "Create a floor.", "inputSchema": {"type": "object", "properties": {"level_id": {"type": "integer"}, "boundary_mm": {"type": "array"}, "floor_type": {"type": "string"}}, "required": ["level_id", "boundary_mm"]}},
    {"name": "revit_list_floors", "description": "List floors.", "inputSchema": {"type": "object", "properties": {}}},
    {"name": "revit_create_ceiling", "description": "Create a ceiling.", "inputSchema": {"type": "object", "properties": {"level_id": {"type": "integer"}, "boundary_mm": {"type": "array"}, "ceiling_type": {"type": "string"}}, "required": ["level_id", "boundary_mm"]}},
    {"name": "revit_list_ceilings", "description": "List ceilings.", "inputSchema": {"type": "object", "properties": {}}},
    {"name": "revit_create_roof", "description": "Create a footprint roof.", "inputSchema": {"type": "object", "properties": {"level_id": {"type": "integer"}, "boundary_mm": {"type": "array"}, "roof_type": {"type": "string"}}, "required": ["level_id", "boundary_mm"]}},
    {"name": "revit_list_roofs", "description": "List roofs.", "inputSchema": {"type": "object", "properties": {}}},
    {"name": "revit_move_element", "description": "Move element by delta_mm.", "inputSchema": {"type": "object", "properties": {"element_id": {"type": "integer"}, "delta_mm": {"type": "array"}}, "required": ["element_id", "delta_mm"]}},
    {"name": "revit_rotate_element", "description": "Rotate element.", "inputSchema": {"type": "object", "properties": {"element_id": {"type": "integer"}, "center_mm": {"type": "array"}, "angle_deg": {"type": "number"}}, "required": ["element_id", "center_mm", "angle_deg"]}},
    {"name": "revit_copy_element", "description": "Copy element.", "inputSchema": {"type": "object", "properties": {"element_id": {"type": "integer"}, "delta_mm": {"type": "array"}}, "required": ["element_id", "delta_mm"]}},
    {"name": "revit_mirror_element", "description": "Mirror element across a vertical plane.", "inputSchema": {"type": "object", "properties": {"element_id": {"type": "integer"}, "axis_mm": {"type": "array"}}, "required": ["element_id", "axis_mm"]}},
    {"name": "revit_measure_distance", "description": "Measure distance between two elements (mm).", "inputSchema": {"type": "object", "properties": {"id1": {"type": "integer"}, "id2": {"type": "integer"}}, "required": ["id1", "id2"]}},
    {"name": "revit_get_bounding_box", "description": "Get element bounding box (mm).", "inputSchema": {"type": "object", "properties": {"id": {"type": "integer"}}, "required": ["id"]}},
    {"name": "revit_list_categories", "description": "List document categories.", "inputSchema": {"type": "object", "properties": {}}},
    {"name": "revit_create_view_3d", "description": "Create a 3D isometric view.", "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}}}},
    {"name": "revit_set_active_view", "description": "Set active view by name or id.", "inputSchema": {"type": "object", "properties": {"view": {"type": "string"}}, "required": ["view"]}},
    {"name": "revit_list_families", "description": "List families (optional category filter).", "inputSchema": {"type": "object", "properties": {"category": {"type": "string"}}}},
    {"name": "revit_get_instance_params", "description": "Get instance parameters.", "inputSchema": {"type": "object", "properties": {"id": {"type": "integer"}}, "required": ["id"]}},
    {"name": "revit_set_instance_param", "description": "Set instance parameter value.", "inputSchema": {"type": "object", "properties": {"instance_id": {"type": "integer"}, "param_name": {"type": "string"}, "value": {}}, "required": ["instance_id", "param_name", "value"]}},
    {"name": "revit_create_schedule", "description": "Create a schedule: category_id, name, fields[].", "inputSchema": {"type": "object", "properties": {"category_id": {"type": "integer"}, "name": {"type": "string"}, "fields": {"type": "array"}}, "required": ["category_id"]}},
    {"name": "revit_modify_schedule", "description": "Modify schedule fields.", "inputSchema": {"type": "object", "properties": {"schedule_id": {"type": "integer"}, "fields": {"type": "array"}}, "required": ["schedule_id"]}},
    {"name": "revit_refresh_schedule", "description": "Refresh schedule(s).", "inputSchema": {"type": "object", "properties": {"schedule_id": {"type": "integer"}}}},
    {"name": "revit_backup_document", "description": "Save a backup copy of the document.", "inputSchema": {"type": "object", "properties": {"path": {"type": "string"}}}},
    {"name": "revit_dry_run", "description": "Plan an operation without executing.", "inputSchema": {"type": "object", "properties": {"operation": {"type": "string"}, "args": {"type": "object"}}, "required": ["operation"]}},
    {"name": "revit_undo_last", "description": "Undo last (best-effort note).", "inputSchema": {"type": "object", "properties": {}}},
    {"name": "revit_list_pipe_types", "description": "List pipe types.", "inputSchema": {"type": "object", "properties": {}}},
    {"name": "revit_list_piping_systems", "description": "List piping systems.", "inputSchema": {"type": "object", "properties": {}}},
    {"name": "revit_create_pipe", "description": "Create a pipe.", "inputSchema": {"type": "object", "properties": {"system_type": {"type": "string"}, "pipe_type": {"type": "string"}, "level_id": {"type": "integer"}, "start_mm": {"type": "array"}, "end_mm": {"type": "array"}, "diameter_mm": {"type": "number"}}, "required": ["level_id", "start_mm", "end_mm"]}},
    {"name": "revit_connect_pipes", "description": "Connect two pipes.", "inputSchema": {"type": "object", "properties": {"id1": {"type": "integer"}, "id2": {"type": "integer"}}, "required": ["id1", "id2"]}},
    {"name": "revit_list_duct_types", "description": "List duct types.", "inputSchema": {"type": "object", "properties": {}}},
    {"name": "revit_list_mechanical_systems", "description": "List mechanical systems.", "inputSchema": {"type": "object", "properties": {}}},
    {"name": "revit_create_duct", "description": "Create a duct.", "inputSchema": {"type": "object", "properties": {"system_type": {"type": "string"}, "duct_type": {"type": "string"}, "level_id": {"type": "integer"}, "start_mm": {"type": "array"}, "end_mm": {"type": "array"}, "width_mm": {"type": "number"}, "height_mm": {"type": "number"}}, "required": ["level_id", "start_mm", "end_mm"]}},
]

# Every static tool carries MCP annotations derived from its name/policy.
_STATIC_TOOL_NAMES = frozenset(tool["name"] for tool in TOOLS)
for _tool in TOOLS:
    _tool["annotations"] = _static_annotations(_tool["name"])


def tool_call(name, args):
    args = args or {}
    # Static tool policy: migrated tools go to WS, the rest of the writes are
    # rejected before any dispatch (no _api / HTTP call is attempted).
    if name in WS_STATIC_MAP:
        command, mapper = WS_STATIC_MAP[name]
        return _bridge_call("call", {"command": command, "args": mapper(args)}, timeout=180)
    if name in _STATIC_TOOL_NAMES and is_static_write(name):
        return legacy_write_error(name)
    if name.startswith("cmd_"):
        _refresh_bridge_tools()
    if name == "bridge_ping":
        return _bridge_call("ping", {}, timeout=15)
    if name == "bridge_commands":
        return _bridge_call("commands.list", {}, timeout=15)
    if name == "bridge_status":
        ping = _bridge_call("ping", {}, timeout=10)
        try:
            document = _api("GET", "/doc", timeout=30)
        except Exception as ex:
            document = {"error": str(ex)}
        return {"bridge": ping, "document": document}
    if name == "bridge_call" or name == "revit_call":
        return _bridge_call("call", {"command": args.get("command"),
                                     "args": args.get("args") or {}}, timeout=180)
    if name in _BRIDGE_TOOLS:
        command = _BRIDGE_TOOLS[name]["name"]
        return _bridge_call("call", {"command": command, "args": args or {}}, timeout=180)
    if name.startswith("dwg_") and name not in _CHILD_TOOL_MAP:
        _refresh_child_tools(force=True)
    if name in _CHILD_TOOL_MAP:
        return _child_request("tools/call", {"name": _CHILD_TOOL_MAP[name], "arguments": args}, timeout=300)
    # NOTE: revit_ping / revit_open_document / revit_close_document /
    # revit_doc_info / revit_list_levels / revit_list_rooms are handled by
    # WS_STATIC_MAP above (WS commands only; no HTTP GET/POST at all).
    if name == "revit_commands":
        return _api("GET", "/commands", timeout=30)
    if name == "revit_run_heatloss":
        return _api("POST", "/run", {"command": args.get("command") or "calculations"}, timeout=300)
    if name == "revit_create_level":
        return _api("POST", "/create_level", {"name": args.get("name"), "elevation_mm": args.get("elevation_mm", 0)}, timeout=120)
    if name == "revit_list_wall_types":
        return _api("GET", "/wall_types", timeout=60)
    if name == "revit_create_wall_type":
        return _api("POST", "/create_wall_type", {"name": args.get("name"), "layers": args.get("layers")}, timeout=120)
    if name == "revit_create_wall":
        return _api("POST", "/create_wall", {"type_id": args.get("type_id"), "base_level": args.get("base_level"), "top_level": args.get("top_level"), "start_mm": args.get("start_mm"), "end_mm": args.get("end_mm"), "flip": bool(args.get("flip", False))}, timeout=120)
    if name == "revit_cut_opening":
        return _api("POST", "/cut_opening", {"host_wall_id": args.get("host_wall_id"), "width_mm": args.get("width_mm"), "height_mm": args.get("height_mm"), "sill_mm": args.get("sill_mm", 900), "position_mm": args.get("position_mm", 0)}, timeout=120)
    if name == "revit_place_family":
        return _api("POST", "/place_family", {"family": args.get("family"), "type": args.get("type"), "location_mm": args.get("location_mm"), "host_wall_id": args.get("host_wall_id"), "rotation_deg": args.get("rotation_deg", 0), "parameters": args.get("parameters")}, timeout=180)
    if name == "revit_load_family":
        return _api("POST", "/load_family", {"rfa_path": args.get("rfa_path")}, timeout=300)
    if name == "revit_get_element":
        q = urllib.parse.urlencode({"id": args.get("id")})
        return _api("GET", "/element?%s" % q, timeout=60)
    if name == "revit_query_elements":
        q = urllib.parse.urlencode({"category": args.get("category", "OST_Walls"), "level": args.get("level", ""), "limit": args.get("limit", 500)})
        return _api("GET", "/query?%s" % q, timeout=60)
    if name == "revit_delete_elements":
        return _api("POST", "/delete", {"ids": args.get("ids", [])}, timeout=120)
    if name == "revit_create_room":
        return _api("POST", "/create_room", {"level_id": args.get("level_id"), "location_mm": args.get("location_mm"), "number": args.get("number", ""), "name": args.get("name", "")}, timeout=120)
    if name == "revit_list_schedules":
        return _api("GET", "/schedules", timeout=60)
    if name == "revit_get_schedule":
        q = urllib.parse.urlencode({"id": args.get("id")})
        return _api("GET", "/schedule?%s" % q, timeout=60)
    if name == "revit_duplicate_schedule":
        return _api("POST", "/duplicate_schedule", {"source_id": args.get("source_id"), "new_name": args.get("new_name")}, timeout=120)
    if name == "revit_export_schedule":
        return _api("POST", "/export_schedule", {"schedule_id": args.get("schedule_id"), "path": args.get("path")}, timeout=120)
    if name == "revit_create_grid":
        return _api("POST", "/create_grid", {"name": args.get("name"), "start_mm": args.get("start_mm"), "end_mm": args.get("end_mm")}, timeout=120)
    if name == "revit_list_grids":
        return _api("GET", "/grids", timeout=60)
    if name == "revit_create_floor":
        return _api("POST", "/create_floor", {"level_id": args.get("level_id"), "boundary_mm": args.get("boundary_mm"), "floor_type": args.get("floor_type")}, timeout=180)
    if name == "revit_list_floors":
        return _api("GET", "/floors", timeout=60)
    if name == "revit_create_ceiling":
        return _api("POST", "/create_ceiling", {"level_id": args.get("level_id"), "boundary_mm": args.get("boundary_mm"), "ceiling_type": args.get("ceiling_type")}, timeout=180)
    if name == "revit_list_ceilings":
        return _api("GET", "/ceilings", timeout=60)
    if name == "revit_create_roof":
        return _api("POST", "/create_roof", {"level_id": args.get("level_id"), "boundary_mm": args.get("boundary_mm"), "roof_type": args.get("roof_type")}, timeout=180)
    if name == "revit_list_roofs":
        return _api("GET", "/roofs", timeout=60)
    if name == "revit_move_element":
        return _api("POST", "/move_element", {"element_id": args.get("element_id"), "delta_mm": args.get("delta_mm")}, timeout=120)
    if name == "revit_rotate_element":
        return _api("POST", "/rotate_element", {"element_id": args.get("element_id"), "center_mm": args.get("center_mm"), "angle_deg": args.get("angle_deg")}, timeout=120)
    if name == "revit_copy_element":
        return _api("POST", "/copy_element", {"element_id": args.get("element_id"), "delta_mm": args.get("delta_mm")}, timeout=120)
    if name == "revit_mirror_element":
        return _api("POST", "/mirror_element", {"element_id": args.get("element_id"), "axis_mm": args.get("axis_mm")}, timeout=120)
    if name == "revit_measure_distance":
        q = urllib.parse.urlencode({"id1": args.get("id1"), "id2": args.get("id2")})
        return _api("GET", "/measure_distance?%s" % q, timeout=60)
    if name == "revit_get_bounding_box":
        q = urllib.parse.urlencode({"id": args.get("id")})
        return _api("GET", "/bounding_box?%s" % q, timeout=60)
    if name == "revit_list_categories":
        return _api("GET", "/categories", timeout=60)
    if name == "revit_create_view_3d":
        return _api("POST", "/create_view_3d", {"name": args.get("name")}, timeout=120)
    if name == "revit_set_active_view":
        return _api("POST", "/set_active_view", {"view": args.get("view")}, timeout=60)
    if name == "revit_list_families":
        q = urllib.parse.urlencode({"category": args.get("category", "")})
        return _api("GET", "/families?%s" % q, timeout=60)
    if name == "revit_get_instance_params":
        q = urllib.parse.urlencode({"id": args.get("id")})
        return _api("GET", "/instance_params?%s" % q, timeout=60)
    if name == "revit_set_instance_param":
        return _api("POST", "/set_instance_param", {"instance_id": args.get("instance_id"), "param_name": args.get("param_name"), "value": args.get("value")}, timeout=120)
    if name == "revit_create_schedule":
        return _api("POST", "/create_schedule", {"category_id": args.get("category_id"), "name": args.get("name"), "fields": args.get("fields")}, timeout=120)
    if name == "revit_modify_schedule":
        return _api("POST", "/modify_schedule", {"schedule_id": args.get("schedule_id"), "fields": args.get("fields")}, timeout=120)
    if name == "revit_refresh_schedule":
        return _api("POST", "/refresh_schedule", {"schedule_id": args.get("schedule_id")}, timeout=120)
    if name == "revit_backup_document":
        return _api("POST", "/backup_document", {"path": args.get("path")}, timeout=300)
    if name == "revit_dry_run":
        return _api("POST", "/dry_run", {"operation": args.get("operation"), "args": args.get("args")}, timeout=60)
    if name == "revit_undo_last":
        return _api("POST", "/undo_last", {}, timeout=60)
    if name == "revit_list_pipe_types":
        return _api("GET", "/pipe_types", timeout=60)
    if name == "revit_list_piping_systems":
        return _api("GET", "/piping_systems", timeout=60)
    if name == "revit_create_pipe":
        return _api("POST", "/create_pipe", {"system_type": args.get("system_type"), "pipe_type": args.get("pipe_type"), "level_id": args.get("level_id"), "start_mm": args.get("start_mm"), "end_mm": args.get("end_mm"), "diameter_mm": args.get("diameter_mm", 0)}, timeout=120)
    if name == "revit_connect_pipes":
        return _api("POST", "/connect_pipes", {"id1": args.get("id1"), "id2": args.get("id2")}, timeout=120)
    if name == "revit_list_duct_types":
        return _api("GET", "/duct_types", timeout=60)
    if name == "revit_list_mechanical_systems":
        return _api("GET", "/mechanical_systems", timeout=60)
    if name == "revit_create_duct":
        return _api("POST", "/create_duct", {"system_type": args.get("system_type"), "duct_type": args.get("duct_type"), "level_id": args.get("level_id"), "start_mm": args.get("start_mm"), "end_mm": args.get("end_mm"), "width_mm": args.get("width_mm", 0), "height_mm": args.get("height_mm", 0)}, timeout=120)
    raise ValueError("unknown tool: %s" % name)


def handle(msg):
    req_id = msg.get("id")
    method = msg.get("method")
    params = msg.get("params") or {}
    if method == "initialize":
        return {"protocolVersion": PROTOCOL_VERSION, "capabilities": {"tools": {}},
                "serverInfo": {"name": SERVER_NAME, "version": SERVER_VERSION}}
    if method == "notifications/initialized":
        return None
    if method == "ping":
        return {}
    if method == "tools/list":
        return {"tools": TOOLS + _dynamic_tools() + _child_tools()}
    if method == "tools/call":
        try:
            result = tool_call(params.get("name"), params.get("arguments"))
            if isinstance(result, dict) and "content" in result and "isError" in result:
                return result
            if isinstance(result, dict) and result.get("error") == "legacy_write_disabled":
                return {"content": [{"type": "text", "text": json.dumps(result, ensure_ascii=False)}],
                        "isError": True}
            return {"content": [{"type": "text", "text": json.dumps(result, ensure_ascii=False)}],
                    "isError": False}
        except Exception as e:
            return {"content": [{"type": "text",
                                 "text": json.dumps({"error": str(e)}, ensure_ascii=False)}],
                    "isError": True}
    raise ValueError("unsupported method: %s" % method)


def main():
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except json.JSONDecodeError:
            continue
        try:
            resp = handle(msg)
        except Exception as e:
            resp = {"error": {"code": -32603, "message": str(e)}}
        if resp is not None:
            if "id" in msg:
                # JSON-RPC 2.0: payload MUST be nested under "result" (or "error").
                # Without this the official MCP SDK rejects the frame
                # (invalid_union / expected object at path ["result"]) and
                # initialize times out, so no tools ever register.
                if isinstance(resp, dict) and set(resp.keys()) == {"error"}:
                    out = {"jsonrpc": "2.0", "id": msg["id"], "error": resp["error"]}
                else:
                    out = {"jsonrpc": "2.0", "id": msg["id"], "result": resp}
                sys.stdout.write(json.dumps(out, ensure_ascii=False) + "\n")
                sys.stdout.flush()


if __name__ == "__main__":
    main()
