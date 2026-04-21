using FreeAiSsd.Shared.Mvvm;

namespace FreeAiSsd.Shared.Models;

public sealed record ReadinessItem(string Check, bool Passed, string Result)
{
    public static ReadinessItem Pass(string check) => new(check, true, "OK");
    public static ReadinessItem Fail(string check, string reason) => new(check, false, reason);
    public static ReadinessItem Warn(string check, string reason) => new(check, true, reason);
}

public sealed class ModelGridRow(
    string name, string status, string source, string sizingWarning,
    string sizeDisplay, string shaPreview, string lastVerifiedDisplay, bool isOnDiskOnly,
    bool isPresentOnDrive, string tier = "Custom", string bestAt = "")
    : BaseViewModel
{
    private bool _isSelected;

    /// <summary>Whether this row is checked for bulk actions in the merged grid.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string Name { get; } = name;
    public string Status { get; } = status;
    public string Source { get; } = source;
    public string SizingWarning { get; } = sizingWarning;
    public string SizeDisplay { get; } = sizeDisplay;
    public string ShaPreview { get; } = shaPreview;
    public string LastVerifiedDisplay { get; } = lastVerifiedDisplay;
    public bool IsOnDiskOnly { get; } = isOnDiskOnly;
    public bool IsPresentOnDrive { get; } = isPresentOnDrive;
    public string Tier { get; } = tier;
    public string BestAt { get; } = bestAt;
}

/// <summary>
/// Lightweight shared projection of a starter-catalog entry. The PrepApp
/// loads the full catalog from JSON and hands these entries to the VM so
/// the merged Models grid can surface recommended picks alongside user-
/// added and on-disk rows.
/// </summary>
public sealed record StarterCatalogEntry(
    string Tag,
    string SizeTier,
    string BestAt);

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
