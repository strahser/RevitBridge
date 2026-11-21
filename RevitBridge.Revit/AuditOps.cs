using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Analysis;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitBridge
{
    /// <summary>
    /// Pre-flight audit of a template: proves with FilteredElementCollector that no
    /// pre-existing model content (heat-loss generic models, pipes, fittings, heating
    /// devices, old systems) is left in the host document.
    /// </summary>
    internal static class AuditOps
    {
        private sealed class CategorySpec
        {
            public BuiltInCategory Category;
            public string Group; // model | system | analysis
        }

        private static readonly CategorySpec[] Specs =
        {
            // Existing model content (heat-loss / old AR/OV modelling).
            new CategorySpec { Category = BuiltInCategory.OST_GenericModel, Group = "model" },
            new CategorySpec { Category = BuiltInCategory.OST_PipeCurves, Group = "model" },
            new CategorySpec { Category = BuiltInCategory.OST_FlexPipeCurves, Group = "model" },
            new CategorySpec { Category = BuiltInCategory.OST_PipeFitting, Group = "model" },
            new CategorySpec { Category = BuiltInCategory.OST_PipeAccessory, Group = "model" },
            new CategorySpec { Category = BuiltInCategory.OST_PlaceHolderPipes, Group = "model" },
            new CategorySpec { Category = BuiltInCategory.OST_MechanicalEquipment, Group = "model" },
            new CategorySpec { Category = BuiltInCategory.OST_ZoneEquipment, Group = "model" },
            new CategorySpec { Category = BuiltInCategory.OST_PlumbingFixtures, Group = "model" },
            new CategorySpec { Category = BuiltInCategory.OST_PlumbingEquipment, Group = "model" },
            new CategorySpec { Category = BuiltInCategory.OST_SpecialityEquipment, Group = "model" },
            new CategorySpec { Category = BuiltInCategory.OST_DuctCurves, Group = "model" },
            new CategorySpec { Category = BuiltInCategory.OST_FlexDuctCurves, Group = "model" },
            new CategorySpec { Category = BuiltInCategory.OST_DuctFitting, Group = "model" },
            new CategorySpec { Category = BuiltInCategory.OST_DuctAccessory, Group = "model" },
            new CategorySpec { Category = BuiltInCategory.OST_DuctTerminal, Group = "model" },
            new CategorySpec { Category = BuiltInCategory.OST_PlaceHolderDucts, Group = "model" },

            // Existing systems / zones.
            new CategorySpec { Category = BuiltInCategory.OST_HVAC_Zones, Group = "system" },
            new CategorySpec { Category = BuiltInCategory.OST_MEPSystemZone, Group = "system" },

            // Heat-loss analytical model (optional; reported separately).
            new CategorySpec { Category = BuiltInCategory.OST_AnalyticSpaces, Group = "analysis" },
            new CategorySpec { Category = BuiltInCategory.OST_AnalyticalPanel, Group = "analysis" },
            new CategorySpec { Category = BuiltInCategory.OST_MEPLoadAreas, Group = "analysis" },
        };

        private static Document Doc(UIApplication app)
        {
            return app.ActiveUIDocument?.Document
                ?? throw new InvalidOperationException("Нет активного документа.");
        }

        private static string Label(BuiltInCategory category)
        {
            try { return LabelUtils.GetLabelFor(category); }
            catch (Exception) { return category.ToString(); }
        }

        private static List<Element> Elements(Document doc, BuiltInCategory category)
        {
            return new FilteredElementCollector(doc)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .Where(e => !e.ViewSpecific && e.OwnerViewId == ElementId.InvalidElementId)
                .ToList();
        }

        public static object AuditModel(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var groups = new List<Dictionary<string, object>>();
            int modelCount = 0, systemCount = 0, analysisCount = 0;

            foreach (var spec in Specs)
            {
                var elements = Elements(doc, spec.Category);
                var count = elements.Count;
                if (count == 0) continue;

                if (spec.Group == "model") modelCount += count;
                else if (spec.Group == "system") systemCount += count;
                else analysisCount += count;

                groups.Add(new Dictionary<string, object>
                {
                    { "key", spec.Category.ToString() },
                    { "label", Label(spec.Category) },
                    { "group", spec.Group },
                    { "count", count },
                    { "sampleIds", elements.Take(5).Select(e => e.Id.Value).ToArray() },
                });
            }

            var piping = new FilteredElementCollector(doc).OfClass(typeof(PipingSystem))
                .WhereElementIsNotElementType().GetElementCount();
            var mechanical = new FilteredElementCollector(doc).OfClass(typeof(MechanicalSystem))
                .WhereElementIsNotElementType().GetElementCount();
            var energyModels = new FilteredElementCollector(doc).OfClass(typeof(EnergyAnalysisDetailModel))
                .WhereElementIsNotElementType().GetElementCount();
            systemCount += piping + mechanical;
            analysisCount += energyModels;

            if (energyModels > 0)
            {
                groups.Add(new Dictionary<string, object>
                {
                    { "key", "EnergyAnalysisDetailModel" },
                    { "label", "Энергомодель (Revit)" },
                    { "group", "analysis" },
                    { "count", energyModels },
                    { "sampleIds", new FilteredElementCollector(doc)
                        .OfClass(typeof(EnergyAnalysisDetailModel))
                        .WhereElementIsNotElementType().ToElementIds()
                        .Take(5).Select(id => id.Value).ToArray() },
                });
            }

            var spaces = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_MEPSpaces)
                .WhereElementIsNotElementType().GetElementCount();

            var clean = modelCount == 0 && systemCount == 0;
            var cleanIncludingAnalysis = clean && analysisCount == 0;

            return new Dictionary<string, object>
            {
                { "modelCount", modelCount },
                { "systemCount", systemCount },
                { "analysisCount", analysisCount },
                { "pipingSystems", piping },
                { "mechanicalSystems", mechanical },
                { "energyModels", energyModels },
                { "spaces", spaces },
                { "clean", clean },
                { "cleanIncludingAnalysis", cleanIncludingAnalysis },
                { "groups", groups },
            };
        }

        public static object PurgeModelContent(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var includeAnalysis = args?.Value<bool?>("includeAnalysis") ?? true;
            var dryRun = args?.Value<bool?>("dryRun") ?? false;

            var before = (Dictionary<string, object>)AuditModel(app,
                new JObject { ["includeAnalysis"] = includeAnalysis });

            var targetIds = new List<ElementId>();
            foreach (var spec in Specs)
            {
                if (spec.Group == "analysis" && !includeAnalysis) continue;
                targetIds.AddRange(Elements(doc, spec.Category).Select(e => e.Id));
            }
            targetIds.AddRange(new FilteredElementCollector(doc).OfClass(typeof(PipingSystem))
                .WhereElementIsNotElementType().ToElementIds());
            targetIds.AddRange(new FilteredElementCollector(doc).OfClass(typeof(MechanicalSystem))
                .WhereElementIsNotElementType().ToElementIds());

            var unique = targetIds.GroupBy(id => id.Value).Select(g => g.First()).ToList();
            if (dryRun)
            {
                return new Dictionary<string, object>
                {
                    { "dryRun", true }, { "requested", unique.Count },
                    { "ids", unique.Take(50).Select(id => id.Value).ToArray() },
                    { "before", before },
                };
            }

            var deleted = 0;
            var failed = new List<Dictionary<string, object>>();
            using (var tx = new Transaction(doc, "Bridge Purge Model Content"))
            {
                tx.Start();
                foreach (var id in unique)
                {
                    try
                    {
                        if (doc.GetElement(id) == null) continue;
                        doc.Delete(id);
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new Dictionary<string, object>
                        {
                            { "id", id.Value }, { "error", ex.Message },
                        });
                    }
                }
                tx.Commit();
            }

            var after = (Dictionary<string, object>)AuditModel(app,
                new JObject { ["includeAnalysis"] = includeAnalysis });

            return new Dictionary<string, object>
            {
                { "requested", unique.Count },
                { "deleted", deleted },
                { "failed", failed },
                { "before", before },
                { "after", after },
                { "valid", (bool)after["cleanIncludingAnalysis"] },
            };
        }

        public static object ResetEnergyAnalysis(UIApplication app, JObject args)
        {
            var doc = Doc(app);
            var before = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_AnalyticSpaces)
                .WhereElementIsNotElementType().GetElementCount();

            var model = EnergyAnalysisDetailModel.GetMainEnergyAnalysisDetailModel(doc);
            if (model == null)
            {
                return new Dictionary<string, object>
                {
                    { "modelId", null }, { "deletedModel", false },
                    { "before", before }, { "after", before },
                    { "valid", before == 0 }, { "reason", "no energy analysis detail model" },
                };
            }

            var modelId = model.Id.Value;
            var deletedModel = false;
            var error = "";
            using (var tx = new Transaction(doc, "Bridge Reset Energy Analysis"))
            {
                tx.Start();
                try { doc.Delete(model.Id); deletedModel = true; }
                catch (Exception ex) { error = ex.Message; }
                tx.Commit();
            }

            var after = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_AnalyticSpaces)
                .WhereElementIsNotElementType().GetElementCount();

            return new Dictionary<string, object>
            {
                { "modelId", modelId }, { "deletedModel", deletedModel },
                { "before", before }, { "after", after },
                { "error", error }, { "valid", after == 0 },
            };
        }
    }
}
