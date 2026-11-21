using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RevitBridge.Contracts;

namespace RevitBridge.Core
{
    /// <summary>
    /// Discovers and parses bridge manifests. Pure file/JSON work: no Revit API.
    /// Default roots are provided by the Revit host; an explicit file path is also accepted.
    /// </summary>
    public static class BridgeManifestLoader
    {
        private static readonly string[] SkipSegments =
        {
            "\\.git\\", "\\bin\\", "\\obj\\", "\\node_modules\\",
        };

        public static IReadOnlyList<LoadedBridgeManifest> LoadFromRoots(IEnumerable<string> roots)
        {
            var result = new List<LoadedBridgeManifest>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in roots ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(root)) continue;

                try
                {
                    if (File.Exists(root))
                    {
                        Add(result, seen, root);
                        continue;
                    }

                    if (!Directory.Exists(root)) continue;

                    foreach (var file in Directory.EnumerateFiles(root, "*.manifest.json", SearchOption.AllDirectories))
                    {
                        if (SkipSegments.Any(s => file.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0))
                            continue;
                        Add(result, seen, file);
                    }
                }
                catch (Exception)
                {
                    // A broken/unreadable root must not take down the bridge.
                }
            }

            return result;
        }

        /// <summary>Loads a single manifest file; returns null for invalid/empty manifests.</summary>
        public static LoadedBridgeManifest LoadFile(string file)
        {
            try
            {
                var full = Path.GetFullPath(file);
                var text = File.ReadAllText(full);
                var manifest = JsonConvert.DeserializeObject<BridgeManifest>(text);
                if (manifest == null || manifest.Commands == null || manifest.Commands.Count == 0)
                    return null;

                return new LoadedBridgeManifest
                {
                    Manifest = manifest,
                    Directory = Path.GetDirectoryName(full),
                    Path = full,
                };
            }
            catch (Exception)
            {
                // Invalid/unreadable manifest: skip; the host logs the file name.
                return null;
            }
        }

        private static void Add(List<LoadedBridgeManifest> result, HashSet<string> seen, string file)
        {
            var full = Path.GetFullPath(file);
            if (!seen.Add(full)) return;

            var loaded = LoadFile(full);
            if (loaded != null) result.Add(loaded);
        }

        /// <summary>
        /// Resolves a manifest assembly path. Tries absolute, manifest-relative,
        /// then common build output folders.
        /// </summary>
        public static string ResolveAssemblyPath(LoadedBridgeManifest manifest)
        {
            var assembly = manifest.Manifest?.Assembly;
            if (string.IsNullOrWhiteSpace(assembly)) return null;

            if (Path.IsPathRooted(assembly))
                return File.Exists(assembly) ? assembly : null;

            var candidates = new List<string>
            {
                Path.Combine(manifest.Directory, assembly),
                Path.Combine(manifest.Directory, "bin", "Debug.R24", assembly),
                Path.Combine(manifest.Directory, "bin", "Release.R24", assembly),
                Path.Combine(manifest.Directory, "..", "bin", "Debug.R24", assembly),
                Path.Combine(manifest.Directory, "..", "bin", "Release.R24", assembly),
            };

            foreach (var candidate in candidates)
            {
                var full = Path.GetFullPath(candidate);
                if (File.Exists(full)) return full;
            }

            return null;
        }
    }
}
