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
                AllowWrites = ReadBool("REVIT_BRIDGE_ALLOW_WRITES", true),
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
                });

            Register("revit.close_document", "Close the active document", BridgeTransaction.Write,
                JToken.Parse("{\"type\":\"object\",\"properties\":{\"save\":{\"type\":\"boolean\"}}}"),
                args => _handler.Run(app => McpOps.Close(app, args?.Value<bool?>("save") ?? false), 180000));
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
            var descriptor = new CommandDescriptor
            {
                Name = spec.Name,
                Description = spec.Description ?? spec.Name,
                Provider = loaded.Manifest.Provider ?? "plugin",
                Transaction = (spec.Transaction ?? "read").ToLowerInvariant(),
                ArgsSchema = spec.ArgsSchema,
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
