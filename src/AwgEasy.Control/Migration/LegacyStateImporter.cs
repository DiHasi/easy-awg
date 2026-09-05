using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwgEasy.Contracts;

namespace AwgEasy.Control;

// Shape of the single-server state.json / backup export, kept only so existing deployments can
// be adopted without regenerating anything.
public sealed class LegacyState
{
    public string ServerPrivateKey { get; set; } = string.Empty;
    public string ServerPublicKey { get; set; } = string.Empty;
    public ServerObfuscationProfile? ServerObfuscation { get; set; }
    public List<LegacyClient> Clients { get; set; } = [];
}

public sealed class LegacyClient
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string PrivateKey { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string PresharedKey { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ClientObfuscationOverrides? Obfuscation { get; set; }
}

public sealed class LegacyBackup
{
    public int Version { get; set; }
    public LegacyState? State { get; set; }
}

/// <summary>
/// Adopts an existing single-server deployment.
///
/// This is the migration path that keeps current users connected: importing the old server key
/// pair means every already-issued client config still points at the same cryptographic
/// identity, so nothing has to be reissued and no tunnel breaks.
/// </summary>
public sealed class LegacyStateImporter(
    FleetRepository fleet,
    ClientRepository clients,
    FleetService fleetService,
    EventLog events,
    ILogger<LegacyStateImporter> logger)
{
    public (ImportResultResponse? Result, ApiError Error) Import(string json, bool replaceExistingClients)
    {
        LegacyState? state;
        try
        {
            // Accept either a raw state.json or a wrapped backup export.
            var backup = JsonSerializer.Deserialize(json, ControlJsonContext.Default.LegacyBackup);
            state = backup?.State ?? JsonSerializer.Deserialize(json, ControlJsonContext.Default.LegacyState);
        }
        catch (JsonException exception)
        {
            return (null, new ApiError("import_invalid_json", $"Could not parse the legacy state: {exception.Message}"));
        }

        if (state is null || string.IsNullOrWhiteSpace(state.ServerPrivateKey) || string.IsNullOrWhiteSpace(state.ServerPublicKey))
        {
            return (null, new ApiError("import_missing_identity", "Legacy state does not contain the server key pair."));
        }

        var current = fleetService.Current;
        var warnings = new List<string>();
        var now = DateTimeOffset.UtcNow;

        if (!Ipv4Network.TryParse(current.Subnet, out var network, out var subnetError))
        {
            return (null, subnetError);
        }

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenAddresses = new HashSet<string>(StringComparer.Ordinal);
        var importable = new List<ClientRecord>();

        foreach (var legacy in state.Clients)
        {
            if (string.IsNullOrWhiteSpace(legacy.Name) || string.IsNullOrWhiteSpace(legacy.Address)
                || string.IsNullOrWhiteSpace(legacy.PrivateKey) || string.IsNullOrWhiteSpace(legacy.PublicKey))
            {
                warnings.Add($"Skipped a client with missing fields (id '{legacy.Id}').");
                continue;
            }

            if (!seenNames.Add(legacy.Name) || !seenAddresses.Add(legacy.Address))
            {
                warnings.Add($"Skipped duplicate client '{legacy.Name}' ({legacy.Address}).");
                continue;
            }

            // Warn rather than reject: the operator may be moving to a wider subnet on purpose,
            // and refusing the whole import over one address would be worse than flagging it.
            if (!System.Net.IPAddress.TryParse(legacy.Address, out var address) || !network.Contains(address))
            {
                warnings.Add($"Client '{legacy.Name}' address {legacy.Address} is outside the configured subnet {current.Subnet}.");
            }

            importable.Add(new ClientRecord(
                string.IsNullOrWhiteSpace(legacy.Id) ? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) : legacy.Id,
                legacy.Name.Trim(),
                legacy.Address,
                legacy.PrivateKey,
                legacy.PublicKey,
                legacy.PresharedKey,
                legacy.Enabled,
                legacy.Obfuscation?.Normalize(),
                legacy.CreatedAt == default ? now : legacy.CreatedAt,
                now));
        }

        if (replaceExistingClients)
        {
            clients.DeleteAll();
        }
        else if (clients.List().Count > 0)
        {
            return (null, new ApiError("import_would_conflict", "Clients already exist. Re-run with replace enabled to overwrite them."));
        }

        fleet.ReplaceIdentity(state.ServerPrivateKey, state.ServerPublicKey, now);

        if (state.ServerObfuscation is not null)
        {
            fleet.UpdateObfuscation(state.ServerObfuscation.Normalize(), now);
        }

        foreach (var client in importable)
        {
            clients.Insert(client);
        }

        var revision = fleetService.BumpRevision("legacy state imported");
        events.Record("fleet.imported", $"Imported legacy state: {importable.Count} client(s), {warnings.Count} warning(s).");
        logger.LogInformation("Imported {Count} clients from legacy state.", importable.Count);

        return (new ImportResultResponse(importable.Count, revision, warnings.ToArray()), ApiError.Empty);
    }
}
