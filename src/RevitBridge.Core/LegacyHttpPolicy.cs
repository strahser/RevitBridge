using System;

namespace RevitBridge.Core
{
    /// <summary>
    /// Policy for the legacy localhost HTTP API. The HTTP surface predates the
    /// WebSocket bridge and has no token/write policy; only GET (read) routes
    /// are allowed. All writes must go through the WebSocket command router.
    /// </summary>
    public static class LegacyHttpPolicy
    {
        public const string WriteDisabledCode = "legacy_write_disabled";

        public static bool IsReadOnlyRequest(string method)
        {
            return string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase);
        }
    }
}
