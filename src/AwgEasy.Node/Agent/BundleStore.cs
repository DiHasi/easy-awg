using System.Text.Json;
using AwgEasy.Contracts;

namespace AwgEasy.Node;

/// <summary>
/// Caches the last accepted bundle on disk.
///
/// This is what makes the node fail-static: if the control plane is unreachable - down, blocked,
/// or simply being upgraded - the agent re-applies the cached bundle and keeps serving VPN
/// traffic indefinitely. A node must never tear down its peers because it cannot reach the
/// control plane; losing management is not a reason to lose the data plane.
/// </summary>
public sealed class BundleStore(NodeOptions options, ILogger<BundleStore> logger)
{
    private readonly object _lock = new();

    public SignedBundle? Load()
    {
        lock (_lock)
        {
            if (!File.Exists(options.CachedBundlePath))
            {
                return null;
            }

            try
            {
                var json = File.ReadAllText(options.CachedBundlePath);
                return JsonSerializer.Deserialize(json, NodeJsonContext.Default.SignedBundle);
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
                logger.LogWarning(exception, "Cached bundle at {Path} could not be read.", options.CachedBundlePath);
                return null;
            }
        }
    }

    public void Save(SignedBundle bundle)
    {
        lock (_lock)
        {
            var directory = Path.GetDirectoryName(options.CachedBundlePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = options.CachedBundlePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(bundle, NodeJsonContext.Default.SignedBundle));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temp, options.CachedBundlePath, overwrite: true);
        }
    }
}
