using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Models;
using FreeAiSsd.Shared.Services;
using FreeAiSsd.Shared.ViewModels;
using Moq;
using Xunit;

namespace FreeAiSsd.Tests;

public class PrepViewModelTests
{
    private readonly Mock<IDriveService> _driveService = new();
    private readonly Mock<IModelService> _modelService = new();
    private readonly Mock<IOllamaPackageService> _ollamaPackageService = new();
    private readonly Mock<IPrereqService> _prereqService = new();
    private readonly Mock<IArtifactStagingService> _artifactStagingService = new();
    private readonly Mock<IReadinessService> _readinessService = new();
    private readonly Mock<IEncryptionService> _encryptionService = new();
    private readonly Mock<IDialogService> _dialogService = new();
    private readonly Mock<ILogService> _logService = new();
    private readonly Mock<IElevationService> _elevationService = new();

    private static DriveTarget MakeDrive(string rootPath, string label = "SSD",
        long freeBytes = 64_000_000_000, long totalBytes = 128_000_000_000,
        bool isRemovable = true, bool isFixed = false, string warning = "")
        => new(label, rootPath, label, freeBytes, totalBytes, "NTFS", true, isRemovable, isFixed, warning);

    private PrepViewModel CreateViewModel()
    {
        return new PrepViewModel(
            _driveService.Object,
            _modelService.Object,
            _ollamaPackageService.Object,
            _prereqService.Object,
            _artifactStagingService.Object,
            _readinessService.Object,
            _encryptionService.Object,
            _dialogService.Object,
            _logService.Object,
            _elevationService.Object);
    }

    private void SetupDefaultMocks(IReadOnlyList<DriveTarget>? drives = null, bool encrypted = false)
    {
        drives ??= new List<DriveTarget> { MakeDrive("E:\\") };
        _driveService.Setup(d => d.GetCandidateDrives(It.IsAny<bool>())).Returns(drives);
        _encryptionService.Setup(e => e.IsEncryptionEnabled(It.IsAny<string>())).Returns(encrypted);
        _modelService.Setup(m => m.LoadConfigAsync(It.IsAny<string>())).ReturnsAsync(new PortableConfig());
        _modelService.Setup(m => m.DiscoverModelsOnDisk(It.IsAny<string>())).Returns(Array.Empty<string>());
        _modelService.Setup(m => m.GetSizingWarnings(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<int?>())).Returns(new List<string>());
        _driveService.Setup(d => d.GetFreeDiskSpaceGb(It.IsAny<string>())).Returns(100);
        string? problem = null;
        _artifactStagingService.Setup(a => a.AreMacArtifactsAvailable(out problem)).Returns(false);
    }

    [Fact]
    public void Initialize_LoadsDrives_AndSelectsFirst()
    {
        var drives = new List<DriveTarget>
        {
            MakeDrive("E:\\", "Test SSD"),
            MakeDrive("F:\\", "Other Drive")
        };
        SetupDefaultMocks(drives);

        var vm = CreateViewModel();
        vm.Initialize();

        Assert.Equal(2, vm.Drives.Count);
        Assert.Equal("E:\\", vm.SelectedDrive?.RootPath);
    }

    [Fact]
    public void Initialize_NoDrives_SelectedDriveIsNull()
    {
        SetupDefaultMocks(new List<DriveTarget>());

        var vm = CreateViewModel();
        vm.Initialize();

        Assert.Null(vm.SelectedDrive);
        Assert.False(vm.HasDriveSelected);
    }

