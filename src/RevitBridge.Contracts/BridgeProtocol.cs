namespace RevitBridge.Contracts
{
    /// <summary>
    /// Wire protocol constants shared by the Revit add-in and the MCP client.
    /// One port, one envelope: { v, id, type, payload }.
    /// </summary>
    public static class BridgeProtocol
    {
        public const int Version = 1;

        /// <summary>WebSocket endpoint (same HttpListener port as /health).</summary>
        public const string WebSocketPath = "/bridge";

        /// <summary>HTTP readiness endpoint (no WebSocket needed).</summary>
        public const string HealthPath = "/health";

        public const string Hello = "hello";
        public const string Ready = "ready";
        public const string Ping = "ping";
        public const string Pong = "pong";
        public const string CommandsList = "commands.list";
        public const string CommandsResult = "commands.result";
        public const string Call = "call";
        public const string CallResult = "call.result";
        public const string CallError = "call.error";
        public const string CallProgress = "call.progress";
        public const string Cancel = "cancel";
    }

    public static class BridgeErrorCodes
    {
        public const string BadRequest = "bad_request";
        public const string Unauthorized = "unauthorized";
        public const string UnknownType = "unknown_type";
        public const string UnknownCommand = "unknown_command";
        public const string Forbidden = "forbidden";
        public const string Timeout = "timeout";
        public const string Internal = "internal";
    }
}
