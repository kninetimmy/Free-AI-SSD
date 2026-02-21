namespace FreeAiSsd.Shared.Documents;

/// <summary>
/// Axis filter settings extracted from a DCS diff.lua axis entry.
/// These correspond to the values the user sets in the DCS Axis Tune dialog.
/// </summary>
public sealed class DcsAxisFilter
{
    /// <summary>User-configured curvature value (0 = linear, positive = S-curve).</summary>
    public double Curvature { get; set; }

    /// <summary>Center deadzone (0–1 range, e.g. 0.05 = 5%).</summary>
    public double Deadzone { get; set; }

    /// <summary>Whether the axis input is inverted.</summary>
    public bool Invert { get; set; }

    /// <summary>Input saturation on the X (physical) axis (0–1).</summary>
    public double SaturationX { get; set; } = 1.0;

    /// <summary>Output saturation on the Y (game) axis (0–1).</summary>
    public double SaturationY { get; set; } = 1.0;
}

/// <summary>
/// A single analog axis binding extracted from a DCS diff.lua file.
/// </summary>
public sealed class DcsAxisBinding
{
    /// <summary>Human-readable name as stored in the diff (e.g. "Pitch", "Roll").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Physical device this binding came from (e.g. "Saitek X-56 Rhino Stick").</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>DCS key code assigned to this axis (e.g. "JOY_Y", "JOY_RZ").</summary>
    public string KeyCode { get; set; } = string.Empty;

    /// <summary>Inferred display category (e.g. "Flight Controls"). Empty if unknown.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Axis filter/curve settings, if present in the diff.</summary>
    public DcsAxisFilter? Filter { get; set; }
}

/// <summary>
/// A single button/key binding extracted from a DCS diff.lua file.
/// </summary>
public sealed class DcsButtonBinding
{
    /// <summary>Human-readable name as stored in the diff (e.g. "Gun Trigger - SECOND DETENT").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Physical device this binding came from (e.g. "Saitek X-56 Rhino Stick").</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>DCS key code assigned to this button (e.g. "JOY_BTN1", "JOY_BTN9").</summary>
    public string KeyCode { get; set; } = string.Empty;

    /// <summary>Inferred display category (e.g. "Weapons", "Navigation / Sensors"). Empty if unknown.</summary>
    public string Category { get; set; } = string.Empty;
}

/// <summary>
/// All bindings parsed from a single diff.lua file, representing one physical input device.
/// </summary>
public sealed class DcsDeviceBindings
{
    /// <summary>Friendly display name of the device (e.g. "Saitek X-56 Rhino Stick").</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>Absolute path to the source diff.lua file.</summary>
    public string SourceFilePath { get; set; } = string.Empty;

    /// <summary>Axis bindings parsed from the axisDiffs section.</summary>
    public List<DcsAxisBinding> Axes { get; set; } = new();

    /// <summary>Button bindings parsed from the keyDiffs section.</summary>
    public List<DcsButtonBinding> Buttons { get; set; } = new();
}

/// <summary>
/// Merged bindings for all physical devices belonging to one aircraft.
/// This is the final output of <see cref="DcsBindingParser.MergeDevices"/>.
/// </summary>
public sealed class DcsAircraftBindings
{
    /// <summary>Readable aircraft name (e.g. "F/A-18C Hornet").</summary>
    public string AircraftName { get; set; } = string.Empty;

    /// <summary>UTC timestamp of when this binding set was generated.</summary>
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Ordered list of device names whose diff files were merged.</summary>
    public List<string> SourceDevices { get; set; } = new();

    /// <summary>All axis bindings across all devices, in source order.</summary>
    public List<DcsAxisBinding> Axes { get; set; } = new();

    /// <summary>All button bindings across all devices, in source order.</summary>
    public List<DcsButtonBinding> Buttons { get; set; } = new();
}
