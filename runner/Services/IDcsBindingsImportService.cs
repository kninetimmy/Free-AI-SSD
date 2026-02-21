using FreeAiSsd.Shared.Documents;

namespace FreeAiSsd.Runner.Services;

/// <summary>
/// Wraps DCS bindings detection, scanning, and batch-import for the Runner UI.
/// All business logic lives here; UI code only calls into this service.
/// </summary>
public interface IDcsBindingsImportService
{
    event Action<string>? LogMessage;

    /// <summary>
    /// Attempts to auto-detect the DCS saved games folder.
    /// Returns null when DCS is not found in the standard location.
    /// </summary>
    DcsInstallation? DetectDcsInstallation();

    /// <summary>
    /// Wraps a manually supplied path as a <see cref="DcsInstallation"/>.
    /// </summary>
    DcsInstallation WithManualPath(string path);

    /// <summary>
    /// Scans the given DCS installation for aircraft with binding files.
    /// </summary>
    IReadOnlyList<DcsAircraftInfo> ScanAircraft(DcsInstallation installation);

    /// <summary>
    /// Returns the folder names of aircraft whose generated .txt files already exist
    /// in the specified library's files directory.
    /// </summary>
    IReadOnlySet<string> GetAlreadyImportedFolderNames(
        IEnumerable<DcsAircraftInfo> aircraft,
        string ssdRoot,
        string libraryId);

    /// <summary>
    /// Runs the batch processor for the selected aircraft, writing one .txt file per
    /// aircraft into the library's files directory. Progress callback receives the
    /// friendly name of the aircraft currently being processed.
    /// </summary>
    Task<DcsBatchSummary> ImportBindingsAsync(
        IEnumerable<DcsAircraftInfo> selectedAircraft,
        string ssdRoot,
        string libraryId,
        Action<string>? onProgress = null,
        CancellationToken ct = default);
}
