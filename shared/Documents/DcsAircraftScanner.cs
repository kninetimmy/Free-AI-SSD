using System.Text.RegularExpressions;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.Shared.Documents;

/// <summary>
/// Scans a DCS World saved games folder for aircraft binding files.
/// <para>
/// DCS organises bindings under:
/// <c>Saved Games/DCS/Config/Input/{aircraft}/{device-class}/{device} {GUID}.diff.lua</c>
/// where <c>{device-class}</c> is one of <c>joystick</c>, <c>keyboard</c>, or <c>mouse</c>,
/// and each physical device gets its own <c>"&lt;name&gt; {GUID}.diff.lua"</c> file (the file
/// name carries the device name and its instance GUID — there is no file literally named
/// <c>diff.lua</c> on disk).
/// </para>
/// <para>
/// For each aircraft folder found, the scanner enumerates every <c>*.diff.lua</c> file beneath
/// it (across all device-class folders) and recovers the device name from each file name. The
/// results are ordered by friendly aircraft name and are ready to pass to
/// <see cref="DcsBatchProcessor"/>.
/// </para>
/// </summary>
public static class DcsAircraftScanner
{
    private const string DiffLuaSuffix = ".diff.lua";

    /// <summary>
    /// Folders that live alongside the aircraft folders under <c>Config/Input</c> but are not
    /// aircraft — they must not be reported as importable modules. <c>UiLayer</c> holds the
    /// non-aircraft UI-layer key binds (map, comms menu, etc.).
    /// </summary>
    private static readonly HashSet<string> NonAircraftFolders =
        new(StringComparer.OrdinalIgnoreCase) { "UiLayer" };

    /// <summary>
    /// Recovers the device name from a DCS diff file name by stripping the trailing
    /// <c>" {GUID}.diff.lua"</c>. The device name and a brace-wrapped instance GUID are both
    /// embedded in the file name (e.g. <c>"VKBsim Gladiator EVO R {4C91...0000}.diff.lua"</c>).
    /// </summary>
    private static readonly Regex DeviceFileRx = new(
        @"^(?<name>.*?)\s*\{[0-9A-Fa-f-]+\}" + @"\.diff\.lua$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
            if (NonAircraftFolders.Contains(folderName))
            {
                continue;
            }

            var devices = new List<DcsDeviceInfo>();

            // DCS writes one "<device> {GUID}.diff.lua" per physical device inside the
            // aircraft's joystick/keyboard/mouse folders. Enumerate them all (filtering on
            // the suffix in code rather than via a glob to avoid the platform-specific
            // quirks of a multi-dot search pattern) rather than looking for a literal
            // "diff.lua", which never exists on disk.
            var diffPaths = Directory
                .EnumerateFiles(aircraftDir, "*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(DiffLuaSuffix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);

            foreach (var diffPath in diffPaths)
            {
                devices.Add(new DcsDeviceInfo
                {
                    DeviceFolderName = ExtractDeviceName(Path.GetFileName(diffPath)),
                    DiffLuaPath      = diffPath,
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

    /// <summary>
    /// Recovers the physical device name from a DCS diff file name by stripping the trailing
    /// <c>" {GUID}.diff.lua"</c>. Falls back to the file name minus <c>".diff.lua"</c> when the
    /// GUID token is absent (defensive — real DCS files always carry one).
    /// </summary>
    internal static string ExtractDeviceName(string fileName)
    {
        var match = DeviceFileRx.Match(fileName);
        if (match.Success)
        {
            return match.Groups["name"].Value.Trim();
        }

        return fileName.EndsWith(DiffLuaSuffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^DiffLuaSuffix.Length].Trim()
            : fileName.Trim();
    }
}
