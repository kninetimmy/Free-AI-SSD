namespace FreeAiSsd.Shared;

/// <summary>
/// Whether a previously-configured SSD is usable, from a cheap presence-only
/// inspection (no decrypt, no SHA hashing). Drives the PrepApp launch-time
/// "resume setup" prompt (#2) — distinct from the full <c>ReadinessService</c>
/// run, which hashes model blobs and only happens once the user reaches
/// Finalize.
/// </summary>
public enum SsdSetupCompletionState
{
    /// <summary>
    /// Not our drive, or fully staged with usable models present — no prompt.
    /// </summary>
    Complete,

    /// <summary>
    /// Config + models present, but no platform runtime is staged: neither the
    /// Windows runner (<c>windows/runner/FreeAiSsd.Runner.exe</c>) nor the macOS
    /// bundle (<c>Runner.app</c>) exists. Finalize was never run, or was
    /// interrupted before staging. Resume routes to the Finalize step.
    /// </summary>
    RuntimeNotStaged,

    /// <summary>
    /// Config present but no usable models on disk — none discovered, or a
    /// discovered model's blob is missing (an interrupted / partial pull).
    /// Resume routes to the Models step so the user re-pulls, then finishes.
    /// </summary>
    ModelsMissingOrIncomplete,
}

/// <summary>
/// Where the PrepApp should land the user when they accept the resume-setup
/// prompt (#2). The VM raises this; the view maps it to its step machine (the
/// VM has no notion of <c>PrepFlowStep</c>, which lives in the view layer).
/// </summary>
public enum ResumeSetupTarget
{
    /// <summary>Finish staging the runtime — go to the Finalize step.</summary>
    Finalize,

    /// <summary>Re-pull missing / partial models — go to the Models step.</summary>
    Models,
}

/// <summary>
/// Result of <c>SsdSetupCompletionProbe.Inspect</c>. <see cref="Detail"/> is a
/// short human-readable explanation suitable for the resume-prompt body.
/// </summary>
public sealed record SsdSetupCompletionResult(
    SsdSetupCompletionState State,
    string Detail)
{
    /// <summary>True when nothing needs resuming (don't prompt).</summary>
    public bool IsComplete => State == SsdSetupCompletionState.Complete;

    /// <summary>Shared "nothing to do" instance.</summary>
    public static SsdSetupCompletionResult Complete { get; } =
        new(SsdSetupCompletionState.Complete, string.Empty);
}
