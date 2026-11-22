using System;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitBridge.Contracts;

namespace RevitBridge.Commands
{
    /// <summary>
    /// A self-contained group of bridge commands. Modules are registered by the
    /// composition root; they never touch the HTTP/WebSocket transports.
    /// </summary>
    internal interface ICommandModule
    {
        void Register(ICommandRegistrar registrar);
    }

    /// <summary>
    /// Narrow registration surface for command modules. The host owns the
    /// Revit-thread dispatch and the registry; modules only describe commands.
    /// </summary>
    internal interface ICommandRegistrar
    {
        void Add(
            string name,
            string description,
            BridgeTransaction transaction,
            JToken argsSchema,
            Func<UIApplication, JToken, object> handler,
            int timeoutMs,
            CommandSafety safety = null);
    }
}
