namespace FreeAiSsd.Runner;

/// <summary>
/// The runner's top-level tab selection — Mac-parity initiative #44
/// stage 1. Matches the four tabs in <c>MainWindow.xaml</c>
/// (Chat / Voice / Sim / System).
/// </summary>
/// <remarks>
/// Implemented as a Grid-based "tab" (overlapping grids, one
/// <c>Visibility=Visible</c> at a time) rather than a WPF
/// <c>TabControl</c>: the runner code-behind holds many direct
/// <c>x:Name</c> references across cards, and <c>TabControl</c>
/// destroys/recreates its content on switch which would invalidate
/// those references. Same pattern as the prep-app's
/// <see cref="PrepFlowStep"/> view-layer state machine.
/// </remarks>
public enum RunnerTab
{
    /// <summary>Chat: model + prompt + response + RAG library + log.</summary>
    Chat,

    /// <summary>Voice: Push-to-Talk / HOTAS input (Flight Sim profile only).</summary>
    Voice,

    /// <summary>Sim: DCS controller binding import (Flight Sim profile only).</summary>
    Sim,

    /// <summary>System: hardware compatibility, prerequisites, dependencies.</summary>
    System,
}
