using System;
using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitBridge
{
    /// <summary>
    /// Invokes DwgParser's LiveRevitOps by reflection. The JSON string contract avoids
    /// sharing a Newtonsoft JObject type identity across plugin assemblies.
    /// </summary>
    internal static class LiveOpsInvoker
    {
        public static object Invoke(UIApplication app, string assemblyPath, string typeName, string operation, JToken args)
        {
            var assembly = ResolveAssembly(assemblyPath);
            if (assembly == null)
                throw new InvalidOperationException("Assembly not found: " + assemblyPath);

            var type = assembly.GetType(typeName, false);
            if (type == null)
                throw new InvalidOperationException("Type not found: " + typeName);

            var method = type.GetMethod("TryExecuteJson",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
                throw new InvalidOperationException("TryExecuteJson not found in " + typeName);

            var json = args == null ? "{}" : JsonConvert.SerializeObject(args);
            object[] parameters = { app, operation, json, null };
            var ok = (bool)method.Invoke(null, parameters);
            if (!ok)
                throw new InvalidOperationException("Unknown live operation: " + operation);

            return parameters[3];
        }

        private static Assembly ResolveAssembly(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                try { return Assembly.LoadFrom(path); }
                catch (Exception) { }
            }

            var name = Path.GetFileNameWithoutExtension(path ?? "");
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
                        return assembly;
                }
                catch (Exception) { }
            }

            return null;
        }
    }
}
