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
    /// Attempts to relaunch the current app elevated, forwarding the given
    /// command-line args to the new instance. On success, the current
    /// process should be shut down by the implementation (the new elevated
    /// instance takes over). Returns false if the user declined UAC; throws
    /// on genuine launch failures.
    /// </summary>
    /// <param name="forwardArgs">
    /// Args to pass to the elevated instance. Forwarded via
    /// <c>ProcessStartInfo.ArgumentList</c> so each value is quoted safely
    /// — never string-concatenated. Pass null / empty for no args.
    /// </param>
    bool TryRelaunchElevated(IEnumerable<string>? forwardArgs = null);
}
