using System.Text.Json;
using System.Text.Json.Serialization;
using AwgEasy.Contracts;

namespace AwgEasy.Node;

/// <summary>
/// Agent-local types only. Everything on the wire is serialized through
/// <see cref="ContractsJsonContext"/> so both ends of the protocol agree byte for byte.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(AgentIdentityDocument))]
[JsonSerializable(typeof(NodeHealthResponse))]
[JsonSerializable(typeof(SignedBundle))]
[JsonSerializable(typeof(DesiredStateBundle))]
[JsonSerializable(typeof(EnrollRequest))]
[JsonSerializable(typeof(EnrollResponse))]
[JsonSerializable(typeof(NodeStatusReport))]
[JsonSerializable(typeof(NodeStatusAck))]
[JsonSerializable(typeof(ApiError))]
internal sealed partial class NodeJsonContext : JsonSerializerContext;
