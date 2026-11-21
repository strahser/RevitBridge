using System;

namespace RevitBridge.Contracts
{
    /// <summary>
    /// Marks an IExternalCommand (or a bridge command host method) as a bridge command.
    /// The add-in reads this metadata; plugins only reference Contracts, never the bridge host.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class RevitBridgeCommandAttribute : Attribute
    {
        public RevitBridgeCommandAttribute(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public string Description { get; set; }

        public BridgeTransaction Transaction { get; set; } = BridgeTransaction.Read;

        /// <summary>Optional JSON Schema (as a JSON string) for command arguments.</summary>
        public string ArgsSchemaJson { get; set; }
    }
}
