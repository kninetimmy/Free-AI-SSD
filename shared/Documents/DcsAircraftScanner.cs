using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.Shared.Documents;

/// <summary>
/// Scans a DCS World saved games folder for aircraft binding files.
/// <para>
/// DCS organises bindings under:
/// <c>Saved Games/DCS/Config/Input/{aircraft}/{device-folder}/diff.lua</c>
/// </para>
/// <para>
/// For each aircraft folder found, the scanner enumerates device sub-folders that
/// contain a <c>diff.lua</c> file. The results are ordered by friendly aircraft name
/// and are ready to pass to <see cref="DcsBatchProcessor"/>.
/// </para>
/// </summary>
public static class DcsAircraftScanner
{
    private const string DiffLuaFileName = "diff.lua";

    /// <summary>
    /// Scans the <c>Config/Input</c> directory within <paramref name="savedGamesPath"/>
    /// and returns one <see cref="DcsAircraftInfo"/> per aircraft folder found.
    /// </summary>
    /// <param name="savedGamesPath">
    /// Absolute path to the DCS saved games folder (i.e. the value from
    /// <see cref="DcsInstallation.SavedGamesPath"/>).
    /// </param>
    /// <param name="log">Optional log service for informational and warning messages.</param>
    /// <returns>
    /// A read-only list sorted ascending by <see cref="DcsAircraftInfo.FriendlyName"/>.
    /// Returns an empty list (never null) when the directory does not exist or contains no aircraft.
    /// </returns>
    public static IReadOnlyList<DcsAircraftInfo> ScanAircraft(
        string savedGamesPath,
        ILogService? log = null)
    {
        var configInputPath = Path.Combine(savedGamesPath, "Config", "Input");

        if (!Directory.Exists(configInputPath))
        {
            log?.Info($"[DcsAircraftScanner] Config/Input not found at: {configInputPath}");
            return Array.Empty<DcsAircraftInfo>();
        }

        var results = new List<DcsAircraftInfo>();

        foreach (var aircraftDir in Directory.EnumerateDirectories(configInputPath))
        {
            var folderName = Path.GetFileName(aircraftDir);
            var devices    = new List<DcsDeviceInfo>();

            foreach (var deviceDir in Directory.EnumerateDirectories(aircraftDir))
            {
                var diffLuaPath = Path.Combine(deviceDir, DiffLuaFileName);
                if (!File.Exists(diffLuaPath))
                {
                    continue;
                }

                devices.Add(new DcsDeviceInfo
                {
                    DeviceFolderName = Path.GetFileName(deviceDir),
                    DiffLuaPath      = diffLuaPath,
                });
            }

            // HasBindings: at least one diff.lua is non-empty (contains user customisations).
            var hasBindings = devices.Any(d => new FileInfo(d.DiffLuaPath).Length > 0);

            results.Add(new DcsAircraftInfo
            {
                FolderName   = folderName,
                FriendlyName = DcsBindingParser.MapAircraftName(folderName),
                Devices      = devices.AsReadOnly(),
                HasBindings  = hasBindings,
            });
        }

        log?.Info($"[DcsAircraftScanner] Found {results.Count} aircraft under {configInputPath}.");

        return results
            .OrderBy(a => a.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }
}
