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
    bool isPresentOnDrive, string tier = "Custom", string bestAt = "",
    long? pullCount = null,
    IReadOnlyList<string>? capabilities = null,
    double? parametersBillion = null,
    DateTimeOffset? lastUpdated = null,
    ModelSource sourceKind = ModelSource.Ollama)
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
    /// <summary>
    /// F2a: approximate pull count from ollama.com/library. Null for
    /// rows whose tag isn't in the live catalog (custom, on-disk-only,
    /// or anything from the bundled list before Refresh). Drives the
    /// "Most popular" filter cap on the merged grid.
    /// </summary>
    public long? PullCount { get; } = pullCount;
    /// <summary>
    /// C4: capability tags from ollama.com/library (e.g. "tools",
    /// "vision", "thinking", "audio"). Empty/null when not from the
    /// live catalog — capability filter treats those as pass-through
    /// so configured + on-disk rows stay visible while the user
    /// narrows the recommended list.
    /// </summary>
    public IReadOnlyList<string> Capabilities { get; } = capabilities ?? Array.Empty<string>();
    /// <summary>C3: numeric parameter count in billions (8.0, 1.5, 0.335).
    /// Null when the source size token is absent or unparseable.</summary>
    public double? ParametersBillion { get; } = parametersBillion;
    /// <summary>C5: approximate last-updated timestamp scraped from
    /// ollama.com. Null when not from the live catalog.</summary>
    public DateTimeOffset? LastUpdated { get; } = lastUpdated;
    /// <summary>
    /// C27: which catalog source produced this row. Defaults to
    /// <see cref="ModelSource.Ollama"/> so configured / on-disk rows
    /// and existing bundled-catalog projections behave unchanged.
    /// Hugging Face rows surface as <see cref="ModelSource.HuggingFace"/>
    /// — Stage 1 uses the discriminator only for source-scoped
    /// affordances (download button disabled, source-aware tooltip);
    /// Stage 4 will use it to drive per-quant row expansion.
    /// Named <c>SourceKind</c> to avoid clashing with the existing
    /// string <see cref="Source"/> column ("Recommended"/"Config"/"Disk").
    /// </summary>
    public ModelSource SourceKind { get; } = sourceKind;
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
    string BestAt,
    /// <summary>
    /// F2a: approximate pull count from ollama.com/library, populated
    /// during the live Refresh path. Null for the bundled catalog
    /// (which predates the field). Drives the "Most popular" picker
    /// filter — entries without a count fall outside the popular cap.
    /// </summary>
    long? PullCount = null,
    /// <summary>
    /// C4: capability tags scraped per model card ("tools", "vision",
    /// "thinking", "audio"). Empty for the bundled catalog and for
    /// custom entries — the capability filter treats empty as
    /// pass-through so it doesn't accidentally hide configured rows.
    /// </summary>
    IReadOnlyList<string>? Capabilities = null,
    /// <summary>C3: numeric parameter count in billions; null when
    /// unknown.</summary>
    double? ParametersBillion = null,
    /// <summary>C5: approximate last-updated timestamp from
    /// ollama.com; null when unknown.</summary>
    DateTimeOffset? LastUpdated = null,
    /// <summary>C27 Stage 1: which catalog source produced this entry.
    /// Defaults to <see cref="ModelSource.Ollama"/> so bundled-catalog
    /// JSON and the existing live ollama.com projection compile
    /// unchanged. Hugging Face entries set <see cref="ModelSource.HuggingFace"/>
    /// so the picker can gate source-scoped affordances.</summary>
    ModelSource Source = ModelSource.Ollama);

/// <summary>C27 Stage 1: model catalog source. <see cref="Ollama"/>
/// covers the bundled <c>starter-models.json</c> and the live
/// ollama.com scrape; <see cref="HuggingFace"/> covers the HF Search
/// API path (anonymous read in Stage 1; token auth deferred to
/// Stage 3). Stages 2/4 layer pull integration and per-quant row
/// expansion on top of this discriminator.</summary>
public enum ModelSource
{
    Ollama,
    HuggingFace,
}

[Flags]
public enum PrepTargets
{
    None = 0,
    Windows = 1,
    Mac = 2
}

/// <summary>
/// C5: model picker sort mode. <c>Popular</c> matches ollama.com's
/// natural insertion order (descending pull count) and is the default
/// — composes with the Most-popular toggle's top-15 cap. <c>Newest</c>
/// orders by scraped <c>x-test-updated</c> timestamp descending; rows
/// without a timestamp (bundled / configured / on-disk) sort last.
/// <c>Alphabetical</c> sorts by tag ascending, ignoring case.
/// </summary>
public enum ModelSortMode
{
    Popular,
    Newest,
    Alphabetical,
}

public enum ModelRemoveChoice
{
    Cancel,
    ConfigOnly,
    DeleteFromDisk
}
