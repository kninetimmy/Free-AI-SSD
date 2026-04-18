using System.Diagnostics;

namespace FreeAiSsd.Shared;

/// <summary>
/// Generic utility for launching external processes asynchronously with
/// stdout/stderr streaming. Used by the prep app to run Ollama commands
/// (pull, serve) and other shell operations with real-time output.
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    /// Launches a process and streams its stdout/stderr to an optional callback.
    /// Blocks asynchronously until the process exits or cancellation is requested.
    /// </summary>
    /// <param name="fileName">Executable path or command name.</param>
    /// <param name="arguments">Command-line arguments.</param>
    /// <param name="workingDirectory">Working directory for the process.</param>
    /// <param name="env">Optional environment variable overrides.</param>
    /// <param name="onOutput">Callback invoked for each non-empty output line (both stdout and stderr).</param>
    /// <param name="ct">Cancellation token to abort the operation.</param>
    /// <returns>The process exit code.</returns>
    public static Task<int> RunAsync(string fileName, string arguments, string workingDirectory, IDictionary<string, string>? env = null, Action<string>? onOutput = null, CancellationToken ct = default)
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
        return RunInternalAsync(startInfo, env, onOutput, ct);
    }

    /// <summary>
    /// Overload that populates <see cref="ProcessStartInfo.ArgumentList"/> instead of
    /// a single string. Required when any argument could contain spaces, quotes, or
    /// metacharacters — the OS does the quoting so we can't accidentally inject.
    /// </summary>
    public static Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, IDictionary<string, string>? env = null, Action<string>? onOutput = null, CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }
        return RunInternalAsync(startInfo, env, onOutput, ct);
    }

    private static async Task<int> RunInternalAsync(ProcessStartInfo startInfo, IDictionary<string, string>? env, Action<string>? onOutput, CancellationToken ct)
    {
        if (env != null)
        {
            foreach (var pair in env)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();

        // Consume stdout and stderr concurrently to prevent buffer deadlocks.
        var outputTask = Consume(process.StandardOutput, onOutput, ct);
        var errorTask = Consume(process.StandardError, onOutput, ct);

        await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(ct));
        return process.ExitCode;
    }

    /// <summary>
    /// Reads lines from a stream reader until EOF or cancellation,
    /// invoking the callback for each non-empty line.
    /// </summary>
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
