namespace FreeAiSsd.Shared;

/// <summary>
/// Defines the canonical directory structure of the portable SSD.
/// All path constants are relative to the SSD root directory.
///
/// SSD Directory Layout:
///   <root>/
///   ├── windows/
///   │   ├── tools/
///   │   │   ├── ollama/       → Windows Ollama binary + trust attestation
///   │   │   └── prereqs/      → Bundled prerequisite installers (VC++, .NET)
///   │   └── runner/           → Published Windows Runner WPF application
///   ├── mac/
///   │   ├── tools/
///   │   │   └── ollama/       → macOS Ollama universal binary
///   │   └── Runner.app/       → macOS Runner application bundle
///   ├── models/
///   │   └── blobs/            → Ollama model blob storage
///   ├── config/               → portable-config.json (or encrypted variant)
///   ├── logs/                 → Runtime log files
///   └── cache/                → Temporary download cache
/// </summary>
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
    public const string Docs = "docs";
    public const string DocLibraries = "docs/libraries";
    public const string DocLibrariesRegistry = "docs/libraries.json";

    /// <summary>Backward-compatible aliases for Windows paths (used by older code).</summary>
    public const string Tools = WindowsTools;
    public const string Ollama = WindowsOllama;
    public const string Prereqs = WindowsPrereqs;
    public const string Runner = WindowsRunner;

    /// <summary>
    /// Creates all directories in the SSD layout structure under the given root.
    /// Safe to call multiple times; existing directories are not affected.
    /// </summary>
    /// <param name="root">The SSD root directory path.</param>
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
                     Cache,
                     Docs,
                     DocLibraries
                 })
        {
            Directory.CreateDirectory(Path.Combine(root, relative));
        }
    }
}
