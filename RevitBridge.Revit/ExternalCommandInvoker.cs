using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitBridge
{
    /// <summary>
    /// Invokes a plugin IExternalCommand through the bridge. The command keeps its
    /// own assembly and business logic; the bridge only supplies command data.
    /// </summary>
    internal static class ExternalCommandInvoker
    {
        public static object Invoke(UIApplication app, string assemblyPath, string typeName, JToken args)
        {
            var type = ResolveType(assemblyPath, typeName);
            if (type == null)
                throw new InvalidOperationException("Command type not found: " + typeName);

            var command = Activator.CreateInstance(type) as IExternalCommand;
            if (command == null)
                throw new InvalidOperationException("Type is not IExternalCommand: " + typeName);

            var data = McpOps.ForgeCommandData(app);
            var message = "";
            var elements = new ElementSet();

            // Manifest commands are plain IExternalCommand instances; pass their JSON
            // arguments through an environment variable (restored afterwards).
            var previousArgs = Environment.GetEnvironmentVariable("REVIT_BRIDGE_ARGS_JSON");
            var previousCommand = Environment.GetEnvironmentVariable("REVIT_BRIDGE_COMMAND");
            try
            {
                Environment.SetEnvironmentVariable("REVIT_BRIDGE_ARGS_JSON",
                    args == null ? "" : args.ToString(Newtonsoft.Json.Formatting.None));
                Environment.SetEnvironmentVariable("REVIT_BRIDGE_COMMAND", typeName);

                var result = command.Execute(data, ref message, elements);
                if (result == Result.Failed)
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(message) ? "Command failed: " + typeName : message);

                return new Dictionary<string, object>
                {
                    { "type", typeName },
                    { "result", result.ToString() },
                    { "message", message ?? "" },
                };
            }
            finally
            {
                Environment.SetEnvironmentVariable("REVIT_BRIDGE_ARGS_JSON", previousArgs);
                Environment.SetEnvironmentVariable("REVIT_BRIDGE_COMMAND", previousCommand);
            }
        }

        private static Type ResolveType(string assemblyPath, string typeName)
        {
            string loadError = null;
            string state = assemblyPath == null
                ? "path=null"
                : (File.Exists(assemblyPath) ? "path=exists" : "path=missing");

            if (!string.IsNullOrWhiteSpace(assemblyPath) && File.Exists(assemblyPath))
            {
                // Load bytes first: PE32+ (x64) IL-only plugin assemblies are rejected by
                // Assembly.LoadFrom in .NET Framework (0x80131515).
                try
                {
                    var assembly = Assembly.Load(File.ReadAllBytes(assemblyPath));
                    var type = assembly.GetType(typeName, false);
                    if (type != null) return type;
                    loadError = "assembly loaded from bytes, type not found; types=" + string.Join(", ",
                        SafeTypeNames(assembly));
                }
                catch (Exception ex)
                {
                    loadError = ex.GetType().Name + ": " + ex.Message;

                    try
                    {
                        var assembly = Assembly.LoadFrom(assemblyPath);
                        var type = assembly.GetType(typeName, false);
                        if (type != null) return type;
                        loadError += "; loadFrom=type not found";
                    }
                    catch (Exception ex2)
                    {
                        var fle = ex2 as FileLoadException;
                        loadError += "; loadFrom=" + ex2.GetType().Name + ": " + ex2.Message
                            + (fle?.FusionLog == null ? "" : "; fusion=" + fle.FusionLog);
                    }
                }
            }

            // Fallback: loaded assemblies + Revit Addins probe (HeatLoss-style deployment).
            var fallback = McpOps.FindCommandType(typeName);
            if (fallback != null) return fallback;

            throw new InvalidOperationException(
                "Command type not found: " + typeName + " (" + state + "; assembly=" + assemblyPath +
                (loadError == null ? "" : "; loadError=" + loadError) + ")");
        }

        private static IEnumerable<string> SafeTypeNames(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes().Select(t => t.FullName).Take(20);
            }
            catch (Exception)
            {
                return new[] { "<GetTypes failed>" };
            }
        }
    }
}
