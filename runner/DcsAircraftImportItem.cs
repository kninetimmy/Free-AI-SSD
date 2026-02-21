using FreeAiSsd.Shared.Documents;

namespace FreeAiSsd.Runner;

/// <summary>
/// UI view-model for a single aircraft row in the Bindings Import aircraft selection list.
/// Wraps <see cref="DcsAircraftInfo"/> and adds checkbox state and display helpers.
/// </summary>
public sealed class DcsAircraftImportItem
{
    public DcsAircraftInfo Aircraft { get; init; } = null!;

    /// <summary>Whether this aircraft is checked for import.</summary>
    public bool IsSelected { get; set; }

    /// <summary>True when a generated .txt file already exists in the library.</summary>
    public bool AlreadyImported { get; init; }

    /// <summary>
    /// Only aircraft with at least one device folder can be imported.
    /// Aircraft with no devices are shown but their checkbox is disabled.
    /// </summary>
    public bool CanImport => Aircraft.Devices.Count > 0;

    /// <summary>Primary label shown next to the checkbox.</summary>
    public string DisplayLabel
    {
        get
        {
            var count = Aircraft.Devices.Count;
            var devices = count == 1 ? "1 device" : $"{count} devices";
            var suffix = Aircraft.HasBindings ? string.Empty : " — no custom bindings";
            return $"{Aircraft.FriendlyName} — {devices}{suffix}";
        }
    }

    /// <summary>Controls the "(already imported)" badge visibility.</summary>
    public System.Windows.Visibility AlreadyImportedVisibility =>
        AlreadyImported ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
}
