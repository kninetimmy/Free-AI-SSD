namespace FreeAiSsd.Shared.Models;

/// <summary>
/// Shared defaults for the VR companion so the prep seeder, the companion
/// runtime nudge, and the Settings placeholder all agree on one value.
/// </summary>
public static class CompanionDefaults
{
    /// <summary>
    /// Keyboard push-to-talk binding seeded onto a freshly prepped FlightSim
    /// drive. Keeps <see cref="CompanionConfig.IsComplete"/> true on first
    /// launch (no forced Settings dialog); flight-sim users rebind to a HOTAS
    /// button afterward. Matches the <c>key:&lt;Key&gt;</c> format parsed by
    /// the companion's keyboard PTT hook.
    /// </summary>
    public const string DefaultPttBinding = "key:F8";
}