    [Fact]
    public void ShowFixedDrives_Toggle_RefreshesDrives()
    {
        var removable = new List<DriveTarget> { MakeDrive("E:\\", "USB") };
        var withFixed = new List<DriveTarget>
        {
            MakeDrive("E:\\", "USB"),
            MakeDrive("C:\\", "System", isRemovable: false, isFixed: true)
        };
        _driveService.Setup(d => d.GetCandidateDrives(false)).Returns(removable);
        _driveService.Setup(d => d.GetCandidateDrives(true)).Returns(withFixed);
        _encryptionService.Setup(e => e.IsEncryptionEnabled(It.IsAny<string>())).Returns(false);
        _modelService.Setup(m => m.LoadConfigAsync(It.IsAny<string>())).ReturnsAsync(new PortableConfig());
        _modelService.Setup(m => m.DiscoverModelsOnDisk(It.IsAny<string>())).Returns(Array.Empty<string>());
        _modelService.Setup(m => m.GetSizingWarnings(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<int?>())).Returns(new List<string>());
        _driveService.Setup(d => d.GetFreeDiskSpaceGb(It.IsAny<string>())).Returns(100);
        string? problem = null;
        _artifactStagingService.Setup(a => a.AreMacArtifactsAvailable(out problem)).Returns(false);

        var vm = CreateViewModel();
        vm.Initialize();
        Assert.Single(vm.Drives);

        vm.ShowFixedDrives = true;
        Assert.Equal(2, vm.Drives.Count);
    }

    [Fact]
    public void EncryptedDrive_BlocksWriteOperations()
    {
        SetupDefaultMocks(encrypted: true);

        var vm = CreateViewModel();
        vm.Initialize();

        Assert.True(vm.IsSelectedDriveEncrypted);
        Assert.False(vm.CanMutateDrive);
    }

    [Fact]
    public void CanMutateDrive_TrueByDefault()
    {
        var vm = CreateViewModel();
        Assert.True(vm.CanMutateDrive);
    }

    [Fact]
    public void AddModelCommand_NoDrive_CannotExecute()
    {
        SetupDefaultMocks(new List<DriveTarget>());

        var vm = CreateViewModel();
        vm.Initialize();
        vm.ModelTagInput = "llama3:latest";

        Assert.False(vm.AddModelCommand.CanExecute(null));
    }

    [Fact]
    public void AddModelCommand_EmptyTag_LogsWarning()
    {
        SetupDefaultMocks();
        _driveService.Setup(d => d.EnsureWritable(It.IsAny<string>(), It.IsAny<string>(), out It.Ref<string?>.IsAny)).Returns(true);

        var vm = CreateViewModel();
        vm.Initialize();
        vm.ModelTagInput = "";
        vm.AddModelCommand.Execute(null);

        Assert.Contains(vm.LogLines, l => l.Contains("Enter a model tag before adding."));
    }

    [Fact]
    public void AddModelCommand_ValidTag_UpsertAndSaves()
    {
        SetupDefaultMocks();
        _driveService.Setup(d => d.EnsureWritable(It.IsAny<string>(), It.IsAny<string>(), out It.Ref<string?>.IsAny)).Returns(true);
        var config = new PortableConfig();
        _modelService.Setup(m => m.LoadConfigAsync(It.IsAny<string>())).ReturnsAsync(config);

        var vm = CreateViewModel();
        vm.Initialize();
        vm.ModelTagInput = "llama3:latest";
        vm.AddModelCommand.Execute(null);

        Thread.Sleep(100);

        _modelService.Verify(m => m.UpsertModel(It.IsAny<List<ModelConfigEntry>>(), "llama3:latest", ModelInstallStatus.NotInstalled), Times.Once);
        _modelService.Verify(m => m.SaveConfigAsync(It.IsAny<string>(), config), Times.AtLeastOnce);
    }

    [Fact]
    public void SelectedDriveWarning_ShowsEncryptionWarning()
    {
        SetupDefaultMocks(encrypted: true);

        var vm = CreateViewModel();
        vm.Initialize();

        Assert.Contains(PrepDriveWriteGuard.ReadOnlyReason, vm.SelectedDriveWarning);
    }

