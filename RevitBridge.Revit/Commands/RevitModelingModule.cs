using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitBridge.Contracts;

namespace RevitBridge.Commands
{
    /// <summary>
    /// Modeling/annotation operations that are not part of the document/session
    /// core: tags, statistics, filters, view overrides, dimensions and simple
    /// line/framing creation.
    /// </summary>
    internal sealed class RevitModelingModule : ICommandModule
    {
        public void Register(ICommandRegistrar registrar)
        {
            registrar.Add("revit.tag_walls", "Tag walls in the current view",
                BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"limit\":{\"type\":\"integer\"}}}"),
                (app, args) => ExtendedOps.TagWalls(app, args as JObject ?? new JObject()), 120000);

            registrar.Add("revit.tag_rooms", "Tag rooms in the current view",
                BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"limit\":{\"type\":\"integer\"}}}"),
                (app, args) => ExtendedOps.TagRooms(app, args as JObject ?? new JObject()), 120000);

            registrar.Add("revit.model_statistics", "Model statistics by category/type/level",
                BridgeTransaction.Read, null,
                (app, args) => ExtendedOps.ModelStatistics(app, args as JObject ?? new JObject()), 120000);

            registrar.Add("revit.material_quantities", "Material areas and volumes",
                BridgeTransaction.Read, null,
                (app, args) => ExtendedOps.MaterialQuantities(app, args as JObject ?? new JObject()), 120000);

            registrar.Add("revit.ai_element_filter", "Filter elements by category/level/type/parameter",
                BridgeTransaction.Read,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"category\":{\"type\":\"string\"},\"level\":{\"type\":\"string\"},\"typeName\":{\"type\":\"string\"},\"parameterName\":{\"type\":\"string\"},\"parameterValue\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\"}}}"),
                (app, args) => ExtendedOps.FilterElements(app, args as JObject ?? new JObject()), 60000);

            registrar.Add("revit.operate_element", "Select/color/hide/isolate elements",
                BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\"},\"ids\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}},\"color\":{\"type\":\"string\"}},\"required\":[\"action\",\"ids\"]}"),
                (app, args) => ExtendedOps.OperateElement(app, args as JObject ?? new JObject()), 60000,
                ViewOnlySafety());

            registrar.Add("revit.color_splash", "Color elements by a parameter value",
                BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"parameterName\":{\"type\":\"string\"}},\"required\":[\"parameterName\"]}"),
                (app, args) => ExtendedOps.ColorSplash(app, args as JObject ?? new JObject()), 120000,
                ViewOnlySafety());

            registrar.Add("revit.create_dimensions", "Create a linear dimension between two elements",
                BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"elementId1\":{\"type\":\"integer\"},\"elementId2\":{\"type\":\"integer\"},\"offsetMm\":{\"type\":\"number\"}},\"required\":[\"elementId1\",\"elementId2\"]}"),
                (app, args) => ExtendedOps.CreateDimensions(app, args as JObject ?? new JObject()), 60000);

            registrar.Add("revit.create_structural_framing", "Create a structural framing grid",
                BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"levelId\":{\"type\":\"integer\"},\"startMm\":{\"type\":\"array\"},\"endMm\":{\"type\":\"array\"},\"spacingMm\":{\"type\":\"number\"},\"count\":{\"type\":\"integer\"}},\"required\":[\"levelId\",\"startMm\",\"endMm\"]}"),
                (app, args) => ExtendedOps.CreateStructuralFraming(app, args as JObject ?? new JObject()), 120000);

            registrar.Add("revit.create_line_element", "Create a wall/pipe/duct by two points",
                BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\"},\"startMm\":{\"type\":\"array\"},\"endMm\":{\"type\":\"array\"}},\"required\":[\"kind\",\"startMm\",\"endMm\"]}"),
                (app, args) => ExtendedOps.CreateLineElement(app, args as JObject ?? new JObject()), 120000);
        }

        private static CommandSafety ViewOnlySafety()
        {
            return new CommandSafety
            {
                ReadOnly = false,
                WritesDocument = false,
                Destructive = false,
                RequiresConfirmation = false,
                BackupRequired = false,
                Idempotent = false,
                OpenWorld = false,
            };
        }
    }
}
