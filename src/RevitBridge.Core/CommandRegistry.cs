using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RevitBridge.Contracts;

namespace RevitBridge.Core
{
    /// <summary>
    /// Pure command registry: no Revit API, no transport. The Revit host registers
    /// delegates; the router resolves them by name.
    /// </summary>
    public sealed class CommandRegistry
    {
        private sealed class Entry
        {
            public CommandDescriptor Descriptor;
            public Func<JToken, JToken> Handler;
        }

        private readonly Dictionary<string, Entry> _commands =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        public int Count => _commands.Count;

        public void Register(CommandDescriptor descriptor, Func<JToken, JToken> handler)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (string.IsNullOrWhiteSpace(descriptor.Name))
                throw new ArgumentException("Command name is required", nameof(descriptor));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (_commands.ContainsKey(descriptor.Name))
                throw new InvalidOperationException("Duplicate command: " + descriptor.Name);

            _commands[descriptor.Name] = new Entry
            {
                Descriptor = descriptor,
                Handler = handler,
            };
        }

        public bool TryGet(string name, out CommandDescriptor descriptor, out Func<JToken, JToken> handler)
        {
            descriptor = null;
            handler = null;
            if (string.IsNullOrEmpty(name)) return false;

            Entry entry;
            if (!_commands.TryGetValue(name, out entry)) return false;

            descriptor = entry.Descriptor;
            handler = entry.Handler;
            return true;
        }

        public IReadOnlyList<CommandDescriptor> List()
        {
            return _commands.Values
                .Select(x => x.Descriptor)
                .OrderBy(x => x.Name, StringComparer.Ordinal)
                .ToList();
        }
    }
}
