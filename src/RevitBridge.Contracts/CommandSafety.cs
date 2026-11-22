using Newtonsoft.Json;

namespace RevitBridge.Contracts
{
    /// <summary>
    /// Agent-facing safety metadata. It is intentionally separate from the
    /// transaction kind: a write may be a harmless save or a destructive purge.
    /// </summary>
    public sealed class CommandSafety
    {
        [JsonProperty("readOnly")]
        public bool ReadOnly { get; set; }

        [JsonProperty("writesDocument")]
        public bool WritesDocument { get; set; }

        [JsonProperty("destructive")]
        public bool Destructive { get; set; }

        [JsonProperty("requiresConfirmation")]
        public bool RequiresConfirmation { get; set; }

        [JsonProperty("backupRequired")]
        public bool BackupRequired { get; set; }

        [JsonProperty("idempotent")]
        public bool Idempotent { get; set; }

        [JsonProperty("openWorld")]
        public bool OpenWorld { get; set; }
    }
}
