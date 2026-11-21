using System;
using Newtonsoft.Json.Linq;
using RevitBridge.Contracts;
using RevitBridge.Core;

namespace RevitBridge
{
    /// <summary>
    /// Registers the built-in Revit commands for the bridge. This is the only place
    /// where the transport layer touches Revit operations; plugin commands are added
    /// later through manifests without changing this class.
    /// </summary>
    internal sealed class BridgeCommandHost
    {
        private readonly McpHandler _handler;

        public BridgeCommandHost(McpHandler handler)
        {
            _handler = handler;
            Registry = new CommandRegistry();
            var policy = new BridgePolicy
            {
                Token = Environment.GetEnvironmentVariable("REVIT_BRIDGE_TOKEN") ?? "",
                AllowWrites = ReadBool("REVIT_BRIDGE_ALLOW_WRITES", true),
            };
            Router = new BridgeRouter(Registry, policy);
            RegisterBuiltins();
        }

        public CommandRegistry Registry { get; }

        public BridgeRouter Router { get; }

        private static bool ReadBool(string name, bool fallback)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private void RegisterBuiltins()
        {
            Register("revit.ping", "Ping Revit and return version info", BridgeTransaction.Read, null,
                args => _handler.Run(app => McpOps.Ping(app), 60000));

            Register("revit.doc_info", "Active document info and counts", BridgeTransaction.Read, null,
                args => _handler.Run(app => McpOps.DocInfo(app), 60000));

            Register("revit.list_levels", "List levels", BridgeTransaction.Read, null,
                args => _handler.Run(app => McpOps.Levels(app), 60000));

            Register("revit.list_rooms", "List rooms and MEP spaces", BridgeTransaction.Read,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"limit\":{\"type\":\"integer\"},\"offset\":{\"type\":\"integer\"}}}"),
                args =>
                {
                    var limit = args?.Value<int?>("limit") ?? 50;
                    var offset = args?.Value<int?>("offset") ?? 0;
                    return _handler.Run(app => McpOps.Rooms(app, limit, offset), 60000);
                });

            Register("revit.open_document", "Open a .rvt document", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"detach\":{\"type\":\"boolean\"},\"audit\":{\"type\":\"boolean\"}},\"required\":[\"path\"]}"),
                args =>
                {
                    var path = args?.Value<string>("path");
                    if (string.IsNullOrWhiteSpace(path))
                        throw new ArgumentException("args.path is required");
                    var detach = args?.Value<bool?>("detach") ?? false;
                    var audit = args?.Value<bool?>("audit") ?? false;
                    return _handler.Run(app => McpOps.Open(app, path, detach, audit), 600000);
                });

            Register("revit.close_document", "Close the active document", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"save\":{\"type\":\"boolean\"}}}"),
                args => _handler.Run(app => McpOps.Close(app, args?.Value<bool?>("save") ?? false), 180000));
        }

        private void Register(string name, string description, BridgeTransaction transaction, JToken argsSchema,
            Func<JToken, object> handler)
        {
            Registry.Register(
                new CommandDescriptor
                {
                    Name = name,
                    Description = description,
                    Provider = "RevitBridge",
                    Transaction = transaction.ToString().ToLowerInvariant(),
                    ArgsSchema = argsSchema,
                },
                args =>
                {
                    var result = handler(args);
                    return result == null ? JValue.CreateNull() : JToken.FromObject(result);
                });
        }
    }
}
