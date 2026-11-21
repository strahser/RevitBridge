using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitBridge.Contracts
{
    /// <summary>Single envelope for all WebSocket messages.</summary>
    public sealed class BridgeMessage
    {
        [JsonProperty("v", Order = 1)]
        public int V { get; set; } = BridgeProtocol.Version;

        [JsonProperty("id", Order = 2)]
        public string Id { get; set; }

        [JsonProperty("type", Order = 3)]
        public string Type { get; set; }

        [JsonProperty("payload", Order = 4, NullValueHandling = NullValueHandling.Ignore)]
        public JToken Payload { get; set; }

        public static BridgeMessage Request(string id, string type, object payload = null)
        {
            return new BridgeMessage
            {
                Id = id,
                Type = type,
                Payload = payload == null ? null : JToken.FromObject(payload),
            };
        }

        public static BridgeMessage Error(string id, string code, string message, string hint = null, bool retryable = false)
        {
            return new BridgeMessage
            {
                Id = id,
                Type = BridgeProtocol.CallError,
                Payload = JToken.FromObject(new BridgeError
                {
                    Code = code,
                    Message = message,
                    Hint = hint,
                    Retryable = retryable,
                }),
            };
        }
    }

    public sealed class BridgeError
    {
        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("hint", NullValueHandling = NullValueHandling.Ignore)]
        public string Hint { get; set; }

        [JsonProperty("retryable")]
        public bool Retryable { get; set; }
    }
}
