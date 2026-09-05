using System.Text.Json;
using System.Text.Json.Serialization;

namespace AwgEasy.Contracts;

/// <summary>
/// Source-generated serialization for the wire contract. Lives here rather than in each
/// consumer so the control plane and the agent cannot drift into serializing the same bundle
/// two different ways - which would break signature verification.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(DesiredStateBundle))]
[JsonSerializable(typeof(SignedBundle))]
[JsonSerializable(typeof(EnrollRequest))]
[JsonSerializable(typeof(EnrollResponse))]
[JsonSerializable(typeof(NodeStatusReport))]
[JsonSerializable(typeof(NodeStatusAck))]
[JsonSerializable(typeof(ApiError))]
public sealed partial class ContractsJsonContext : JsonSerializerContext;
