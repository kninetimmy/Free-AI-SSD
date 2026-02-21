using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.Shared.Documents;

/// <summary>
/// Locates the DCS World saved games folder on the host machine.
/// <para>
/// DCS stores user profiles and input bindings under the Windows "Saved Games" shell folder,
/// with a sub-directory named either <c>DCS</c> (stable) or <c>DCS.openbeta</c> (open beta).
/// Both paths are checked in that order; the first one found is returned.
/// </para>
/// <para>
/// If auto-detection fails (DCS not installed, or running on a non-Windows host during
/// development/testing), call <see cref="WithManualPath"/> with a path supplied by the user.
/// </para>
/// </summary>
public static class DcsSavedGamesLocator
{
    // Checked in priority order: stable before open-beta.
    private static readonly (string SubFolder, DcsSavedGamesVariant Variant)[] Candidates =
    [
        ("DCS",          DcsSavedGamesVariant.DCS),
        ("DCS.openbeta", DcsSavedGamesVariant.DCSOpenBeta),
    ];

    /// <summary>
    /// Attempts to auto-detect a DCS saved games folder under the current user's profile.
    /// Returns <see langword="null"/> when neither known location exists (DCS not installed,
    /// or the process is running on a non-Windows platform without a Saved Games folder).
    /// </summary>
    /// <param name="log">Optional log service; receives an info message for the path found.</param>
    public static DcsInstallation? TryDetect(ILogService? log = null)
    {
        // Environment.SpecialFolder.UserProfile works on Windows, macOS and Linux.
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(userProfile))
        {
            log?.Info("[DcsSavedGamesLocator] UserProfile is empty; cannot auto-detect DCS folder.");
            return null;
        }

        var savedGamesRoot = Path.Combine(userProfile, "Saved Games");

        foreach (var (subFolder, variant) in Candidates)
        {
            var candidatePath = Path.Combine(savedGamesRoot, subFolder);
            if (Directory.Exists(candidatePath))
            {
                log?.Info($"[DcsSavedGamesLocator] Found DCS saved games ({subFolder}): {candidatePath}");
                return new DcsInstallation
                {
                    SavedGamesPath    = candidatePath,
                    Variant           = variant,
                    IsManuallySelected = false,
                };
            }
        }

        log?.Info("[DcsSavedGamesLocator] DCS saved games folder not found in standard locations.");
        return null;
    }

    /// <summary>
    /// Wraps a user-supplied path as a <see cref="DcsInstallation"/>.
    /// The caller is responsible for verifying the path contains a valid DCS profile before
    /// proceeding to scan; this method only validates that the argument is non-empty.
    /// </summary>
    /// <param name="path">Absolute path to the DCS saved games folder chosen by the user.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="path"/> is null or whitespace.</exception>
    public static DcsInstallation WithManualPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Saved games path must not be empty.", nameof(path));
        }

        return new DcsInstallation
        {
            SavedGamesPath    = path,
            Variant           = DcsSavedGamesVariant.DCS,
            IsManuallySelected = true,
        };
    }

    /// <summary>
    /// Returns the absolute path to the <c>Config/Input</c> directory within the
    /// supplied installation. This is the root directory scanned by <see cref="DcsAircraftScanner"/>.
    /// </summary>
    public static string GetConfigInputPath(DcsInstallation installation) =>
        Path.Combine(installation.SavedGamesPath, "Config", "Input");
}
