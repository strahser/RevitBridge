using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using RevitBridge.Contracts;
using RevitBridge.Core;

namespace RevitBridge;

/// <summary>
/// Minimal localhost HTTP API (HttpListener, 127.0.0.1 only, no auth — local dev bridge).
/// Read-only routes:
///   GET  /health          -> readiness probe (no WebSocket needed)
///   GET  /ping            -> Revit version info
///   GET  /doc             -> active document info + counts
///   GET  /rooms?limit=&amp;offset=
///   GET  /levels
///   GET  /wall_types
///   GET  /element?id=
///   GET  /query?category=&amp;level=&amp;limit=
///   GET  /schedules
///   GET  /schedule?id=
///   GET  /commands        -> list HeatLoss command keys
///   GET  /grids
///   GET  /floors
///   GET  /ceilings
///   GET  /roofs
///   GET  /measure_distance?id1=&amp;id2=
///   GET  /bounding_box?id=
///   GET  /categories
///   GET  /families?category=
///   GET  /instance_params?id=
///   GET  /pipe_types
///   GET  /piping_systems
///   GET  /duct_types
///   GET  /mechanical_systems
///   WebSocket /bridge     -> all mutations go through the BridgeRouter here.
/// POST/PUT/DELETE and any other non-GET request are rejected with
/// 403 legacy_write_disabled (LegacyHttpPolicy); writes use the WebSocket bridge.
/// </summary>
internal class McpHttpServer
{
    private readonly int _port;
    private readonly McpHandler _handler;
    private readonly BridgeRouter _router;
    private HttpListener _listener;
    private Thread _thread;
    private volatile bool _running;

    public McpHttpServer(int port, McpHandler handler, BridgeRouter router)
    {
        _port = port;
        _handler = handler;
        _router = router;
    }

