using System;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using RevitBridge.Core;

namespace RevitBridge
{
    /// <summary>
    /// WebSocket endpoint on the same HttpListener port as the HTTP API.
    /// One request/response envelope per text message; the router is transport-agnostic.
    /// </summary>
    internal static class BridgeWebSocket
    {
        public static void Handle(HttpListenerContext context, BridgeRouter router)
        {
            WebSocket socket = null;
            try
            {
                socket = context.AcceptWebSocketAsync(null).GetAwaiter().GetResult().WebSocket;
                McpLog.Write("WebSocket connected");

                var buffer = new byte[64 * 1024];
                var text = new StringBuilder();

                while (socket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result;
                    try
                    {
                        result = socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None)
                            .GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        McpLog.Write("WebSocket receive failed: " + ex.Message);
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        try
                        {
                            socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None)
                                .GetAwaiter().GetResult();
                        }
                        catch (Exception) { }
                        break;
                    }

                    text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (!result.EndOfMessage) continue;

                    var request = text.ToString();
                    text.Clear();

                    string response;
                    try
                    {
                        response = router.Handle(request);
                    }
                    catch (Exception ex)
                    {
                        response = "{\"v\":1,\"type\":\"call.error\",\"payload\":{\"code\":\"internal\",\"message\":"
                            + Newtonsoft.Json.JsonConvert.ToString(ex.Message) + "}}";
                    }

                    var bytes = Encoding.UTF8.GetBytes(response);
                    try
                    {
                        socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                            CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        McpLog.Write("WebSocket send failed: " + ex.Message);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                McpLog.Write("WebSocket accept failed: " + ex.Message);
            }
            finally
            {
                try { socket?.Dispose(); } catch (Exception) { }
                McpLog.Write("WebSocket disconnected");
            }
        }
    }
}
