using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
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

        _viewModel.SystemRamGb = SystemResources.GetSystemRamGb();
        _viewModel.GpuVramGb = SystemResources.GetGpuVramGb();

        DataContext = _viewModel;

        _viewModel.LogLines.CollectionChanged += LogLines_CollectionChanged;

        Loaded += OnWindowLoaded;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.Initialize();

        var starterCatalog = StarterModelCatalog.Build(
            _viewModel.SystemRamGb,
            _viewModel.GpuVramGb);

        foreach (var row in starterCatalog)
            _viewModel.StarterModels.Add(row);

        if (StarterModelGrid.ItemsSource is ICollectionView view ||
            System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.StarterModels) is ICollectionView defaultView)
        {
            var cv = System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.StarterModels);
            cv.GroupDescriptions.Clear();
            cv.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription("SizeTier"));
        }

        var prefStore = new PrepTargetPreferenceStore();
        var targets = prefStore.Load();
        _viewModel.PrepareWindows = targets.HasFlag(PrepTargets.Windows);
        _viewModel.PrepareMac = targets.HasFlag(PrepTargets.Mac);

        _viewModel.OnPrepTargetsChanged = () =>
        {
            var current = PrepTargets.None;
            if (_viewModel.PrepareWindows) current |= PrepTargets.Windows;
            if (_viewModel.PrepareMac) current |= PrepTargets.Mac;
            prefStore.Save(current);
        };
    }

    private void ModelStatusGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid grid)
        {
            _viewModel.SelectedModelRows = grid.SelectedItems
                .OfType<ModelGridRow>()
                .ToList()
                .AsReadOnly();
        }
    }

    private void LogLines_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && LogListBox.Items.Count > 0)
        {
            LogListBox.ScrollIntoView(LogListBox.Items[^1]);
        }
    }
}
