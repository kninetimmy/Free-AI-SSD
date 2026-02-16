namespace FreeAiSsd.Shared;

public static class SsdLayout
{
    public const string Tools = "tools";
    public const string Ollama = "tools/ollama";
    public const string Models = "models";
    public const string Blobs = "models/blobs";
    public const string Config = "config";
    public const string Logs = "logs";
    public const string Cache = "cache";
    public const string Runner = "runner";

    public static void EnsureStructure(string root)
    {
        foreach (var relative in new[] { Tools, Ollama, Models, Blobs, Config, Logs, Cache, Runner })
        {
            Directory.CreateDirectory(Path.Combine(root, relative));
        }
    }
}
