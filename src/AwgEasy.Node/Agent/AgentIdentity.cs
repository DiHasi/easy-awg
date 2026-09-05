using System.Text.Json;
using System.Text.Json.Serialization;
using AwgEasy.Contracts;

namespace AwgEasy.Node;

/// <summary>
/// Persisted agent identity. The private key is generated on this node and never leaves it -
/// only the public half is ever sent to the control plane, at enrollment.
/// </summary>
public sealed class AgentIdentityDocument
{
    public string? NodeId { get; set; }

    /// <summary>Base64url PKCS#8 of this agent's ECDSA P-256 private key.</summary>
    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>Base64url SubjectPublicKeyInfo matching <see cref="PrivateKey"/>.</summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>Control-plane signing key pinned at enrollment; every bundle is verified against it.</summary>
    public string? ControlSigningPublicKey { get; set; }

    public string? ControlSigningKeyId { get; set; }

    public long AppliedRevision { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public bool IsEnrolled => !string.IsNullOrWhiteSpace(NodeId) && !string.IsNullOrWhiteSpace(ControlSigningPublicKey);
}

public sealed class AgentIdentityStore(NodeOptions options, ILogger<AgentIdentityStore> logger)
{
    private readonly object _lock = new();

    public AgentIdentityDocument LoadOrCreate()
    {
        lock (_lock)
        {
            if (File.Exists(options.AgentIdentityPath))
            {
                var json = File.ReadAllText(options.AgentIdentityPath);
                var existing = JsonSerializer.Deserialize(json, NodeJsonContext.Default.AgentIdentityDocument);
                if (existing is not null && !string.IsNullOrWhiteSpace(existing.PrivateKey))
                {
                    return existing;
                }

                logger.LogWarning("Agent identity at {Path} is unusable. Generating a new key pair.", options.AgentIdentityPath);
            }

            using var key = BundleSigning.CreateKey();
            var document = new AgentIdentityDocument
            {
                PrivateKey = BundleSigning.ExportPrivateKey(key),
                PublicKey = BundleSigning.ExportPublicKey(key)
            };

            SaveUnsafe(document);
            logger.LogInformation("Generated a new agent key pair at {Path}.", options.AgentIdentityPath);
            return document;
        }
    }

    public void Save(AgentIdentityDocument document)
    {
        lock (_lock)
        {
            SaveUnsafe(document);
        }
    }

    private void SaveUnsafe(AgentIdentityDocument document)
    {
        var directory = Path.GetDirectoryName(options.AgentIdentityPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = options.AgentIdentityPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(document, NodeJsonContext.Default.AgentIdentityDocument));
        SetOwnerOnlyPermissions(temp);
        File.Move(temp, options.AgentIdentityPath, overwrite: true);
    }

    private static void SetOwnerOnlyPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
