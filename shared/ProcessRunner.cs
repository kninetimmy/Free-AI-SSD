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

        // Kill the child the moment cancellation is requested. Two reasons this must happen on
        // the token (not just in a catch after the await): (1) Process.Dispose — the using above —
        // does NOT terminate the OS process, so without an explicit kill a cancelled/timed-out
        // launch orphans the child and the resources it holds (e.g. the OCR per-image timeout
        // path, where an orphaned tesseract keeps its temp image open and the caller's File.Delete
        // then leaks it); (2) the redirected-pipe ReadLineAsync below does not reliably honor the
        // token mid-read, so a child that has gone quiet keeps the Consume readers — and thus the
        // WhenAll — blocked until it exits on its own. Killing closes the pipes, which unblocks the
        // reads and lets cancellation surface promptly. Matches the kill-on-cancel pattern already
        // in ModelOperations / OllamaServerHandle / OllamaLifecycleService — this was the only
        // launcher missing it.
        await using var killOnCancel = ct.Register(() => TryKillTree(process));

        // Consume stdout and stderr concurrently to prevent buffer deadlocks.
        var outputTask = Consume(process.StandardOutput, onOutput, ct);
        var errorTask = Consume(process.StandardError, onOutput, ct);

        try
        {
            await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(ct));
        }
        catch
        {
            // Also kill when a reader faults without cancellation (the registration above only
            // fires on the token), so that failure path can't orphan the child either. Best-effort
            // and idempotent with the registration; never mask the original failure.
            TryKillTree(process);
            throw;
        }

        // Killing the child on cancel makes WaitForExitAsync above complete normally (the process
        // exited) rather than throwing — a race that would otherwise let a cancelled run return an
        // exit code instead of cancelling. Surface cancellation deterministically so callers (the
        // OCR per-image timeout, intended-shutdown of Ollama) see the OperationCanceledException
        // they expect.
        ct.ThrowIfCancellationRequested();
        return process.ExitCode;
    }

    /// <summary>
    /// Terminates the process and its children if it is still running. Swallows races
    /// (the process exiting between the check and the kill) and access errors so the
    /// caller's original cancellation/failure is the one that surfaces.
    /// </summary>
    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
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
