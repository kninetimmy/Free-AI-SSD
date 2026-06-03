namespace FreeAiSsd.Shared;

/// <summary>
/// Simple file-based logger that writes timestamped log entries to the SSD's
/// logs directory. Each component (prep, runner) gets its own daily log file
/// named "{component}-{YYYYMMDD}.log". All timestamps are UTC in ISO 8601 format.
/// </summary>
public sealed class SsdLogger
{
    private readonly string _logFilePath;
    private readonly object _sync = new();

    /// <summary>
    /// Creates a logger for a specific component, initializing the log file
    /// in the SSD's logs directory with today's date.
    /// </summary>
    /// <param name="ssdRoot">Root path of the portable SSD.</param>
    /// <param name="name">Component name used in the log filename (e.g., "prep", "runner").</param>
    public SsdLogger(string ssdRoot, string name)
    {
        Directory.CreateDirectory(Path.Combine(ssdRoot, SsdLayout.Logs));
        _logFilePath = Path.Combine(ssdRoot, SsdLayout.Logs, $"{name}-{DateTime.UtcNow:yyyyMMdd}.log");
    }

    /// <summary>Writes a DEBUG-level log entry with the current UTC timestamp.</summary>
    public void Debug(string message) => Write("DEBUG", message);

    /// <summary>Writes an INFO-level log entry with the current UTC timestamp.</summary>
    public void Info(string message) => Write("INFO", message);

    /// <summary>Writes a WARN-level log entry with the current UTC timestamp.</summary>
    public void Warn(string message) => Write("WARN", message);

    /// <summary>Writes an ERROR-level log entry with the current UTC timestamp.</summary>
    public void Error(string message) => Write("ERROR", message);

    /// <summary>
    /// Appends a single log line to the file. Each line follows the format:
    /// "2024-01-15T14:30:00.0000000+00:00 [INFO] Message text"
    /// <para>
    /// Opened with <see cref="FileShare.ReadWrite"/> so a second process holding the same
    /// daily log file can't cause a sharing-violation <see cref="IOException"/> — that
    /// exception used to escape as an unhandled, terminating crash that killed the next
    /// runner launch. Logging is best-effort: a write that still fails after a short retry
    /// is dropped rather than propagated, because the logger must never take down the app.
    /// </para>
    /// </summary>
    private void Write(string level, string message)
    {
        var line = $"{DateTime.UtcNow:o} [{level}] {message}{Environment.NewLine}";
        lock (_sync)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using var stream = new FileStream(
                        _logFilePath, FileMode.Append, FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var writer = new StreamWriter(stream);
                    writer.Write(line);
                    return;
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(20);
                }
                catch
                {
                    // Final failure (still locked, drive removed, etc.): drop the line.
                    return;
                }
            }
        }
    }
}
