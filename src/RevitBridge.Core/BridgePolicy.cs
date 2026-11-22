using System;
using System.Collections.Generic;
using RevitBridge.Contracts;

namespace RevitBridge.Core
{
    /// <summary>
    /// Authorization and command policy. Pure C#: no Revit, no transport.
    /// Writes are opt-in; dangerous commands are denied by default.
    /// </summary>
    public sealed class BridgePolicy
    {
        public BridgePolicy()
        {
            DeniedCommands = new HashSet<string>(StringComparer.Ordinal)
            {
                // Arbitrary C# execution is never exposed through the bridge.
                "send_code_to_revit",
                "bridge.exec",
                "bridge.eval",
            };
        }

        /// <summary>Empty token means "local dev, no auth" (live mode).</summary>
        public string Token { get; set; }

        public bool AllowWrites { get; set; } = false;

        public HashSet<string> DeniedCommands { get; }

        public bool IsAuthorized(string token)
        {
            if (string.IsNullOrEmpty(Token)) return true;
            return string.Equals(Token, token, StringComparison.Ordinal);
        }

        /// <summary>Returns null when allowed, otherwise a structured error.</summary>
        public BridgeError CheckCall(CommandDescriptor descriptor)
        {
            if (descriptor == null)
            {
                return new BridgeError
                {
                    Code = BridgeErrorCodes.UnknownCommand,
                    Message = "Unknown command",
                };
            }

            if (DeniedCommands.Contains(descriptor.Name))
            {
                return new BridgeError
                {
                    Code = BridgeErrorCodes.Forbidden,
                    Message = "Command is denied by policy: " + descriptor.Name,
                    Hint = "Arbitrary code execution is not exposed through RevitBridge.",
                };
            }

            if (!AllowWrites && string.Equals(descriptor.Transaction, "write", StringComparison.OrdinalIgnoreCase))
            {
                return new BridgeError
                {
                    Code = BridgeErrorCodes.Forbidden,
                    Message = "Write commands are disabled: " + descriptor.Name,
                    Hint = "Start the bridge with REVIT_BRIDGE_ALLOW_WRITES=1 to enable writes.",
                };
            }

            return null;
        }
    }
}
