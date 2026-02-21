namespace FreeAiSsd.Shared.Documents;

/// <summary>
/// Identifies which variant of DCS World saved games was found.
/// </summary>
public enum DcsSavedGamesVariant
{
    /// <summary>Stable release: Saved Games/DCS</summary>
    DCS,

    /// <summary>Open beta release: Saved Games/DCS.openbeta</summary>
    DCSOpenBeta,
}

/// <summary>
/// Represents a located DCS World saved games installation.
/// Produced by <see cref="DcsSavedGamesLocator"/>.
/// </summary>
public sealed class DcsInstallation
{
    /// <summary>Absolute path to the DCS saved games folder (e.g. C:\Users\Pilot\Saved Games\DCS).</summary>
    public string SavedGamesPath { get; set; } = string.Empty;

    /// <summary>Whether this is the stable or open-beta variant.</summary>
    public DcsSavedGamesVariant Variant { get; set; }

    /// <summary>True when the path was supplied by the user rather than auto-detected.</summary>
    public bool IsManuallySelected { get; set; }
}

/// <summary>
/// Describes a single physical device folder found inside an aircraft's Config/Input tree.
/// Each device folder contains a diff.lua with that device's bindings.
/// </summary>
public sealed class DcsDeviceInfo
{
    /// <summary>Name of the device sub-folder (e.g. "Saitek X-56 Rhino Stick").</summary>
    public string DeviceFolderName { get; set; } = string.Empty;

    /// <summary>Absolute path to the diff.lua file for this device.</summary>
    public string DiffLuaPath { get; set; } = string.Empty;
}

/// <summary>
/// Describes one aircraft entry found by <see cref="DcsAircraftScanner"/>.
/// </summary>
public sealed class DcsAircraftInfo
{
    /// <summary>Raw DCS folder name (e.g. "FA-18C_hornet").</summary>
    public string FolderName { get; set; } = string.Empty;

    /// <summary>Human-readable aircraft name derived via <see cref="DcsBindingParser.MapAircraftName"/>.</summary>
    public string FriendlyName { get; set; } = string.Empty;

    /// <summary>Device folders found under this aircraft, each containing a diff.lua.</summary>
    public IReadOnlyList<DcsDeviceInfo> Devices { get; set; } = Array.Empty<DcsDeviceInfo>();

    /// <summary>
    /// True when at least one diff.lua for this aircraft is non-empty.
    /// Aircraft with only default/empty files return false.
    /// </summary>
    public bool HasBindings { get; set; }
}

/// <summary>
/// Result for a single aircraft processed by <see cref="DcsBatchProcessor"/>.
/// </summary>
public sealed class DcsBatchItemResult
{
    /// <summary>Raw DCS folder name of the aircraft that was processed.</summary>
    public string AircraftFolderName { get; set; } = string.Empty;

    /// <summary>Human-readable aircraft name.</summary>
    public string AircraftFriendlyName { get; set; } = string.Empty;

    /// <summary>True when the aircraft was successfully parsed and written to disk.</summary>
    public bool Success { get; set; }

    /// <summary>Absolute path of the generated .txt file. Null when <see cref="Success"/> is false.</summary>
    public string? OutputFilePath { get; set; }

    /// <summary>Human-readable failure reason. Null when <see cref="Success"/> is true.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Number of axis bindings included in the output. Zero on failure.</summary>
    public int AxisCount { get; set; }

    /// <summary>Number of button bindings included in the output. Zero on failure.</summary>
    public int ButtonCount { get; set; }
}

/// <summary>
/// Aggregated summary returned by <see cref="DcsBatchProcessor.ProcessAircraftWithSummary"/>.
/// </summary>
public sealed class DcsBatchSummary
{
    /// <summary>Number of aircraft that were successfully processed.</summary>
    public int Succeeded { get; set; }

    /// <summary>Number of aircraft that failed to process.</summary>
    public int Failed { get; set; }

    /// <summary>Per-aircraft results in the order they were processed.</summary>
    public IReadOnlyList<DcsBatchItemResult> Results { get; set; } = Array.Empty<DcsBatchItemResult>();

    /// <summary>
    /// Absolute paths of all successfully generated .txt files.
    /// Pass this list directly to <c>DocumentIngestor.IngestFilesAsync</c> to load
    /// the binding documents into a RAG library.
    /// </summary>
    public IReadOnlyList<string> OutputFilePaths { get; set; } = Array.Empty<string>();
}
