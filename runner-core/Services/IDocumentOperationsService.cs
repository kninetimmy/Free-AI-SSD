using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Documents;

namespace FreeAiSsd.Runner.Services;

/// <summary>
/// Snapshot of library state for the UI to display.
/// </summary>
public record LibraryDisplayInfo(
    List<string> Options,
    int SelectedIndex,
    DocumentLibraryManifest? ActiveLibrary);

public interface IDocumentOperationsService
{
    event Action<string>? LogMessage;

    /// <summary>
    /// Builds the display info for the library combo box (option list, selected index, active manifest).
    /// </summary>
    LibraryDisplayInfo GetLibraryDisplayInfo(PortableConfig config);

    /// <summary>
    /// Returns the library ID at the given combo index, or null if "None" is selected.
    /// </summary>
    string? GetLibraryIdByIndex(int selectedIndex);

    /// <summary>
    /// Sets the active library in both config and registry. Returns the loaded manifest (or null).
    /// </summary>
    Task<DocumentLibraryManifest?> SetActiveLibraryAsync(PortableConfig config, string ssdRoot, string? libraryId);

    /// <summary>
    /// Creates a new document library and updates config to make it active.
    /// </summary>
    Task<DocumentLibraryManifest> CreateLibraryAsync(PortableConfig config, string ssdRoot, string name);

    /// <summary>
    /// Ingests the given files into the active library (copies, chunks, embeds, indexes).
    /// </summary>
    Task IngestFilesAsync(DocumentLibraryManifest library, string[] filePaths, string host,
        PortableConfig config, Action<IndexingProgress>? progress = null);

    /// <summary>
    /// Adds a watched folder to the library manifest. Returns true if newly added.
    /// </summary>
    Task<bool> AddWatchedFolderAsync(DocumentLibraryManifest library, string folderPath);

    /// <summary>
    /// Re-ingests all files found in the library's watched folders.
    /// </summary>
    Task SweepFoldersAsync(DocumentLibraryManifest library, string host,
        PortableConfig config, Action<IndexingProgress>? progress = null);

    /// <summary>
    /// Drops and rebuilds the vector index from existing source files.
    /// </summary>
    Task RebuildIndexAsync(DocumentLibraryManifest library, string host,
        PortableConfig config, Action<IndexingProgress>? progress = null);

    /// <summary>
    /// Removes a file from the library (deletes stored copy, removes from index and manifest).
    /// </summary>
    Task RemoveFileAsync(DocumentLibraryManifest library, string storedRelativePath);

    /// <summary>
    /// Persists the portable config to disk.
    /// </summary>
    Task SaveConfigAsync(PortableConfig config, string ssdRoot);
}
