using System.Diagnostics;

namespace AwgEasy.Node;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

public static class ProcessRunner
{
    /// <summary>Thrown when the executable is absent or cannot be launched at all.</summary>
    public static bool IsMissingExecutable(Exception exception)
        => exception is FileNotFoundException or System.ComponentModel.Win32Exception or InvalidOperationException;

    public static async Task<ProcessResult> RunAsync(string fileName, string[] arguments, string? input, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardInput = input is not null;
        process.StartInfo.UseShellExecute = false;

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {fileName}.");
        }

        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    /// <summary>Runs a command, returning null when the executable is unavailable instead of throwing.</summary>
    public static async Task<ProcessResult?> TryRunAsync(string fileName, string[] arguments, string? input, CancellationToken cancellationToken)
    {
        try
        {
            return await RunAsync(fileName, arguments, input, cancellationToken);
        }
        catch (Exception exception) when (IsMissingExecutable(exception))
        {
            return null;
        }
    }
}
