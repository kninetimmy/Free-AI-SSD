namespace FreeAiSsd.Companion;

internal sealed class CompanionLog
{
    private readonly string _path;
    private readonly object _sync = new();

    public CompanionLog()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreeAiSsd.Companion");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "log.txt");
    }

    public void Write(string message)
    {
        lock (_sync)
        {
            File.AppendAllText(_path, $"[{DateTime.UtcNow:O}] {message}{Environment.NewLine}");
        }
    }
}
