using System;
using RevitBridge.Contracts;

namespace RevitBridge.Core
{
    /// <summary>
    /// Default safety classification for bridge commands. Explicit metadata can
    /// still override it; this keeps all commands safely classified even before
    /// a manifest/plugin provides its own safety block.
    /// </summary>
    public static class CommandSafetyDefaults
    {
        public static CommandSafety For(string name, string transaction)
        {
            var readOnly = string.Equals(transaction, "read", StringComparison.OrdinalIgnoreCase);
            var lower = (name ?? "").ToLowerInvariant();
            var destructive = lower.Contains("delete")
                || lower.Contains("clear")
                || lower.Contains("purge")
                || lower.Contains("reset")
                || lower.Contains("close_document");
            var confirmation = destructive
                || lower.Contains("save_document")
                || lower.Contains("open_document");
            var idempotent = readOnly
                || lower.Contains("list")
                || lower.Contains("get")
                || lower.Contains("ping")
                || lower.Contains("doc_info");

            return new CommandSafety
            {
                ReadOnly = readOnly,
                WritesDocument = !readOnly,
                Destructive = destructive,
                RequiresConfirmation = confirmation,
                BackupRequired = destructive,
                Idempotent = idempotent,
                OpenWorld = false,
            };
        }
    }
}
