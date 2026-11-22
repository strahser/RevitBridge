using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitBridge.Contracts
{
    public enum BridgeTransaction
    {
        Read = 0,
        Write = 1,
        None = 2,
    }

    /// <summary>Public command metadata exposed through commands.list.</summary>
    public sealed class CommandDescriptor
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("provider")]
        public string Provider { get; set; }

        [JsonProperty("transaction")]
        public string Transaction { get; set; }

        [JsonProperty("argsSchema", NullValueHandling = NullValueHandling.Ignore)]
        public JToken ArgsSchema { get; set; }

        [JsonProperty("safety", NullValueHandling = NullValueHandling.Ignore)]
        public CommandSafety Safety { get; set; }
    }
}
