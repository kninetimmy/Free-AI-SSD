using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using FreeAiSsd.Shared.Documents;
using FreeAiSsd.Shared.Models;
using FreeAiSsd.Shared.Mvvm;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.Shared.ViewModels;

public class PrepViewModel : BaseViewModel
{
    private readonly IDriveService _driveService;
    private readonly IModelService _modelService;
    private readonly IOllamaPackageService _ollamaPackageService;
    private readonly IPrereqService _prereqService;
    private readonly IArtifactStagingService _artifactStagingService;
    private readonly IReadinessService _readinessService;
    private readonly IEncryptionService _encryptionService;
    private readonly IDialogService _dialogService;
    private readonly ILogService _logService;
    private readonly IElevationService _elevationService;

    /// <summary>
    /// C27 Stage 2: view-host-supplied hook that fetches HF disk-budget
    /// warnings for the HF rows in a Download batch. Receives repo IDs
    /// (already stripped of the <c>hf.co/</c> prefix) plus the drive's
    /// free disk bytes; returns one warning string per repo that needs
    /// flagging (empty list = no warnings). Null hook (default) skips
    /// HF disk-budget warnings — pull still proceeds. Keeps the VM free
    /// of a direct dependency on <c>IHuggingFaceCatalogService</c> (which
    /// lives in <c>prep-core</c>, downstream of this assembly).
    /// </summary>
    public Func<IReadOnlyList<string>, long, CancellationToken, Task<IReadOnlyList<string>>>? HuggingFaceSizingWarningsHook { get; set; }

    /// <summary>
    /// C27 Stage 4: view-host-supplied hook that lazily fetches the per-
    /// quant GGUF rows for a Hugging Face repo. Receives the bare repoId
    /// (no <c>hf.co/</c> prefix); returns the projected quant child rows
    /// the VM inserts beneath the parent. Same delegate-hook posture as
    /// <see cref="HuggingFaceSizingWarningsHook"/> — keeps <c>shared/</c>
    /// free of a direct dependency on <c>prep-core/</c>.
    /// </summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<StarterCatalogEntry>>>? HuggingFaceQuantExpansionHook { get; set; }

    /// <summary>
    /// C27 Stage 3: raised when <see cref="HuggingFaceTokenInput"/> changes
    /// so the view-host can push the new token into its
    /// <c>HuggingFaceCatalogService</c> instance (or, on Mac, into the
    /// sidecar's per-request payload). Carrying the token through an event
    /// keeps the VM free of any direct reference to the service.
    /// </summary>
    public event EventHandler<string?>? HuggingFaceTokenChanged;

    private IReadOnlyList<DriveTarget> _drives = Array.Empty<DriveTarget>();
    private DriveTarget? _selectedDrive;
    private bool _showFixedDrives;
    private bool _isSelectedDriveEncrypted;
    private string _statusText = string.Empty;
    private double _progressValue;
    private bool _progressIsIndeterminate;
    private bool _isModelOperationRunning;
    // Backs the in-place progress label fed by OllamaPullClient's
    // NDJSON frames (each one rendered to a single human-readable
    // line via OllamaPullProgress.ToDisplayString). Empty between
    // pulls.
    private string _pullProgressLine = string.Empty;
    private string _modelTagInput = string.Empty;
    private bool _prepareWindows = true;
    private bool _prepareMac;
    private bool _isMacPrepAvailable;
    private string _macPrepAvailabilityMessage = string.Empty;
    private string _prereqStatusText = string.Empty;
    private bool _enableEncryption;
    private string _volumeLabel = "Portable AI";
    private CancellationTokenSource? _modelOperationCts;
    private int? _systemRamGb;
    private int? _gpuVramGb;
    private bool _installVrCompanion;
    private string _companionHostAddress = string.Empty;
    private int _companionHostPort = 41555;
    private UserProfile? _selectedProfile;
    private string _profileSelectionWarning = string.Empty;
    private readonly SynchronizationContext? _uiSyncContext;

    // B3-Redux phase 2 state: filled from command-line args at startup
    // so the view model can decide whether to auto-resume a format
    // across the UAC relaunch and whether to emit diagnostic logging.
    private bool _diagEnabled;
    private string? _autoResumeFormatRoot;
    private string _autoResumeFormatLabel = string.Empty;
    private bool _isElevated;

    private readonly HashSet<string> _provenanceCheckedRoots = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<StarterCatalogEntry> _starterCatalog = Array.Empty<StarterCatalogEntry>();

    // F2a: picker filter state. The XAML wraps ModelRows in a
    // ListCollectionView whose Filter callback consults
    // IsModelRowVisible — search runs over Name + BestAt; the popular
    // toggle restricts recommended-only rows to the precomputed
    // top-N tag set so the filter callback stays O(1) per row.
    private string _modelSearchText = string.Empty;
    private bool _showOnlyMostPopular;
    private int _mostPopularLimit = DefaultMostPopularLimit;
    private HashSet<string> _topPopularStarterTags = new(StringComparer.OrdinalIgnoreCase);

    // C3 / C4 / C5 picker filter state. All three default to "no
    // filter" (null cap, empty capability set, Popular sort) so the
    // initial picker view matches the F2a v1.3.22 behavior; each
    // setter raises ModelRowsViewInvalidated so the view-host can
    // refresh its CollectionView in place.
    private double? _maxParametersBillion;
    private readonly HashSet<string> _requiredCapabilities = new(StringComparer.OrdinalIgnoreCase);
    private ModelSortMode _sortMode = ModelSortMode.Popular;

    // C27 Stage 1: catalog source dropdown. Ollama covers the bundled
    // catalog + live ollama.com scrape; HuggingFace covers the HF
    // Search API path. Switching sources clears _starterCatalog and
    // raises ActiveSourceChanged so the view-host can dispatch a
    // source-appropriate fetch (default popular GGUF for HF, bundled
    // load for Ollama). Filter state (search/cap/capabilities/sort/
    // Most-popular toggle) survives the switch — switching source is
    // about *which* catalog feeds the picker, not how it's narrowed.
    private ModelSource _activeSource = ModelSource.Ollama;

    // C27 Stage 3: optional Hugging Face token entered inline next to
    // the Source dropdown when ActiveSource == HuggingFace. Stored to
    // the encrypted portable-config at finalize time. Empty by default
    // (anonymous read). Setter raises HuggingFaceTokenChanged so the
    // view-host can push the new token into its catalog service / IPC
    // payload immediately — important when the user types a token to
    // unlock a gated row mid-session.
    private string _huggingFaceTokenInput = string.Empty;

    // C27 Stage 4: set of repoIds whose per-quant children have been
    // materialized into ModelRows. Tracked so a re-collapse can locate
    // and remove the child rows without re-fetching, and so a follow-on
    // expand of the same repo skips the API roundtrip. Keys are bare
    // repoIds (no `hf.co/` prefix).
    private readonly HashSet<string> _expandedRepos =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>C26: choices surfaced in the Most-popular limit dropdown.
    /// 50 is the upper cap — anything higher starts feeling like "show
    /// all" without the explicit toggle.</summary>
    public static readonly IReadOnlyList<int> MostPopularLimitOptions = new[] { 10, 15, 25, 50 };

    /// <summary>C26: default Most-popular cap. 15 matches the F2a
    /// behavior so users who don't touch the dropdown see no change.</summary>
    public const int DefaultMostPopularLimit = 15;

    /// <summary>C4: the four capability toggles surfaced as picker
    /// chips. Anchored as constants so the UI labels and the filter
    /// keys stay in sync; lowercase matches the scraped values from
    /// ollama.com's <c>x-test-capability</c> spans (validated against
    /// the 2026-05-07 fixture).</summary>
    public const string CapabilityTools = "tools";
    public const string CapabilityVision = "vision";
    public const string CapabilityThinking = "thinking";
    public const string CapabilityAudio = "audio";

    /// <summary>F2a: emitted when the picker filter state changes so
    /// the view-host can refresh its CollectionView.</summary>
    public event EventHandler? ModelRowsViewInvalidated;

    /// <summary>C27 Stage 1: emitted when <see cref="ActiveSource"/>
    /// changes so the view-host can clear its catalog state and
    /// dispatch a source-appropriate fetch (Ollama = bundled load;
    /// HuggingFace = popular-GGUF default query). Separate from
    /// <see cref="ModelRowsViewInvalidated"/> because a source switch
    /// is a fetch trigger, not just a view-refresh trigger.</summary>
    public event EventHandler? ActiveSourceChanged;

    public PrepViewModel(
        IDriveService driveService,
        IModelService modelService,
        IOllamaPackageService ollamaPackageService,
        IPrereqService prereqService,
        IArtifactStagingService artifactStagingService,
        IReadinessService readinessService,
        IEncryptionService encryptionService,
        IDialogService dialogService,
        ILogService logService,
        IElevationService elevationService)
    {
        _driveService = driveService;
        _modelService = modelService;
        _ollamaPackageService = ollamaPackageService;
        _prereqService = prereqService;
        _artifactStagingService = artifactStagingService;
        _readinessService = readinessService;
        _encryptionService = encryptionService;
        _dialogService = dialogService;
        _logService = logService;
        _elevationService = elevationService;
        _isElevated = elevationService.IsElevated();
        _uiSyncContext = SynchronizationContext.Current;

        ModelRows = new ObservableCollection<ModelGridRow>();
        ReadinessItems = new ObservableCollection<ReadinessItem>();
        LogLines = new ObservableCollection<string>();

        // M11: keep the picker's "Showing X of Y" caption in sync with
        // every change that shifts the visible row set (search text,
        // Most-popular toggle, catalog swap). Subscribing once here is
        // simpler than duplicating an OnPropertyChanged at each callsite
        // and will not miss future invalidation paths.
        ModelRowsViewInvalidated += (_, _) => OnPropertyChanged(nameof(StarterRowCountCaption));
        ModelRows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(StarterRowCountCaption));

