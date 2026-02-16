using System.Diagnostics;

namespace FreeAiSsd.Shared;

public static class ProcessRunner
{
    public static async Task<int> RunAsync(string fileName, string arguments, string workingDirectory, IDictionary<string, string>? env = null, Action<string>? onOutput = null, CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (env != null)
        {
            foreach (var pair in env)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();

        var outputTask = Consume(process.StandardOutput, onOutput, ct);
        var errorTask = Consume(process.StandardError, onOutput, ct);

        await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(ct));
        return process.ExitCode;
    }

    private static async Task Consume(StreamReader reader, Action<string>? onOutput, CancellationToken ct)
    {
        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (!string.IsNullOrWhiteSpace(line))
            {
                onOutput?.Invoke(line);
            }
        }
    }
}