    [Fact]
    public void BuildModelGridRows_MergesConfigAndDisk()
    {
        SetupDefaultMocks();
        var config = new PortableConfig();
        config.Models.Add(new ModelConfigEntry { Name = "llama3:latest", Status = ModelInstallStatus.Installed, Sha256 = "abcdef1234567890", SizeBytes = 4_000_000_000 });
        _modelService.Setup(m => m.LoadConfigAsync(It.IsAny<string>())).ReturnsAsync(config);
        _modelService.Setup(m => m.DiscoverModelsOnDisk(It.IsAny<string>())).Returns(new[] { "llama3:latest", "orphan:model" });

        var vm = CreateViewModel();
        vm.Initialize();

        Thread.Sleep(200);

        Assert.Equal(2, vm.ModelRows.Count);
        var configRow = vm.ModelRows.First(r => r.Name == "llama3:latest");
        Assert.Equal("Ready", configRow.Status);
        Assert.False(configRow.IsOnDiskOnly);

        var orphanRow = vm.ModelRows.First(r => r.Name == "orphan:model");
        Assert.Equal("OnDiskOnly", orphanRow.Status);
        Assert.True(orphanRow.IsOnDiskOnly);
    }

    [Fact]
    public void DetermineConfiguredState_InstalledReturnsReady()
    {
        var model = new ModelConfigEntry { Name = "test", Status = ModelInstallStatus.Installed };
        Assert.Equal("Ready", GetState(model, true));
        Assert.Equal("Ready", GetState(model, false));
    }

    [Fact]
    public void DetermineConfiguredState_NotInstalledAndNotOnDisk()
    {
        var model = new ModelConfigEntry { Name = "test", Status = ModelInstallStatus.NotInstalled };
        Assert.Equal("ConfiguredNotDownloaded", GetState(model, false));
    }

    [Fact]
    public void FormatSize_FormatsCorrectly()
    {
        Assert.Equal("1 KB", PrepViewModel.FormatSize(1024));
        Assert.Equal("1 MB", PrepViewModel.FormatSize(1024 * 1024));
        Assert.Equal("3.73 GB", PrepViewModel.FormatSize(4_000_000_000));
        Assert.Equal("0 B", PrepViewModel.FormatSize(0));
    }

    [Fact]
    public void PrepTargets_Flags_WorkCorrectly()
    {
        var vm = CreateViewModel();
        vm.PrepareWindows = true;
        vm.PrepareMac = false;

        Assert.True(vm.PrepareWindows);
        Assert.False(vm.PrepareMac);
    }

    [Fact]
    public void MacPrepUnavailable_ForcesPrepareMacFalse()
    {
        string? problem = "macOS Runner.app.zip not found";
        _artifactStagingService.Setup(a => a.AreMacArtifactsAvailable(out problem)).Returns(false);
        _driveService.Setup(d => d.GetCandidateDrives(false)).Returns(new List<DriveTarget>());

        var vm = CreateViewModel();
        vm.PrepareMac = true;
        vm.Initialize();

        Assert.False(vm.IsMacPrepAvailable);
        Assert.False(vm.PrepareMac);
        Assert.Equal("macOS Runner.app.zip not found", vm.MacPrepAvailabilityMessage);
    }