    public void Start()
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add("http://127.0.0.1:" + _port + "/");
        _listener.Start();
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "RevitBridge-Http" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch (Exception) { }
        try { _listener?.Close(); } catch (Exception) { }
    }

    private void Loop()
    {
        while (_running)
        {
            HttpListenerContext ctx = null;
            try
            {
                ctx = _listener.GetContext();
            }
            catch (Exception)
            {
                return; // listener stopped
            }
            try
            {
                Handle(ctx);
            }
            catch (Exception ex)
            {
                McpLog.Write("Handle failed: " + ex.Message);
                try { WriteJson(ctx, 500, Ok(false, null, ex.Message)); } catch (Exception) { }
            }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var path = (req.Url.AbsolutePath ?? "/").TrimEnd('/');
        if (path.Length == 0) path = "/";
        McpLog.Write(req.HttpMethod + " " + path);

        if (req.IsWebSocketRequest && path == BridgeProtocol.WebSocketPath)
        {
            BridgeWebSocket.Handle(ctx, _router);
            return;
        }

        if (req.HttpMethod == "GET" && path == BridgeProtocol.HealthPath)
        {
            WriteJson(ctx, 200, new Dictionary<string, object>
            {
                { "ok", true },
                { "status", "ok" },
                { "service", "RevitBridge" },
                { "protocol", BridgeProtocol.Version },
                { "commands", _router.CommandCount },
            });
            return;
        }

        // The legacy HTTP surface has no token/write policy. Only GET is a
        // read; every mutation must go through the WebSocket BridgeRouter.
        if (!LegacyHttpPolicy.IsReadOnlyRequest(req.HttpMethod))
        {
            WriteJson(ctx, 403, new Dictionary<string, object>
            {
                { "ok", false },
                { "error", LegacyHttpPolicy.WriteDisabledCode },
                { "message", "Legacy HTTP writes are disabled; use WebSocket bridge commands." },
            });
            return;
        }

        switch (req.HttpMethod + " " + path)
        {
            case "GET /ping":
                WriteJson(ctx, 200, Ok(true, _handler.Run(app => McpOps.Ping(app), 60000), null));
                return;
            case "GET /doc":
                WriteJson(ctx, 200, Ok(true, _handler.Run(app => McpOps.DocInfo(app)), null));
                return;
            case "GET /rooms":
                {
                    var q = ParseQuery(req.Url.Query);
                    var limit = GetInt(q, "limit", 50);
                    var offset = GetInt(q, "offset", 0);
                    WriteJson(ctx, 200, Ok(true, _handler.Run(app => McpOps.Rooms(app, limit, offset)), null));
                    return;
                }
            case "GET /levels":
                WriteJson(ctx, 200, Ok(true, _handler.Run(app => McpOps.Levels(app)), null));
                return;
            case "GET /wall_types":
                WriteJson(ctx, 200, Ok(true, _handler.Run(app => McpOps.ListWallTypes(app)), null));
                return;
            case "GET /element":
                {
                    var q = ParseQuery(req.Url.Query);
                    long id;
                    if (!q.TryGetValue("id", out var ids) || !long.TryParse(ids, out id)) throw new ArgumentException("id required");
                    WriteJson(ctx, 200, Ok(true, _handler.Run(app => McpOps.GetElement(app, id)), null));
                    return;
                }
            case "GET /query":
                {
                    var q = ParseQuery(req.Url.Query);
                    var cat = q.TryGetValue("category", out var c) ? c : "OST_Walls";
                    var level = q.TryGetValue("level", out var l) ? l : "";
                    var limit = GetInt(q, "limit", 500);
                    WriteJson(ctx, 200, Ok(true, _handler.Run(app => McpOps.QueryElements(app, cat, level, limit)), null));
                    return;
                }
            case "GET /schedules":
                WriteJson(ctx, 200, Ok(true, _handler.Run(app => McpOps.ListSchedules(app)), null));
                return;
            case "GET /schedule":
                {
                    var q = ParseQuery(req.Url.Query);
                    long id;
                    if (!q.TryGetValue("id", out var ids) || !long.TryParse(ids, out id)) throw new ArgumentException("id required");
                    WriteJson(ctx, 200, Ok(true, _handler.Run(app => McpOps.GetSchedule(app, id)), null));
                    return;
                }
            case "GET /commands":
                WriteJson(ctx, 200, Ok(true, McpOps.ListHeatLossCommands(), null));
                return;
            case "GET /grids":
                {
                    var data = _handler.Run(app => McpOps.ListGrids(app), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "GET /floors":
                {
                    var data = _handler.Run(app => McpOps.ListFloors(app), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "GET /ceilings":
                {
                    var data = _handler.Run(app => McpOps.ListCeilings(app), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "GET /roofs":
                {
                    var data = _handler.Run(app => McpOps.ListRoofs(app), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "GET /measure_distance":
                {
                    var q = ParseQuery(req.Url.Query);
                    var id1 = GetInt(q, "id1", 0);
                    var id2 = GetInt(q, "id2", 0);
                    var data = _handler.Run(app => McpOps.MeasureDistance(app, id1, id2), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "GET /bounding_box":
                {
                    var q = ParseQuery(req.Url.Query);
                    var id = GetInt(q, "id", 0);
                    var data = _handler.Run(app => McpOps.GetBoundingBox(app, id), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "GET /categories":
                {
                    var data = _handler.Run(app => McpOps.ListCategories(app), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "GET /families":
                {
                    var q = ParseQuery(req.Url.Query);
                    var category = q.TryGetValue("category", out var c) ? c : "";
                    var data = _handler.Run(app => McpOps.ListFamilies(app, category), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "GET /instance_params":
                {
                    var q = ParseQuery(req.Url.Query);
                    var id = GetInt(q, "id", 0);
                    var data = _handler.Run(app => McpOps.GetInstanceParams(app, id), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "GET /pipe_types":
                {
                    var data = _handler.Run(app => McpOps.ListPipeTypes(app), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "GET /piping_systems":
                {
                    var data = _handler.Run(app => McpOps.ListPipingSystems(app), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "GET /duct_types":
                {
                    var data = _handler.Run(app => McpOps.ListDuctTypes(app), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            case "GET /mechanical_systems":
                {
                    var data = _handler.Run(app => McpOps.ListMechanicalSystems(app), 60000);
                    WriteJson(ctx, 200, Ok(true, data, null));
                    return;
                }
            default:
                WriteJson(ctx, 404, Ok(false, null, "Unknown route: " + req.HttpMethod + " " + path));
                return;
        }
    }

    private static Dictionary<string, object> Ok(bool ok, object data, string error)
    {
        return new Dictionary<string, object>
        {
            { "ok", ok },
            { "data", data },
            { "error", error },
        };
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return d;
        var q = query.TrimStart('?');
        foreach (var part in q.Split('&'))
        {
            if (part.Length == 0) continue;
            var kv = part.Split(new[] { '=' }, 2);
            var k = Uri.UnescapeDataString(kv[0]);
            var v = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
            d[k] = v;
        }
        return d;
    }

    private static int GetInt(Dictionary<string, string> q, string key, int def)
    {
        string v;
        int n;
        if (q.TryGetValue(key, out v) && int.TryParse(v, out n)) return n;
        return def;
    }

    private static void WriteJson(HttpListenerContext ctx, int status, object payload)
    {
        var json = JsonConvert.SerializeObject(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }
}
