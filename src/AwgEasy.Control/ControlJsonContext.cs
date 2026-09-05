using System.Text.Json;
using System.Text.Json.Serialization;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ClientResponse))]
[JsonSerializable(typeof(ClientResponse[]))]
[JsonSerializable(typeof(CreateClientRequest))]
[JsonSerializable(typeof(UpdateClientRequest))]
[JsonSerializable(typeof(FleetResponse))]
[JsonSerializable(typeof(NodeResponse))]
[JsonSerializable(typeof(NodeResponse[]))]
[JsonSerializable(typeof(CreateNodeRequest))]
[JsonSerializable(typeof(EnrollmentTokenResponse))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(EventResponse))]
[JsonSerializable(typeof(EventResponse[]))]
[JsonSerializable(typeof(ClientStatsResponse))]
[JsonSerializable(typeof(ClientStatsResponse[]))]
[JsonSerializable(typeof(ImportResultResponse))]
[JsonSerializable(typeof(ClientShareResponse))]
[JsonSerializable(typeof(PublicShareResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(ApiError))]
[JsonSerializable(typeof(ServerObfuscationProfile))]
[JsonSerializable(typeof(ClientObfuscationOverrides))]
[JsonSerializable(typeof(SignedBundle))]
[JsonSerializable(typeof(EnrollRequest))]
[JsonSerializable(typeof(EnrollResponse))]
[JsonSerializable(typeof(NodeStatusReport))]
[JsonSerializable(typeof(NodeStatusAck))]
[JsonSerializable(typeof(LegacyState))]
[JsonSerializable(typeof(LegacyBackup))]
internal sealed partial class ControlJsonContext : JsonSerializerContext;