    [Fact]
    public void StatusText_Changes_RaisesPropertyChanged()
    {
        var vm = CreateViewModel();
        var changed = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PrepViewModel.StatusText)) changed = true;
        };

        vm.StatusText = "Test status";
        Assert.True(changed);
        Assert.Equal("Test status", vm.StatusText);
    }

    [Fact]
    public void LogLines_InitiallyEmpty()
    {
        var vm = CreateViewModel();
        Assert.Empty(vm.LogLines);
    }

    [Fact]
    public void CancelOperationCommand_CanExecute_OnlyWhenRunning()
    {
        var vm = CreateViewModel();
        Assert.False(vm.CancelOperationCommand.CanExecute(null));
    }

    [Fact]
    public void ProgressValue_CanBeSet()
    {
        var vm = CreateViewModel();
        vm.ProgressValue = 50;
        Assert.Equal(50, vm.ProgressValue);
    }

    [Fact]
    public void ProgressIsIndeterminate_CanBeSet()
    {
        var vm = CreateViewModel();
        vm.ProgressIsIndeterminate = true;
        Assert.True(vm.ProgressIsIndeterminate);
    }

    private static string GetState(ModelConfigEntry model, bool onDisk)
    {
        var method = typeof(PrepViewModel).GetMethod("DetermineConfiguredState",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string)method!.Invoke(null, new object[] { model, onDisk })!;
    }

    // ───── Format & Prepare flow ─────

    private void SetupFormatPath(bool elevated)
    {
        SetupDefaultMocks();
        _driveService.Setup(d => d.EnsureWritable(It.IsAny<string>(), It.IsAny<string>(), out It.Ref<string?>.IsAny)).Returns(true);
        _elevationService.Setup(e => e.IsElevated()).Returns(elevated);
    }

    [Fact]
    public void FormatPrepare_NotElevated_UserDeclinesAdminPrompt_Aborts()
    {
        SetupFormatPath(elevated: false);
        _dialogService.Setup(d => d.ConfirmFixedDrive(It.IsAny<string>())).Returns(true);
        _dialogService.Setup(d => d.ConfirmErase(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        _dialogService.Setup(d => d.Confirm(It.IsAny<string>(), "Administrator required")).Returns(false);

        var vm = CreateViewModel();
        vm.Initialize();
        vm.FormatPrepareCommand.Execute(null);
        Thread.Sleep(100);

        _elevationService.Verify(e => e.TryRelaunchElevated(), Times.Never);
        _driveService.Verify(d => d.FormatAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains(vm.LogLines, l => l.Contains("administrator privileges required"));
    }

    [Fact]
    public void FormatPrepare_NotElevated_UserAccepts_UacDeclined_LogsAndStops()
    {
        SetupFormatPath(elevated: false);
        _dialogService.Setup(d => d.ConfirmErase(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        _dialogService.Setup(d => d.Confirm(It.IsAny<string>(), "Administrator required")).Returns(true);
        _elevationService.Setup(e => e.TryRelaunchElevated()).Returns(false);

        var vm = CreateViewModel();
        vm.Initialize();
        vm.FormatPrepareCommand.Execute(null);
        Thread.Sleep(100);

        _elevationService.Verify(e => e.TryRelaunchElevated(), Times.Once);
        _driveService.Verify(d => d.FormatAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains(vm.LogLines, l => l.Contains("UAC prompt was declined"));
    }

    [Fact]
    public void FormatPrepare_EraseNotConfirmed_Aborts()
    {
        SetupFormatPath(elevated: true);
        _dialogService.Setup(d => d.ConfirmErase(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        var vm = CreateViewModel();
        vm.Initialize();
        vm.FormatPrepareCommand.Execute(null);
        Thread.Sleep(100);

        _driveService.Verify(d => d.FormatAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains(vm.LogLines, l => l.Contains("Format cancelled by user"));
    }

    [Fact]
    public void FormatPrepare_Elevated_HappyPath_FormatsAndPreparesStructure()
    {
        SetupFormatPath(elevated: true);
        _dialogService.Setup(d => d.ConfirmErase(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        _driveService.Setup(d => d.FormatAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var vm = CreateViewModel();
        vm.Initialize();
        vm.FormatPrepareCommand.Execute(null);
        Thread.Sleep(200);

        _driveService.Verify(d => d.FormatAsync("E:\\", It.IsAny<string>(), "NTFS", It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
        _driveService.Verify(d => d.EnsureSsdStructure("E:\\"), Times.Once);
        _modelService.Verify(m => m.SaveConfigAsync(It.IsAny<string>(), It.IsAny<PortableConfig>()), Times.AtLeastOnce);
    }

    [Fact]
    public void FormatPrepare_FormatThrows_LogsAndShowsError()
    {
        SetupFormatPath(elevated: true);
        _dialogService.Setup(d => d.ConfirmErase(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        _driveService.Setup(d => d.FormatAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Format-Volume failed on E:\\ (exit 5)."));

        var vm = CreateViewModel();
        vm.Initialize();
        vm.FormatPrepareCommand.Execute(null);
        Thread.Sleep(200);

        _driveService.Verify(d => d.EnsureSsdStructure(It.IsAny<string>()), Times.Never);
        Assert.Contains(vm.LogLines, l => l.Contains("Drive preparation failed"));
        _dialogService.Verify(d => d.ShowError(It.Is<string>(s => s.Contains("Format-Volume failed")), "Format failed"), Times.Once);
    }
}
