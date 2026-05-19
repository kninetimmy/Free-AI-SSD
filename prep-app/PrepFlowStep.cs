namespace FreeAiSsd.PrepApp;

/// <summary>
/// The prep tool's one-step-at-a-time flow. This is a view-layer state
/// machine only — the shared <see cref="FreeAiSsd.Shared.ViewModels.PrepViewModel"/>
/// has no notion of a step and stays untouched (it is cross-platform and
/// test-covered). The Windows runner of this enum mirrors the Mac
/// <c>PrepFlowStep</c> screen list (see
/// <c>docs/mac-ui-reference/RECONSTRUCTION.md</c> §3) where a Windows
/// equivalent exists; Mac's transient progress screens (Formatting /
/// Staging / Readiness) collapse into the persistent log panel here, and
/// the Windows-only Profile / Format-setup decisions get their own steps
/// per the owner's "dedicated steps, grouped by decision" call.
/// </summary>
/// <remarks>
/// "Manage models" is not a distinct enum case: it is <see cref="Models"/>
/// entered with the manage flag set (reached off the linear path from the
/// already-configured-drive banner on <see cref="Drive"/>). The numbered
/// steps for the header counter are Profile…Done (6 total); Welcome is
/// unnumbered, mirroring the Mac labels.
/// </remarks>
public enum PrepFlowStep
{
    /// <summary>A1 — intro/orientation. No Windows counterpart today.</summary>
    Welcome,

    /// <summary>Windows-only — choose the default Runner profile.</summary>
    Profile,

    /// <summary>A2 — pick the target drive; hosts the configured-drive banner.</summary>
    Drive,

    /// <summary>Windows-only — prep targets, volume label, encryption, VR companion.</summary>
    FormatSetup,

    /// <summary>A-MP / A7 — the model picker (also the body of "Manage models").</summary>
    Models,

    /// <summary>A8 — finalize + readiness checks.</summary>
    Finalize,

    /// <summary>A10 — success / next steps.</summary>
    Done,
}
