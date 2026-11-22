using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
        private static readonly object ProbeLock = new object();
        private static readonly List<string> ProbeDirectories = new List<string>();

        private readonly McpHandler _handler;

        public BridgeCommandHost(McpHandler handler)
        {
            _handler = handler;
            Registry = new CommandRegistry();
            var policy = new BridgePolicy
            {
                Token = Environment.GetEnvironmentVariable("REVIT_BRIDGE_TOKEN") ?? "",
                AllowWrites = ReadBool("REVIT_BRIDGE_ALLOW_WRITES", false),
            };
            Router = new BridgeRouter(Registry, policy);
            AppDomain.CurrentDomain.AssemblyResolve += ResolveLoadedAssembly;
            RegisterBuiltins();
            LoadManifests();
        }

        /// <summary>
        /// Plugin binaries may reference a different RevitAPI patch than the running
        /// Revit. Resolve to the already loaded assembly (same simple name) instead of
        /// failing on the exact version.
        /// </summary>
        private static Assembly ResolveLoadedAssembly(object sender, ResolveEventArgs args)
        {
            var requested = new AssemblyName(args.Name);

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (string.Equals(assembly.GetName().Name, requested.Name, StringComparison.OrdinalIgnoreCase))
                        return assembly;
                }
                catch (Exception) { }
            }

            List<string> probes;
            lock (ProbeLock) probes = new List<string>(ProbeDirectories);
            foreach (var directory in probes)
            {
                try
                {
                    var candidate = Path.Combine(directory, requested.Name + ".dll");
                    if (File.Exists(candidate)) return Assembly.Load(File.ReadAllBytes(candidate));
                }
                catch (Exception) { }
            }

            try
            {
                var requesting = args.RequestingAssembly;
                if (requesting != null && !string.IsNullOrEmpty(requesting.Location))
                {
                    var candidate = Path.Combine(Path.GetDirectoryName(requesting.Location) ?? "", requested.Name + ".dll");
                    if (File.Exists(candidate)) return Assembly.Load(File.ReadAllBytes(candidate));
                }
            }
            catch (Exception) { }

            return null;
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
                }, new CommandSafety
                {
                    ReadOnly = false,
                    WritesDocument = false,
                    Destructive = false,
                    RequiresConfirmation = false,
                    BackupRequired = false,
                    Idempotent = false,
                    OpenWorld = false,
                });

            Register("revit.close_document", "Close the active document", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"save\":{\"type\":\"boolean\"}}}"),
                args => _handler.Run(app => McpOps.Close(app, args?.Value<bool?>("save") ?? false), 180000));
            Register("revit.save_document", "Save the active document (optionally Save As)", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}"),
                args => _handler.Run(app => McpOps.Save(app, args?.Value<string>("path")), 600000));
            Register("revit.new_project_from_template", "Create and save a project from a .rte template", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"templatePath\":{\"type\":\"string\"},\"projectPath\":{\"type\":\"string\"},\"activate\":{\"type\":\"boolean\"}},\"required\":[\"templatePath\",\"projectPath\"]}"),
                args => _handler.Run(app => McpOps.NewProjectFromTemplate(app,
                    args?.Value<string>("templatePath"),
                    args?.Value<string>("projectPath"),
                    args?.Value<bool?>("activate") ?? true), 600000));

            // Ported from RevitMcpServer command set (P4).
            Register("revit.current_view_info", "Current view info", BridgeTransaction.Read, null,
                args => _handler.Run(app => ExtendedOps.CurrentViewInfo(app), 60000));
            Register("revit.current_view_elements", "Elements in the current view", BridgeTransaction.Read,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"categories\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"limit\":{\"type\":\"integer\"}}}"),
                args => _handler.Run(app => ExtendedOps.CurrentViewElements(app, args as JObject ?? new JObject()), 60000));
            Register("revit.selected_elements", "Currently selected elements", BridgeTransaction.Read,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"limit\":{\"type\":\"integer\"}}}"),
                args => _handler.Run(app => ExtendedOps.SelectedElements(app, args as JObject ?? new JObject()), 60000));
            Register("revit.element_parameters", "Read instance and type parameters of an element", BridgeTransaction.Read,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"elementId\":{\"type\":\"integer\"}},\"required\":[\"elementId\"]}"),
                args => _handler.Run(app => ExtendedOps.ElementParameters(app, args as JObject ?? new JObject()), 60000));
            Register("revit.tag_walls", "Tag walls in the current view", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"limit\":{\"type\":\"integer\"}}}"),
                args => _handler.Run(app => ExtendedOps.TagWalls(app, args as JObject ?? new JObject()), 120000));
            Register("revit.tag_rooms", "Tag rooms in the current view", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"limit\":{\"type\":\"integer\"}}}"),
                args => _handler.Run(app => ExtendedOps.TagRooms(app, args as JObject ?? new JObject()), 120000));
            Register("revit.model_statistics", "Model statistics by category/type/level", BridgeTransaction.Read, null,
                args => _handler.Run(app => ExtendedOps.ModelStatistics(app, args as JObject ?? new JObject()), 120000));
            Register("revit.material_quantities", "Material areas and volumes", BridgeTransaction.Read, null,
                args => _handler.Run(app => ExtendedOps.MaterialQuantities(app, args as JObject ?? new JObject()), 120000));
            Register("revit.ai_element_filter", "Filter elements by category/level/type/parameter", BridgeTransaction.Read,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"category\":{\"type\":\"string\"},\"level\":{\"type\":\"string\"},\"typeName\":{\"type\":\"string\"},\"parameterName\":{\"type\":\"string\"},\"parameterValue\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\"}}}"),
                args => _handler.Run(app => ExtendedOps.FilterElements(app, args as JObject ?? new JObject()), 60000));
            Register("revit.operate_element", "Select/color/hide/isolate elements", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\"},\"ids\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}},\"color\":{\"type\":\"string\"}},\"required\":[\"action\",\"ids\"]}"),
                args => _handler.Run(app => ExtendedOps.OperateElement(app, args as JObject ?? new JObject()), 60000),
                ViewOnlySafety());
            Register("revit.color_splash", "Color elements by a parameter value", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"parameterName\":{\"type\":\"string\"}},\"required\":[\"parameterName\"]}"),
                args => _handler.Run(app => ExtendedOps.ColorSplash(app, args as JObject ?? new JObject()), 120000),
                ViewOnlySafety());
            Register("revit.create_dimensions", "Create a linear dimension between two elements", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"elementId1\":{\"type\":\"integer\"},\"elementId2\":{\"type\":\"integer\"},\"offsetMm\":{\"type\":\"number\"}},\"required\":[\"elementId1\",\"elementId2\"]}"),
                args => _handler.Run(app => ExtendedOps.CreateDimensions(app, args as JObject ?? new JObject()), 60000));
            Register("revit.create_structural_framing", "Create a structural framing grid", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"levelId\":{\"type\":\"integer\"},\"startMm\":{\"type\":\"array\"},\"endMm\":{\"type\":\"array\"},\"spacingMm\":{\"type\":\"number\"},\"count\":{\"type\":\"integer\"}},\"required\":[\"levelId\",\"startMm\",\"endMm\"]}"),
                args => _handler.Run(app => ExtendedOps.CreateStructuralFraming(app, args as JObject ?? new JObject()), 120000));
            Register("revit.create_line_element", "Create a wall/pipe/duct by two points", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\"},\"startMm\":{\"type\":\"array\"},\"endMm\":{\"type\":\"array\"}},\"required\":[\"kind\",\"startMm\",\"endMm\"]}"),
                args => _handler.Run(app => ExtendedOps.CreateLineElement(app, args as JObject ?? new JObject()), 120000));

            // Pipeline operations (P7): template -> AR link -> spaces -> views -> network -> sheets.
            Register("pipeline.list_views", "List views and view templates", BridgeTransaction.Read,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"includeTemplates\":{\"type\":\"boolean\"}}}"),
                args => _handler.Run(app => PipelineOps.ListViews(app, args as JObject ?? new JObject()), 60000));
            Register("pipeline.list_sheets", "List sheets", BridgeTransaction.Read, null,
                args => _handler.Run(app => PipelineOps.ListSheets(app, args as JObject ?? new JObject()), 60000));
            Register("pipeline.clear_model", "Delete links/spaces/model/created floor views, keep schedules/sheets/templates/levels/grids", BridgeTransaction.Write, null,
                args => _handler.Run(app => PipelineOps.ClearModel(app, args as JObject ?? new JObject()), 600000));
            Register("pipeline.set_link_room_bounding", "Enable/disable Room Bounding on Revit link types", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"linkTypeId\":{\"type\":\"integer\"},\"enabled\":{\"type\":\"boolean\"}}}"),
                args => _handler.Run(app => PipelineOps.SetLinkRoomBounding(app, args as JObject ?? new JObject()), 120000));
            Register("pipeline.regenerate", "Regenerate the active document", BridgeTransaction.Read, null,
                args => _handler.Run(app => PipelineOps.Regenerate(app, args as JObject ?? new JObject()), 60000));
            Register("pipeline.link_revit", "Link an AR .rvt model", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}"),
                args => _handler.Run(app => PipelineOps.LinkRevit(app, args as JObject ?? new JObject()), 300000));
            Register("pipeline.create_spaces", "Create MEP spaces from linked rooms (or plan circuits) and validate Area>0", BridgeTransaction.Write, null,
                args => _handler.Run(app => PipelineOps.CreateSpaces(app, args as JObject ?? new JObject()), 300000));
            Register("pipeline.delete_spaces", "Delete MEP spaces (OST_MEP_SPACES) and validate no leftovers", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"ids\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}}}"),
                args => _handler.Run(app => PipelineOps.DeleteSpaces(app, args as JObject ?? new JObject()), 300000));
            Register("pipeline.create_plan_view", "Create a floor plan view and apply a view template", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"levelId\":{\"type\":\"integer\"},\"name\":{\"type\":\"string\"},\"templateName\":{\"type\":\"string\"}},\"required\":[\"levelId\"]}"),
                args => _handler.Run(app => PipelineOps.CreatePlanView(app, args as JObject ?? new JObject()), 120000));
            Register("pipeline.delete_views", "Delete views by ids", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"ids\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}},\"required\":[\"ids\"]}"),
                args => _handler.Run(app => PipelineOps.DeleteViews(app, args as JObject ?? new JObject()), 120000));
            Register("pipeline.connect_terminal_duct", "Connect an air terminal to a duct", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"terminalId\":{\"type\":\"integer\"},\"ductId\":{\"type\":\"integer\"}},\"required\":[\"terminalId\",\"ductId\"]}"),
                args => _handler.Run(app => PipelineOps.ConnectTerminalToDuct(app, args as JObject ?? new JObject()), 120000));
            Register("pipeline.create_system", "Create a mechanical system and add elements", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"typeName\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"},\"elementIds\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}}}"),
                args => _handler.Run(app => PipelineOps.CreateMechanicalSystem(app, args as JObject ?? new JObject()), 120000));
            Register("pipeline.place_air_terminal", "Place an air terminal on a level and report its connectors", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"familyName\":{\"type\":\"string\"},\"typeName\":{\"type\":\"string\"},\"levelId\":{\"type\":\"integer\"},\"xMm\":{\"type\":\"number\"},\"yMm\":{\"type\":\"number\"},\"zMm\":{\"type\":\"number\"},\"rotationDeg\":{\"type\":\"number\"}}}"),
                args => _handler.Run(app => PipelineOps.PlaceAirTerminal(app, args as JObject ?? new JObject()), 120000));
            Register("pipeline.list_systems", "List mechanical system instances and member counts", BridgeTransaction.Read, null,
                args => _handler.Run(app => PipelineOps.ListMechanicalSystems(app, args as JObject ?? new JObject()), 60000));
            Register("pipeline.validate_system", "Validate a mechanical system: members, classification, flow, connectivity", BridgeTransaction.Read,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"systemId\":{\"type\":\"integer\"},\"name\":{\"type\":\"string\"}}}"),
                args => _handler.Run(app => PipelineOps.ValidateSystem(app, args as JObject ?? new JObject()), 60000));
            Register("pipeline.get_schedule", "Read schedule table data (header + rows) natively", BridgeTransaction.Read,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"scheduleId\":{\"type\":\"integer\"},\"maxRows\":{\"type\":\"integer\"}},\"required\":[\"scheduleId\"]}"),
                args => _handler.Run(app => PipelineOps.GetScheduleData(app, args as JObject ?? new JObject()), 60000));
            Register("pipeline.set_parameter", "Set any element/type parameter by name", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"elementId\":{\"type\":\"integer\"},\"name\":{\"type\":\"string\"},\"value\":{}},\"required\":[\"elementId\",\"name\",\"value\"]}"),
                args => _handler.Run(app => PipelineOps.SetParameter(app, args as JObject ?? new JObject()), 120000));
            Register("pipeline.set_view_scale", "Set a view scale directly (View.Scale)", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"viewId\":{\"type\":\"integer\"},\"scale\":{\"type\":\"integer\"}},\"required\":[\"viewId\",\"scale\"]}"),
                args => _handler.Run(app => PipelineOps.SetViewScale(app, args as JObject ?? new JObject()), 60000));
            Register("pipeline.set_view_crop", "Set a rectangular view crop (minMm/maxMm)", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"viewId\":{\"type\":\"integer\"},\"minMm\":{\"type\":\"array\"},\"maxMm\":{\"type\":\"array\"}},\"required\":[\"viewId\",\"minMm\",\"maxMm\"]}"),
                args => _handler.Run(app => PipelineOps.SetViewCrop(app, args as JObject ?? new JObject()), 60000));
            Register("pipeline.audit_model", "Collector audit: no existing pipes/fittings/equipment/generic models/systems", BridgeTransaction.Read,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"includeAnalysis\":{\"type\":\"boolean\"}}}"),
                args => _handler.Run(app => AuditOps.AuditModel(app, args as JObject ?? new JObject()), 120000));
            Register("pipeline.purge_model_content", "Delete existing model content (pipes/fittings/equipment/generic models/systems) and re-audit", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"includeAnalysis\":{\"type\":\"boolean\"},\"dryRun\":{\"type\":\"boolean\"}}}"),
                args => _handler.Run(app => AuditOps.PurgeModelContent(app, args as JObject ?? new JObject()), 600000));
            Register("pipeline.reset_energy_analysis", "Delete Revit energy analysis detail model and its analytical spaces", BridgeTransaction.Write, null,
                args => _handler.Run(app => AuditOps.ResetEnergyAnalysis(app, args as JObject ?? new JObject()), 300000));
            Register("pipeline.create_sheet", "Create a sheet", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"number\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"},\"titleBlockName\":{\"type\":\"string\"},\"titleBlockFamily\":{\"type\":\"string\"}}}"),
                args => _handler.Run(app => PipelineOps.CreateSheet(app, args as JObject ?? new JObject()), 120000));
            Register("pipeline.place_view", "Place a view on a sheet", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"sheetId\":{\"type\":\"integer\"},\"viewId\":{\"type\":\"integer\"},\"xMm\":{\"type\":\"number\"},\"yMm\":{\"type\":\"number\"}},\"required\":[\"sheetId\",\"viewId\"]}"),
                args => _handler.Run(app => PipelineOps.PlaceViewOnSheet(app, args as JObject ?? new JObject()), 120000));
            Register("pipeline.place_schedule", "Place a schedule on a sheet", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"sheetId\":{\"type\":\"integer\"},\"scheduleId\":{\"type\":\"integer\"},\"xMm\":{\"type\":\"number\"},\"yMm\":{\"type\":\"number\"}},\"required\":[\"sheetId\",\"scheduleId\"]}"),
                args => _handler.Run(app => PipelineOps.PlaceScheduleOnSheet(app, args as JObject ?? new JObject()), 120000));
            Register("pipeline.layout_sheet", "Crop views, choose sheet format/scale, pack plan + room explication and validate", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"sheetId\":{\"type\":\"integer\"},\"titleBlockName\":{\"type\":\"string\"},\"number\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"},\"viewIds\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}},\"scheduleIds\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}},\"marginMm\":{\"type\":\"number\"},\"gapMm\":{\"type\":\"number\"},\"bottomReserveMm\":{\"type\":\"number\"},\"cropMarginMm\":{\"type\":\"number\"},\"scale\":{\"type\":\"integer\"},\"clearSheet\":{\"type\":\"boolean\"}},\"required\":[\"viewIds\"]}"),
                args => _handler.Run(app => SheetLayoutOps.LayoutSheet(app, args as JObject ?? new JObject()), 600000));
            Register("pipeline.compose_sheet", "Compose a drawing sheet with duplicated views, safe box and box validation", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"sheetId\":{\"type\":\"integer\"},\"titleBlockName\":{\"type\":\"string\"},\"number\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"},\"marginMm\":{\"type\":\"number\"},\"gapMm\":{\"type\":\"number\"},\"stampReserveMm\":{\"type\":\"number\"},\"safeBoxMm\":{\"type\":\"array\"},\"cropMarginMm\":{\"type\":\"number\"},\"cropIncludeLinked\":{\"type\":\"boolean\"},\"clearSheet\":{\"type\":\"boolean\"},\"independentCopy\":{\"type\":\"boolean\"},\"drawFrame\":{\"type\":\"boolean\"},\"labelViews\":{\"type\":\"boolean\"},\"views\":{\"type\":\"array\"},\"schedules\":{\"type\":\"array\"}},\"required\":[\"views\"]}"),
                args => _handler.Run(app => SheetComposeOps.ComposeSheet(app, args as JObject ?? new JObject()), 600000));
            Register("pipeline.list_title_blocks", "Measure title block formats (A4/A3/A2/A1) from the template", BridgeTransaction.Read, null,
                args => _handler.Run(app => SheetLayoutOps.ListTitleBlocks(app, args as JObject ?? new JObject()), 120000));
            Register("pipeline.inspect_titleblock_family", "Inspect frame/stamp geometry of an existing title block family", BridgeTransaction.Read,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"familyName\":{\"type\":\"string\"}}}"),
                args => _handler.Run(app => WideTitleBlockOps.Inspect(app, args as JObject ?? new JObject()), 120000));
            Register("pipeline.collect_spec", "Collect OV elements for specification: systems, types, sizes, lengths, diagnostics", BridgeTransaction.Read,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"categories\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"levelIds\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}},\"systemNames\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"includeUnassigned\":{\"type\":\"boolean\"},\"resultFile\":{\"type\":\"string\"}}}"),
                args => _handler.Run(app => SpecOps.Collect(app, args as JObject ?? new JObject()), 300000));
            Register("pipeline.debug_spaces", "Debug space level ids, bounding boxes and locations", BridgeTransaction.Read, null,
                args => _handler.Run(app => SheetLayoutOps.DebugSpaces(app, args as JObject ?? new JObject()), 60000));
            Register("pipeline.create_level_explication", "Duplicate the room explication schedule and filter it by level", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"levelId\":{\"type\":\"integer\"},\"name\":{\"type\":\"string\"}},\"required\":[\"levelId\"]}"),
                args => _handler.Run(app => SheetLayoutOps.CreateLevelExplication(app, args as JObject ?? new JObject()), 120000));
            Register("pipeline.export_sheet_pdf", "Export a sheet to PDF", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"sheetId\":{\"type\":\"integer\"},\"path\":{\"type\":\"string\"}},\"required\":[\"sheetId\",\"path\"]}"),
                args => _handler.Run(app => PipelineOps.ExportSheetPdf(app, args as JObject ?? new JObject()), 300000),
                FileExportSafety());
        }

        private void LoadManifests()
        {
            var configured = Environment.GetEnvironmentVariable("REVIT_BRIDGE_MANIFESTS");
            var roots = string.IsNullOrWhiteSpace(configured)
                ? new[] { @"D:\Projects\01-bim", @"D:\Projects\04-network", @"D:\Projects\03-heating" }
                : configured.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            var manifests = BridgeManifestLoader.LoadFromRoots(roots);
            foreach (var loaded in manifests)
            {
                var assemblyPath = BridgeManifestLoader.ResolveAssemblyPath(loaded);
                if (!string.IsNullOrWhiteSpace(assemblyPath))
                {
                    var directory = Path.GetDirectoryName(assemblyPath);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        lock (ProbeLock)
                        {
                            if (!ProbeDirectories.Contains(directory))
                                ProbeDirectories.Add(directory);
                        }

                        // Preload sibling assemblies so a plugin loaded from bytes can
                        // resolve its own dependencies (Domain/Application/Infrastructure).
                        try
                        {
                            foreach (var dll in Directory.GetFiles(directory, "*.dll"))
                            {
                                try { Assembly.Load(File.ReadAllBytes(dll)); }
                                catch (Exception) { }
                            }
                        }
                        catch (Exception) { }
                    }
                }

                foreach (var spec in loaded.Manifest.Commands)
                {
                    if (string.IsNullOrWhiteSpace(spec.Name) || string.IsNullOrWhiteSpace(spec.Type)) continue;
                    try
                    {
                        RegisterManifestCommand(loaded, spec, assemblyPath);
                    }
                    catch (Exception)
                    {
                        // Duplicate or broken command must not break the whole bridge.
                    }
                }
            }
        }

        private void RegisterManifestCommand(LoadedBridgeManifest loaded, BridgeCommandSpec spec, string assemblyPath)
        {
            var transactionName = (spec.Transaction ?? "read").ToLowerInvariant();
            var descriptor = new CommandDescriptor
            {
                Name = spec.Name,
                Description = spec.Description ?? spec.Name,
                Provider = loaded.Manifest.Provider ?? "plugin",
                Transaction = transactionName,
                ArgsSchema = spec.ArgsSchema,
                Safety = CommandSafetyDefaults.For(spec.Name, transactionName),
            };

            if (string.Equals(spec.Kind, BridgeCommandSpec.KindLiveOps, StringComparison.OrdinalIgnoreCase))
            {
                Registry.Register(descriptor, args =>
                {
                    var result = _handler.Run(
                        app => WithSuppressedUi(() => LiveOpsInvoker.Invoke(app, assemblyPath, spec.Type, spec.Operation, args)),
                        300000);
                    return result == null ? JValue.CreateNull() : JToken.FromObject(result);
                });
                return;
            }

            Registry.Register(descriptor, args =>
            {
                var result = _handler.Run(
                    app => WithSuppressedUi(() => ExternalCommandInvoker.Invoke(app, assemblyPath, spec.Type, args)),
                    300000);
                return result == null ? JValue.CreateNull() : JToken.FromObject(result);
            });
        }

        private static T WithSuppressedUi<T>(Func<T> action)
        {
            var previous = Environment.GetEnvironmentVariable("REVIT_BRIDGE_SUPPRESS_UI");
            Environment.SetEnvironmentVariable("REVIT_BRIDGE_SUPPRESS_UI", "1");
            try
            {
                return action();
            }
            finally
            {
                Environment.SetEnvironmentVariable("REVIT_BRIDGE_SUPPRESS_UI", previous);
            }
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

        private void Register(string name, string description, BridgeTransaction transaction, JToken argsSchema,
            Func<JToken, object> handler, CommandSafety safety = null)
        {
            var transactionName = transaction.ToString().ToLowerInvariant();
            Registry.Register(
                new CommandDescriptor
                {
                    Name = name,
                    Description = description,
                    Provider = "RevitBridge",
                    Transaction = transactionName,
                    ArgsSchema = argsSchema,
                    Safety = safety ?? CommandSafetyDefaults.For(name, transactionName),
                },
                args =>
                {
                    var result = handler(args);
                    return result == null ? JValue.CreateNull() : JToken.FromObject(result);
                });
        }
    }
}
