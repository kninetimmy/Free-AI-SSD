using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.Shared.Documents;

/// <summary>
/// Batch-processes a selection of DCS aircraft and writes one plain-text binding
/// document per aircraft, ready for ingestion by the RAG document library.
/// <para>
/// Typical usage:
/// <code>
/// var processor = new DcsBatchProcessor(log);
/// var outputDir = DcsBatchProcessor.GetDefaultOutputDirectory(libraryManager, libraryId);
/// var summary   = processor.ProcessAircraftWithSummary(selected, outputDir);
///
/// // Hand the generated paths to the document ingestor:
/// await ingestor.IngestFilesAsync(manifest, summary.OutputFilePaths, host, config);
/// </code>
/// </para>
/// </summary>
public sealed class DcsBatchProcessor
{
    private readonly ILogService? _log;

    public DcsBatchProcessor(ILogService? log = null)
    {
        _log = log;
    }

    // -------------------------------------------------------------------------
    // Output directory helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the default directory into which generated binding files should be written.
    /// This is the <c>files</c> sub-folder of the specified document library, so the
    /// generated <c>.txt</c> files are co-located with other library documents and can be
    /// passed directly to <c>DocumentIngestor.IngestFilesAsync</c> as source paths.
    /// </summary>
    /// <param name="libraryManager">The document library manager for the active SSD.</param>
    /// <param name="libraryId">ID of the target document library.</param>
    public static string GetDefaultOutputDirectory(DocumentLibraryManager libraryManager, string libraryId) =>
        libraryManager.GetFilesPath(libraryId);

    // -------------------------------------------------------------------------
    // Core batch processing
    // -------------------------------------------------------------------------

    /// <summary>
    /// Processes each aircraft in <paramref name="selectedAircraft"/>: merges all device
    /// diff.lua files via <see cref="DcsBindingParser"/>, formats the result as plain text,
    /// and writes one <c>.txt</c> file per aircraft into <paramref name="outputDirectory"/>.
    /// </summary>
    /// <param name="selectedAircraft">Aircraft to process (from <see cref="DcsAircraftScanner"/>).</param>
    /// <param name="outputDirectory">Directory where generated files are written. Created if absent.</param>
    /// <param name="cancellationToken">Cooperative cancellation between aircraft.</param>
    /// <returns>
    /// One <see cref="DcsBatchItemResult"/> per aircraft, in the order they were supplied.
    /// </returns>
    public IReadOnlyList<DcsBatchItemResult> ProcessAircraft(
        IEnumerable<DcsAircraftInfo> selectedAircraft,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);

        var results = new List<DcsBatchItemResult>();

        foreach (var aircraft in selectedAircraft)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(ProcessSingleAircraft(aircraft, outputDirectory));
        }

        return results.AsReadOnly();
    }

    /// <summary>
    /// Convenience wrapper that calls <see cref="ProcessAircraft"/> and wraps the results
    /// in a <see cref="DcsBatchSummary"/> with counts and the list of generated file paths.
    /// </summary>
    /// <param name="selectedAircraft">Aircraft to process.</param>
    /// <param name="outputDirectory">Directory where generated files are written. Created if absent.</param>
    /// <param name="cancellationToken">Cooperative cancellation between aircraft.</param>
    public DcsBatchSummary ProcessAircraftWithSummary(
        IEnumerable<DcsAircraftInfo> selectedAircraft,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var results = ProcessAircraft(selectedAircraft, outputDirectory, cancellationToken);

        var outputPaths = results
            .Where(r => r.Success && r.OutputFilePath is not null)
            .Select(r => r.OutputFilePath!)
            .ToList()
            .AsReadOnly();

        return new DcsBatchSummary
        {
            Succeeded       = results.Count(r => r.Success),
            Failed          = results.Count(r => !r.Success),
            Results         = results,
            OutputFilePaths = outputPaths,
        };
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private DcsBatchItemResult ProcessSingleAircraft(DcsAircraftInfo aircraft, string outputDirectory)
    {
        try
        {
            var deviceInputs = aircraft.Devices
                .Select(d => (d.DiffLuaPath, d.DeviceFolderName))
                .ToList();

            var merged = DcsBindingParser.MergeDevices(aircraft.FolderName, deviceInputs, _log);
            var text   = DcsBindingParser.FormatOutput(merged);

            var safeName    = SanitizeFileName(aircraft.FolderName);
            var outputPath  = Path.Combine(outputDirectory, $"{safeName}.txt");

            File.WriteAllText(outputPath, text, Encoding.UTF8);

            _log?.Info(
                $"[DcsBatchProcessor] Written: {outputPath} " +
                $"({merged.Axes.Count} axes, {merged.Buttons.Count} buttons)");

            return new DcsBatchItemResult
            {
                AircraftFolderName  = aircraft.FolderName,
                AircraftFriendlyName = aircraft.FriendlyName,
                Success             = true,
                OutputFilePath      = outputPath,
                AxisCount           = merged.Axes.Count,
                ButtonCount         = merged.Buttons.Count,
            };
        }
        catch (Exception ex)
        {
            _log?.Error(
                $"[DcsBatchProcessor] Failed to process '{aircraft.FolderName}': {ex.Message}");

            return new DcsBatchItemResult
            {
                AircraftFolderName  = aircraft.FolderName,
                AircraftFriendlyName = aircraft.FriendlyName,
                Success             = false,
                FailureReason       = ex.Message,
            };
        }
    }

    /// <summary>
    /// Strips characters that are illegal in file names on both Windows and POSIX systems.
    /// Replaces runs of invalid characters with a single underscore.
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();

        // Split on any invalid character and rejoin with underscore
        var parts = name.Split(invalid, StringSplitOptions.RemoveEmptyEntries);
        return string.Join("_", parts);
    }
}
