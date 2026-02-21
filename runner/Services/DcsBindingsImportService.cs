using FreeAiSsd.Shared.Documents;

namespace FreeAiSsd.Runner.Services;

/// <summary>
/// Implements <see cref="IDcsBindingsImportService"/> by delegating to the shared-library
/// DCS classes: <see cref="DcsSavedGamesLocator"/>, <see cref="DcsAircraftScanner"/>,
/// and <see cref="DcsBatchProcessor"/>.
/// </summary>
public sealed class DcsBindingsImportService : IDcsBindingsImportService
{
    private readonly DocumentLibraryManager _libraryManager;

    public DcsBindingsImportService(DocumentLibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public event Action<string>? LogMessage;

    public DcsInstallation? DetectDcsInstallation()
    {
        var result = DcsSavedGamesLocator.TryDetect();
        if (result is not null)
            Log($"DCS detected ({result.Variant}): {result.SavedGamesPath}");
        else
            Log("DCS saved games folder not found in standard location.");
        return result;
    }

    public DcsInstallation WithManualPath(string path) =>
        DcsSavedGamesLocator.WithManualPath(path);

    public IReadOnlyList<DcsAircraftInfo> ScanAircraft(DcsInstallation installation)
    {
        var aircraft = DcsAircraftScanner.ScanAircraft(installation.SavedGamesPath);
        Log($"Scan complete: {aircraft.Count} aircraft folder(s) found.");
        return aircraft;
    }

    public IReadOnlySet<string> GetAlreadyImportedFolderNames(
        IEnumerable<DcsAircraftInfo> aircraft,
        string ssdRoot,
        string libraryId)
    {
        var filesPath = _libraryManager.GetFilesPath(libraryId);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in aircraft)
        {
            var safe = SanitizeFileName(a.FolderName);
            if (File.Exists(Path.Combine(filesPath, $"{safe}.txt")))
                result.Add(a.FolderName);
        }

        return result;
    }

    public Task<DcsBatchSummary> ImportBindingsAsync(
        IEnumerable<DcsAircraftInfo> selectedAircraft,
        string ssdRoot,
        string libraryId,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        var outputDir = DcsBatchProcessor.GetDefaultOutputDirectory(_libraryManager, libraryId);
        Log($"Writing binding files to: {outputDir}");

        var aircraftList = selectedAircraft.ToList();

        // Run synchronous file I/O on a thread-pool thread so the UI stays responsive.
        return Task.Run(() =>
        {
            var processor = new DcsBatchProcessor();
            var results = new List<DcsBatchItemResult>();

            foreach (var aircraft in aircraftList)
            {
                ct.ThrowIfCancellationRequested();
                onProgress?.Invoke(aircraft.FriendlyName);
                var batchResults = processor.ProcessAircraft(new[] { aircraft }, outputDir, ct);
                results.AddRange(batchResults);
            }

            var succeeded = results.Count(r => r.Success);
            var failed = results.Count(r => !r.Success);
            Log($"Import complete: {succeeded} succeeded, {failed} failed.");

            var outputPaths = results
                .Where(r => r.Success && r.OutputFilePath is not null)
                .Select(r => r.OutputFilePath!)
                .ToList()
                .AsReadOnly();

            return new DcsBatchSummary
            {
                Succeeded = succeeded,
                Failed = failed,
                Results = results.AsReadOnly(),
                OutputFilePaths = outputPaths,
            };
        }, ct);
    }

    private void Log(string message) => LogMessage?.Invoke(message);

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var parts = name.Split(invalid, StringSplitOptions.RemoveEmptyEntries);
        return string.Join("_", parts);
    }
}
