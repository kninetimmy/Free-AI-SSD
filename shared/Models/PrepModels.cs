namespace FreeAiSsd.Shared.Models;

public sealed record ReadinessItem(string Check, bool Passed, string Result)
{
    public static ReadinessItem Pass(string check) => new(check, true, "OK");
    public static ReadinessItem Fail(string check, string reason) => new(check, false, reason);
    public static ReadinessItem Warn(string check, string reason) => new(check, true, reason);
}

public sealed class ModelGridRow(
    string name, string status, string source, string sizingWarning,
    string sizeDisplay, string shaPreview, string lastVerifiedDisplay, bool isOnDiskOnly)
{
    /// <summary>Whether this model is checked for pull/install operations. Auto-ticked for configured models.</summary>
    public bool IsSelected { get; set; } = !isOnDiskOnly;
    public string Name { get; } = name;
    public string Status { get; } = status;
    public string Source { get; } = source;
    public string SizingWarning { get; } = sizingWarning;
    public string SizeDisplay { get; } = sizeDisplay;
    public string ShaPreview { get; } = shaPreview;
    public string LastVerifiedDisplay { get; } = lastVerifiedDisplay;
    public bool IsOnDiskOnly { get; } = isOnDiskOnly;
}

public sealed class StarterModelRow(
    string tag, string @params, string sizeTier,
    string description, string useCasesDisplay, string sizingWarning)
{
    public bool IsSelected { get; set; }
    public string Tag { get; } = tag;
    public string Params { get; } = @params;
    public string SizeTier { get; } = sizeTier;
    public string Description { get; } = description;
    public string UseCasesDisplay { get; } = useCasesDisplay;
    public string SizingWarning { get; set; } = sizingWarning;
}

[Flags]
public enum PrepTargets
{
    None = 0,
    Windows = 1,
    Mac = 2
}

public enum ModelRemoveChoice
{
    Cancel,
    ConfigOnly,
    DeleteFromDisk
}
