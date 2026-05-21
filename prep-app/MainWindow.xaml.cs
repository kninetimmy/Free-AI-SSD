using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using FreeAiSsd.PrepApp.Services;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Models;
using FreeAiSsd.Shared.ViewModels;

namespace FreeAiSsd.PrepApp;

public partial class MainWindow : Window
{
    private readonly PrepViewModel _viewModel;
    private PrepTargetPreferenceStore? _prefStore;

    // F2: live catalog fetcher. Owns its own HttpClient — the service is
    // long-lived (MainWindow == app lifetime) and the fetch only fires
    // on user click, so HttpClientFactory churn isn't worth the wiring.
    private readonly LiveModelCatalogService _liveModelCatalogService = new();
    // C27 Stage 1: same posture for Hugging Face. Long-lived; owns its
    // own HttpClient. Cache is per-instance, so reusing the field
    // across source switches preserves hot-results between toggles.
    private readonly HuggingFaceCatalogService _hfCatalogService = new();
    private bool _isRefreshingCatalog;
    // C27 Stage 1: in-flight CTS so toggling source mid-refresh cancels
    // the stale fetch. The plan's "cancel and restart" decision avoids
    // a race where a slow Ollama fetch lands after the user switched to
    // HF and overwrites the HF catalog with stale Ollama rows.
    private CancellationTokenSource? _activeRefreshCts;
    // C27 Stage 1: 350ms debounce on the search box when ActiveSource
    // is HuggingFace — HF needs server-side search. Local Ollama
    // filtering is unchanged (the textbox keeps driving
    // ModelSearchText / IsModelRowVisible).
    private DispatcherTimer? _hfSearchDebounceTimer;
    private static readonly TimeSpan HfSearchDebounce = TimeSpan.FromMilliseconds(350);

    // View-layer step machine (PrepFlowStep). The shared PrepViewModel
    // has no step concept; this controller only toggles which step panel
    // is visible and configures the persistent header/footer. "Manage
    // models" is the Models step entered with _isManageMode set (reached
    // off the linear path from the already-configured-drive banner).
    private PrepFlowStep _currentStep = PrepFlowStep.Welcome;
    private bool _isManageMode;

    // Loaded from preferences and written back unchanged. The FTUE
    // spotlight tour was removed in favour of the step flow itself; this
    // field is kept only so the on-disk PrepPreferenceSnapshot schema is
    // unchanged (no settings migration).
    private bool _ftueCompleted;

    public MainWindow()
    {
        // The VM must exist before InitializeComponent() runs: XAML
        // ComboBoxes with IsSelected="True" defaults fire their
        // SelectionChanged handlers during parse, and those handlers
        // dereference _viewModel. Keeping construction here also means
        // any future XAML-fired event sees a real VM.
        var logLines = new ObservableCollection<string>();
        var logService = new LogService(logLines, Dispatcher);
        var dialogService = new DialogService(() => this);
        var driveService = new DriveService();
        var modelService = new ModelService();
        var ollamaPackageService = new OllamaPackageService();
        var prereqService = new PrereqService(dialogService);
        var artifactStagingService = new ArtifactStagingService();
        var readinessService = new ReadinessService(modelService);
        var encryptionService = new EncryptionService();
        var elevationService = new WindowsElevationService();
        var piperStagingService = new PiperStagingService();

        _viewModel = new PrepViewModel(
            driveService,
            modelService,
            ollamaPackageService,
            prereqService,
            artifactStagingService,
            readinessService,
            encryptionService,
            dialogService,
            logService,
            elevationService,
            piperStagingService);

        InitializeComponent();

        _viewModel.SystemRamGb = SystemResources.GetTotalSystemRamGb();
        _viewModel.GpuVramGb = SystemResources.GetGpuVramGb();

        // C27 Stage 2: VM stays free of an IHuggingFaceCatalogService
        // dependency (HF service lives in prep-core, downstream of the
        // shared assembly that hosts the VM). The view-host owns the
        // service and feeds the VM through a delegate hook.
        _viewModel.HuggingFaceSizingWarningsHook = FetchHuggingFaceSizingWarningsAsync;
        // C27 Stage 3: when the user edits the inline HF token field,
        // push the new value into the catalog service so subsequent
        // search / siblings requests carry the Bearer header. The token
        // also lands in PortableConfig.HuggingFaceToken at finalize time.
        _viewModel.HuggingFaceTokenChanged += OnHuggingFaceTokenChanged;
        // C27 Stage 4: lazy per-quant row expansion. The VM fires this
        // hook when the user clicks the chevron on an HF parent row;
        // we project siblings[] into one StarterCatalogEntry per GGUF
        // quant and the VM inserts the resulting child rows.
        _viewModel.HuggingFaceQuantExpansionHook = FetchHuggingFaceQuantsAsync;

        // Thread the parsed command-line intent through to the view
        // model so the elevation banner binds correctly and the
        // auto-resume-format path can fire after Initialize() runs.
        var startup = App.StartupArgs;
        _viewModel.ApplyStartupIntent(
            startup.AutoResumeFormatRoot,
            startup.AutoResumeLabel,
            startup.DiagEnabled);

        DataContext = _viewModel;

        _viewModel.LogLines.CollectionChanged += LogLines_CollectionChanged;

        // C6: banner's Manage-models button raises this event; enter the
        // Models step in manage mode (off the linear path). VM has no
        // reference to the view's step machine, hence the event hook.
        _viewModel.ModelsTabRequested += (_, _) => EnterManageModels();

        // C7: banner's Unlock button raises this event; show the modal
        // passphrase prompt and hand a successful unlock back to the VM
        // via ApplyUnlockResult. Failure paths re-show the dialog with an
        // inline error so the user can retry without re-clicking Unlock.
        _viewModel.UnlockRequested += OnUnlockRequested;

        ShowModelDetailsToggle.Checked += OnShowModelDetailsChanged;
        ShowModelDetailsToggle.Unchecked += OnShowModelDetailsChanged;

        Loaded += OnWindowLoaded;
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.Initialize();

        await LoadStarterCatalogAsync();

        _prefStore = new PrepTargetPreferenceStore();
        var pref = _prefStore.LoadSettings();
        _viewModel.PrepareWindows = pref.PrepTargets.HasFlag(PrepTargets.Windows);
        _viewModel.PrepareMac = pref.PrepTargets.HasFlag(PrepTargets.Mac);
        _viewModel.InstallVrCompanion = pref.InstallVrCompanion;
        _viewModel.CompanionHostAddress = pref.CompanionHostAddress;
        _viewModel.CompanionHostPort = pref.CompanionHostPort;
        _viewModel.SelectedProfile = pref.SelectedProfile;

        _ftueCompleted = pref.FtueCompleted;
        SyncProfileSelectionCards();

        _viewModel.OnPreferenceStateChanged = SaveCurrentPreferences;

        // Re-evaluate footer gating (Continue enable/disable) whenever the
        // VM signals a change to a step's precondition.
        _viewModel.PropertyChanged += OnStepGatingPropertyChanged;

        // A relaunch mid-format (elevated auto-resume) should land on the
        // step whose log surfaces the operation, not the welcome screen.
        GoToStep(_viewModel.HasAutoResumeIntent
            ? PrepFlowStep.FormatSetup
            : PrepFlowStep.Welcome);

        // Fire auto-resume after Initialize() has loaded drives and after
        // the initial step is set. If no intent was passed this is a no-op.
        _ = _viewModel.TryAutoResumeFormatAsync();
    }

