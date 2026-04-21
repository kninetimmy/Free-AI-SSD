using System.Collections.ObjectModel;
using System.Collections.Specialized;
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

        var entries = loadResult.Catalog.Models
            .Select(m => new StarterCatalogEntry(
                m.Tag,
                m.SizeTier,
                // "Best at" = description + comma-joined use cases, mirroring
                // the pre-merge Starter grid's Best-at + Use-cases columns.
                string.IsNullOrWhiteSpace(m.Description)
                    ? string.Join(", ", m.UseCases)
                    : m.UseCases.Count == 0
                        ? m.Description
                        : $"{m.Description} ({string.Join(", ", m.UseCases)})"))
            .ToList();

        await _viewModel.SetStarterCatalogAsync(entries);

        // Group the merged grid by Tier so Small / Medium / Large / Custom
        // show as visual sections (same affordance the pre-merge Starter
        // grid offered, now applied to the unified ModelRows collection).
        var collectionView = new ListCollectionView(_viewModel.ModelRows);
        collectionView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ModelGridRow.Tier)));
        ModelStatusGrid.ItemsSource = collectionView;
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
