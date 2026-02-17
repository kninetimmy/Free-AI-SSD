namespace FreeAiSsd.Shared;

public static class SsdLayout
{
    public const string Windows = "windows";
    public const string Mac = "mac";

    public const string WindowsTools = "windows/tools";
    public const string WindowsOllama = "windows/tools/ollama";
    public const string WindowsPrereqs = "windows/tools/prereqs";
    public const string WindowsRunner = "windows/runner";

    public const string MacTools = "mac/tools";
    public const string MacOllama = "mac/tools/ollama";
    public const string MacRunner = "mac/Runner.app";

    public const string Models = "models";
    public const string Blobs = "models/blobs";
    public const string Config = "config";
    public const string Logs = "logs";
    public const string Cache = "cache";

    // Backwards-compatible aliases for Windows paths.
    public const string Tools = WindowsTools;
    public const string Ollama = WindowsOllama;
    public const string Prereqs = WindowsPrereqs;
    public const string Runner = WindowsRunner;

    public static void EnsureStructure(string root)
    {
        foreach (var relative in new[]
                 {
                     Windows,
                     WindowsTools,
                     WindowsOllama,
                     WindowsPrereqs,
                     WindowsRunner,
                     Mac,
                     MacTools,
                     MacOllama,
                     Models,
                     Blobs,
                     Config,
                     Logs,
                     Cache
                 })
        {
            Directory.CreateDirectory(Path.Combine(root, relative));
        }
    }
}
