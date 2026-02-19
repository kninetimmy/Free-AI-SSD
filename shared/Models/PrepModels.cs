namespace FreeAiSsd.Shared.Models;

public sealed record ReadinessItem(string Check, bool Passed, string Result)
{
    public static ReadinessItem Pass(string check) => new(check, true, "OK");
    public static ReadinessItem Fail(string check, string reason) => new(check, false, reason);
    public static ReadinessItem Warn(string check, string reason) => new(check, true, reason);
}

public sealed record ModelGridRow(
    string Name, string Status, string Source, string SizingWarning,
    string SizeDisplay, string ShaPreview, string LastVerifiedDisplay, bool IsOnDiskOnly);

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
