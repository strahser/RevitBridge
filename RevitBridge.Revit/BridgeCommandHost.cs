using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitBridge.Commands;
using RevitBridge.Contracts;
using RevitBridge.Core;

namespace RevitBridge
{
    /// <summary>
    /// Registers the built-in Revit commands for the bridge. This is the only place
    /// where the transport layer touches Revit operations; plugin commands are added
    /// later through manifests without changing this class.
    /// </summary>
    internal sealed class BridgeCommandHost : ICommandRegistrar
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
            new RevitCoreModule().Register(this);
            new PipelineModule().Register(this);
            new RevitModelingModule().Register(this);
        }

        private void LoadManifests()
        {
            var roots = ResolveManifestRoots();
            var manifests = BridgeManifestLoader.LoadFromRoots(roots);
            foreach (var loaded in manifests)
            {
                var assemblyPath = BridgeManifestLoader.ResolveAssemblyPath(loaded);
                RegisterProbeDirectory(assemblyPath);
                RegisterManifestCommands(loaded, assemblyPath);
            }
        }

        private static string[] ResolveManifestRoots()
        {
            var configured = Environment.GetEnvironmentVariable("REVIT_BRIDGE_MANIFESTS");
            return string.IsNullOrWhiteSpace(configured)
                ? new[] { @"D:\Projects\01-bim", @"D:\Projects\04-network", @"D:\Projects\03-heating" }
                : configured.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private void RegisterProbeDirectory(string assemblyPath)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath)) return;
            var directory = Path.GetDirectoryName(assemblyPath);
            if (string.IsNullOrWhiteSpace(directory)) return;

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

        private void RegisterManifestCommands(LoadedBridgeManifest loaded, string assemblyPath)
        {
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

        public void Add(
            string name,
            string description,
            BridgeTransaction transaction,
            JToken argsSchema,
            Func<UIApplication, JToken, object> handler,
            int timeoutMs,
            CommandSafety safety = null)
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
                    var result = _handler.Run(app => handler(app, args), timeoutMs);
                    return result == null ? JValue.CreateNull() : JToken.FromObject(result);
                });
        }

    }
}
