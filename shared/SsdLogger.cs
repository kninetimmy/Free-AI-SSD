namespace FreeAiSsd.Shared;

public sealed class SsdLogger
{
    private readonly string _logFilePath;

    public SsdLogger(string ssdRoot, string name)
    {
        Directory.CreateDirectory(Path.Combine(ssdRoot, SsdLayout.Logs));
        _logFilePath = Path.Combine(ssdRoot, SsdLayout.Logs, $"{name}-{DateTime.UtcNow:yyyyMMdd}.log");
    }

    public void Info(string message) => Write("INFO", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTime.UtcNow:o} [{level}] {message}{Environment.NewLine}";
        File.AppendAllText(_logFilePath, line);
    }
}
