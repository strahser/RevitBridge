using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitBridge
{
    internal static class ParameterOps
    {
        public static object SetParameter(UIApplication app, JObject args)
        {
            var doc = BridgeOps.Doc(app);
            var elementId = BridgeOps.Id(args, "elementId");
            var element = doc.GetElement(elementId)
                ?? throw new InvalidOperationException("Element not found: " + elementId.Value);
            var name = args?.Value<string>("name");
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("args.name is required");

            var parameter = element.LookupParameter(name)
                ?? throw new InvalidOperationException("Parameter not found: " + name);
            if (parameter.IsReadOnly) throw new InvalidOperationException("Parameter is read-only: " + name);

            var token = args?["value"];
            using (var tx = new Transaction(doc, "Bridge Set Parameter"))
            {
                tx.Start();
                if (token is JValue value)
                {
                    switch (value.Type)
                    {
                        case JTokenType.Float: parameter.Set(value.Value<double>()); break;
                        case JTokenType.Integer: parameter.Set(value.Value<int>()); break;
                        case JTokenType.Boolean: parameter.Set(value.Value<bool>() ? 1 : 0); break;
                        default: parameter.Set(value.ToString()); break;
                    }
                }
                else if (token != null)
                {
                    parameter.Set(token.ToString());
                }
                doc.Regenerate();
                tx.Commit();
            }

            object current;
            switch (parameter.StorageType)
            {
                case StorageType.String: current = parameter.AsString(); break;
                case StorageType.Integer: current = parameter.AsInteger(); break;
                case StorageType.Double: current = parameter.AsDouble(); break;
                case StorageType.ElementId: current = parameter.AsElementId()?.Value; break;
                default: current = null; break;
            }

            return new Dictionary<string, object>
            {
                { "elementId", elementId.Value }, { "name", name },
                { "value", current }, { "valid", true },
            };
        }
    }
}
