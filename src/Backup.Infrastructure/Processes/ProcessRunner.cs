using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Backup.Application.Abstractions;

namespace Backup.Infrastructure.Processes;

public sealed class ProcessRunner : IProcessRunner
{
    private const int MaxErrorChars = 4000;

    public async Task RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? stdoutFile = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new ProcessFailedException($"No se pudo ejecutar '{fileName}'. ¿Está instalado y en el PATH? ({ex.Message})");
        }

        var stderr = new StringBuilder();
        var stderrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
            {
                if (stderr.Length < MaxErrorChars)
                {
                    stderr.AppendLine(line);
                }
            }
        }, cancellationToken);

        Task stdoutTask;
        if (stdoutFile is not null)
        {
            stdoutTask = CopyToFileAsync(process.StandardOutput.BaseStream, stdoutFile, cancellationToken);
        }
        else
        {
            stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
        }

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            var detail = stderr.ToString().Trim();
            throw new ProcessFailedException(
                $"'{Path.GetFileName(fileName)}' terminó con código {process.ExitCode}." + (detail.Length > 0 ? $" {detail}" : string.Empty));
        }
    }

    private static async Task CopyToFileAsync(Stream source, string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        await source.CopyToAsync(file, cancellationToken);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // El proceso ya terminó.
        }
    }
}