    private async Task LoadStarterCatalogAsync()
    {
        var loadResult = StarterModelCatalogLoader.Load(AppContext.BaseDirectory);
        if (!string.IsNullOrWhiteSpace(loadResult.Warning))
        {
            StarterCatalogWarningText.Text = loadResult.Warning;
            StarterCatalogWarningText.Visibility = Visibility.Visible;
        }
        else
        {
            StarterCatalogWarningText.Text = string.Empty;
            StarterCatalogWarningText.Visibility = Visibility.Collapsed;
        }

        await _viewModel.SetStarterCatalogAsync(ProjectCatalog(loadResult.Catalog));

        // C27 Stage 1: subscribe once so a Source dropdown change kicks
        // off the appropriate refetch. Wiring lives in the OnLoaded
        // path so the VM is fully constructed before we attach.
        _viewModel.ActiveSourceChanged += OnActiveSourceChanged;
        // C27 Stage 1: ModelSearchText fires on every keystroke; under
        // the HF source we debounce and re-fire search-hf rather than
        // hitting the API on each character.
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Group the merged grid by Tier so Small / Medium / Large / Custom
        // show as visual sections (same affordance the pre-merge Starter
        // grid offered, now applied to the unified ModelRows collection).
        var collectionView = new ListCollectionView(_viewModel.ModelRows);
        collectionView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ModelGridRow.Tier)));
        // F2a: route picker filter state through the VM. Filter callback
        // returns true for a row when it passes the search + popular
        // checks; the VM raises ModelRowsViewInvalidated on every state
        // change so we can refresh the live view in place.
        collectionView.Filter = item => item is not ModelGridRow row || _viewModel.IsModelRowVisible(row);
        ApplySortDescriptions(collectionView, _viewModel.SortMode);
        _viewModel.ModelRowsViewInvalidated += (_, _) =>
        {
            // CollectionView.Refresh must run on the dispatcher thread —
            // the event may fire from a background catalog reload.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplySortDescriptions(collectionView, _viewModel.SortMode);
                collectionView.Refresh();
            }));
        };
        ModelStatusGrid.ItemsSource = collectionView;
    }

    /// <summary>
    /// C5: translate the VM's <see cref="ModelSortMode"/> into
    /// <see cref="SortDescription"/>s on the picker's
    /// <see cref="ListCollectionView"/>. Sorting layers under the
    /// existing Tier grouping — within each tier group rows order by
    /// the chosen mode. Reapplied on every filter invalidation so a
    /// dropdown change re-sorts in place without rebuilding the view.
    /// </summary>
    private static void ApplySortDescriptions(ListCollectionView view, ModelSortMode mode)
    {
        view.SortDescriptions.Clear();
        switch (mode)
        {
            case ModelSortMode.Newest:
                view.SortDescriptions.Add(new SortDescription(nameof(ModelGridRow.LastUpdated), ListSortDirection.Descending));
                break;
            case ModelSortMode.Alphabetical:
                view.SortDescriptions.Add(new SortDescription(nameof(ModelGridRow.Name), ListSortDirection.Ascending));
                break;
            case ModelSortMode.Popular:
            default:
                view.SortDescriptions.Add(new SortDescription(nameof(ModelGridRow.PullCount), ListSortDirection.Descending));
                break;
        }
    }

    /// <summary>C3: dropdown selection → VM <c>MaxParametersBillion</c>.
    /// Tag carries the cap as a string ("" = no cap, "7" = ≤7B etc).</summary>
    private void ParameterCapCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.SelectedItem is not ComboBoxItem item) return;
        var raw = item.Tag as string;
        if (string.IsNullOrEmpty(raw))
        {
            _viewModel.MaxParametersBillion = null;
            return;
        }
        if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cap))
        {
            _viewModel.MaxParametersBillion = cap;
        }
    }

    /// <summary>C5: dropdown selection → VM <c>SortMode</c>. SelectedIndex
    /// 0 = Popular, 1 = Newest, 2 = A–Z (matches the XAML item order).</summary>
    private void SortModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        _viewModel.SortMode = combo.SelectedIndex switch
        {
            1 => ModelSortMode.Newest,
            2 => ModelSortMode.Alphabetical,
            _ => ModelSortMode.Popular,
        };
    }

    /// <summary>C26: dropdown selection → VM <c>MostPopularLimit</c>.
    /// Tag carries the integer cap; falls back to the default if the
    /// tag can't be parsed (defensive — XAML hardcodes the strings).</summary>
    private void MostPopularLimitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is string raw && int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var limit))
        {
            _viewModel.MostPopularLimit = limit;
        }
    }

    /// <summary>C27 Stage 1: dropdown selection → VM <c>ActiveSource</c>.
    /// The setter raises <see cref="PrepViewModel.ActiveSourceChanged"/>,
    /// which <see cref="OnActiveSourceChanged"/> picks up and dispatches
    /// the source-appropriate fetch.</summary>
    private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.SelectedItem is not ComboBoxItem item) return;
        var raw = item.Tag as string;
        _viewModel.ActiveSource = string.Equals(raw, "HuggingFace", StringComparison.Ordinal)
            ? ModelSource.HuggingFace
            : ModelSource.Ollama;
    }

    /// <summary>
    /// C27 Stage 3: PasswordBox doesn't expose two-way Binding to its
    /// .Password property (by design — the plaintext never lands in the
    /// XAML tree). We sync into the VM on every change here. The
    /// PasswordBox itself remains the storage of record while PrepApp is
    /// running; finalize reads <see cref="PrepViewModel.HuggingFaceTokenInput"/>
    /// to seal into the encrypted config.
    /// </summary>
    private void HuggingFaceTokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox box) return;
        _viewModel.HuggingFaceTokenInput = box.Password ?? string.Empty;
    }

    /// <summary>
    /// C27 Stage 3: VM raised <see cref="PrepViewModel.HuggingFaceTokenChanged"/>;
    /// push the new value into the catalog service so subsequent search /
    /// siblings requests carry the Bearer header. Null = clear the token
    /// (reverts to anonymous mode).
    /// </summary>
    private void OnHuggingFaceTokenChanged(object? sender, string? token)
    {
        _hfCatalogService.UpdateAuthToken(token);
    }

    /// <summary>
    /// C7: zero any cached <see cref="FreeAiSsd.Shared.Services.UnlockMaterial"/>
    /// when the main window closes. The VM holds the material; calling
    /// <see cref="PrepViewModel.LockManageSession"/> runs
    /// <see cref="System.Security.Cryptography.CryptographicOperations.ZeroMemory"/>
    /// on the derived-key buffer. Safe to call when no session is unlocked.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        _viewModel.LockManageSession();
        base.OnClosed(e);
    }

    /// <summary>
    /// C7: handles the VM's <see cref="PrepViewModel.UnlockRequested"/>
    /// event. Shows the modal passphrase dialog and, on submission, calls
    /// <see cref="FreeAiSsd.Shared.SsdEncryption.TryUnlockPortableConfigWithMaterial"/>
    /// directly. On success runs the plaintext-migration sweep (mirrors
    /// Runner + Mac so encrypted drives never accumulate plaintext
    /// secrets) and hands the result to <see cref="PrepViewModel.ApplyUnlockResult"/>.
    /// Failure re-shows the dialog with an inline error so the user can
    /// retry without re-clicking the banner button.
    /// </summary>
    private async void OnUnlockRequested(object? sender, EventArgs e)
    {
        var drive = _viewModel.SelectedDrive;
        if (drive is null) return;

        string? priorError = null;
        while (true)
        {
            var dialog = new UnlockDialog { Owner = this };
            if (priorError is not null) dialog.InitialError = priorError;
            var result = dialog.ShowDialog();
            if (result != true) return; // Cancel — banner stays yellow.

            var password = dialog.Password;
            if (FreeAiSsd.Shared.SsdEncryption.TryUnlockPortableConfigWithMaterial(
                    drive.RootPath, password,
                    out var config, out var material, out var error))
            {
                if (config is null || material is null)
                {
                    priorError = "Unlock returned an empty result. Try again.";
                    continue;
                }

                // Mirror Runner + Mac: absorb / discard stale plaintext so
                // an encrypted drive can never accumulate a plaintext
                // config alongside the sealed blob.
                try
                {
                    var migration = await FreeAiSsd.Shared.SsdEncryption.TryMigratePlaintextAsync(
                        drive.RootPath, material).ConfigureAwait(true);
                    // PlaintextMigrationResult is informational; nothing to
                    // act on here beyond logging (the migration itself
                    // already updated the blob if applicable).
                    _ = migration;
                }
                catch (Exception migrationEx)
                {
                    // Migration failures are non-fatal — the unlock itself
                    // succeeded, the plaintext just stays on disk. Log so
                    // the user can see it in the log lines area.
                    _viewModel.LogLines.Add(
                        $"[Plaintext-migration] Failed: {migrationEx.Message} — plaintext preserved.");
                }

                _viewModel.ApplyUnlockResult(config, material);
                return;
            }

            // Re-show with the error inline. Mirrors the Mac behavior:
            // sheet stays open, password field is cleared (PasswordBox
            // doesn't persist text across dialog instances anyway).
            priorError = string.IsNullOrEmpty(error) ? "Unlock failed." : error;
        }
    }

    /// <summary>C27 Stage 1: routes a source change to the right fetch.
    /// Cancels any in-flight refresh first (the plan's cancel-and-
    /// restart decision avoids a stale fetch landing on top of the
    /// freshly switched source).</summary>
    private async void OnActiveSourceChanged(object? sender, EventArgs e)
    {
        CancelActiveRefresh();
        switch (_viewModel.ActiveSource)
        {
            case ModelSource.HuggingFace:
                await RefreshHuggingFaceCatalogAsync(search: null);
                break;
            case ModelSource.Ollama:
            default:
                // Restore the bundled list synchronously so the picker
                // refills immediately; the user can then click Refresh
                // to hit ollama.com again.
                await LoadBundledOllamaCatalogAsync();
                break;
        }
    }

    /// <summary>C27 Stage 1: re-load the bundled starter-models.json
    /// after a source switch back to Ollama. Mirrors the boot-time
    /// path in <see cref="LoadStarterCatalogAsync"/> but skips the
    /// CollectionView wiring (already attached) and the warning
    /// banner (a switch back has no surface for the bundled-load
    /// warning to retrigger).</summary>
    private async Task LoadBundledOllamaCatalogAsync()
    {
        var loadResult = StarterModelCatalogLoader.Load(AppContext.BaseDirectory);
        await _viewModel.SetStarterCatalogAsync(ProjectCatalog(loadResult.Catalog));
        CatalogLastUpdatedText.Text = loadResult.Catalog.Models.Count > 0
            ? $"Bundled Ollama list: {loadResult.Catalog.Models.Count} models. Click Refresh to fetch the latest from ollama.com."
            : "Bundled list empty. Click Refresh to fetch from ollama.com.";
        CatalogLastUpdatedText.Visibility = Visibility.Visible;
    }

    /// <summary>C27 Stage 1: PropertyChanged listener that fires the
    /// HF debounce timer when ActiveSource == HuggingFace and the
    /// search text changes. Under Ollama the textbox stays a pure
    /// local filter (no host roundtrip).</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PrepViewModel.ModelSearchText)) return;
        if (_viewModel.ActiveSource != ModelSource.HuggingFace) return;

        if (_hfSearchDebounceTimer is null)
        {
            _hfSearchDebounceTimer = new DispatcherTimer { Interval = HfSearchDebounce };
            _hfSearchDebounceTimer.Tick += async (_, _) =>
            {
                _hfSearchDebounceTimer!.Stop();
                await RefreshHuggingFaceCatalogAsync(
                    search: string.IsNullOrWhiteSpace(_viewModel.ModelSearchText) ? null : _viewModel.ModelSearchText);
            };
        }
        _hfSearchDebounceTimer.Stop();
        _hfSearchDebounceTimer.Start();
    }

    /// <summary>C27 Stage 1: shared HF refresh path used by Source
    /// switch, search debounce, and the Refresh button when the active
    /// source is HF. Soft failure mirrors
    /// <see cref="RefreshCatalogButton_Click"/>.</summary>
    private async Task RefreshHuggingFaceCatalogAsync(string? search)
    {
        if (_isRefreshingCatalog) return;
        _isRefreshingCatalog = true;
        RefreshCatalogButton.IsEnabled = false;
        var originalContent = RefreshCatalogButton.Content;
        RefreshCatalogButton.Content = "Refreshing…";

        _activeRefreshCts = new CancellationTokenSource();
        var ct = _activeRefreshCts.Token;

        try
        {
            var query = new HuggingFaceSearchQuery(search);
            var result = await _hfCatalogService.SearchAsync(query, ct);
            await _viewModel.SetStarterCatalogAsync(ProjectHuggingFaceCatalog(result.Catalog));

            StarterCatalogWarningText.Text = string.Empty;
            StarterCatalogWarningText.Visibility = Visibility.Collapsed;

            CatalogLastUpdatedText.Text = result.Catalog.Models.Count > 0
                ? $"Hugging Face: {result.Catalog.Models.Count} GGUF repos{(string.IsNullOrWhiteSpace(search) ? " (popular)" : $" for '{search}'")} (fetched {result.FetchedAt.LocalDateTime:g})."
                : string.IsNullOrWhiteSpace(search)
                    ? "No GGUF repos returned by Hugging Face. The list will repopulate on the next refresh."
                    : $"No GGUF repos match '{search}'. Try a different search or clear it to see popular GGUF models.";
            CatalogLastUpdatedText.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            // Cancellation from a source switch or a follow-on debounce
            // tick — the next refresh path owns the UI; do nothing.
        }
        catch (LiveCatalogFetchException ex)
        {
            CatalogLastUpdatedText.Text = ex.Reason == LiveCatalogFetchReason.NonSuccessStatus && ex.StatusCode == "429"
                ? "Hugging Face is rate-limiting requests. Wait a minute and try again."
                : $"Hugging Face fetch failed ({ex.Reason}): {ex.Message}. Switch back to Ollama to keep going.";
            CatalogLastUpdatedText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            CatalogLastUpdatedText.Text = $"Hugging Face fetch failed unexpectedly: {ex.Message}.";
            CatalogLastUpdatedText.Visibility = Visibility.Visible;
        }
        finally
        {
            RefreshCatalogButton.Content = originalContent;
            RefreshCatalogButton.IsEnabled = true;
            _isRefreshingCatalog = false;
            _activeRefreshCts?.Dispose();
            _activeRefreshCts = null;
        }
    }

    /// <summary>
    /// C27 Stage 2: fetches HF siblings for each <paramref name="repoIds"/>
    /// entry and produces human-readable disk-budget warnings. Mirrors
    /// the <see cref="ModelService.BuildPullSelectionWarnings"/> output
    /// shape so the existing <c>ConfirmSizingWarnings</c> dialog renders
    /// HF warnings alongside Ollama-side ones with no shape changes.
    ///
    /// Gated/private repos surface a Stage-3 pointer instead of a size.
    /// Per-repo fetch failures fall through (logged via the VM's
    /// <c>AppendLog</c> indirectly when the hook throws — this method
    /// catches at the inner level so one bad repo doesn't poison the
    /// whole batch).
    /// </summary>
    private async Task<IReadOnlyList<string>> FetchHuggingFaceSizingWarningsAsync(
        IReadOnlyList<string> repoIds,
        long freeBytes,
        CancellationToken ct)
    {
        var warnings = new List<string>();
        foreach (var repoId in repoIds)
        {
            if (ct.IsCancellationRequested) break;
            HuggingFaceModelDetails details;
            try
            {
                details = await _hfCatalogService.FetchSiblingsAsync(repoId, ct);
            }
            catch (LiveCatalogFetchException ex)
            {
                if (ex.Reason == LiveCatalogFetchReason.NonSuccessStatus
                    && (ex.StatusCode == "401" || ex.StatusCode == "403"))
                {
                    warnings.Add(
                        $"hf.co/{repoId}: Hugging Face refused the metadata request ({ex.StatusCode}). " +
                        "If the repo is gated or private, enter your HF token in the field above the model list.");
                }
                else
                {
                    warnings.Add(
                        $"hf.co/{repoId}: could not fetch file sizes ({ex.Reason}: {ex.Message}). " +
                        "Pull will proceed without a disk-budget check.");
                }
                continue;
            }

            if (details.Gated || details.Private)
            {
                warnings.Add(
                    $"hf.co/{repoId}: this repo is " +
                    (details.Gated ? "gated" : "private") +
                    " and requires a Hugging Face token. Enter your HF token in the field above the model list, then retry.");
                continue;
            }

            var pick = HuggingFaceCatalogService.PickSizingFile(details.Siblings);
            if (pick is null || pick.TotalBytes <= 0)
            {
                // No sized GGUF in the manifest — surface as info, not
                // a blocker. Ollama may still pull successfully.
                warnings.Add(
                    $"hf.co/{repoId}: no GGUF file sizes available on Hugging Face. " +
                    "Pull will proceed without a disk-budget check.");
                continue;
            }

            var sizeGb = pick.TotalBytes / (1024.0 * 1024 * 1024);
            var headroom = freeBytes > 0
                ? freeBytes - (pick.TotalBytes * 2L)
                : long.MinValue;
            var partSuffix = pick.PartCount > 1
                ? $" ({pick.PartCount}-part split)"
                : string.Empty;

            if (freeBytes <= 0)
            {
                // Free space unknown — surface size only; don't pretend
                // we know whether it fits.
                warnings.Add(
                    $"hf.co/{repoId}: {pick.PrimaryFilename} ≈ {sizeGb:F1} GB{partSuffix}. " +
                    "Free space on the target drive could not be determined.");
            }
            else if (headroom < 0)
            {
                // Less than 2× headroom — the precondition
                // OllamaModelStager.EnsureStagingFreeSpace enforces.
                var freeGb = freeBytes / (1024.0 * 1024 * 1024);
                warnings.Add(
                    $"hf.co/{repoId}: {pick.PrimaryFilename} ≈ {sizeGb:F1} GB{partSuffix}; " +
                    $"target drive has {freeGb:F1} GB free — leaves under 2× headroom " +
                    "(Ollama needs staging space for the merge).");
            }
            // else: fits comfortably — no warning needed.
        }
        return warnings;
    }

    /// <summary>
    /// C27 Stage 4: chevron click handler on a parent HF row. Delegates
    /// to the VM's <see cref="PrepViewModel.ToggleRepoExpansionAsync"/>;
    /// the VM fetches via <see cref="FetchHuggingFaceQuantsAsync"/>
    /// (wired as <see cref="PrepViewModel.HuggingFaceQuantExpansionHook"/>)
    /// and inserts the resulting children.
    /// </summary>
    private async void RepoExpandToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ModelGridRow row) return;
        await _viewModel.ToggleRepoExpansionAsync(row);
    }

    /// <summary>
    /// C27 Stage 4: VM-supplied hook to fetch per-quant rows for an HF
    /// repo. Reuses the existing <see cref="HuggingFaceCatalogService.FetchSiblingsAsync"/>
    /// path (cache-friendly — Stage 2 already populated it for the
    /// disk-budget warning), then projects each GGUF sibling into a
    /// quant child entry. Multi-part series collapse into a single
    /// entry via <see cref="ProjectQuantChildren"/>.
    /// </summary>
    private async Task<IReadOnlyList<StarterCatalogEntry>> FetchHuggingFaceQuantsAsync(
        string repoId, CancellationToken ct)
    {
        var details = await _hfCatalogService.FetchSiblingsAsync(repoId, ct);
        if (details.Gated || details.Private)
        {
            // Surface as no children — the parent stays in the picker
            // and the user gets the Stage-3 token nudge from the inline
            // PasswordBox above the picker.
            return Array.Empty<StarterCatalogEntry>();
        }
        return HuggingFaceQuantProjector.Project(repoId, details.Siblings);
    }

    /// <summary>C27 Stage 1: cancel any in-flight refresh so a source
    /// switch never leaves a stale fetch landing on top of the new
    /// source's catalog.</summary>
    private void CancelActiveRefresh()
    {
        try
        {
            _activeRefreshCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished and disposed — nothing to cancel.
        }
        _hfSearchDebounceTimer?.Stop();
    }

    /// <summary>C27 Stage 1: HF catalog → <see cref="StarterCatalogEntry"/>
    /// projection. HF entries carry empty Capabilities (no 1:1 mapping
    /// to ollama.com's tools/vision/thinking/audio) and empty Description
    /// (Stage 1 doesn't fetch READMEs). The C25 pass-through marker on
    /// the picker handles "no capability data" gracefully.</summary>
    private static IReadOnlyList<StarterCatalogEntry> ProjectHuggingFaceCatalog(StarterModelCatalog catalog)
    {
        return catalog.Models
            .Select(m => new StarterCatalogEntry(
                m.Tag,
                m.SizeTier,
                string.Empty,
                m.PullCount,
                Capabilities: m.UseCases.ToList(),
                ParametersBillion: m.ParametersBillion,
                LastUpdated: m.LastUpdated,
                Source: m.Source,
                // C27 Stage 4: HF rows are repo-level — clicking the
                // chevron lazy-fetches per-quant children via the
                // HuggingFaceQuantExpansionHook wired in MainWindow.
                IsExpandable: m.Source == ModelSource.HuggingFace))
            .ToList();
    }

    /// <summary>
    /// Project the rich <see cref="StarterModelEntry"/> into the lighter
    /// <see cref="StarterCatalogEntry"/> the merged grid consumes.
    /// "Best at" combines description + comma-joined use cases, mirroring
    /// the pre-merge Starter grid's Best-at + Use-cases columns.
    /// Shared between bundled and live (F2) catalog paths.
    /// </summary>
    private static IReadOnlyList<StarterCatalogEntry> ProjectCatalog(StarterModelCatalog catalog)
    {
        return catalog.Models
            .Select(m => new StarterCatalogEntry(
                m.Tag,
                m.SizeTier,
                string.IsNullOrWhiteSpace(m.Description)
                    ? string.Join(", ", m.UseCases)
                    : m.UseCases.Count == 0
                        ? m.Description
                        : $"{m.Description} ({string.Join(", ", m.UseCases)})",
                m.PullCount,
                Capabilities: m.UseCases.ToList(),
                ParametersBillion: m.ParametersBillion,
                LastUpdated: m.LastUpdated,
                Source: m.Source))
            .ToList();
    }

    /// <summary>
    /// F2: fetch the latest catalog from ollama.com/library and swap it
    /// in. On any failure (typed <see cref="LiveCatalogFetchException"/>
    /// or unexpected) the existing catalog stays in place so the user's
    /// session isn't disturbed; the failure surfaces in the log + a
    /// "fetch failed" caption rather than a modal dialog.
    /// </summary>
    private async void RefreshCatalogButton_Click(object sender, RoutedEventArgs e)
    {
        // C27 Stage 1: dispatch on ActiveSource. HF re-uses the existing
        // search text (preserves the user's narrowing across a manual
        // refresh); Ollama keeps the prior fetch path.
        if (_viewModel.ActiveSource == ModelSource.HuggingFace)
        {
            await RefreshHuggingFaceCatalogAsync(
                search: string.IsNullOrWhiteSpace(_viewModel.ModelSearchText) ? null : _viewModel.ModelSearchText);
            return;
        }

        if (_isRefreshingCatalog) return;

        _isRefreshingCatalog = true;
        RefreshCatalogButton.IsEnabled = false;
        var originalContent = RefreshCatalogButton.Content;
        RefreshCatalogButton.Content = "Refreshing…";

        _activeRefreshCts = new CancellationTokenSource();
        var ct = _activeRefreshCts.Token;

        try
        {
            var result = await _liveModelCatalogService.FetchAsync(ct);
            await _viewModel.SetStarterCatalogAsync(ProjectCatalog(result.Catalog));

            // The bundled-load path may have populated the warning text
            // earlier — clear it now that the live catalog won.
            StarterCatalogWarningText.Text = string.Empty;
            StarterCatalogWarningText.Visibility = Visibility.Collapsed;

            CatalogLastUpdatedText.Text =
                $"Last updated: {result.FetchedAt.LocalDateTime:g} (live, {result.Catalog.Models.Count} models from {result.SourceUrl}).";
            CatalogLastUpdatedText.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            // Source switch or follow-on refresh owns the UI from here.
        }
        catch (LiveCatalogFetchException ex)
        {
            CatalogLastUpdatedText.Text = $"Refresh failed ({ex.Reason}): {ex.Message}. Using bundled list.";
            CatalogLastUpdatedText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            CatalogLastUpdatedText.Text = $"Refresh failed unexpectedly: {ex.Message}. Using bundled list.";
            CatalogLastUpdatedText.Visibility = Visibility.Visible;
        }
        finally
        {
            RefreshCatalogButton.Content = originalContent;
            RefreshCatalogButton.IsEnabled = true;
            _isRefreshingCatalog = false;
            _activeRefreshCts?.Dispose();
            _activeRefreshCts = null;
        }
    }

    private void LogLines_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null || e.NewItems.Count == 0)
            return;

        var newestItem = e.NewItems[e.NewItems.Count - 1];
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => LogListBox.ScrollIntoView(newestItem)));
    }

    // ─────────────────────────────────────────────────────────────
    // Progressive disclosure: "Show details" toggle on Configured
    // models. Hides Sha256 / Last verified columns by default.
    // ─────────────────────────────────────────────────────────────
    private void OnShowModelDetailsChanged(object sender, RoutedEventArgs e)
    {
        var show = ShowModelDetailsToggle.IsChecked == true;
        var vis = show ? Visibility.Visible : Visibility.Collapsed;
        Sha256Column.Visibility = vis;
        LastVerifiedColumn.Visibility = vis;
    }

    // ─────────────────────────────────────────────────────────────
    // View-layer step machine (PrepFlowStep). Toggles the visible step
    // panel and configures the persistent header counter + footer nav.
    // The shared PrepViewModel is unchanged. See
    // docs/mac-ui-reference/RECONSTRUCTION.md §1, §3.
    // ─────────────────────────────────────────────────────────────

    /// <summary>Enter the Models step in manage mode (off the linear
    /// path — raised by the configured-drive banner's Manage button).</summary>
    private void EnterManageModels()
    {
        _isManageMode = true;
        GoToStep(PrepFlowStep.Models);
    }

    /// <summary>Show one step panel, hide the rest, and reconfigure the
    /// header counter, footer buttons, and log-panel visibility.</summary>
    private void GoToStep(PrepFlowStep step)
    {
        // Leaving the Models step in either direction drops manage mode
        // so a later linear visit behaves normally.
        if (step != PrepFlowStep.Models)
        {
            _isManageMode = false;
        }

        _currentStep = step;

        StepWelcome.Visibility = Visibility.Collapsed;
        StepProfile.Visibility = Visibility.Collapsed;
        StepDrive.Visibility = Visibility.Collapsed;
        StepFormatSetup.Visibility = Visibility.Collapsed;
        StepModels.Visibility = Visibility.Collapsed;
        StepFinalize.Visibility = Visibility.Collapsed;
        StepDone.Visibility = Visibility.Collapsed;
        CurrentStepPanel(step).Visibility = Visibility.Visible;

        var counter = StepCounterLabel(step);
        StepCounterText.Text = counter;
        StepCounterText.Visibility = string.IsNullOrEmpty(counter)
            ? Visibility.Collapsed
            : Visibility.Visible;

        // The log surfaces the long-running operations (format / pull /
        // finalize); the calm steps hide it entirely.
        LogPanel.Visibility = step is PrepFlowStep.FormatSetup
            or PrepFlowStep.Models or PrepFlowStep.Finalize
            ? Visibility.Visible
            : Visibility.Collapsed;

        BackButton.Visibility = HasBack(step) ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButton.Content = PrimaryLabel(step);
        UpdateFooterState();
    }

    private UIElement CurrentStepPanel(PrepFlowStep step) => step switch
    {
        PrepFlowStep.Welcome => StepWelcome,
        PrepFlowStep.Profile => StepProfile,
        PrepFlowStep.Drive => StepDrive,
        PrepFlowStep.FormatSetup => StepFormatSetup,
        PrepFlowStep.Models => StepModels,
        PrepFlowStep.Finalize => StepFinalize,
        PrepFlowStep.Done => StepDone,
        _ => StepWelcome,
    };

    private string StepCounterLabel(PrepFlowStep step) => step switch
    {
        PrepFlowStep.Profile => "1 / 6 — Profile",
        PrepFlowStep.Drive => "2 / 6 — Choose drive",
        PrepFlowStep.FormatSetup => "3 / 6 — Set up drive",
        PrepFlowStep.Models => _isManageMode ? "Manage models" : "4 / 6 — Models",
        PrepFlowStep.Finalize => "5 / 6 — Finalize",
        PrepFlowStep.Done => "6 / 6 — Done",
        _ => string.Empty, // Welcome is unnumbered (mirrors the Mac labels).
    };

    private bool HasBack(PrepFlowStep step) =>
        step is not (PrepFlowStep.Welcome or PrepFlowStep.Done);

    private string PrimaryLabel(PrepFlowStep step) => step switch
    {
        PrepFlowStep.Welcome => "Get started",
        PrepFlowStep.Models => _isManageMode ? "Done" : "Continue",
        PrepFlowStep.Done => "Quit",
        _ => "Continue",
    };

    /// <summary>Continue is gated on the step's precondition: a profile
    /// must be chosen before leaving Profile, a drive before leaving Drive
    /// (and not while the configured-drive banner owns the path).</summary>
    private void UpdateFooterState()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(UpdateFooterState));
            return;
        }

        switch (_currentStep)
        {
            case PrepFlowStep.Profile:
                PrimaryButton.IsEnabled = _viewModel.HasSelectedProfile;
                PrimaryButton.ToolTip = _viewModel.HasSelectedProfile
                    ? null
                    : "Pick a Runner profile to continue.";
                break;
            case PrepFlowStep.Drive:
                var blockedByConfigured = _viewModel.ShowAlreadyConfiguredBanner;
                PrimaryButton.IsEnabled = _viewModel.HasDriveSelected && !blockedByConfigured;
                PrimaryButton.ToolTip = !_viewModel.HasDriveSelected
                    ? "Select a target drive to continue."
                    : blockedByConfigured
                        ? "This drive is already prepared — use Manage models or Start over above."
                        : null;
                break;
            default:
                PrimaryButton.IsEnabled = true;
                PrimaryButton.ToolTip = null;
                break;
        }
    }

    private void OnStepGatingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PrepViewModel.HasSelectedProfile):
            case nameof(PrepViewModel.HasDriveSelected):
            case nameof(PrepViewModel.SelectedDrive):
            case nameof(PrepViewModel.SelectedProfile):
            case nameof(PrepViewModel.ShowAlreadyConfiguredBanner):
            case nameof(PrepViewModel.CanInitiateFreshFormat):
                UpdateFooterState();
                break;
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        switch (_currentStep)
        {
            case PrepFlowStep.Profile:
                GoToStep(PrepFlowStep.Welcome);
                break;
            case PrepFlowStep.Drive:
                GoToStep(PrepFlowStep.Profile);
                break;
            case PrepFlowStep.FormatSetup:
                GoToStep(PrepFlowStep.Drive);
                break;
            case PrepFlowStep.Models:
                GoToStep(_isManageMode ? PrepFlowStep.Drive : PrepFlowStep.FormatSetup);
                break;
            case PrepFlowStep.Finalize:
                GoToStep(PrepFlowStep.Models);
                break;
        }
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        switch (_currentStep)
        {
            case PrepFlowStep.Welcome:
                GoToStep(PrepFlowStep.Profile);
                break;
            case PrepFlowStep.Profile:
                GoToStep(PrepFlowStep.Drive);
                break;
            case PrepFlowStep.Drive:
                GoToStep(PrepFlowStep.FormatSetup);
                break;
            case PrepFlowStep.FormatSetup:
                GoToStep(PrepFlowStep.Models);
                break;
            case PrepFlowStep.Models:
                if (_isManageMode)
                {
                    Close();
                }
                else
                {
                    GoToStep(PrepFlowStep.Finalize);
                }
                break;
            case PrepFlowStep.Finalize:
                GoToStep(PrepFlowStep.Done);
                break;
            case PrepFlowStep.Done:
                Close();
                break;
        }
    }

    private void SaveCurrentPreferences()
    {
        if (_prefStore is null)
        {
            return;
        }

        // Fires on every keystroke while CompanionHostAddress is being
        // edited (UpdateSourceTrigger=PropertyChanged). Use the cached
        // _ftueCompleted instead of touching disk each time.
        var current = PrepTargets.None;
        if (_viewModel.PrepareWindows) current |= PrepTargets.Windows;
        if (_viewModel.PrepareMac) current |= PrepTargets.Mac;
        _prefStore.SaveSettings(new PrepPreferenceSnapshot(
            current,
            _viewModel.SelectedProfile,
            _viewModel.InstallVrCompanion,
            _viewModel.CompanionHostAddress,
            _viewModel.CompanionHostPort,
            _ftueCompleted));
    }

    private void FlightSimProfileCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        SelectProfile(UserProfile.FlightSim);

    private void GeneralAssistantProfileCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        SelectProfile(UserProfile.GeneralAssistant);

    private void ProfileCard_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not (System.Windows.Input.Key.Space or System.Windows.Input.Key.Enter))
        {
            return;
        }

        if (sender == FlightSimProfileCard)
        {
            SelectProfile(UserProfile.FlightSim);
        }
        else if (sender == GeneralAssistantProfileCard)
        {
            SelectProfile(UserProfile.GeneralAssistant);
        }
    }

    private void SelectProfile(UserProfile profile)
    {
        _viewModel.SelectedProfile = profile;
        SyncProfileSelectionCards();
    }

    private void SyncProfileSelectionCards()
    {
        ApplyProfileCardState(FlightSimProfileCard, _viewModel.SelectedProfile == UserProfile.FlightSim);
        ApplyProfileCardState(GeneralAssistantProfileCard, _viewModel.SelectedProfile == UserProfile.GeneralAssistant);
    }

    private static void ApplyProfileCardState(Border card, bool selected)
    {
        var resources = Application.Current.Resources;
        if (selected)
        {
            card.BorderBrush = (System.Windows.Media.Brush)resources["FocusBorderGradientBrush"];
            card.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = (System.Windows.Media.Color)resources["AccentCyanColor"],
                ShadowDepth = 0,
                BlurRadius = 20,
                Opacity = 0.75
            };
        }
        else
        {
            card.BorderBrush = (System.Windows.Media.Brush)resources["SurfaceBorderBrush"];
            card.Effect = (System.Windows.Media.Effects.Effect)resources["RaisedDarkShadow"];
        }
    }
}