        RefreshDrivesCommand = new RelayCommand(RefreshDrives);
        AddModelCommand = new AsyncRelayCommand(AddModelAsync, () => CanMutateDrive && HasDriveSelected);
        ClearSelectionCommand = new RelayCommand(ClearSelection);
        AddOrphanToConfigCommand = new AsyncRelayCommand(AddOrphanToConfigAsync, () => CanMutateDrive && HasDriveSelected);
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => CanMutateDrive && HasDriveSelected);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync, () => CanMutateDrive && HasDriveSelected);
        CancelOperationCommand = new RelayCommand(CancelOperation, () => _isModelOperationRunning);
        FormatPrepareCommand = new AsyncRelayCommand(FormatPrepareAsync, () => CanMutateDrive && HasDriveSelected);
        FinalizeCommand = new AsyncRelayCommand(FinalizeAsync, () => CanMutateDrive && HasDriveSelected);
        CheckPrereqUpdatesCommand = new AsyncRelayCommand(CheckPrereqUpdatesAsync, () => CanMutateDrive && HasDriveSelected);
        CheckReadinessCommand = new AsyncRelayCommand(CheckReadinessAsync, () => CanMutateDrive && HasDriveSelected);
    }

    public IReadOnlyList<DriveTarget> Drives
    {
        get => _drives;
        private set => SetProperty(ref _drives, value);
    }

    public DriveTarget? SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            if (SetProperty(ref _selectedDrive, value))
            {
                OnSelectedDriveChanged();
            }
        }
    }

    public bool ShowFixedDrives
    {
        get => _showFixedDrives;
        set
        {
            if (SetProperty(ref _showFixedDrives, value))
            {
                RefreshDrives();
            }
        }
    }

    public bool IsSelectedDriveEncrypted
    {
        get => _isSelectedDriveEncrypted;
        private set => SetProperty(ref _isSelectedDriveEncrypted, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        set => SetProperty(ref _progressValue, value);
    }

    public bool ProgressIsIndeterminate
    {
        get => _progressIsIndeterminate;
        set => SetProperty(ref _progressIsIndeterminate, value);
    }

    public bool IsModelOperationRunning
    {
        get => _isModelOperationRunning;
        private set
        {
            if (SetProperty(ref _isModelOperationRunning, value))
            {
                RaiseAllCommandsCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Latest pull progress line — one human-readable summary per
    /// Ollama <c>/api/pull</c> NDJSON frame, rendered through
    /// <see cref="OllamaPullProgress.ToDisplayString"/>. Bound to a
    /// single Text element so a long pull renders as one in-place
    /// line rather than spamming the log surface. Empty between pulls.
    /// </summary>
    public string PullProgressLine
    {
        get => _pullProgressLine;
        private set => SetProperty(ref _pullProgressLine, value);
    }

    public string ModelTagInput
    {
        get => _modelTagInput;
        set => SetProperty(ref _modelTagInput, value);
    }

    public Action? OnPreferenceStateChanged { get; set; }

    public bool PrepareWindows
    {
        get => _prepareWindows;
        set
        {
            if (SetProperty(ref _prepareWindows, value))
                OnPreferenceStateChanged?.Invoke();
        }
    }

    public bool PrepareMac
    {
        get => _prepareMac;
        set
        {
            if (!_isMacPrepAvailable) value = false;
            if (SetProperty(ref _prepareMac, value))
                OnPreferenceStateChanged?.Invoke();
        }
    }

    public bool IsMacPrepAvailable
    {
        get => _isMacPrepAvailable;
        private set => SetProperty(ref _isMacPrepAvailable, value);
    }

    public string MacPrepAvailabilityMessage
    {
        get => _macPrepAvailabilityMessage;
        private set => SetProperty(ref _macPrepAvailabilityMessage, value);
    }

    public string PrereqStatusText
    {
        get => _prereqStatusText;
        set => SetProperty(ref _prereqStatusText, value);
    }

    public bool EnableEncryption
    {
        get => _enableEncryption;
        set
        {
            if (SetProperty(ref _enableEncryption, value))
            {
                // C27 Stage 3: encryption toggle drives the HF token's
                // plaintext-warning banner.
                OnPropertyChanged(nameof(IsHuggingFaceTokenPlaintextWarningVisible));
            }
        }
    }

    /// <summary>
    /// C27 Stage 3: surface a yellow defense-in-depth warning when the
    /// user has entered an HF token but left encryption off. Persists the
    /// token (the user's call), but makes the trade-off visible. Bound
    /// to the inline UI banner under the token field.
    /// </summary>
    public bool IsHuggingFaceTokenPlaintextWarningVisible
        => !_enableEncryption
           && _activeSource == ModelSource.HuggingFace
           && !string.IsNullOrWhiteSpace(_huggingFaceTokenInput);

    public string VolumeLabel
    {
        get => _volumeLabel;
        set => SetProperty(ref _volumeLabel, value);
    }

    /// <summary>
    /// True when the current process is running with admin rights. Drives
    /// the persistent elevation banner in the PrepApp window so the user
    /// always knows which window (elevated vs. not) they're looking at.
    /// </summary>
    public bool IsElevated
    {
        get => _isElevated;
        private set
        {
            if (SetProperty(ref _isElevated, value))
            {
                OnPropertyChanged(nameof(ElevationBannerText));
            }
        }
    }

    /// <summary>
    /// True iff valid auto-resume intent was parsed from startup args
    /// (root + label survived revalidation). Used by the banner to pick
    /// the "ready to continue" copy over the generic "click format"
    /// fallback copy.
    /// </summary>
    public bool HasAutoResumeIntent => !string.IsNullOrEmpty(_autoResumeFormatRoot);

    /// <summary>
    /// Copy shown in the elevation banner. Consumers bind
    /// <see cref="IsElevated"/> to Visibility and this string to Text.
    /// </summary>
    public string ElevationBannerText =>
        HasAutoResumeIntent
            ? "Running as administrator — format operation ready to continue."
            : "Running as administrator. Click Format & Prepare Drive to continue.";

    public bool InstallVrCompanion
    {
        get => _installVrCompanion;
        set
        {
            if (SetProperty(ref _installVrCompanion, value))
                OnPreferenceStateChanged?.Invoke();
        }
    }

    public string CompanionHostAddress
    {
        get => _companionHostAddress;
        set
        {
            if (SetProperty(ref _companionHostAddress, value))
                OnPreferenceStateChanged?.Invoke();
        }
    }

    public int CompanionHostPort
    {
        get => _companionHostPort;
        set
        {
            if (SetProperty(ref _companionHostPort, value))
                OnPreferenceStateChanged?.Invoke();
        }
    }

    public UserProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                OnPropertyChanged(nameof(HasSelectedProfile));
                if (_selectedProfile is not null)
                    ProfileSelectionWarning = string.Empty;
                OnPreferenceStateChanged?.Invoke();
            }
        }
    }

    public bool HasSelectedProfile => _selectedProfile is not null;

    public string ProfileSelectionWarning
    {
        get => _profileSelectionWarning;
        private set => SetProperty(ref _profileSelectionWarning, value);
    }

    public int? SystemRamGb
    {
        get => _systemRamGb;
        set => SetProperty(ref _systemRamGb, value);
    }

    public int? GpuVramGb
    {
        get => _gpuVramGb;
        set => SetProperty(ref _gpuVramGb, value);
    }

    public ObservableCollection<ModelGridRow> ModelRows { get; }
    public ObservableCollection<ReadinessItem> ReadinessItems { get; }
    public ObservableCollection<string> LogLines { get; }

    /// <summary>F2a: case-insensitive substring filter applied to
    /// Name and BestAt. Empty string = no search filter.</summary>
    public string ModelSearchText
    {
        get => _modelSearchText;
        set
        {
            if (SetProperty(ref _modelSearchText, value ?? string.Empty))
                ModelRowsViewInvalidated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>F2a: when true, recommended-only rows are restricted
    /// to the top <see cref="MostPopularLimit"/> by pull count.
    /// Configured / on-disk rows always remain visible.</summary>
    public bool ShowOnlyMostPopular
    {
        get => _showOnlyMostPopular;
        set
        {
            if (SetProperty(ref _showOnlyMostPopular, value))
                ModelRowsViewInvalidated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>C26: top-N cap for the "Most popular" filter. Defaults
    /// to <see cref="DefaultMostPopularLimit"/> (15) so the F2a v1.3.22
    /// behavior is preserved when the user doesn't touch the dropdown.
    /// Setter recomputes the precomputed top-tag set and invalidates
    /// the view so the picker re-applies the new cap immediately.</summary>
    public int MostPopularLimit
    {
        get => _mostPopularLimit;
        set
        {
            if (SetProperty(ref _mostPopularLimit, value))
            {
                RecomputeTopPopularStarterTags();
                ModelRowsViewInvalidated?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>C3: upper bound on parameter count in billions; null
    /// means no cap. Rows whose ParametersBillion is unknown pass
    /// through (configured/custom and pre-Refresh bundled entries).</summary>
    public double? MaxParametersBillion
    {
        get => _maxParametersBillion;
        set
        {
            if (SetProperty(ref _maxParametersBillion, value))
                ModelRowsViewInvalidated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>C4: capability toggles. Each property mirrors a chip
    /// in the picker. AND semantics — selecting tools+vision keeps
    /// only entries advertising both. Backed by a single HashSet so
    /// the predicate stays O(k) per row where k is the active
    /// capability count.</summary>
    public bool FilterCapabilityTools
    {
        get => _requiredCapabilities.Contains(CapabilityTools);
        set => SetCapabilityFilter(CapabilityTools, value);
    }

    public bool FilterCapabilityVision
    {
        get => _requiredCapabilities.Contains(CapabilityVision);
        set => SetCapabilityFilter(CapabilityVision, value);
    }

    public bool FilterCapabilityThinking
    {
        get => _requiredCapabilities.Contains(CapabilityThinking);
        set => SetCapabilityFilter(CapabilityThinking, value);
    }

    public bool FilterCapabilityAudio
    {
        get => _requiredCapabilities.Contains(CapabilityAudio);
        set => SetCapabilityFilter(CapabilityAudio, value);
    }

    private void SetCapabilityFilter(string capability, bool value)
    {
        var changed = value
            ? _requiredCapabilities.Add(capability)
            : _requiredCapabilities.Remove(capability);
        if (!changed) return;
        OnPropertyChanged(capability switch
        {
            CapabilityTools => nameof(FilterCapabilityTools),
            CapabilityVision => nameof(FilterCapabilityVision),
            CapabilityThinking => nameof(FilterCapabilityThinking),
            CapabilityAudio => nameof(FilterCapabilityAudio),
            _ => string.Empty,
        });
        OnPropertyChanged(nameof(HasActiveCapabilityFilter));
        ModelRowsViewInvalidated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>C4: snapshot of the active capability filter set.
    /// Exposed for the caption formatter and tests.</summary>
    public IReadOnlyCollection<string> ActiveCapabilityFilters => _requiredCapabilities;

    /// <summary>C25: true when at least one capability chip is engaged.
    /// The picker's row style uses this to gate the pass-through marker
    /// — rows with empty <see cref="ModelGridRow.Capabilities"/> render
    /// muted only when the user has narrowed by capability, so the
    /// signal is invisible in the default view.</summary>
    public bool HasActiveCapabilityFilter => _requiredCapabilities.Count > 0;

    /// <summary>C5: sort mode applied to ModelRows by the view-host.
    /// The VM exposes the property; the WPF code-behind translates it
    /// to <c>SortDescription</c>s on the ListCollectionView, and the
    /// Mac picker uses the value directly inside
    /// <c>applyStarterModelFilters</c>.</summary>
    public ModelSortMode SortMode
    {
        get => _sortMode;
        set
        {
            if (SetProperty(ref _sortMode, value))
                ModelRowsViewInvalidated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>C27 Stage 1: which catalog source the picker is
    /// currently browsing. Switching sources clears the starter
    /// catalog (configured + on-disk rows stay visible — they're real
    /// local state, not source-derived) and raises
    /// <see cref="ActiveSourceChanged"/> so the view-host can refetch.
    /// Filter state (search / cap / capabilities / sort / Most-popular
    /// toggle) is intentionally preserved: switching source changes
    /// the data underneath the filters, not the user's filter intent.
    /// </summary>
    public ModelSource ActiveSource
    {
        get => _activeSource;
        set
        {
            if (SetProperty(ref _activeSource, value))
            {
                ActiveSourceChanged?.Invoke(this, EventArgs.Empty);
                // C27 Stage 3: token field + plaintext warning track source.
                OnPropertyChanged(nameof(IsHuggingFaceTokenFieldVisible));
                OnPropertyChanged(nameof(IsHuggingFaceTokenPlaintextWarningVisible));
            }
        }
    }

    /// <summary>
    /// C27 Stage 3: Hugging Face access token entered inline next to the
    /// Source dropdown. Persisted to <c>PortableConfig.HuggingFaceToken</c>
    /// at <see cref="FinalizeAsync"/> time; rides the AES-256-GCM seal
    /// when encryption is on. Empty default = anonymous mode. Setter
    /// raises <see cref="HuggingFaceTokenChanged"/> so the view-host can
    /// refresh the service's auth header without waiting for finalize.
    /// </summary>
    public string HuggingFaceTokenInput
    {
        get => _huggingFaceTokenInput;
        set
        {
            var trimmed = value ?? string.Empty;
            if (SetProperty(ref _huggingFaceTokenInput, trimmed))
            {
                HuggingFaceTokenChanged?.Invoke(
                    this,
                    string.IsNullOrWhiteSpace(trimmed) ? null : trimmed.Trim());
                OnPropertyChanged(nameof(IsHuggingFaceTokenPlaintextWarningVisible));
                OnPropertyChanged(nameof(HuggingFaceSelectionNeedsToken));
            }
        }
    }

    /// <summary>
    /// C27 Stage 3: Hugging Face token entry is only relevant when the
    /// active source is HuggingFace. Bound to the inline PasswordBox /
    /// SecureField visibility so the field disappears under the Ollama
    /// source.
    /// </summary>
    public bool IsHuggingFaceTokenFieldVisible
        => _activeSource == ModelSource.HuggingFace;

    /// <summary>
    /// 2026-05-12 field test: HF pulls (even of public GGUFs) fail
    /// without a Bearer token in Ollama's <c>HF_TOKEN</c> env — HF's
    /// public-repo rate-limit refuses the unauth'd request and the
    /// pull errors instantly, leaving a red "≥1 installed model"
    /// on the readiness screen. True when any checked row is an HF
    /// tag and the inline token field is empty. Drives the explainer
    /// modal that fires when the user clicks Download anyway.
    /// </summary>
    public bool HuggingFaceSelectionNeedsToken
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_huggingFaceTokenInput)) return false;
            return ModelRows.Any(r => r.IsSelected
                && r.SourceKind == ModelSource.HuggingFace
                && !r.IsPresentOnDrive);
        }
    }

    /// <summary>M11: caption that announces the visible row count + cap
    /// reason. Empty when no filter or search is active. Mirrors the Mac
    /// picker's <c>starterRowCountCaption</c> via the shared
    /// <see cref="StarterRowCountCaption"/> formatter — wording stays
    /// in sync across both platforms.</summary>
    public string StarterRowCountCaption
    {
        get
        {
            var total = ModelRows.Count;
            var visible = ModelRows.Count(IsModelRowVisible);
            var hasSearch = !string.IsNullOrWhiteSpace(_modelSearchText);
            return FreeAiSsd.Shared.Models.StarterRowCountCaption.Format(
                visible: visible, total: total,
                showOnlyMostPopular: _showOnlyMostPopular, hasSearch: hasSearch,
                maxParametersBillion: _maxParametersBillion,
                requiredCapabilities: _requiredCapabilities,
                sortMode: _sortMode);
        }
    }

    /// <summary>F2a: row-visibility predicate the WPF
    /// <c>ListCollectionView.Filter</c> callback delegates to.
    /// Pure function over <see cref="ModelGridRow"/> + current filter
    /// state; safe to unit-test.</summary>
    public bool IsModelRowVisible(ModelGridRow row)
    {
        // C27 Stage 4: hide quant child rows whose parent repo is not
        // currently expanded. Children stay in ModelRows so a re-expand
        // doesn't re-hit the API — visibility is purely a filter pass.
        if (!string.IsNullOrEmpty(row.ParentRepoId)
            && !_expandedRepos.Contains(row.ParentRepoId))
        {
            return false;
        }

        var needle = _modelSearchText.Trim();
        if (needle.Length > 0)
        {
            var nameMatch = row.Name.Contains(needle, StringComparison.OrdinalIgnoreCase);
            var bestAtMatch = !string.IsNullOrEmpty(row.BestAt)
                && row.BestAt.Contains(needle, StringComparison.OrdinalIgnoreCase);
            var tierMatch = !string.IsNullOrEmpty(row.Tier)
                && row.Tier.Contains(needle, StringComparison.OrdinalIgnoreCase);
            if (!nameMatch && !bestAtMatch && !tierMatch)
                return false;
        }

        if (_showOnlyMostPopular && IsStarterOnlyRecommendationRow(row))
        {
            if (!_topPopularStarterTags.Contains(row.Name))
                return false;
        }

        // C3: parameter cap. Rows whose ParametersBillion is unknown
        // (configured/custom/on-disk and bundled-catalog entries
        // before Refresh) pass through — same posture as the C4
        // capability filter and the F2a Most-popular toggle for
        // missing pull counts.
        if (_maxParametersBillion.HasValue
            && row.ParametersBillion.HasValue
            && row.ParametersBillion.Value > _maxParametersBillion.Value)
        {
            return false;
        }

        // C4: AND semantics — entry must carry every selected
        // capability. Rows with no capability list (configured/custom
        // /bundled) pass through so users narrowing recommendations
        // don't accidentally hide their already-installed models.
        if (_requiredCapabilities.Count > 0 && row.Capabilities.Count > 0)
        {
            foreach (var required in _requiredCapabilities)
            {
                if (!row.Capabilities.Contains(required, StringComparer.OrdinalIgnoreCase))
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// F2a: precompute the set of recommended-row tags that pass the
    /// "Most popular" cap so <see cref="IsModelRowVisible"/> stays
    /// O(1) per row. Called whenever the starter catalog changes.
    /// </summary>
    private void RecomputeTopPopularStarterTags()
    {
        _topPopularStarterTags = _starterCatalog
            .Where(e => e.PullCount.HasValue)
            .OrderByDescending(e => e.PullCount!.Value)
            .Take(_mostPopularLimit)
            .Select(e => e.Tag)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public RelayCommand RefreshDrivesCommand { get; }
    public AsyncRelayCommand AddModelCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public AsyncRelayCommand AddOrphanToConfigCommand { get; }
    public AsyncRelayCommand DownloadCommand { get; }
    public AsyncRelayCommand RemoveCommand { get; }
    public RelayCommand CancelOperationCommand { get; }
    public AsyncRelayCommand FormatPrepareCommand { get; }
    public AsyncRelayCommand FinalizeCommand { get; }
    public AsyncRelayCommand CheckPrereqUpdatesCommand { get; }
    public AsyncRelayCommand CheckReadinessCommand { get; }

    public bool CanMutateDrive => !_isModelOperationRunning && !_isSelectedDriveEncrypted;
    public bool HasDriveSelected => _selectedDrive is not null;

    public string SelectedDriveWarning
    {
        get
        {
            var warnings = new List<string>();
            if (_selectedDrive is not null && !string.IsNullOrWhiteSpace(_selectedDrive.Warning))
                warnings.Add(_selectedDrive.Warning);
            if (_isSelectedDriveEncrypted)
                warnings.Add(PrepDriveWriteGuard.ReadOnlyReason);
            return string.Join(Environment.NewLine, warnings);
        }
    }

    public void Initialize()
    {
        CheckMacArtifactAvailability();
        RefreshDrives();
    }

    /// <summary>
    /// Seed the view model with values parsed from command-line args.
    /// Called by MainWindow after construction and before
    /// <see cref="Initialize"/>. Values are expected to have already
    /// been revalidated by <c>PrepStartupArgs.Parse</c> — this method
    /// does no further input validation, it just stashes the state.
    /// </summary>
    public void ApplyStartupIntent(
        string? autoResumeFormatRoot,
        string autoResumeFormatLabel,
        bool diagEnabled)
    {
        _autoResumeFormatRoot = autoResumeFormatRoot;
        _autoResumeFormatLabel = autoResumeFormatLabel ?? string.Empty;
        _diagEnabled = diagEnabled;
        OnPropertyChanged(nameof(HasAutoResumeIntent));
        OnPropertyChanged(nameof(ElevationBannerText));
    }

    /// <summary>
    /// If auto-resume intent was set via <see cref="ApplyStartupIntent"/>,
    /// attempts to resume the format operation that triggered the UAC
    /// relaunch. Intent is consumed on attempt (pass or fail) so it
    /// never fires twice. Safe to call when no intent is set — returns
    /// immediately. Must run after <see cref="Initialize"/>.
    /// </summary>
    public async Task TryAutoResumeFormatAsync()
    {
        var root = _autoResumeFormatRoot;
        if (string.IsNullOrEmpty(root)) return;

        // Consume intent before any branch that might leave state mid-
        // flight. The banner flips to its no-intent copy after this.
        var requestedLabel = _autoResumeFormatLabel;
        _autoResumeFormatRoot = null;
        _autoResumeFormatLabel = string.Empty;
        OnPropertyChanged(nameof(HasAutoResumeIntent));
        OnPropertyChanged(nameof(ElevationBannerText));

        // Drive-letter drift guard: user may have unplugged the SSD
        // between Format-click and UAC-approval. Re-enumerate fresh
        // (not relying on Drives already being populated) and bail if
        // the requested root is no longer present.
        var drives = _driveService.GetCandidateDrives(_showFixedDrives);
        var match = drives.FirstOrDefault(d =>
            string.Equals(d.RootPath, root, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            AppendLog($"Auto-resume format cancelled: drive {root} is no longer present.");
            _dialogService.ShowWarning(
                $"The drive {root} that you asked to format is no longer connected." + Environment.NewLine + Environment.NewLine +
                "Reconnect the drive and click Format & Prepare Drive to continue.",
                "Drive not found");
            return;
        }

        Drives = drives;
        SelectedDrive = match;
        VolumeLabel = requestedLabel;
        AppendLog($"Auto-resume: continuing format of {root} (label '{requestedLabel}') after UAC relaunch.");

        // FormatPrepareAsync contains the non-negotiable ConfirmErase
        // dialog — that is the post-relaunch safety gate the user must
        // click through before Format-Volume actually runs. We never
        // bypass it.
        await FormatPrepareAsync();
    }

    private void RefreshDrives()
    {
        Drives = _driveService.GetCandidateDrives(_showFixedDrives);
        SelectedDrive = Drives.Count > 0 ? Drives[0] : null;
    }

    private void OnSelectedDriveChanged()
    {
        RefreshEncryptionState();
        OnPropertyChanged(nameof(SelectedDriveWarning));
        OnPropertyChanged(nameof(CanMutateDrive));
        OnPropertyChanged(nameof(HasDriveSelected));
        RaiseAllCommandsCanExecuteChanged();
        _ = RefreshModelStatusesAsync();
        _ = CheckAndPromptLibraryReindexAsync();
    }

    private async Task CheckAndPromptLibraryReindexAsync()
    {
        if (_isModelOperationRunning) return;
        if (_isSelectedDriveEncrypted) return;
        if (_selectedDrive is null) return;

        var root = _selectedDrive.RootPath;
        if (!_provenanceCheckedRoots.Add(root)) return;

        PortableConfig config;
        try { config = await _modelService.LoadConfigAsync(GetConfigPath(root)); }
        catch { return; }

        var currentModel = config.EmbeddingModelName;
        if (string.IsNullOrWhiteSpace(currentModel)) return;

        var libraryManager = new DocumentLibraryManager(root);
        var mismatches = libraryManager.ScanProvenanceMismatches(currentModel);
        if (mismatches.Count == 0) return;

        var lines = string.Join("\n", mismatches.Select(m => $"- {m.Name} ({m.LastEmbeddingModel})"));
        var message = $"The following document libraries were indexed with a different embedding model:\n\n{lines}\n\nCurrent model: '{currentModel}'. Reindex all affected libraries now?";
        const string title = "Embedding Model Changed — Reindex Required";

        bool confirm;
        if (_uiSyncContext is null || SynchronizationContext.Current == _uiSyncContext)
        {
            confirm = _dialogService.Confirm(message, title);
        }
        else
        {
            var tcs = new TaskCompletionSource<bool>();
            _uiSyncContext.Post(_ => tcs.SetResult(_dialogService.Confirm(message, title)), null);
            confirm = await tcs.Task;
        }

        if (!confirm)
        {
            AppendLog("Reindex skipped. Document search results may be incorrect until libraries are reindexed.");
            return;
        }

        var ollamaDir = Path.Combine(root, SsdLayout.Ollama);
        var ollamaExe = _ollamaPackageService.ResolveOllamaExe(ollamaDir);
        if (ollamaExe is null)
        {
            AppendLog("Reindex aborted: Ollama executable not found on this drive. Run Finalize first.");
            return;
        }

        var modelsRoot = Path.Combine(root, SsdLayout.Models);
        SetModelOperationState(true, "Reindexing document libraries...");
        IOllamaServerHandle? serverHandle = null;
        try
        {
            serverHandle = await _ollamaPackageService.StartTemporaryServerAsync(ollamaExe, modelsRoot, AppendLog, CancellationToken.None);
            var ingestor = new DocumentIngestor(libraryManager, new EmbeddingClient());

            foreach (var manifest in mismatches)
            {
                AppendLog($"Reindexing '{manifest.Name}'...");
                try
                {
                    await ingestor.RebuildIndexAsync(manifest, serverHandle.Host, config, cancellationToken: CancellationToken.None);
                    AppendLog($"Reindexed '{manifest.Name}' successfully.");
                }
                catch (Exception ex)
                {
                    AppendLog($"Reindex failed for '{manifest.Name}': {ex.Message}");
                }
            }
            SetModelOperationState(false, "Reindex complete");
        }
        catch (Exception ex)
        {
            AppendLog($"Reindex operation failed: {ex.Message}");
            SetModelOperationState(false, "Reindex failed");
        }
        finally
        {
            serverHandle?.Dispose();
            if (_isModelOperationRunning)
                SetModelOperationState(false);
        }
    }

    private void RefreshEncryptionState()
    {
        if (_selectedDrive is not null)
        {
            IsSelectedDriveEncrypted = _encryptionService.IsEncryptionEnabled(_selectedDrive.RootPath);
        }
        else
        {
            IsSelectedDriveEncrypted = false;
        }

        if (_isSelectedDriveEncrypted && !_isModelOperationRunning)
        {
            StatusText = "Encrypted drive selected (read-only in PrepApp)";
        }
    }

    private bool EnsureWritable(string operationName)
    {
        if (_selectedDrive is null)
        {
            StatusText = "Select a target drive first";
            AppendLog($"{operationName} blocked: no drive selected.");
            return false;
        }
        if (!_driveService.EnsureWritable(_selectedDrive.RootPath, operationName, out var blockedMessage))
        {
            StatusText = "Encrypted drive selected (read-only in PrepApp)";
            AppendLog(blockedMessage ?? $"{operationName} blocked: drive is encrypted.");
            return false;
        }
        return true;
    }

    private void CheckMacArtifactAvailability()
    {
        IsMacPrepAvailable = _artifactStagingService.AreMacArtifactsAvailable(out var problem);
        MacPrepAvailabilityMessage = problem ?? string.Empty;
        if (!_isMacPrepAvailable && _prepareMac)
        {
            PrepareMac = false;
            PrepareWindows = true;
        }
    }

    private PrepTargets GetSelectedPrepTargets()
    {
        var targets = PrepTargets.None;
        if (_prepareWindows) targets |= PrepTargets.Windows;
        if (_prepareMac) targets |= PrepTargets.Mac;
        return targets;
    }

    // MAC10a: Windows-only → NTFS, anything that includes Mac → exFAT.
    // APFS is deferred (MAC1) until a Mac-native prep workflow exists, so
    // exFAT is the only cross-OS option Windows PrepApp can stage today.
    internal static string ResolveFileSystem(PrepTargets targets) =>
        targets switch
        {
            PrepTargets.None => throw new InvalidOperationException(
                "PrepTargets must include Windows or Mac before resolving a filesystem."),
            PrepTargets.Windows => "NTFS",
            _ => "exFAT",
        };

    private async Task AddModelAsync()
    {
        var tag = (_modelTagInput ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(tag))
        {
            AppendLog("Enter a model tag before adding.");
            return;
        }
        if (_selectedDrive is null)
        {
            AppendLog("Select a target drive first.");
            return;
        }
        if (!EnsureWritable("Add model")) return;

        var configPath = GetConfigPath(_selectedDrive.RootPath);
        _driveService.EnsureSsdStructure(_selectedDrive.RootPath);
        var config = await _modelService.LoadConfigAsync(configPath);
        _modelService.UpsertModel(config.Models, tag, ModelInstallStatus.NotInstalled);
        await _modelService.SaveConfigAsync(configPath, config);
        await RefreshModelStatusesAsync();
        ModelTagInput = string.Empty;
        AppendLog($"Added model '{tag}' to config.");
    }

    private void ClearSelection()
    {
        foreach (var row in ModelRows)
            row.IsSelected = false;
    }

    /// <summary>
    /// Seed the VM with the starter-catalog entries the PrepApp loaded
    /// from JSON. Calling this triggers a grid refresh so recommended
    /// rows appear immediately — even before the user has selected a
    /// drive or added anything to config. Idempotent.
    /// </summary>
    public async Task SetStarterCatalogAsync(IEnumerable<StarterCatalogEntry> entries)
    {
        _starterCatalog = entries?.ToList() ?? new List<StarterCatalogEntry>();
        RecomputeTopPopularStarterTags();
        await RefreshModelStatusesAsync();
        // Catalog swap can shift which rows belong to the popular cap;
        // tell the host to refresh its CollectionView so the new top-N
        // takes effect without requiring the user to re-toggle.
        ModelRowsViewInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private async Task AddOrphanToConfigAsync()
    {
        if (_selectedDrive is null)
        {
            AppendLog("Select a target drive first.");
            return;
        }
        if (!EnsureWritable("Add on-disk model to config")) return;

        var selectedOrphans = GetCheckedModelRows().Where(r => r.IsOnDiskOnly).ToList();
        if (selectedOrphans.Count == 0)
        {
            AppendLog("Check one or more OnDiskOnly model rows to add to config.");
            return;
        }

        var configPath = GetConfigPath(_selectedDrive.RootPath);
        var config = await _modelService.LoadConfigAsync(configPath);
        foreach (var row in selectedOrphans)
        {
            _modelService.UpsertModel(config.Models, row.Name, ModelInstallStatus.NotInstalled);
            AppendLog($"Added orphaned model '{row.Name}' to config.");
        }
        await _modelService.SaveConfigAsync(configPath, config);
        await RefreshModelStatusesAsync();
    }

    private IReadOnlyList<ModelGridRow> GetCheckedModelRows()
        => ModelRows.Where(r => r.IsSelected).ToList().AsReadOnly();

    private static bool IsStarterOnlyRecommendationRow(ModelGridRow row)
        => string.Equals(row.Source, "Recommended", StringComparison.OrdinalIgnoreCase);

    private static string DescribeRemoveSelection(IReadOnlyList<ModelGridRow> rows)
        => rows.Count == 1 ? rows[0].Name : $"{rows.Count} selected models";

    private async Task DownloadAsync()
    {
        if (!EnsureWritable("Download models")) return;
        if (_selectedDrive is null)
        {
            AppendLog("Select a target drive first.");
            return;
        }

        var checkedRows = GetCheckedModelRows();
        if (checkedRows.Count == 0)
        {
            StatusText = "No models checked — check one or more models to download";
            AppendLog("Check one or more models to download.");
            return;
        }

        // C27 Stage 2: HF rows are now downloadable. Ollama natively
        // accepts `hf.co/<owner>/<repo>` tags and pulls the default
        // quant (Q4_K_M when present). Disk-budget warnings come from
        // HuggingFaceSizingWarningsHook (view-host-supplied) which
        // fetches `siblings[].lfs.size` per repo for the checked rows.
        var hfRows = checkedRows
            .Where(r => r.SourceKind == ModelSource.HuggingFace && !r.IsPresentOnDrive)
            .ToList();

        // 2026-05-12 field test: HF pulls fail without a Bearer token
        // in Ollama's HF_TOKEN env — even for public GGUFs (HF rate-
        // limits anonymous downloads). Block the click and walk the
        // user through obtaining a free read-only token; open the HF
        // token settings page in their browser if they confirm.
        if (hfRows.Count > 0 && string.IsNullOrWhiteSpace(_huggingFaceTokenInput))
        {
            var openTokenPage = _dialogService.Confirm(
                "You checked one or more Hugging Face models. Ollama needs a free read-only " +
                "token from huggingface.co to pull them — even for public GGUFs " +
                "(HF rate-limits anonymous downloads).\n\n" +
                "1. Sign in or sign up at huggingface.co (free).\n" +
                "2. Open Settings → Access Tokens, click \"Create new token\".\n" +
                "3. Choose \"Read\" (classic) — or \"Fine-grained\" with only " +
                "\"Read access to contents of all public repos\" checked.\n" +
                "4. Copy the token (starts with `hf_…`) and paste it into the HF token " +
                "field on the Models tab.\n" +
                "5. Click Download again.\n\n" +
                "The token is stored on the SSD (sealed with AES-256-GCM when encryption " +
                "is on; plaintext otherwise — a yellow warning surfaces in that case).\n\n" +
                "Open the Hugging Face token page in your browser now?",
                "Hugging Face token required");
            if (openTokenPage)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "https://huggingface.co/settings/tokens",
                        UseShellExecute = true,
                    });
                }
                catch (Exception ex)
                {
                    AppendLog($"Could not open browser for HF token page: {ex.Message}. " +
                              "Visit https://huggingface.co/settings/tokens manually.");
                }
            }
            AppendLog("Download blocked: Hugging Face models selected without a token.");
            StatusText = "Hugging Face token required";
            return;
        }

        var selected = checkedRows
            .Where(r => !r.IsPresentOnDrive)
            .Select(r => r.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (selected.Count == 0)
        {
            StatusText = "All checked models are already on the drive";
            AppendLog("All checked models are already on the drive — nothing to download.");
            return;
        }

        var skippedCount = checkedRows.Count - selected.Count;
        if (skippedCount > 0)
            AppendLog($"Skipping {skippedCount} checked row(s) already on the drive.");

        _driveService.EnsureSsdStructure(_selectedDrive.RootPath);

        if (!await ConfirmSizingWarningsIfNeededAsync(selected, hfRows)) return;
        await PullModelsAsync(selected);
        ClearSelection();
    }

    private async Task<bool> ConfirmSizingWarningsIfNeededAsync(
        IReadOnlyList<string> models,
        IReadOnlyList<ModelGridRow> hfRows)
    {
        if (_selectedDrive is null) return true;

        var warnings = new List<string>(
            _modelService.BuildPullSelectionWarnings(models, _selectedDrive.RootPath, _systemRamGb, _gpuVramGb));

        // C27 Stage 2: HF rows in the batch get their own warnings
        // computed from real bytes (`siblings[].lfs.size`) rather than
        // the parameter-count heuristic. Hook is null in tests that
        // don't exercise HF — that's fine, pull still proceeds without
        // a disk-budget warning, matching the posture for any other
        // catalog metadata gap.
        var hook = HuggingFaceSizingWarningsHook;
        if (hfRows.Count > 0 && hook is not null)
        {
            var repoIds = hfRows
                .Select(r => StripHuggingFacePrefix(r.Name))
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (repoIds.Count > 0)
            {
                // GetFreeDiskSpaceGb returns int? — null when the drive
                // is detached or unreadable. Treat that as "free space
                // unknown" and pass 0 so the hook can still surface
                // approximate-size warnings (Stage 2's warnings are
                // primarily about the actual download size, not a hard
                // failure if free disk is unmeasurable).
                var freeGb = _driveService.GetFreeDiskSpaceGb(_selectedDrive.RootPath);
                var freeBytes = (long)(freeGb ?? 0) * 1024L * 1024 * 1024;
                try
                {
                    var hfWarnings = await hook(repoIds, freeBytes, CancellationToken.None);
                    if (hfWarnings is { Count: > 0 })
                    {
                        warnings.AddRange(hfWarnings);
                    }
                }
                catch (Exception ex)
                {
                    // Hook failure (HF API 4xx/5xx, network) is non-
                    // fatal — log and proceed. The pull itself may
                    // still fail with disk-full mid-stream, but that's
                    // a less common path than HF rate-limiting on the
                    // metadata fetch.
                    AppendLog($"Could not fetch Hugging Face disk-budget data: {ex.Message}");
                }
            }
        }

        if (warnings.Count > 0)
        {
            if (!_dialogService.ConfirmSizingWarnings(warnings))
            {
                AppendLog("Download cancelled after sizing warning.");
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// C27 Stage 4: toggle expansion of a Hugging Face repo row. On
    /// first expand, invokes <see cref="HuggingFaceQuantExpansionHook"/>
    /// to fetch <c>siblings[]</c>, projects each GGUF into a quant
    /// child entry, and inserts the children directly below the
    /// parent in <see cref="ModelRows"/>. Subsequent toggles flip
    /// expansion state and add/remove the same children from the
    /// view's filter pass (rows survive in the collection — the
    /// filter callback gates visibility via <see cref="ModelGridRow.ParentRepoId"/>).
    /// No-op (with a log line) when the hook is null — the WPF host
    /// wires it from <c>HuggingFaceCatalogService.FetchSiblingsAsync</c>
    /// and the Mac sidecar wires it from the <c>hf-siblings</c> IPC arm.
    /// </summary>
    public async Task ToggleRepoExpansionAsync(ModelGridRow parent, CancellationToken ct = default)
    {
        if (parent is null) throw new ArgumentNullException(nameof(parent));
        if (!parent.IsExpandable) return;
        if (parent.IsExpanding) return;

        var repoId = StripHuggingFacePrefix(parent.Name);
        if (string.IsNullOrWhiteSpace(repoId)) return;

        // Re-collapse: flip the bit and refresh the filter callback so
        // the children fall out of the visible set. The child rows stay
        // in ModelRows so a follow-on expand is instant (no API call).
        if (parent.IsExpanded)
        {
            parent.IsExpanded = false;
            _expandedRepos.Remove(repoId);
            ModelRowsViewInvalidated?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Already materialized? Just flip the bit and refresh.
        if (_expandedRepos.Contains(repoId)
            && ModelRows.Any(r => string.Equals(r.ParentRepoId, repoId, StringComparison.OrdinalIgnoreCase)))
        {
            parent.IsExpanded = true;
            ModelRowsViewInvalidated?.Invoke(this, EventArgs.Empty);
            return;
        }

        var hook = HuggingFaceQuantExpansionHook;
        if (hook is null)
        {
            AppendLog($"Cannot expand {parent.Name}: no Hugging Face quant-expansion hook is wired.");
            return;
        }

        parent.IsExpanding = true;
        try
        {
            IReadOnlyList<StarterCatalogEntry> children;
            try
            {
                children = await hook(repoId, ct);
            }
            catch (Exception ex)
            {
                AppendLog($"Could not fetch quants for hf.co/{repoId}: {ex.Message}");
                return;
            }

            if (children.Count == 0)
            {
                AppendLog($"hf.co/{repoId} has no GGUF quants to expand.");
                parent.IsExpanded = true; // Still mark expanded so the chevron flips.
                _expandedRepos.Add(repoId);
                return;
            }

            var parentIndex = ModelRows.IndexOf(parent);
            if (parentIndex < 0)
            {
                AppendLog($"Parent row for hf.co/{repoId} missing from picker; aborting expand.");
                return;
            }

            // Children inherit the parent's PullCount + LastUpdated so
            // they sort adjacent to the parent under Popular / Newest;
            // alphabetical sort uses the tag (parent + ":quant") which
            // naturally clusters them. The chevron + indent in WPF / Mac
            // is the visual hierarchy cue.
            var insertAt = parentIndex + 1;
            foreach (var child in children)
            {
                var childRow = new ModelGridRow(
                    name: child.Tag,
                    status: "Not downloaded",
                    source: "Recommended",
                    sizingWarning: "OK",
                    sizeDisplay: child.QuantSizeBytes is { } q && q > 0 ? FormatSize(q) : "—",
                    shaPreview: "—",
                    lastVerifiedDisplay: "—",
                    isOnDiskOnly: false,
                    isPresentOnDrive: false,
                    tier: parent.Tier,
                    bestAt: child.BestAt,
                    pullCount: parent.PullCount,
                    capabilities: parent.Capabilities,
                    parametersBillion: parent.ParametersBillion,
                    lastUpdated: parent.LastUpdated,
                    sourceKind: ModelSource.HuggingFace,
                    isExpandable: false,
                    parentRepoId: repoId,
                    quantLabel: child.QuantLabel,
                    quantSizeBytes: child.QuantSizeBytes);
                ModelRows.Insert(insertAt++, childRow);
            }

            parent.IsExpanded = true;
            _expandedRepos.Add(repoId);
            ModelRowsViewInvalidated?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            parent.IsExpanding = false;
        }
    }

    /// <summary>
    /// C27 Stage 3: build the env-var dictionary passed into the temp
    /// Ollama server for HF GGUF pulls. Returns null (= no extra env)
    /// when the token is missing/empty so the server's env doesn't grow
    /// unnecessarily. <c>HF_TOKEN</c> is the modern Ollama variable
    /// name; we also set <c>HUGGING_FACE_HUB_TOKEN</c> for older builds
    /// that haven't picked up the rename. Internal for direct unit
    /// testing — the helper is pure so a token round-trip pin can run
    /// without spinning up a real Ollama process.
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? BuildHuggingFaceEnv(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var trimmed = token.Trim();
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HF_TOKEN"] = trimmed,
            ["HUGGING_FACE_HUB_TOKEN"] = trimmed,
        };
    }

    /// <summary>
    /// C27 Stage 2: strip the <c>hf.co/</c> prefix from an HF row's tag
    /// to recover the bare repoId (<c>owner/repo</c>) that
    /// <see cref="HuggingFaceSizingWarningsHook"/> expects. Returns the
    /// input unchanged when the prefix is absent.
    /// </summary>
    internal static string StripHuggingFacePrefix(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return tag;
        const string prefix = "hf.co/";
        return tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? tag[prefix.Length..]
            : tag;
    }

    private async Task PullModelsAsync(IReadOnlyList<string> models)
    {
        if (_isModelOperationRunning)
        {
            AppendLog("A model operation is already running.");
            return;
        }
        if (_selectedDrive is null)
        {
            AppendLog("Select a target drive first.");
            return;
        }
        if (!EnsureWritable("Download operation")) return;

        var root = _selectedDrive.RootPath;
        var configPath = GetConfigPath(root);
        _modelOperationCts = new CancellationTokenSource();
        SetModelOperationState(true, "Downloading...");
        ProgressIsIndeterminate = true;

        IOllamaServerHandle? serverHandle = null;
        try
        {
            var ollamaExe = await _ollamaPackageService.EnsureOllamaReadyAsync(
                root, AppendLog,
                new Progress<DownloadProgress>(p =>
                {
                    ProgressIsIndeterminate = false;
                    ProgressValue = p.Percent;
                    StatusText = $"Downloading Ollama {p.Percent:F1}%";
                }),
                _modelOperationCts.Token);

            var modelsRoot = Path.Combine(root, SsdLayout.Models);

            // Start a controlled temporary server so that `ollama pull` doesn't
            // auto-start an uncontrolled background server (which can open tray
            // icons, persist after the app exits, and cause crashes).
            ProgressIsIndeterminate = true;
            StatusText = "Starting temporary Ollama server...";
            // C27 Stage 3: pass the user's HF token (if any) into the
            // temp server's env so gated/private `hf.co/...` pulls
            // can authenticate. Ollama reads HF_TOKEN /
            // HUGGING_FACE_HUB_TOKEN at /api/pull time. Non-HF pulls
            // ignore the env entries — harmless to set unconditionally.
            var hfEnv = BuildHuggingFaceEnv(_huggingFaceTokenInput);
            serverHandle = await _ollamaPackageService.StartTemporaryServerAsync(
                ollamaExe, modelsRoot, AppendLog, _modelOperationCts.Token, hfEnv);

            foreach (var model in models)
            {
                _modelOperationCts.Token.ThrowIfCancellationRequested();
                await _modelService.UpdateModelStatusAsync(configPath, model, ModelInstallStatus.Downloading);
                StatusText = $"Downloading {model}...";
                AppendLog($"Downloading {model}...");

                // MAC31: seed the in-place progress line with a "Resuming
                // from NN%..." message when partial blobs already exist
                // for this tag. Without this, a retry after a cancelled
                // pull starts visually at 0% even though Ollama IS
                // resumable — which made cancel-and-retry feel broken
                // in the v1.3.10 mac field test.
                var seed = _modelService.EstimatePartialPullProgress(modelsRoot, model);
                PullProgressLine = seed > 0
                    ? $"Resuming {model} from {seed:P0}…"
                    : $"Pulling {model}…";

                try
                {
                    var result = await _modelService.PullModelAsync(
                        ollamaExe, modelsRoot, model, AppendLog, _modelOperationCts.Token, serverHandle.Host,
                        // Each NDJSON frame from Ollama's /api/pull is
                        // rendered to a single in-place progress line.
                        // SetPullProgressLineSafe dispatches via the UI
                        // sync context because the lambda fires from
                        // OllamaPullClient's HTTP-response drain thread.
                        onProgress: progress => SetPullProgressLineSafe(progress.ToDisplayString()));

                    await _modelService.UpdateModelStatusAsync(configPath, model, ModelInstallStatus.Installed, result.Sha256, result.SizeBytes, DateTime.UtcNow);
                    AppendLog($"Downloaded {model} ({FormatSize(result.SizeBytes)}). Sha256 {result.Sha256[..8]}...");
                }
                catch (OperationCanceledException)
                {
                    await _modelService.UpdateModelStatusAsync(configPath, model, ModelInstallStatus.NotInstalled);
                    AppendLog($"Download cancelled for {model}.");
                    throw;
                }
                catch (Exception ex)
                {
                    await _modelService.UpdateModelStatusAsync(configPath, model, ModelInstallStatus.Failed);
                    AppendLog($"Failed to download {model}: {ex.Message}");
                }
            }

            // C2: ensure the configured embedding model is on the SSD too.
            // Chat-model picker (F2a) ignores the embedder, so without this
            // step a freshly-prepped drive ingests 0/N chunks at first
            // library use ("embedding failures exceeded threshold"). The
            // pull reuses the temp server already running for the chat
            // models, so it costs no extra startup.
            var configForEmbed = await _modelService.LoadConfigAsync(configPath);
            await EnsureEmbeddingModelInstalledAsync(
                ollamaExe, modelsRoot, serverHandle.Host, configPath,
                configForEmbed.EmbeddingModelName, _modelOperationCts.Token);

            StatusText = "Download complete";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Download cancelled";
        }
        finally
        {
            serverHandle?.Dispose();
            ProgressIsIndeterminate = false;
            // MAC31: clear the in-place progress label when the batch
            // ends so the next pull starts from a known-empty state.
            PullProgressLine = string.Empty;
            SetModelOperationState(false, StatusText);
            _modelOperationCts?.Dispose();
            _modelOperationCts = null;
            await RefreshModelStatusesAsync();
        }
    }

    // C2: idempotent disk-truth pull of the embedder. Two callers:
    // (1) DownloadAsync tail, reusing the temp server already running for
    //     the chat-model loop; (2) EnsureEmbeddingModelOnFinalizeAsync,
    //     which spins its own server only when the disk-check shows the
    //     embedder is missing. Best-effort: a failed pull logs but does
    //     not abort the wider operation — offline users can still finalize
    //     without RAG, and Stage 2 of C2 surfaces a readable runtime error
    //     if they try to ingest without an embedder.
    private async Task EnsureEmbeddingModelInstalledAsync(
        string ollamaExe,
        string modelsRoot,
        string ollamaHost,
        string configPath,
        string embeddingModelName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(embeddingModelName))
        {
            AppendLog("No embedding model configured; skipping embedder pull.");
            return;
        }

        var canonicalTag = NormalizeOllamaTag(embeddingModelName);
        var onDisk = _modelService.DiscoverModelsOnDisk(modelsRoot);
        var alreadyInstalled = onDisk.Any(t =>
            string.Equals(NormalizeOllamaTag(t), canonicalTag, StringComparison.OrdinalIgnoreCase));

        if (alreadyInstalled)
        {
            AppendLog($"Embedding model {canonicalTag} already on disk; skipping pull.");
            return;
        }

        AppendLog($"Pulling embedding model {canonicalTag}…");
        await _modelService.UpdateModelStatusAsync(configPath, canonicalTag, ModelInstallStatus.Downloading);

        var seed = _modelService.EstimatePartialPullProgress(modelsRoot, canonicalTag);
        PullProgressLine = seed > 0
            ? $"Resuming {canonicalTag} from {seed:P0}…"
            : $"Pulling {canonicalTag}…";

        try
        {
            var result = await _modelService.PullModelAsync(
                ollamaExe, modelsRoot, canonicalTag, AppendLog, ct, ollamaHost,
                onProgress: progress => SetPullProgressLineSafe(progress.ToDisplayString()));

            await _modelService.UpdateModelStatusAsync(
                configPath, canonicalTag, ModelInstallStatus.Installed,
                result.Sha256, result.SizeBytes, DateTime.UtcNow);
            AppendLog($"Embedding model ready: {canonicalTag} ({FormatSize(result.SizeBytes)}).");
        }
        catch (OperationCanceledException)
        {
            await _modelService.UpdateModelStatusAsync(configPath, canonicalTag, ModelInstallStatus.NotInstalled);
            AppendLog($"Embedding model pull cancelled for {canonicalTag}.");
            throw;
        }
        catch (Exception ex)
        {
            await _modelService.UpdateModelStatusAsync(configPath, canonicalTag, ModelInstallStatus.Failed);
            AppendLog($"Embedding model pull failed for {canonicalTag}: {ex.Message}. Document ingestion will fail until this is resolved.");
            // Intentionally do not rethrow — see method header.
        }
    }

    // C2 finalize-time guard. Disk-check first; only spins a temp server
    // if the embedder is genuinely missing. Mirrors DownloadAsync's
    // server-startup pattern so the pull surface matches MAC40's
    // /api/pull NDJSON contract.
    private async Task EnsureEmbeddingModelOnFinalizeAsync(string root, string configPath, string embeddingModelName)
    {
        if (string.IsNullOrWhiteSpace(embeddingModelName))
        {
            return;
        }

        var modelsRoot = Path.Combine(root, SsdLayout.Models);
        var canonicalTag = NormalizeOllamaTag(embeddingModelName);
        var onDisk = _modelService.DiscoverModelsOnDisk(modelsRoot);
        if (onDisk.Any(t => string.Equals(NormalizeOllamaTag(t), canonicalTag, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        StatusText = "Pulling embedding model…";
        IOllamaServerHandle? serverHandle = null;
        try
        {
            var ollamaExe = await _ollamaPackageService.EnsureOllamaReadyAsync(root, AppendLog, null, CancellationToken.None);
            serverHandle = await _ollamaPackageService.StartTemporaryServerAsync(ollamaExe, modelsRoot, AppendLog, CancellationToken.None);
            await EnsureEmbeddingModelInstalledAsync(
                ollamaExe, modelsRoot, serverHandle.Host, configPath, embeddingModelName, CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppendLog($"Embedding model pull during finalize failed: {ex.Message}. Document ingestion will fail until this is resolved.");
        }
        finally
        {
            serverHandle?.Dispose();
            PullProgressLine = string.Empty;
        }
    }

    // Ollama treats "name" and "name:latest" as the same model on the
    // wire and on disk. DiscoverModelsOnDisk emits "name:latest"; the
    // user-facing PortableConfig.EmbeddingModelName default is bare
    // "nomic-embed-text". Normalize both sides before equality so the
    // disk-truth idempotency check doesn't double-pull.
    private static string NormalizeOllamaTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return string.Empty;
        var trimmed = tag.Trim();
        return trimmed.Contains(':', StringComparison.Ordinal) ? trimmed : $"{trimmed}:latest";
    }

    private async Task RemoveAsync()
    {
        if (_isModelOperationRunning)
        {
            AppendLog("Cannot remove while another model operation is running.");
            return;
        }
        if (_selectedDrive is null)
        {
            AppendLog("Select a target drive first.");
            return;
        }
        if (!EnsureWritable("Remove/delete model")) return;

        var checkedRows = GetCheckedModelRows();
        if (checkedRows.Count == 0)
        {
            AppendLog("Check one or more models to remove.");
            return;
        }

        var removableRows = checkedRows.Where(row => !IsStarterOnlyRecommendationRow(row)).ToList();
        if (removableRows.Count == 0)
        {
            AppendLog("Remove only works for models already in the configuration or on the drive.");
            return;
        }

        var skippedCount = checkedRows.Count - removableRows.Count;
        if (skippedCount > 0)
            AppendLog($"Skipping {skippedCount} recommended row(s) that are not yet on the drive or in the configuration.");

        var choice = _dialogService.PromptRemoveModel(DescribeRemoveSelection(removableRows));
        if (choice == ModelRemoveChoice.Cancel)
        {
            AppendLog("Remove cancelled.");
            return;
        }

        var configPath = GetConfigPath(_selectedDrive.RootPath);
        var config = await _modelService.LoadConfigAsync(configPath);

        if (choice == ModelRemoveChoice.ConfigOnly)
        {
            var configDirty = false;
            foreach (var selectedRow in removableRows)
            {
                var model = config.Models.FirstOrDefault(m => string.Equals(m.Name, selectedRow.Name, StringComparison.OrdinalIgnoreCase));
                if (model is null)
                {
                    AppendLog($"{selectedRow.Name} is already only on the drive and not in the configuration.");
                    continue;
                }

                config.Models.Remove(model);
                configDirty = true;
                AppendLog(selectedRow.IsPresentOnDrive
                    ? $"{selectedRow.Name}: removed from configuration only (disk contents kept)."
                    : $"{selectedRow.Name}: removed from configuration.");
            }

            if (configDirty)
                await _modelService.SaveConfigAsync(configPath, config);

            await RefreshModelStatusesAsync();
            return;
        }

        var deleteFailures = new List<string>();
        var configDirtyForDelete = false;
        SetModelOperationState(
            true,
            removableRows.Count == 1
                ? $"Deleting {removableRows[0].Name} from disk..."
                : $"Deleting {removableRows.Count} models from disk...");
        IOllamaServerHandle? serverHandle = null;
        try
        {
            foreach (var selectedRow in removableRows.Where(row => !row.IsPresentOnDrive))
            {
                var model = config.Models.FirstOrDefault(m => string.Equals(m.Name, selectedRow.Name, StringComparison.OrdinalIgnoreCase));
                if (model is null)
                {
                    AppendLog($"{selectedRow.Name}: no on-disk files were present to delete.");
                    continue;
                }

                config.Models.Remove(model);
                configDirtyForDelete = true;
                AppendLog($"{selectedRow.Name}: removed from configuration (no on-disk files were present).");
            }

            var rowsPresentOnDrive = removableRows.Where(row => row.IsPresentOnDrive).ToList();
            if (rowsPresentOnDrive.Count > 0)
            {
                var ollamaExe = await _ollamaPackageService.EnsureOllamaReadyAsync(
                    _selectedDrive.RootPath, AppendLog, null, CancellationToken.None);
                var modelsRoot = Path.Combine(_selectedDrive.RootPath, SsdLayout.Models);

                // Start a controlled temporary server once for the whole batch.
                serverHandle = await _ollamaPackageService.StartTemporaryServerAsync(
                    ollamaExe, modelsRoot, AppendLog, CancellationToken.None);

                foreach (var selectedRow in rowsPresentOnDrive)
                {
                    try
                    {
                        AppendLog($"Deleting {selectedRow.Name} from disk with ollama rm...");
                        await _modelService.DeleteModelAsync(ollamaExe, modelsRoot, selectedRow.Name, AppendLog, CancellationToken.None, serverHandle.Host);

                        var model = config.Models.FirstOrDefault(m => string.Equals(m.Name, selectedRow.Name, StringComparison.OrdinalIgnoreCase));
                        if (model is not null)
                        {
                            config.Models.Remove(model);
                            configDirtyForDelete = true;
                            AppendLog($"{selectedRow.Name}: deleted from disk and removed from configuration.");
                        }
                        else
                        {
                            AppendLog($"{selectedRow.Name}: deleted from disk.");
                        }
                    }
                    catch (Exception ex)
                    {
                        deleteFailures.Add($"{selectedRow.Name}: {ex.Message}");
                        AppendLog($"Delete failed for {selectedRow.Name}: {ex.Message}");
                    }
                }
            }

            if (configDirtyForDelete)
                await _modelService.SaveConfigAsync(configPath, config);

            if (deleteFailures.Count == 1)
            {
                _dialogService.ShowError($"Failed to delete model from disk: {deleteFailures[0]}", "Delete failed");
            }
            else if (deleteFailures.Count > 1)
            {
                var message =
                    "Failed to delete some models from disk:"
                    + Environment.NewLine + Environment.NewLine
                    + string.Join(Environment.NewLine, deleteFailures.Select(f => $"- {f}"));
                _dialogService.ShowError(message, "Delete failed");
            }
        }
        finally
        {
            serverHandle?.Dispose();
            SetModelOperationState(false);
            await RefreshModelStatusesAsync();
        }
    }

    private void CancelOperation()
    {
        _modelOperationCts?.Cancel();
        AppendLog("Cancellation requested for current model operation.");
    }

    private async Task FormatPrepareAsync()
    {
        if (_selectedDrive is null)
        {
            AppendLog("Select a target drive first.");
            return;
        }
        if (!EnsureWritable("Format & Prepare Drive")) return;

        var root = _selectedDrive.RootPath;

        // Refuse to format the drive the PrepApp itself is running from —
        // that would wipe the running executable out from under us.
        var appRoot = Path.GetPathRoot(AppContext.BaseDirectory);
        if (!string.IsNullOrEmpty(appRoot) &&
            string.Equals(Path.GetPathRoot(root), appRoot, StringComparison.OrdinalIgnoreCase))
        {
            _dialogService.ShowError(
                $"Cannot format {root} because PrepApp is running from that drive.{Environment.NewLine}" +
                "Move PrepApp to another drive and try again.",
                "Self-format blocked");
            AppendLog($"Format aborted: PrepApp is running from {appRoot}.");
            return;
        }

        if (_selectedDrive.IsFixed)
        {
            if (!_dialogService.ConfirmFixedDrive(root))
            {
                AppendLog("Format cancelled by user.");
                return;
            }
        }

        var fileSystem = ResolveFileSystem(GetSelectedPrepTargets());

        if (!_dialogService.ConfirmErase(root,
            _driveService.GetFreeDiskSpaceGb(root)?.ToString() ?? "unknown",
            fileSystem))
        {
            AppendLog("Format cancelled by user.");
            return;
        }

        if (!_elevationService.IsElevated())
        {
            var relaunch = _dialogService.Confirm(
                "Formatting a drive requires administrator privileges." + Environment.NewLine + Environment.NewLine +
                "Relaunch PrepApp as administrator now? You'll be prompted by Windows to approve.",
                "Administrator required");
            if (!relaunch)
            {
                AppendLog("Format cancelled: administrator privileges required.");
                return;
            }

            try
            {
                // Forward the current format intent across the UAC gap so
                // the elevated instance can auto-resume (subject to the
                // ConfirmErase dialog as the non-negotiable safety gate)
                // instead of leaving the user staring at a fresh PrepApp
                // window wondering why their format didn't proceed.
                var relaunchArgs = new List<string>
                {
                    $"--autoresume-format={root}",
                    $"--autoresume-label={_volumeLabel ?? string.Empty}"
                };
                if (_diagEnabled) relaunchArgs.Add("--diag");

                if (!_elevationService.TryRelaunchElevated(relaunchArgs))
                {
                    AppendLog("Format cancelled: UAC prompt was declined.");
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"Could not relaunch as administrator: {ex.Message}", "Elevation failed");
                AppendLog($"Elevated relaunch failed: {ex.Message}");
            }
            return;
        }

        // Mark the whole format+prepare flow as a busy operation so the
        // other mutating commands (Download, Remove, Finalize) are
        // gated by CanMutateDrive while Format-Volume is running. Without
        // this, a user could kick off a download against a drive root that's
        // mid-erase and either fail against a disappearing volume or
        // repopulate the drive before SaveConfigAsync lands.
        SetModelOperationState(true, $"Formatting {root}...");

        // B3-Redux diagnostic sidecar — only opened when the --diag flag
        // was passed at launch. Duplicates every log line written during
        // the format flow into a text file on disk because the UI
        // LogListBox doesn't support free-text selection. Default runs
        // skip this entirely (clean log, no temp file).
        var diagPath = Path.Combine(Path.GetTempPath(), "freeai-format-diagnostic.log");
        StreamWriter? diagSink = null;
        if (_diagEnabled)
        {
            try
            {
                diagSink = new StreamWriter(diagPath, append: false)
                {
                    AutoFlush = true
                };
                diagSink.WriteLine($"# B3-Redux diagnostic log — started {DateTime.Now:O}");
            }
            catch (Exception ex)
            {
                AppendLog($"[diag] Could not open sidecar log at {diagPath}: {ex.Message}");
            }
        }

        void DiagLog(string line)
        {
            AppendLog(line);
            try { diagSink?.WriteLine(line); } catch { /* best-effort */ }
        }

        try
        {
            ProgressIsIndeterminate = true;

            var preLabel = _selectedDrive.VolumeLabel;
            if (_diagEnabled)
            {
                DiagLog("=== B3-Redux diagnostic snapshot (pre-format) ===");
                DiagLog($"Sidecar log file     : {diagPath}");
                DiagLog($"Selected root        : {root}");
                DiagLog($"Selected label (pre) : \"{preLabel}\"");
                DiagLog($"Requested label      : \"{_volumeLabel}\"");
                DiagLog($"IsFixed              : {_selectedDrive.IsFixed}");
                DiagLog($"IsElevated (at call) : {_elevationService.IsElevated()}");
                DiagLog($"PrepApp base dir     : {AppContext.BaseDirectory}");
                DiagLog($"PrepApp drive root   : {Path.GetPathRoot(AppContext.BaseDirectory)}");
            }
            AppendLog($"Formatting {root} as {fileSystem} (label: '{_volumeLabel}')...");

            await _driveService.FormatAsync(
                root,
                _volumeLabel,
                fileSystem,
                onOutput: DiagLog,
                verboseDiagnostics: _diagEnabled,
                ct: CancellationToken.None);

            StatusText = "Preparing drive structure...";
            _driveService.EnsureSsdStructure(root);

            var configPath = GetConfigPath(root);
            var config = new PortableConfig
            {
                PreparedAtUtc = DateTime.UtcNow,
                OllamaPort = 11434,
                PreferredCompute = "cpu"
            };
            await _modelService.SaveConfigAsync(configPath, config);

            AppendLog($"Drive formatted and structure created on {root}.");

            // Re-enumerate drives so the dropdown picks up the new volume
            // label and post-format free-bytes instead of stale metadata
            // captured before the format. Preserve selection by root path
            // so the user's choice doesn't jump to Drives[0].
            Drives = _driveService.GetCandidateDrives(_showFixedDrives);
            SelectedDrive = Drives.FirstOrDefault(d =>
                string.Equals(d.RootPath, root, StringComparison.OrdinalIgnoreCase))
                ?? (Drives.Count > 0 ? Drives[0] : null);

            if (_diagEnabled)
            {
                DiagLog("=== B3-Redux diagnostic snapshot (post-format) ===");
                DiagLog($"Enumerated drives    : {Drives.Count}");
                foreach (var d in Drives)
                {
                    DiagLog($"  {d.RootPath} label=\"{d.VolumeLabel}\" fixed={d.IsFixed}");
                }
                var selRoot = SelectedDrive?.RootPath ?? "(null)";
                var selLabel = SelectedDrive?.VolumeLabel ?? "(null)";
                DiagLog($"Selected after       : {selRoot}");
                DiagLog($"Selected label (post): \"{selLabel}\"");
                DiagLog($"Root letter match    : {string.Equals(selRoot, root, StringComparison.OrdinalIgnoreCase)}");
                DiagLog($"Label actually chgd  : {!string.Equals(preLabel, selLabel, StringComparison.Ordinal)}");
                DiagLog("=== end B3-Redux diagnostic ===");
                AppendLog($"[diag] Full diagnostic log saved to: {diagPath}");
            }

            await RefreshModelStatusesAsync();
            SetModelOperationState(false, "Drive prepared");
        }
        catch (Exception ex)
        {
            AppendLog($"Drive preparation failed: {ex.Message}");
            if (_diagEnabled)
            {
                DiagLog($"Exception type       : {ex.GetType().FullName}");
                DiagLog($"Stack trace          :{Environment.NewLine}{ex.StackTrace}");
                AppendLog($"[diag] Full diagnostic log saved to: {diagPath}");
            }
            _dialogService.ShowError(ex.Message, "Format failed");
            SetModelOperationState(false, "Prepare failed");
        }
        finally
        {
            ProgressIsIndeterminate = false;
            try { diagSink?.Dispose(); } catch { /* best-effort */ }
        }
    }

    private async Task FinalizeAsync()
    {
        if (_isModelOperationRunning)
        {
            AppendLog("Finalize is disabled while model operations are running.");
            return;
        }
        if (_selectedDrive is null)
        {
            AppendLog("Select a target drive first.");
            return;
        }
        if (!TryGetFinalizeProfile(out var selectedProfile))
        {
            return;
        }
        if (!EnsureWritable("Finalize SSD")) return;

        if (_selectedDrive.IsFixed)
        {
            if (!_dialogService.ConfirmFixedDrive(_selectedDrive.RootPath))
            {
                AppendLog("Finalize cancelled.");
                return;
            }
        }

        var root = _selectedDrive.RootPath;
        try
        {
            ProgressValue = 0;
            StatusText = "Preparing folders...";
            _driveService.EnsureSsdStructure(root);

            var configPath = GetConfigPath(root);
            var config = await _modelService.LoadConfigAsync(configPath);
            config.PreparedAtUtc = DateTime.UtcNow;
            config.OllamaPort = 11434;
            config.PreferredCompute = "cpu";
            config.IsEncrypted = false;
            config.EncryptionScheme = null;
            // MAC34: ensure NetworkApiKey is populated before any persist.
            // Pre-MAC34, this defaulted to "" with NetworkRequireApiKey=true,
            // so toggling Network Mode would 503 every request via the
            // RunnerLocalApiService fail-closed guard. Mirror the Mac
            // PrepApp's EncryptedConfigWriter.generateRandomApiKey behavior:
            // 32 bytes of OS RNG, hex-encoded, set once at first prep.
            // Existing keys are preserved (idempotent re-finalize).
            if (string.IsNullOrWhiteSpace(config.NetworkApiKey))
            {
                config.NetworkApiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
                    .ToLowerInvariant();
            }

            // C27 Stage 3: thread the user-entered HF token through. Trim
            // whitespace; empty input *preserves* any existing token on
            // the drive (re-finalize is idempotent for credentials —
            // the user shouldn't have to re-type the token every time).
            // Mirrors the NetworkApiKey idempotent-re-finalize posture
            // a few lines above.
            var hfToken = (_huggingFaceTokenInput ?? string.Empty).Trim();
            if (hfToken.Length > 0)
            {
                config.HuggingFaceToken = hfToken;
            }

            await _modelService.SaveConfigAsync(configPath, config);
            await RefreshModelStatusesAsync();

            var installedCount = config.Models.Count(m => m.Status == ModelInstallStatus.Installed);
            if (installedCount == 0)
            {
                _dialogService.ShowWarning(
                    "Cannot finalize SSD with zero installed models. Use Model Manager to pull at least one model first.",
                    "Finalize blocked");
                AppendLog("Finalize blocked: zero installed models.");
                StatusText = "Finalize blocked";
                return;
            }

            // C2: best-effort embed-model guard. DownloadAsync's tail also
            // runs this, but a user can reach Finalize via paths that skip
            // Download (e.g. drive already had chat models on disk before
            // the user opened PrepApp). Idempotent — disk-truth check skips
            // the pull when the embedder is already present, so re-finalize
            // is free.
            await EnsureEmbeddingModelOnFinalizeAsync(root, configPath, config.EmbeddingModelName);

            CheckMacArtifactAvailability();

            var targets = GetSelectedPrepTargets();
            if (targets == PrepTargets.None)
            {
                _dialogService.ShowWarning("Select at least one prep target (Windows and/or macOS).", "Finalize blocked");
                AppendLog("Finalize blocked: no prep target selected.");
                return;
            }

            if (targets.HasFlag(PrepTargets.Windows))
            {
                await _ollamaPackageService.EnsureOllamaReadyAsync(root, AppendLog, null, CancellationToken.None);

                StatusText = "Staging offline prerequisites...";
                await _prereqService.StagePrerequisitesAsync(root, AppendLog, CancellationToken.None);

                StatusText = "Staging Windows runner payload...";
                await _artifactStagingService.StageRunnerAsync(root, AppendLog);

                if (InstallVrCompanion)
                {
                    if (CompanionHostPort < 1 || CompanionHostPort > 65535)
                    {
                        _dialogService.ShowWarning("Companion host port must be between 1 and 65535.", "Finalize blocked");
                        StatusText = "Finalize blocked";
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(CompanionHostAddress))
                    {
                        var hostType = Uri.CheckHostName(CompanionHostAddress.Trim());
                        if (hostType == UriHostNameType.Unknown)
                        {
                            _dialogService.ShowWarning("Companion host must be a dotted-quad IPv4 or a resolvable hostname.", "Finalize blocked");
                            StatusText = "Finalize blocked";
                            return;
                        }
                    }

                    StatusText = "Staging VR companion payload...";
                    await _artifactStagingService.StageCompanionAsync(root, AppendLog);

                    var companionDir = Path.Combine(root, "companion");
                    var companionConfig = new CompanionConfig
                    {
                        HostAddress = CompanionHostAddress,
                        HostPort = CompanionHostPort,
                        ApiKey = config.NetworkApiKey,
                        PttBinding = string.Empty,
                        AutoReconnect = true,
                        SchemaVersion = 1
                    };
                    companionConfig.Save(Path.Combine(companionDir, "companion-config.json"));

                    var readmeLines = new[]
                    {
                        "Free AI SSD VR Companion Quick Setup",
                        "1. Plug this SSD into your VR PC.",
                        "2. Open the companion folder and run FreeAiSsd.Companion.exe.",
                        "3. Verify HostAddress/HostPort in companion-config.json or Settings.",
                        "4. Hold your configured PTT binding while speaking in DCS.",
                        "5. Release PTT to send /api/voice/query to the host Runner.",
                        "6. AI response audio plays on this VR PC output device."
                    };
                    await File.WriteAllLinesAsync(Path.Combine(companionDir, "README-VR.txt"), readmeLines);
                    AppendLog("VR companion staged to SSD:/companion.");
                }
            }

            if (targets.HasFlag(PrepTargets.Mac))
            {
                if (!_artifactStagingService.AreMacArtifactsAvailable(out var macProblem))
                {
                    var message = macProblem ?? "macOS artifacts are unavailable.";
                    AppendLog($"Finalize blocked: {message}");
                    _dialogService.ShowWarning(message, "macOS prep unavailable");
                    StatusText = "Finalize blocked";
                    return;
                }

                StatusText = "Staging macOS Runner...";
                await _artifactStagingService.StageMacRunnerAsync(root, AppendLog, CancellationToken.None);
                StatusText = "Staging macOS Ollama runtime...";
                await _artifactStagingService.StageMacOllamaAsync(root, AppendLog, CancellationToken.None);
            }

            var readiness = await _readinessService.RunReadinessChecksAsync(root, AppendLog, CancellationToken.None);
            RefreshReadinessItems(readiness);
            if (readiness.Any(r => !r.Passed))
            {
                var failures = string.Join(Environment.NewLine, readiness.Where(r => !r.Passed).Select(r => $"- {r.Check}: {r.Result}"));
                _dialogService.ShowWarning(
                    $"Cannot finalize SSD yet. Missing or invalid items:{Environment.NewLine}{failures}",
                    "SSD readiness check failed");
                StatusText = "Finalize blocked (readiness failed)";
                return;
            }

            config.ActiveProfile = selectedProfile;
            ProfileDefaults.Apply(config, selectedProfile);

            if (_enableEncryption)
            {
                var passphrase = _dialogService.PromptForEncryptionPassword();
                if (passphrase is null)
                {
                    AppendLog("Finalize cancelled: encryption passphrase setup cancelled.");
                    StatusText = "Finalize blocked";
                    return;
                }

                config.IsEncrypted = true;
                config.EncryptionScheme = SsdEncryption.SchemeName;
                await _encryptionService.EnableConfigEncryptionAsync(root, config, passphrase);
                AppendLog("Drive encryption enabled. Runner will now require unlock before use.");
            }
            else
            {
                await _modelService.SaveConfigAsync(configPath, config);
            }

            ProgressValue = 100;
            StatusText = "Complete";
            AppendLog("SSD finalized successfully.");

            // MAC32: pre-MAC32 Finalize ended silently — the user stayed on
            // the prep tab with no clear "you're done" affordance, so the
            // v1.3.10 mac field test reported Finish as broken on Mac and
            // ambiguous on Windows. Modal pops only on the full success
            // path; every early-return failure branch above bails before
            // reaching here. Copy mirrors mac-prep-app DoneStepView so
            // cross-OS docs cite one phrase.
            _dialogService.ShowInfo(
                "Your SSD is ready. Open Runner.exe on this SSD to start chatting.",
                "Setup complete");
        }
        catch (Exception ex)
        {
            StatusText = "Finalize failed";
            AppendLog($"Finalize failed: {ex.Message}");
        }
    }

    private async Task CheckPrereqUpdatesAsync()
    {
        if (_selectedDrive is null)
        {
            AppendLog("Select a target drive first.");
            return;
        }
        if (!EnsureWritable("Check prerequisite updates")) return;

        PrereqStatusText = "Prereqs: updating";
        try
        {
            await _prereqService.UpdatePrereqsOnlineAsync(_selectedDrive.RootPath, AppendLog, CancellationToken.None);
            PrereqStatusText = "Prereqs: up-to-date";
            AppendLog("Prereq update check complete.");
        }
        catch (Exception ex)
        {
            PrereqStatusText = "Prereqs: failed";
            AppendLog($"Prereq update check failed: {ex.Message}");
        }
    }

    private async Task CheckReadinessAsync()
    {
        if (_selectedDrive is null)
        {
            AppendLog("Select a target drive first.");
            return;
        }
        if (!EnsureWritable("Check SSD readiness")) return;

        var checks = await _readinessService.RunReadinessChecksAsync(_selectedDrive.RootPath, AppendLog, CancellationToken.None);
        RefreshReadinessItems(checks);

        var message = string.Join(Environment.NewLine, checks.Select(c => $"[{(c.Passed ? '✓' : '✗')}] {c.Check}: {c.Result}"));
        if (checks.All(c => c.Passed))
            _dialogService.ShowInfo(message, "SSD Readiness");
        else
            _dialogService.ShowWarning(message, "SSD Readiness");
    }

    public async Task RefreshModelStatusesAsync()
    {
        if (_selectedDrive is null)
        {
            // Even without a drive, surface the starter catalog so the
            // merged grid isn't empty on first launch. Sizing warnings
            // are "OK" placeholder — they require a drive root to compute.
            ModelRows.Clear();
            foreach (var row in BuildStarterOnlyRows(freeDiskGb: null, takenNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
                ModelRows.Add(row);
            return;
        }

        var configPath = GetConfigPath(_selectedDrive.RootPath);
        var config = await _modelService.LoadConfigAsync(configPath);

        // Recover stale "Downloading" statuses left behind by a crash or forced exit.
        // If no model operation is currently running, any model still marked as
        // Downloading was interrupted and should be reset to NotInstalled.
        if (!_isModelOperationRunning)
        {
            var staleModels = config.Models.Where(m => m.Status == ModelInstallStatus.Downloading).ToList();
            if (staleModels.Count > 0)
            {
                foreach (var stale in staleModels)
                {
                    stale.Status = ModelInstallStatus.NotInstalled;
                    stale.Sha256 = null;
                    stale.SizeBytes = null;
                    stale.LastVerifiedUtc = null;
                    AppendLog($"Recovered stale download status for '{stale.Name}' → NotInstalled.");
                }
                await _modelService.SaveConfigAsync(configPath, config);
            }
        }

        var discovered = _modelService.DiscoverModelsOnDisk(Path.Combine(_selectedDrive.RootPath, SsdLayout.Models));
        var freeDiskGb = _driveService.GetFreeDiskSpaceGb(_selectedDrive.RootPath);

        var rows = BuildModelGridRows(config.Models, discovered, freeDiskGb);
        ModelRows.Clear();
        foreach (var row in rows)
            ModelRows.Add(row);
    }

    private List<ModelGridRow> BuildModelGridRows(
        IEnumerable<ModelConfigEntry> configModels,
        IReadOnlyCollection<string> discoveredOnDisk,
        int? freeDiskGb)
    {
        var rows = new List<ModelGridRow>();
        var configured = configModels.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var catalogByTag = _starterCatalog.ToDictionary(e => e.Tag, StringComparer.OrdinalIgnoreCase);

        foreach (var model in configured)
        {
            var onDisk = discoveredOnDisk.Contains(model.Name);
            var state = DetermineConfiguredState(model, onDisk);
            var warnings = _modelService.GetSizingWarnings(model.Name, freeDiskGb, _systemRamGb, _gpuVramGb);
            var meta = LookupStarterMeta(catalogByTag, model.Name);
            rows.Add(new ModelGridRow(
                model.Name,
                state,
                "Config",
                warnings.Count == 0 ? "OK" : string.Join("; ", warnings),
                model.SizeBytes.HasValue ? FormatSize(model.SizeBytes.Value) : "—",
                string.IsNullOrWhiteSpace(model.Sha256) ? "—" : model.Sha256[..Math.Min(8, model.Sha256.Length)],
                model.LastVerifiedUtc.HasValue ? model.LastVerifiedUtc.Value.ToLocalTime().ToString("u") : "—",
                isOnDiskOnly: false,
                isPresentOnDrive: onDisk,
                tier: meta.tier,
                bestAt: meta.bestAt,
                pullCount: meta.pullCount,
                capabilities: meta.capabilities,
                parametersBillion: meta.parametersBillion,
                lastUpdated: meta.lastUpdated,
                sourceKind: meta.source));
        }

        var configuredNames = new HashSet<string>(configured.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var discovered in discoveredOnDisk.Where(d => !configuredNames.Contains(d)).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var warnings = _modelService.GetSizingWarnings(discovered, freeDiskGb, _systemRamGb, _gpuVramGb);
            var meta = LookupStarterMeta(catalogByTag, discovered);
            rows.Add(new ModelGridRow(
                discovered,
                "On drive only",
                "Disk",
                warnings.Count == 0 ? "OK" : string.Join("; ", warnings),
                "—", "—", "—",
                isOnDiskOnly: true,
                isPresentOnDrive: true,
                tier: meta.tier,
                bestAt: meta.bestAt,
                pullCount: meta.pullCount,
                capabilities: meta.capabilities,
                parametersBillion: meta.parametersBillion,
                lastUpdated: meta.lastUpdated,
                sourceKind: meta.source));
        }

        // Third pass: recommended starters that aren't in config or on-disk.
        // Lets the merged grid surface Small/Medium/Large groupings before
        // the user has downloaded anything. IsSelected default = false.
        var taken = new HashSet<string>(configuredNames, StringComparer.OrdinalIgnoreCase);
        foreach (var name in discoveredOnDisk) taken.Add(name);
        foreach (var starter in BuildStarterOnlyRows(freeDiskGb, taken))
            rows.Add(starter);

        return rows;
    }

    private IEnumerable<ModelGridRow> BuildStarterOnlyRows(int? freeDiskGb, HashSet<string> takenNames)
    {
        foreach (var entry in _starterCatalog
                     .Where(e => !takenNames.Contains(e.Tag))
                     .OrderBy(e => e.Tag, StringComparer.OrdinalIgnoreCase))
        {
            var warnings = _modelService.GetSizingWarnings(entry.Tag, freeDiskGb, _systemRamGb, _gpuVramGb);
            var row = new ModelGridRow(
                entry.Tag,
                "Not downloaded",
                "Recommended",
                warnings.Count == 0 ? "OK" : string.Join("; ", warnings),
                // C27 Stage 4: quant child rows carry a real size from
                // siblings[].lfs.size; parents still render "—".
                entry.QuantSizeBytes is { } q && q > 0 ? FormatSize(q) : "—",
                "—", "—",
                isOnDiskOnly: false,
                isPresentOnDrive: false,
                tier: entry.SizeTier,
                bestAt: entry.BestAt,
                pullCount: entry.PullCount,
                capabilities: entry.Capabilities ?? Array.Empty<string>(),
                parametersBillion: entry.ParametersBillion,
                lastUpdated: entry.LastUpdated,
                sourceKind: entry.Source,
                isExpandable: entry.IsExpandable,
                parentRepoId: entry.ParentRepoId,
                quantLabel: entry.QuantLabel,
                quantSizeBytes: entry.QuantSizeBytes);
            row.IsSelected = false;
            yield return row;
        }
    }

    private readonly record struct StarterMeta(
        string tier,
        string bestAt,
        long? pullCount,
        IReadOnlyList<string> capabilities,
        double? parametersBillion,
        DateTimeOffset? lastUpdated,
        ModelSource source);

    private static StarterMeta LookupStarterMeta(
        Dictionary<string, StarterCatalogEntry> catalogByTag, string name)
    {
        if (catalogByTag.TryGetValue(name, out var entry))
        {
            return new StarterMeta(
                entry.SizeTier,
                entry.BestAt,
                entry.PullCount,
                entry.Capabilities ?? Array.Empty<string>(),
                entry.ParametersBillion,
                entry.LastUpdated,
                entry.Source);
        }
        return new StarterMeta("Custom", string.Empty, null, Array.Empty<string>(), null, null, ModelSource.Ollama);
    }

    private void RefreshReadinessItems(List<ReadinessItem> checks)
    {
        ReadinessItems.Clear();
        foreach (var check in checks)
            ReadinessItems.Add(check);
    }

    private void SetModelOperationState(bool running, string? status = null)
    {
        IsModelOperationRunning = running;
        if (!string.IsNullOrWhiteSpace(status))
            StatusText = status;
    }

    private bool TryGetFinalizeProfile(out UserProfile selectedProfile)
    {
        if (_selectedProfile is UserProfile profile)
        {
            ProfileSelectionWarning = string.Empty;
            selectedProfile = profile;
            return true;
        }

        const string message =
            "Choose a Runner profile before finishing setup. Flight Sim enables DCS bindings, HOTAS push-to-talk, and voice defaults; General Assistant keeps the runtime chat-first.";
        ProfileSelectionWarning = message;
        _dialogService.ShowWarning(message, "Profile required");
        AppendLog("Finalize blocked: no profile selected.");
        StatusText = "Finalize blocked";
        selectedProfile = default;
        return false;
    }

    private void AppendLog(string message)
    {
        if (_uiSyncContext is null || SynchronizationContext.Current == _uiSyncContext)
        {
            LogLines.Add(message);
        }
        else
        {
            _uiSyncContext.Post(_ => LogLines.Add(message), null);
        }

        _logService.AppendLog(message);
    }

    /// <summary>
    /// Thread-safe setter for <see cref="PullProgressLine"/>.
    /// Mirrors <see cref="AppendLog"/>'s dispatch pattern because the
    /// onProgress lambda fires from <c>OllamaPullClient</c>'s HTTP
    /// response-drain thread, not the UI thread. WPF binding can
    /// usually route a string PropertyChanged across threads on its
    /// own, but going through the sync context keeps the entire VM
    /// consistent and avoids surprising the binding engine when the
    /// surrounding SetProperty triggers other reactive code.
    /// </summary>
    private void SetPullProgressLineSafe(string line)
    {
        if (_uiSyncContext is null || SynchronizationContext.Current == _uiSyncContext)
        {
            PullProgressLine = line;
        }
        else
        {
            _uiSyncContext.Post(_ => PullProgressLine = line, null);
        }
    }

    private void RaiseAllCommandsCanExecuteChanged()
    {
        OnPropertyChanged(nameof(CanMutateDrive));
        OnPropertyChanged(nameof(HasDriveSelected));
        AddModelCommand.RaiseCanExecuteChanged();
        AddOrphanToConfigCommand.RaiseCanExecuteChanged();
        DownloadCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
        CancelOperationCommand.RaiseCanExecuteChanged();
        FormatPrepareCommand.RaiseCanExecuteChanged();
        FinalizeCommand.RaiseCanExecuteChanged();
        CheckPrereqUpdatesCommand.RaiseCanExecuteChanged();
        CheckReadinessCommand.RaiseCanExecuteChanged();
    }

    private static string GetConfigPath(string root) => Path.Combine(root, new PortableConfig().ConfigRelativePath);

    private static string DetermineConfiguredState(ModelConfigEntry model, bool onDisk)
    {
        return model.Status switch
        {
            ModelInstallStatus.Installed => "Downloaded",
            ModelInstallStatus.Downloading => "Downloading…",
            ModelInstallStatus.Failed => "Failed — retry",
            ModelInstallStatus.NotInstalled when !onDisk => "Not downloaded",
            _ => "Not downloaded"
        };
    }

    internal static string FormatSize(long sizeBytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = sizeBytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.##} {1}", size, units[unit]);
    }
}
