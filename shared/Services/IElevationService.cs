namespace FreeAiSsd.Shared.Services;

/// <summary>
/// Abstraction over Windows UAC elevation so the view model can stay
/// platform-neutral and unit-testable.
/// </summary>
public interface IElevationService
{
    /// <summary>True when the current process is running elevated.</summary>
    bool IsElevated();

    /// <summary>
    /// Attempts to relaunch the current app elevated. On success, the current
    /// process should be shut down by the implementation (the new elevated
    /// instance takes over). Returns false if the user declined UAC; throws
    /// on genuine launch failures.
    /// </summary>
    bool TryRelaunchElevated();
}
