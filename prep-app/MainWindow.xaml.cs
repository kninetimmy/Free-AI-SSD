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

    // FTUE state
    private int _ftueStepIndex;
    private FrameworkElement[] _ftueTargets = Array.Empty<FrameworkElement>();
    private (string label, string title, string body)[] _ftueSteps = Array.Empty<(string, string, string)>();

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

        _viewModel = new PrepViewModel(
            driveService,
            modelService,
            ollamaPackageService,
            prereqService,
            artifactStagingService,
            readinessService,
            encryptionService,
            dialogService,
            logService);

        _viewModel.SystemRamGb = SystemResources.GetTotalSystemRamGb();
        _viewModel.GpuVramGb = SystemResources.GetGpuVramGb();

        DataContext = _viewModel;

        _viewModel.LogLines.CollectionChanged += LogLines_CollectionChanged;

        ShowModelDetailsToggle.Checked += OnShowModelDetailsChanged;
        ShowModelDetailsToggle.Unchecked += OnShowModelDetailsChanged;

        Loaded += OnWindowLoaded;
        SizeChanged += OnWindowSizeChanged;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.Initialize();

        LoadStarterCatalog();

        _prefStore = new PrepTargetPreferenceStore();
        var pref = _prefStore.LoadSettings();
        _viewModel.PrepareWindows = pref.PrepTargets.HasFlag(PrepTargets.Windows);
        _viewModel.PrepareMac = pref.PrepTargets.HasFlag(PrepTargets.Mac);
        _viewModel.InstallVrCompanion = pref.InstallVrCompanion;
        _viewModel.CompanionHostAddress = pref.CompanionHostAddress;
        _viewModel.CompanionHostPort = pref.CompanionHostPort;

        _viewModel.OnPrepTargetsChanged = () =>
        {
            var current = PrepTargets.None;
            if (_viewModel.PrepareWindows) current |= PrepTargets.Windows;
            if (_viewModel.PrepareMac) current |= PrepTargets.Mac;
            var existing = _prefStore!.LoadSettings();
            _prefStore.SaveSettings(new PrepPreferenceSnapshot(
                current,
                _viewModel.InstallVrCompanion,
                _viewModel.CompanionHostAddress,
                _viewModel.CompanionHostPort,
                existing.FtueCompleted));
        };

        if (!pref.FtueCompleted)
        {
            StartFtue();
        }
    }

    private void LoadStarterCatalog()
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

        var tierOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Small"] = 0,
            ["Medium"] = 1,
            ["Large"] = 2
        };

        _viewModel.StarterModels.Clear();
        foreach (var entry in loadResult.Catalog.Models
                     .OrderBy(m => tierOrder.TryGetValue(m.SizeTier, out var order) ? order : int.MaxValue)
                     .ThenBy(m => m.Tag, StringComparer.OrdinalIgnoreCase))
        {
            _viewModel.StarterModels.Add(new StarterModelRow(
                entry.Tag,
                entry.Params,
                entry.SizeTier,
                entry.Description,
                string.Join(", ", entry.UseCases),
                string.Empty));
        }

        var collectionView = new ListCollectionView(_viewModel.StarterModels);
        collectionView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(StarterModelRow.SizeTier)));
        StarterModelGrid.ItemsSource = collectionView;
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
    // Actionable empty-state handler: scrolls the Starter models
    // grid into view inside the Model Manager tab.
    // ─────────────────────────────────────────────────────────────
    private void OnBrowseStarterModelsClick(object sender, RoutedEventArgs e)
    {
        StarterModelsCard?.BringIntoView();
    }

    // ─────────────────────────────────────────────────────────────
    // FTUE (First-Time User Experience): 3-step spotlight tour.
    // ─────────────────────────────────────────────────────────────
    private void StartFtue()
    {
        _ftueSteps = new (string, string, string)[]
        {
            ("Step 1 of 3", "Pick your target drive",
                "Choose the drive where Ollama and models will be installed."),
            ("Step 2 of 3", "Choose a starter model",
                "The Starter models card lists vetted picks grouped by size. Toggle one or more to add."),
            ("Step 3 of 3", "Pull & verify",
                "Use Pull/Install to download the models. Verify confirms the files are intact.")
        };
        _ftueTargets = new FrameworkElement[]
        {
            TargetDriveRow,
            StarterModelsCard,
            PullInstallButton
        };

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
        _prefStore?.MarkFtueCompleted();
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (FtueOverlay.Visibility == Visibility.Visible)
        {
            PositionSpotlight();
        }
    }
}
