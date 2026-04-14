using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FreeAiSsd.PrepApp.Services;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Models;
using FreeAiSsd.Shared.ViewModels;

namespace FreeAiSsd.PrepApp;

public partial class MainWindow : Window
{
    private readonly PrepViewModel _viewModel;

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

        Loaded += OnWindowLoaded;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.Initialize();

        LoadStarterCatalog();

        var prefStore = new PrepTargetPreferenceStore();
        var pref = prefStore.LoadSettings();
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
            prefStore.SaveSettings(new PrepPreferenceSnapshot(
                current,
                _viewModel.InstallVrCompanion,
                _viewModel.CompanionHostAddress,
                _viewModel.CompanionHostPort));
        };
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
        if (e.Action == NotifyCollectionChangedAction.Add && LogListBox.Items.Count > 0)
        {
            LogListBox.ScrollIntoView(LogListBox.Items[^1]);
        }
    }
}
