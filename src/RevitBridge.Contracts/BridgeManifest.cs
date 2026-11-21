using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitBridge.Contracts
{
    /// <summary>
    /// Manifest emitted by a plugin repository. The bridge only reads metadata;
    /// command implementations stay inside the plugin assembly.
    /// </summary>
    public sealed class BridgeManifest
    {
        [JsonProperty("provider")]
        public string Provider { get; set; }

        /// <summary>Absolute path, or a path relative to the manifest directory.</summary>
        [JsonProperty("assembly")]
        public string Assembly { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("commands")]
        public List<BridgeCommandSpec> Commands { get; set; } = new List<BridgeCommandSpec>();
    }

    public sealed class BridgeCommandSpec
    {
        public const string KindExternalCommand = "externalCommand";
        public const string KindLiveOps = "liveOps";

        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>Full type name inside the assembly.</summary>
        [JsonProperty("type")]
        public string Type { get; set; }

        /// <summary>externalCommand (IExternalCommand) or liveOps (DwgParser LiveRevitOps).</summary>
        [JsonProperty("kind")]
        public string Kind { get; set; } = KindExternalCommand;

        /// <summary>Operation name for liveOps (e.g. CreateLevel).</summary>
        [JsonProperty("operation")]
        public string Operation { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("transaction")]
        public string Transaction { get; set; } = "read";

        [JsonProperty("argsSchema", NullValueHandling = NullValueHandling.Ignore)]
        public JToken ArgsSchema { get; set; }
    }

    /// <summary>Manifest plus the directory it was loaded from (for relative assembly paths).</summary>
    public sealed class LoadedBridgeManifest
    {
        public BridgeManifest Manifest { get; set; }
        public string Directory { get; set; }
        public string Path { get; set; }
    }
}
