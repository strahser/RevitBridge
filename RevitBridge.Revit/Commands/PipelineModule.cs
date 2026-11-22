using System;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitBridge.Contracts;

namespace RevitBridge.Commands
{
    /// <summary>
    /// Pipeline operations (P7): template -> AR link -> spaces -> views ->
    /// network -> sheets, plus model audit/purge and specification collection.
    /// Mechanical extraction of the pipeline.* registrations from BridgeCommandHost;
    /// names, schemas, timeouts and safety metadata are unchanged.
    /// </summary>
    internal sealed class PipelineModule : ICommandModule
    {
        public void Register(ICommandRegistrar registrar)
        {
            registrar.Add("pipeline.list_views", "List views and view templates", BridgeTransaction.Read,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"includeTemplates\":{\"type\":\"boolean\"}}}"),
                (app, args) => ViewSheetOps.ListViews(app, args as JObject ?? new JObject()), 60000);

            registrar.Add("pipeline.list_sheets", "List sheets", BridgeTransaction.Read, null,
                (app, args) => ViewSheetOps.ListSheets(app, args as JObject ?? new JObject()), 60000);

            registrar.Add("pipeline.clear_model", "Delete links/spaces/model/created floor views, keep schedules/sheets/templates/levels/grids", BridgeTransaction.Write, null,
                (app, args) => ModelOps.ClearModel(app, args as JObject ?? new JObject()), 600000);

            registrar.Add("pipeline.set_link_room_bounding", "Enable/disable Room Bounding on Revit link types", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"linkTypeId\":{\"type\":\"integer\"},\"enabled\":{\"type\":\"boolean\"}}}"),
                (app, args) => ModelOps.SetLinkRoomBounding(app, args as JObject ?? new JObject()), 120000);

            registrar.Add("pipeline.regenerate", "Regenerate the active document", BridgeTransaction.Read, null,
                (app, args) => ModelOps.Regenerate(app, args as JObject ?? new JObject()), 60000);

