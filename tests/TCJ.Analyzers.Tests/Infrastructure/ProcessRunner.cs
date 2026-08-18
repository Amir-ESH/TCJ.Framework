using System.Diagnostics;
using System.Text;

namespace TCJ.Analyzers.Tests.Infrastructure;

internal static class ProcessRunner
{
    public static async Task RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        StringBuilder output = new();
        StringBuilder error = new();

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                output.AppendLine(eventArgs.Data);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                error.AppendLine(eventArgs.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start '{fileName}'.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Command failed with exit code {process.ExitCode}: {fileName} {string.Join(' ', arguments)}" +
                Environment.NewLine +
                output +
                error);
        }
    }
}
