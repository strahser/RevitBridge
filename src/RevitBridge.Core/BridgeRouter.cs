using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitBridge.Contracts;

namespace RevitBridge.Core
{
    /// <summary>
    /// Stateless message router for the WebSocket envelope.
    /// Transport-agnostic: takes a JSON string and returns a JSON string.
    /// </summary>
    public sealed class BridgeRouter
    {
        private readonly CommandRegistry _registry;
        private readonly BridgePolicy _policy;

        public BridgeRouter(CommandRegistry registry, BridgePolicy policy)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        }

        public string RevitVersion { get; set; } = "";

        public int CommandCount => _registry.Count;

        public string Handle(string json)
        {
            BridgeMessage request;
            try
            {
                request = JsonConvert.DeserializeObject<BridgeMessage>(json);
            }
            catch (Exception ex)
            {
                return Serialize(BridgeMessage.Error(null, BridgeErrorCodes.BadRequest,
                    "Invalid JSON: " + ex.Message));
            }

            if (request == null || string.IsNullOrWhiteSpace(request.Type))
            {
                return Serialize(BridgeMessage.Error(request?.Id, BridgeErrorCodes.BadRequest,
                    "Message type is required"));
            }

            switch (request.Type)
            {
                case BridgeProtocol.Hello:
                    return HandleHello(request);
                case BridgeProtocol.Ping:
                    return Serialize(new BridgeMessage
                    {
                        Id = request.Id,
                        Type = BridgeProtocol.Pong,
                        Payload = JToken.FromObject(new
                        {
                            ok = true,
                            revitVersion = RevitVersion,
                            v = BridgeProtocol.Version,
                        }),
                    });
                case BridgeProtocol.CommandsList:
                    return Serialize(new BridgeMessage
                    {
                        Id = request.Id,
                        Type = BridgeProtocol.CommandsResult,
                        Payload = JToken.FromObject(_registry.List()),
                    });
                case BridgeProtocol.Call:
                    return HandleCall(request);
                default:
                    return Serialize(BridgeMessage.Error(request.Id, BridgeErrorCodes.UnknownType,
                        "Unknown message type: " + request.Type));
            }
        }

        private string HandleHello(BridgeMessage request)
        {
            var token = request.Payload?.Value<string>("token");
            if (!_policy.IsAuthorized(token))
            {
                return Serialize(BridgeMessage.Error(request.Id, BridgeErrorCodes.Unauthorized,
                    "Invalid bridge token"));
            }

            return Serialize(new BridgeMessage
            {
                Id = request.Id,
                Type = BridgeProtocol.Ready,
                Payload = JToken.FromObject(new
                {
                    ok = true,
                    protocol = BridgeProtocol.Version,
                    revitVersion = RevitVersion,
                    commands = _registry.Count,
                }),
            });
        }

        private string HandleCall(BridgeMessage request)
        {
            var command = request.Payload?.Value<string>("command");
            var args = request.Payload?["args"];
            if (string.IsNullOrWhiteSpace(command))
            {
                return Serialize(BridgeMessage.Error(request.Id, BridgeErrorCodes.BadRequest,
                    "call.payload.command is required"));
            }

            CommandDescriptor descriptor;
            Func<JToken, JToken> handler;
            if (!_registry.TryGet(command, out descriptor, out handler))
            {
                return Serialize(BridgeMessage.Error(request.Id, BridgeErrorCodes.UnknownCommand,
                    "Unknown command: " + command, "Call commands.list to discover commands."));
            }

            var policyError = _policy.CheckCall(descriptor);
            if (policyError != null)
            {
                return Serialize(new BridgeMessage
                {
                    Id = request.Id,
                    Type = BridgeProtocol.CallError,
                    Payload = JToken.FromObject(policyError),
                });
            }

            try
            {
                var result = handler(args);
                return Serialize(new BridgeMessage
                {
                    Id = request.Id,
                    Type = BridgeProtocol.CallResult,
                    Payload = JToken.FromObject(new
                    {
                        ok = true,
                        command = command,
                        result = result,
                    }),
                });
            }
            catch (TimeoutException ex)
            {
                return Serialize(BridgeMessage.Error(request.Id, BridgeErrorCodes.Timeout,
                    ex.Message, "Revit is busy; retry the call.", retryable: true));
            }
            catch (Exception ex)
            {
                return Serialize(BridgeMessage.Error(request.Id, BridgeErrorCodes.Internal,
                    ex.Message));
            }
        }

        private static string Serialize(BridgeMessage message)
        {
            return JsonConvert.SerializeObject(message);
        }
    }
}
