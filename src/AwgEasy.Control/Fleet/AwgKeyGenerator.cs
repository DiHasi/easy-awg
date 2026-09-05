using System.Diagnostics;

namespace AwgEasy.Control;

/// <summary>
/// Generates AmneziaWG keys by shelling out to `awg`, exactly as the single-server version did.
///
/// The control plane is now the only place keys are created, so the tiny awg binary is copied
/// into this image too. Replacing this with managed X25519 would remove the dependency but needs
/// a third-party curve implementation, which is not worth it while `awg` is already there.
/// </summary>
public interface IAwgKeyGenerator
{
    string GeneratePrivateKey();

    string GeneratePublicKey(string privateKey);

    string GeneratePresharedKey();
}

public sealed class AwgToolKeyGenerator : IAwgKeyGenerator
{
    public string GeneratePrivateKey() => Run("genkey", input: null);

    public string GeneratePublicKey(string privateKey) => Run("pubkey", privateKey + "\n");

    public string GeneratePresharedKey() => Run("genpsk", input: null);

    private static string Run(string argument, string? input)
    {
        using var process = new Process();
        process.StartInfo.FileName = "awg";
        process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardInput = input is not null;
        process.StartInfo.UseShellExecute = false;

        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start awg.");
        }

        if (input is not null)
        {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"awg {argument} failed with code {process.ExitCode}: {error}");
        }

        return output.Trim();
    }
}
