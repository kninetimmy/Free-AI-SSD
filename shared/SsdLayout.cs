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
///   │   │   ├── piper/        → Optional Piper neural TTS binary + voices
///   │   │   ├── tesseract/    → Optional Tesseract OCR binary + tessdata
///   │   │   └── prereqs/      → Bundled prerequisite installers (VC++, .NET)
///   │   └── runner/           → Published Windows Runner WPF application
///   ├── mac/
///   │   └── tools/
///   │       ├── ollama/       → macOS Ollama universal binary
///   │       ├── piper/        → Optional Piper neural TTS binary + voices
///   │       └── tesseract/    → Optional Tesseract OCR binary + tessdata
///   ├── Runner.app/           → macOS Runner application bundle (root-level,
///   │                           launchable directly; the mac-runner-host
///   │                           sidecar ships inside Contents/Resources/)
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
    public const string WindowsPiper = "windows/tools/piper";
    public const string WindowsPiperVoices = "windows/tools/piper/voices";

    /// <summary>Optional Tesseract OCR binary + tessdata (image-text extraction for RAG ingest).</summary>
    public const string WindowsTesseract = "windows/tools/tesseract";

    public const string MacTools = "mac/tools";
    public const string MacOllama = "mac/tools/ollama";
    public const string MacPiper = "mac/tools/piper";
    public const string MacPiperVoices = "mac/tools/piper/voices";

    /// <summary>Optional Tesseract OCR binary + tessdata (image-text extraction for RAG ingest).</summary>
    public const string MacTesseract = "mac/tools/tesseract";

    /// <summary>
    /// macOS Runner bundle, staged at the SSD <b>root</b> (not under mac/)
    /// so the user double-clicks it without digging, mirroring how the
    /// Windows runner is the obvious thing under windows/runner/. The
    /// mac-runner-host sidecar travels inside Runner.app/Contents/
    /// Resources/runner-host/, so there is no separate on-SSD host dir.
    /// Changed from "mac/Runner.app" — a drive prepped by an older release
    /// must be re-prepped to match; the Swift inferSsdRoot() + staging code
    /// move in lockstep with this constant.
    /// </summary>
    public const string MacRunner = "Runner.app";

    public const string Models = "models";
    public const string Blobs = "models/blobs";
    public const string Config = "config";
    public const string Logs = "logs";
    public const string Cache = "cache";
    public const string Docs = "docs";
    public const string DocLibraries = "docs/libraries";
    public const string DocLibrariesRegistry = "docs/libraries.json";
    public const string WhisperModels = "models/whisper";

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
                     WindowsPiper,
                     WindowsPiperVoices,
                     WindowsTesseract,
                     Mac,
                     MacTools,
                     MacOllama,
                     MacPiper,
                     MacPiperVoices,
                     MacTesseract,
                     Models,
                     Blobs,
                     WhisperModels,
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
