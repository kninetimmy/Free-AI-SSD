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

    // FTUE state
    private int _ftueStepIndex;
    private FrameworkElement[] _ftueTargets = Array.Empty<FrameworkElement>();
    private int[] _ftueTargetTabIndex = Array.Empty<int>();
    private (string label, string title, string body)[] _ftueSteps = Array.Empty<(string, string, string)>();

    // Cached once at load time so the per-keystroke save path doesn't
    // have to re-read the settings file on every PropertyChanged tick.
    private bool _ftueCompleted;

    public MainWindow()
    {
        InitializeComponent();

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
            elevationService);

        _viewModel.SystemRamGb = SystemResources.GetTotalSystemRamGb();
        _viewModel.GpuVramGb = SystemResources.GetGpuVramGb();

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

        ShowModelDetailsToggle.Checked += OnShowModelDetailsChanged;
        ShowModelDetailsToggle.Unchecked += OnShowModelDetailsChanged;

        Loaded += OnWindowLoaded;
        SizeChanged += OnWindowSizeChanged;
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

        if (!_ftueCompleted)
        {
            StartFtue();
        }

        // Fire auto-resume after Initialize() has loaded drives and after
        // the FTUE decision, so the confirm-erase dialog isn't competing
        // with the spotlight. If no intent was passed this is a no-op.
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
                Source: m.Source))
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
    // FTUE (First-Time User Experience): 4-step spotlight tour.
    // ─────────────────────────────────────────────────────────────
    private void StartFtue()
    {
        _ftueSteps = new (string, string, string)[]
        {
            ("Step 1 of 4", "How the SSD fits your setup",
                "PrepApp stages one SSD for either a single-PC install or a split AI-host plus VR-companion setup."),
            ("Step 2 of 4", "Choose your default Runner profile",
                "Pick Flight Sim for DCS bindings and HOTAS/PTT defaults, or General Assistant for the chat-first layout."),
            ("Step 3 of 4", "Pick your target drive",
                "Choose the SSD where the Runner payload, Ollama runtime, and models will be staged."),
            ("Step 4 of 4", "Choose and download models",
                "Pick one or more models, then click Download so the drive is ready before finalization.")
        };
        _ftueTargets = new FrameworkElement[]
        {
            TwoMachineExplainerCard,
            ProfileSelectionCard,
            TargetDriveRow,
            StarterModelsCard
        };
        // Which tab each spotlight target lives in.
        // Steps 1-3 live inside the Drive tab (index 1). Step 4 lives
        // inside the Models tab (index 0).
        _ftueTargetTabIndex = new[] { 1, 1, 1, 0 };

        _ftueStepIndex = 0;
        FtueOverlay.Visibility = Visibility.Visible;
        ApplyFtueStep();
    }

    private void ApplyFtueStep()
    {
        if (_ftueStepIndex < 0 || _ftueStepIndex >= _ftueSteps.Length)
        {
            FinishFtue();
            return;
        }

        var (label, title, body) = _ftueSteps[_ftueStepIndex];
        FtueStepLabel.Text = label;
        FtueTitleText.Text = title;
        FtueBodyText.Text = body;
        FtueNextButton.Content = _ftueStepIndex == _ftueSteps.Length - 1 ? "Finish" : "Next";

        // Switch to the tab that hosts this step's spotlight target so
        // the element is actually realized and measurable. Without this,
        // steps 2/3 silently skip the spotlight if the user is on the
        // Drive Setup tab when the FTUE advances.
        if (_ftueStepIndex < _ftueTargetTabIndex.Length)
        {
            var tabIndex = _ftueTargetTabIndex[_ftueStepIndex];
            if (tabIndex >= 0 && tabIndex < MainTabs.Items.Count)
            {
                MainTabs.SelectedIndex = tabIndex;
            }
        }

        // Defer spotlight positioning until the target has a real layout
        // (first-render pass won't have resolved TabItem sizes yet).
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(PositionSpotlight));
    }

    private void PositionSpotlight()
    {
        if (_ftueStepIndex < 0 || _ftueStepIndex >= _ftueTargets.Length)
        {
            FtueSpotlight.Visibility = Visibility.Collapsed;
            return;
        }

        var target = _ftueTargets[_ftueStepIndex];
        if (target is null || !target.IsVisible || target.ActualWidth <= 0 || target.ActualHeight <= 0)
        {
            FtueSpotlight.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            // Canvas and target aren't in an ancestor/descendant relationship
            // (the overlay is a sibling of the main content), so go through
            // TransformToVisual which just needs a common ancestor — the
            // root Grid of the window.
            var transform = target.TransformToVisual(FtueSpotlightCanvas);
            var topLeft = transform.Transform(new Point(0, 0));
            const double pad = 8;
            Canvas.SetLeft(FtueSpotlight, topLeft.X - pad);
            Canvas.SetTop(FtueSpotlight, topLeft.Y - pad);
            FtueSpotlight.Width = target.ActualWidth + pad * 2;
            FtueSpotlight.Height = target.ActualHeight + pad * 2;
            FtueSpotlight.Visibility = Visibility.Visible;
        }
        catch (InvalidOperationException)
        {
            // Target not yet parented into the visual tree (e.g. a tab
            // that hasn't been rendered). Hide the ring; the card
            // caption still guides the user.
            FtueSpotlight.Visibility = Visibility.Collapsed;
        }
    }

    private void OnFtueNextClick(object sender, RoutedEventArgs e)
    {
        _ftueStepIndex++;
        if (_ftueStepIndex >= _ftueSteps.Length)
        {
            FinishFtue();
            return;
        }
        ApplyFtueStep();
    }

    private void OnFtueSkipClick(object sender, RoutedEventArgs e)
    {
        FinishFtue();
    }

    private void FinishFtue()
    {
        FtueOverlay.Visibility = Visibility.Collapsed;
        FtueSpotlight.Visibility = Visibility.Collapsed;
        _ftueCompleted = true;
        SaveCurrentPreferences();
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (FtueOverlay.Visibility == Visibility.Visible)
        {
            PositionSpotlight();
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