            registrar.Add("pipeline.link_revit", "Link an AR .rvt model", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}"),
                (app, args) => ModelOps.LinkRevit(app, args as JObject ?? new JObject()), 300000);

            registrar.Add("pipeline.create_spaces", "Create MEP spaces from linked rooms (or plan circuits) and validate Area>0", BridgeTransaction.Write, null,
                (app, args) => ModelOps.CreateSpaces(app, args as JObject ?? new JObject()), 300000);

            registrar.Add("pipeline.delete_spaces", "Delete MEP spaces (OST_MEP_SPACES) and validate no leftovers", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"ids\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}}}"),
                (app, args) => ModelOps.DeleteSpaces(app, args as JObject ?? new JObject()), 300000);

            registrar.Add("pipeline.create_plan_view", "Create a floor plan view and apply a view template", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"levelId\":{\"type\":\"integer\"},\"name\":{\"type\":\"string\"},\"templateName\":{\"type\":\"string\"}},\"required\":[\"levelId\"]}"),
                (app, args) => ModelOps.CreatePlanView(app, args as JObject ?? new JObject()), 120000);

            registrar.Add("pipeline.delete_views", "Delete views by ids", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"ids\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}},\"required\":[\"ids\"]}"),
                (app, args) => ModelOps.DeleteViews(app, args as JObject ?? new JObject()), 120000);

            registrar.Add("pipeline.connect_terminal_duct", "Connect an air terminal to a duct", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"terminalId\":{\"type\":\"integer\"},\"ductId\":{\"type\":\"integer\"}},\"required\":[\"terminalId\",\"ductId\"]}"),
                (app, args) => MepOps.ConnectTerminalToDuct(app, args as JObject ?? new JObject()), 120000);

            registrar.Add("pipeline.create_system", "Create a mechanical system and add elements", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"typeName\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"},\"elementIds\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}}}"),
                (app, args) => MepOps.CreateMechanicalSystem(app, args as JObject ?? new JObject()), 120000);

            registrar.Add("pipeline.place_air_terminal", "Place an air terminal on a level and report its connectors", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"familyName\":{\"type\":\"string\"},\"typeName\":{\"type\":\"string\"},\"levelId\":{\"type\":\"integer\"},\"xMm\":{\"type\":\"number\"},\"yMm\":{\"type\":\"number\"},\"zMm\":{\"type\":\"number\"},\"rotationDeg\":{\"type\":\"number\"}}}"),
                (app, args) => MepOps.PlaceAirTerminal(app, args as JObject ?? new JObject()), 120000);

            registrar.Add("pipeline.list_systems", "List mechanical system instances and member counts", BridgeTransaction.Read, null,
                (app, args) => MepOps.ListMechanicalSystems(app, args as JObject ?? new JObject()), 60000);

            registrar.Add("pipeline.validate_system", "Validate a mechanical system: members, classification, flow, connectivity", BridgeTransaction.Read,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"systemId\":{\"type\":\"integer\"},\"name\":{\"type\":\"string\"}}}"),
                (app, args) => MepOps.ValidateSystem(app, args as JObject ?? new JObject()), 60000);

            registrar.Add("pipeline.get_schedule", "Read schedule table data (header + rows) natively", BridgeTransaction.Read,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"scheduleId\":{\"type\":\"integer\"},\"maxRows\":{\"type\":\"integer\"}},\"required\":[\"scheduleId\"]}"),
                (app, args) => ScheduleOps.GetScheduleData(app, args as JObject ?? new JObject()), 60000);

            registrar.Add("pipeline.set_parameter", "Set any element/type parameter by name", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"elementId\":{\"type\":\"integer\"},\"name\":{\"type\":\"string\"},\"value\":{}},\"required\":[\"elementId\",\"name\",\"value\"]}"),
                (app, args) => ParameterOps.SetParameter(app, args as JObject ?? new JObject()), 120000);

            registrar.Add("pipeline.set_view_scale", "Set a view scale directly (View.Scale)", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"viewId\":{\"type\":\"integer\"},\"scale\":{\"type\":\"integer\"}},\"required\":[\"viewId\",\"scale\"]}"),
                (app, args) => ViewSheetOps.SetViewScale(app, args as JObject ?? new JObject()), 60000);

            registrar.Add("pipeline.set_view_crop", "Set a rectangular view crop (minMm/maxMm)", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"viewId\":{\"type\":\"integer\"},\"minMm\":{\"type\":\"array\"},\"maxMm\":{\"type\":\"array\"}},\"required\":[\"viewId\",\"minMm\",\"maxMm\"]}"),
                (app, args) => ViewSheetOps.SetViewCrop(app, args as JObject ?? new JObject()), 60000);

            registrar.Add("pipeline.audit_model", "Collector audit: no existing pipes/fittings/equipment/generic models/systems", BridgeTransaction.Read,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"includeAnalysis\":{\"type\":\"boolean\"}}}"),
                (app, args) => AuditOps.AuditModel(app, args as JObject ?? new JObject()), 120000);

            registrar.Add("pipeline.purge_model_content", "Delete existing model content (pipes/fittings/equipment/generic models/systems) and re-audit", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"includeAnalysis\":{\"type\":\"boolean\"},\"dryRun\":{\"type\":\"boolean\"}}}"),
                (app, args) => AuditOps.PurgeModelContent(app, args as JObject ?? new JObject()), 600000);

            registrar.Add("pipeline.reset_energy_analysis", "Delete Revit energy analysis detail model and its analytical spaces", BridgeTransaction.Write, null,
                (app, args) => AuditOps.ResetEnergyAnalysis(app, args as JObject ?? new JObject()), 300000);

            registrar.Add("pipeline.create_sheet", "Create a sheet", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"number\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"},\"titleBlockName\":{\"type\":\"string\"},\"titleBlockFamily\":{\"type\":\"string\"}}}"),
                (app, args) => ViewSheetOps.CreateSheet(app, args as JObject ?? new JObject()), 120000);

            registrar.Add("pipeline.place_view", "Place a view on a sheet", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"sheetId\":{\"type\":\"integer\"},\"viewId\":{\"type\":\"integer\"},\"xMm\":{\"type\":\"number\"},\"yMm\":{\"type\":\"number\"}},\"required\":[\"sheetId\",\"viewId\"]}"),
                (app, args) => ViewSheetOps.PlaceViewOnSheet(app, args as JObject ?? new JObject()), 120000);

            registrar.Add("pipeline.place_schedule", "Place a schedule on a sheet", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"sheetId\":{\"type\":\"integer\"},\"scheduleId\":{\"type\":\"integer\"},\"xMm\":{\"type\":\"number\"},\"yMm\":{\"type\":\"number\"}},\"required\":[\"sheetId\",\"scheduleId\"]}"),
                (app, args) => ViewSheetOps.PlaceScheduleOnSheet(app, args as JObject ?? new JObject()), 120000);

            registrar.Add("pipeline.layout_sheet", "Crop views, choose sheet format/scale, pack plan + room explication and validate", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"sheetId\":{\"type\":\"integer\"},\"titleBlockName\":{\"type\":\"string\"},\"number\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"},\"viewIds\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}},\"scheduleIds\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}},\"marginMm\":{\"type\":\"number\"},\"gapMm\":{\"type\":\"number\"},\"bottomReserveMm\":{\"type\":\"number\"},\"cropMarginMm\":{\"type\":\"number\"},\"scale\":{\"type\":\"integer\"},\"clearSheet\":{\"type\":\"boolean\"}},\"required\":[\"viewIds\"]}"),
                (app, args) => SheetLayoutOps.LayoutSheet(app, args as JObject ?? new JObject()), 600000);

            registrar.Add("pipeline.compose_sheet", "Compose a drawing sheet with duplicated views, safe box and box validation", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"sheetId\":{\"type\":\"integer\"},\"titleBlockName\":{\"type\":\"string\"},\"number\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"},\"marginMm\":{\"type\":\"number\"},\"gapMm\":{\"type\":\"number\"},\"stampReserveMm\":{\"type\":\"number\"},\"safeBoxMm\":{\"type\":\"array\"},\"cropMarginMm\":{\"type\":\"number\"},\"cropIncludeLinked\":{\"type\":\"boolean\"},\"clearSheet\":{\"type\":\"boolean\"},\"independentCopy\":{\"type\":\"boolean\"},\"drawFrame\":{\"type\":\"boolean\"},\"labelViews\":{\"type\":\"boolean\"},\"views\":{\"type\":\"array\"},\"schedules\":{\"type\":\"array\"}},\"required\":[\"views\"]}"),
                (app, args) => SheetComposer.ComposeSheet(app, args as JObject ?? new JObject()), 600000);

            registrar.Add("pipeline.list_title_blocks", "Measure title block formats (A4/A3/A2/A1) from the template", BridgeTransaction.Read, null,
                (app, args) => SheetLayoutOps.ListTitleBlocks(app, args as JObject ?? new JObject()), 120000);

            registrar.Add("pipeline.inspect_titleblock_family", "Inspect frame/stamp geometry of an existing title block family", BridgeTransaction.Read,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"familyName\":{\"type\":\"string\"}}}"),
                (app, args) => WideTitleBlockOps.Inspect(app, args as JObject ?? new JObject()), 120000);

            registrar.Add("pipeline.collect_spec", "Collect OV elements for specification: systems, types, sizes, lengths, diagnostics", BridgeTransaction.Read,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"categories\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"levelIds\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}},\"systemNames\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"includeUnassigned\":{\"type\":\"boolean\"},\"resultFile\":{\"type\":\"string\"}}}"),
                (app, args) => SpecOps.Collect(app, args as JObject ?? new JObject()), 300000);

            registrar.Add("pipeline.debug_spaces", "Debug space level ids, bounding boxes and locations", BridgeTransaction.Read, null,
                (app, args) => SheetLayoutOps.DebugSpaces(app, args as JObject ?? new JObject()), 60000);

            registrar.Add("pipeline.create_level_explication", "Duplicate the room explication schedule and filter it by level", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"levelId\":{\"type\":\"integer\"},\"name\":{\"type\":\"string\"}},\"required\":[\"levelId\"]}"),
                (app, args) => SheetLayoutOps.CreateLevelExplication(app, args as JObject ?? new JObject()), 120000);

            registrar.Add("pipeline.export_sheet_pdf", "Export a sheet to PDF", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"sheetId\":{\"type\":\"integer\"},\"path\":{\"type\":\"string\"}},\"required\":[\"sheetId\",\"path\"]}"),
                (app, args) => ViewSheetOps.ExportSheetPdf(app, args as JObject ?? new JObject()), 300000,
                FileExportSafety());
        }

        private static CommandSafety FileExportSafety()
        {
            return new CommandSafety
            {
                ReadOnly = false,
                WritesDocument = false,
                Destructive = false,
                RequiresConfirmation = false,
                BackupRequired = false,
                Idempotent = true,
                OpenWorld = false,
            };
        }
    }
}
