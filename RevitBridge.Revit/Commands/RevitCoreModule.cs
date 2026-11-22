using System;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitBridge.Contracts;

namespace RevitBridge.Commands
{
    /// <summary>
    /// Session/document primitives: ping, document info, levels/rooms, open/
    /// close/save and current-view queries. No modeling or sheet policy lives
    /// here.
    /// </summary>
    internal sealed class RevitCoreModule : ICommandModule
    {
        public void Register(ICommandRegistrar registrar)
        {
            registrar.Add("revit.ping", "Ping Revit and return version info",
                BridgeTransaction.Read, null,
                (app, args) => McpOps.Ping(app), 60000);

            registrar.Add("revit.doc_info", "Active document info and counts",
                BridgeTransaction.Read, null,
                (app, args) => McpOps.DocInfo(app), 60000);

            registrar.Add("revit.list_levels", "List levels",
                BridgeTransaction.Read, null,
                (app, args) => McpOps.Levels(app), 60000);

            registrar.Add("revit.list_rooms", "List rooms and MEP spaces",
                BridgeTransaction.Read,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"limit\":{\"type\":\"integer\"},\"offset\":{\"type\":\"integer\"}}}"),
                (app, args) => McpOps.Rooms(app,
                    args?.Value<int?>("limit") ?? 50,
                    args?.Value<int?>("offset") ?? 0), 60000);

            registrar.Add("revit.open_document", "Open a .rvt document",
                BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"detach\":{\"type\":\"boolean\"},\"audit\":{\"type\":\"boolean\"}},\"required\":[\"path\"]}"),
                (app, args) =>
                {
                    var path = args?.Value<string>("path");
                    if (string.IsNullOrWhiteSpace(path))
                        throw new ArgumentException("args.path is required");
                    return McpOps.Open(app, path,
                        args?.Value<bool?>("detach") ?? false,
                        args?.Value<bool?>("audit") ?? false);
                }, 600000, new CommandSafety
                {
                    ReadOnly = false,
                    WritesDocument = false,
                    Destructive = false,
                    RequiresConfirmation = false,
                    BackupRequired = false,
                    Idempotent = false,
                    OpenWorld = false,
                });

            registrar.Add("revit.close_document", "Close the active document",
                BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"save\":{\"type\":\"boolean\"}}}"),
                (app, args) => McpOps.Close(app, args?.Value<bool?>("save") ?? false), 180000);

            registrar.Add("revit.save_document", "Save the active document (optionally Save As)",
                BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}"),
                (app, args) => McpOps.Save(app, args?.Value<string>("path")), 600000);

            registrar.Add("revit.new_project_from_template", "Create and save a project from a .rte template",
                BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"templatePath\":{\"type\":\"string\"},\"projectPath\":{\"type\":\"string\"},\"activate\":{\"type\":\"boolean\"}},\"required\":[\"templatePath\",\"projectPath\"]}"),
                (app, args) => McpOps.NewProjectFromTemplate(app,
                    args?.Value<string>("templatePath"),
                    args?.Value<string>("projectPath"),
                    args?.Value<bool?>("activate") ?? true), 600000);

            registrar.Add("revit.current_view_info", "Current view info",
                BridgeTransaction.Read, null,
                (app, args) => ExtendedOps.CurrentViewInfo(app), 60000);

            registrar.Add("revit.current_view_elements", "Elements in the current view",
                BridgeTransaction.Read,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"categories\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"limit\":{\"type\":\"integer\"}}}"),
                (app, args) => ExtendedOps.CurrentViewElements(app, args as JObject ?? new JObject()), 60000);

            registrar.Add("revit.selected_elements", "Currently selected elements",
                BridgeTransaction.Read,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"limit\":{\"type\":\"integer\"}}}"),
                (app, args) => ExtendedOps.SelectedElements(app, args as JObject ?? new JObject()), 60000);

            registrar.Add("revit.element_parameters", "Read instance and type parameters of an element",
                BridgeTransaction.Read,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"elementId\":{\"type\":\"integer\"}},\"required\":[\"elementId\"]}"),
                (app, args) => ExtendedOps.ElementParameters(app, args as JObject ?? new JObject()), 60000);
        }
    }
}
