using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Models;
using FreeAiSsd.Shared.Mvvm;
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
        bool isRemovable = true, bool isFixed = false, string warning = "",
        string driveFormat = "NTFS")
        => new(label, rootPath, label, freeBytes, totalBytes, driveFormat, true, isRemovable, isFixed, warning);

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
        _modelService.Setup(m => m.SaveConfigAsync(It.IsAny<string>(), It.IsAny<PortableConfig>())).Returns(Task.CompletedTask);
        _modelService.Setup(m => m.DiscoverModelsOnDisk(It.IsAny<string>())).Returns(Array.Empty<string>());
        _modelService.Setup(m => m.GetSizingWarnings(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<int?>())).Returns(new List<string>());
        _driveService.Setup(d => d.GetFreeDiskSpaceGb(It.IsAny<string>())).Returns(100);
        string? problem = null;
        _artifactStagingService.Setup(a => a.AreMacArtifactsAvailable(out problem)).Returns(false);
    }

    private static async Task WaitForCommandAsync(AsyncRelayCommand command)
    {
        for (var i = 0; i < 100 && command.IsExecuting; i++)
            await Task.Delay(10);

        Assert.False(command.IsExecuting);
        Assert.Null(command.LastException);
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

    // ───── Task #47: Reformat-to-NTFS shortcut + empty-state hint ─────

    [Fact]
    public void ShowReformatHint_FalseForNtfsSelectedDrive()
    {
        SetupDefaultMocks(new List<DriveTarget> { MakeDrive("E:\\", driveFormat: "NTFS") });

        var vm = CreateViewModel();
        vm.Initialize();

        Assert.False(vm.ShowReformatHint);
        Assert.Equal(string.Empty, vm.ReformatHintText);
        Assert.False(vm.ReformatToNtfsCommand.CanExecute(null));
    }

    [Fact]
    public void ShowReformatHint_TrueForExFatSelectedDrive()
    {
        SetupDefaultMocks(new List<DriveTarget> { MakeDrive("E:\\", driveFormat: "exFAT") });

        var vm = CreateViewModel();
        vm.Initialize();

        Assert.True(vm.ShowReformatHint);
        Assert.Contains("exFAT", vm.ReformatHintText);
        Assert.Contains("NTFS", vm.ReformatHintText);
        Assert.True(vm.ReformatToNtfsCommand.CanExecute(null));
    }

    [Fact]
    public void ShowReformatHint_FalseWhenNoDriveSelected()
    {
        SetupDefaultMocks(new List<DriveTarget>());

        var vm = CreateViewModel();
        vm.Initialize();

        Assert.False(vm.ShowReformatHint);
        Assert.False(vm.ReformatToNtfsCommand.CanExecute(null));
    }

    [Fact]
    public void ShowEmptyDriveHint_TrueWhenNoDrivesEnumerated()
    {
        SetupDefaultMocks(new List<DriveTarget>());

        var vm = CreateViewModel();
        vm.Initialize();

        Assert.True(vm.ShowEmptyDriveHint);
    }

    [Fact]
    public void ShowEmptyDriveHint_FalseWhenAtLeastOneDrivePresent()
    {
        SetupDefaultMocks(new List<DriveTarget> { MakeDrive("E:\\") });

        var vm = CreateViewModel();
        vm.Initialize();

        Assert.False(vm.ShowEmptyDriveHint);
    }

    [Fact]
    public void ShowReformatHint_RaisesPropertyChanged_WhenSelectionFlipsBetweenNtfsAndExfat()
    {
        var ntfs = MakeDrive("E:\\", driveFormat: "NTFS");
        var exfat = MakeDrive("F:\\", driveFormat: "exFAT");
        SetupDefaultMocks(new List<DriveTarget> { ntfs, exfat });

        var vm = CreateViewModel();
        vm.Initialize();

        Assert.False(vm.ShowReformatHint); // E:\ NTFS selected by default

        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);
        vm.SelectedDrive = exfat;

        Assert.True(vm.ShowReformatHint);
        Assert.Contains(nameof(PrepViewModel.ShowReformatHint), raised);
        Assert.Contains(nameof(PrepViewModel.ReformatHintText), raised);
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
        Assert.Equal("Downloaded", configRow.Status);
        Assert.False(configRow.IsOnDiskOnly);
        Assert.True(configRow.IsPresentOnDrive);
        Assert.False(configRow.IsSelected);

        var orphanRow = vm.ModelRows.First(r => r.Name == "orphan:model");
        Assert.Equal("On drive only", orphanRow.Status);
        Assert.True(orphanRow.IsOnDiskOnly);
        Assert.True(orphanRow.IsPresentOnDrive);
        Assert.False(orphanRow.IsSelected);
    }

    [Fact]
    public void DetermineConfiguredState_InstalledReturnsDownloaded()
    {
        var model = new ModelConfigEntry { Name = "test", Status = ModelInstallStatus.Installed };
        Assert.Equal("Downloaded", GetState(model, true));
        Assert.Equal("Downloaded", GetState(model, false));
    }

    [Fact]
    public void DetermineConfiguredState_NotInstalledAndNotOnDisk()
    {
        var model = new ModelConfigEntry { Name = "test", Status = ModelInstallStatus.NotInstalled };
        Assert.Equal("Not downloaded", GetState(model, false));
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
    public async Task FinalizeCommand_NoSelectedProfile_ShowsWarning_AndBlocks()
    {
        SetupDefaultMocks();
        _driveService.Setup(d => d.EnsureWritable(It.IsAny<string>(), It.IsAny<string>(), out It.Ref<string?>.IsAny)).Returns(true);

        var vm = CreateViewModel();
        vm.Initialize();

        vm.FinalizeCommand.Execute(null);
        await WaitForCommandAsync(vm.FinalizeCommand);

        _dialogService.Verify(
            d => d.ShowWarning(
                It.Is<string>(message => message.Contains("Choose a Runner profile before finishing setup.")),
                "Profile required"),
            Times.Once);
        _artifactStagingService.Verify(a => a.StageRunnerAsync(It.IsAny<string>(), It.IsAny<Action<string>>()), Times.Never);
        _modelService.Verify(m => m.SaveConfigAsync(It.IsAny<string>(), It.IsAny<PortableConfig>()), Times.Never);
        Assert.Equal("Finalize blocked", vm.StatusText);
        Assert.Equal(
            "Choose a Runner profile before finishing setup. Flight Sim enables DCS bindings, HOTAS push-to-talk, and voice defaults; General Assistant keeps the runtime chat-first.",
            vm.ProfileSelectionWarning);
        Assert.Contains(vm.LogLines, l => l.Contains("no profile selected"));
    }

    [Fact]
    public async Task FinalizeCommand_SelectedProfile_PersistsActiveProfile_AndProfileDefaults()
    {
        SetupDefaultMocks();
        _driveService.Setup(d => d.EnsureWritable(It.IsAny<string>(), It.IsAny<string>(), out It.Ref<string?>.IsAny)).Returns(true);
        _ollamaPackageService
            .Setup(s => s.EnsureOllamaReadyAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(@"E:\windows\tools\ollama\ollama.exe");
        _prereqService
            .Setup(s => s.StagePrerequisitesAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _artifactStagingService
            .Setup(s => s.StageRunnerAsync(It.IsAny<string>(), It.IsAny<Action<string>>()))
            .Returns(Task.CompletedTask);
        _readinessService
            .Setup(s => s.RunReadinessChecksAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReadinessItem> { ReadinessItem.Pass("Runner payload") });

        var config = new PortableConfig
        {
            Models =
            {
                new ModelConfigEntry { Name = "llama3.2:3b", Status = ModelInstallStatus.Installed }
            }
        };
        _modelService.Setup(m => m.LoadConfigAsync(It.IsAny<string>())).ReturnsAsync(config);

        var vm = CreateViewModel();
        vm.Initialize();
        vm.SelectedProfile = UserProfile.FlightSim;

        vm.FinalizeCommand.Execute(null);
        await WaitForCommandAsync(vm.FinalizeCommand);

        _modelService.Verify(
            m => m.SaveConfigAsync(
                It.IsAny<string>(),
                It.Is<PortableConfig>(saved =>
                    saved.ActiveProfile == UserProfile.FlightSim &&
                    saved.PttEnabled &&
                    saved.TtsEnabled &&
                    saved.AutoSendVoiceInput &&
                    saved.PttActivationSoundEnabled &&
                    saved.PttOverlayEnabled)),
            Times.AtLeastOnce);
        _artifactStagingService.Verify(a => a.StageRunnerAsync("E:\\", It.IsAny<Action<string>>()), Times.Once);
        Assert.Equal(UserProfile.FlightSim, config.ActiveProfile);
        Assert.True(config.PttEnabled);
        Assert.True(config.TtsEnabled);
        Assert.True(config.AutoSendVoiceInput);
        Assert.True(config.PttActivationSoundEnabled);
        Assert.True(config.PttOverlayEnabled);
        Assert.Equal("Complete", vm.StatusText);
        Assert.Equal(string.Empty, vm.ProfileSelectionWarning);
    }

    /// MAC34: pin the API-key generation that closes the
    /// "API key is required by configuration but not set on host" 503
    /// trap. Pre-MAC34 finalize wrote `NetworkApiKey = ""` with
    /// `NetworkRequireApiKey = true`, so any LAN-bound request 503'd. This
    /// test asserts a non-empty 64-char lowercase-hex key is set on the
    /// config that flows through `SaveConfigAsync`.
    [Fact]
    public async Task FinalizeCommand_GeneratesNetworkApiKey_WhenEmpty()
    {
        SetupDefaultMocks();
        _driveService.Setup(d => d.EnsureWritable(It.IsAny<string>(), It.IsAny<string>(), out It.Ref<string?>.IsAny)).Returns(true);
        _ollamaPackageService
            .Setup(s => s.EnsureOllamaReadyAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(@"E:\windows\tools\ollama\ollama.exe");
        _prereqService
            .Setup(s => s.StagePrerequisitesAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _artifactStagingService
            .Setup(s => s.StageRunnerAsync(It.IsAny<string>(), It.IsAny<Action<string>>()))
            .Returns(Task.CompletedTask);
        _readinessService
            .Setup(s => s.RunReadinessChecksAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReadinessItem> { ReadinessItem.Pass("Runner payload") });

        var config = new PortableConfig
        {
            Models =
            {
                new ModelConfigEntry { Name = "llama3.2:3b", Status = ModelInstallStatus.Installed }
            }
        };
        Assert.Equal(string.Empty, config.NetworkApiKey);
        _modelService.Setup(m => m.LoadConfigAsync(It.IsAny<string>())).ReturnsAsync(config);

        var vm = CreateViewModel();
        vm.Initialize();
        vm.SelectedProfile = UserProfile.GeneralAssistant;

        vm.FinalizeCommand.Execute(null);
        await WaitForCommandAsync(vm.FinalizeCommand);

        Assert.False(string.IsNullOrWhiteSpace(config.NetworkApiKey));
        Assert.Equal(64, config.NetworkApiKey.Length);
        Assert.Matches("^[0-9a-f]{64}$", config.NetworkApiKey);
    }

    /// MAC34: confirms `FinalizeCommand` is idempotent on `NetworkApiKey` —
    /// re-finalizing an already-prepped drive must not rotate the key, since
    /// that would invalidate any companions/clients already paired with it.
    [Fact]
    public async Task FinalizeCommand_PreservesExistingNetworkApiKey()
    {
        SetupDefaultMocks();
        _driveService.Setup(d => d.EnsureWritable(It.IsAny<string>(), It.IsAny<string>(), out It.Ref<string?>.IsAny)).Returns(true);
        _ollamaPackageService
            .Setup(s => s.EnsureOllamaReadyAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(@"E:\windows\tools\ollama\ollama.exe");
        _prereqService
            .Setup(s => s.StagePrerequisitesAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _artifactStagingService
            .Setup(s => s.StageRunnerAsync(It.IsAny<string>(), It.IsAny<Action<string>>()))
            .Returns(Task.CompletedTask);
        _readinessService
            .Setup(s => s.RunReadinessChecksAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReadinessItem> { ReadinessItem.Pass("Runner payload") });

        const string preExistingKey = "deadbeef" + "deadbeef" + "deadbeef" + "deadbeef" + "deadbeef" + "deadbeef" + "deadbeef" + "deadbeef";
        var config = new PortableConfig
        {
            NetworkApiKey = preExistingKey,
            Models =
            {
                new ModelConfigEntry { Name = "llama3.2:3b", Status = ModelInstallStatus.Installed }
            }
        };
        _modelService.Setup(m => m.LoadConfigAsync(It.IsAny<string>())).ReturnsAsync(config);

        var vm = CreateViewModel();
        vm.Initialize();
        vm.SelectedProfile = UserProfile.GeneralAssistant;

        vm.FinalizeCommand.Execute(null);
        await WaitForCommandAsync(vm.FinalizeCommand);

        Assert.Equal(preExistingKey, config.NetworkApiKey);
    }

    /// MAC30: pin the encryption-OFF (default) finalize path. With
    /// EnableEncryption = false the SaveConfigAsync plaintext write is the
    /// only persistence call — the encryption service must not be invoked
    /// and the dialog service must not prompt for a passphrase. Pre-MAC30
    /// the toggle defaulted off but the contract was undocumented; this
    /// test locks it in so a future refactor can't silently re-introduce
    /// a mandatory passphrase prompt.
    [Fact]
    public async Task FinalizeCommand_EncryptionDisabled_WritesPlaintext_AndDoesNotInvokeEncryption()
    {
        SetupDefaultMocks();
        _driveService.Setup(d => d.EnsureWritable(It.IsAny<string>(), It.IsAny<string>(), out It.Ref<string?>.IsAny)).Returns(true);
        _ollamaPackageService
            .Setup(s => s.EnsureOllamaReadyAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(@"E:\windows\tools\ollama\ollama.exe");
        _prereqService
            .Setup(s => s.StagePrerequisitesAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _artifactStagingService
            .Setup(s => s.StageRunnerAsync(It.IsAny<string>(), It.IsAny<Action<string>>()))
            .Returns(Task.CompletedTask);
        _readinessService
            .Setup(s => s.RunReadinessChecksAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReadinessItem> { ReadinessItem.Pass("Runner payload") });

        var config = new PortableConfig
        {
            Models = { new ModelConfigEntry { Name = "llama3.2:3b", Status = ModelInstallStatus.Installed } }
        };
        _modelService.Setup(m => m.LoadConfigAsync(It.IsAny<string>())).ReturnsAsync(config);

        var vm = CreateViewModel();
        vm.Initialize();
        vm.SelectedProfile = UserProfile.GeneralAssistant;
        Assert.False(vm.EnableEncryption);

        vm.FinalizeCommand.Execute(null);
        await WaitForCommandAsync(vm.FinalizeCommand);

        _modelService.Verify(
            m => m.SaveConfigAsync(It.IsAny<string>(), It.IsAny<PortableConfig>()),
            Times.AtLeastOnce);
        _encryptionService.Verify(
            e => e.EnableConfigEncryptionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
        _encryptionService.Verify(
            e => e.EnableConfigEncryptionAsync(It.IsAny<string>(), It.IsAny<PortableConfig>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _dialogService.Verify(d => d.PromptForEncryptionPassword(), Times.Never);
        Assert.False(config.IsEncrypted);
        Assert.Equal("Complete", vm.StatusText);
    }

    /// MAC32: pre-MAC32 Finalize ended silently with no completion
    /// affordance — user stayed on the prep tab unsure whether anything
    /// had happened. Modal must pop on the full success path.
    [Fact]
    public async Task FinalizeCommand_OnSuccess_ShowsCompletionModal()
    {
        SetupDefaultMocks();
        _driveService.Setup(d => d.EnsureWritable(It.IsAny<string>(), It.IsAny<string>(), out It.Ref<string?>.IsAny)).Returns(true);
        _ollamaPackageService
            .Setup(s => s.EnsureOllamaReadyAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(@"E:\windows\tools\ollama\ollama.exe");
        _prereqService
            .Setup(s => s.StagePrerequisitesAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _artifactStagingService
            .Setup(s => s.StageRunnerAsync(It.IsAny<string>(), It.IsAny<Action<string>>()))
            .Returns(Task.CompletedTask);
        _readinessService
            .Setup(s => s.RunReadinessChecksAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReadinessItem> { ReadinessItem.Pass("Runner payload") });

        var config = new PortableConfig
        {
            Models =
            {
                new ModelConfigEntry { Name = "llama3.2:3b", Status = ModelInstallStatus.Installed }
            }
        };
        _modelService.Setup(m => m.LoadConfigAsync(It.IsAny<string>())).ReturnsAsync(config);

        var vm = CreateViewModel();
        vm.Initialize();
        vm.SelectedProfile = UserProfile.GeneralAssistant;

        vm.FinalizeCommand.Execute(null);
        await WaitForCommandAsync(vm.FinalizeCommand);

        _dialogService.Verify(
            d => d.ShowInfo(
                It.Is<string>(m => m.Contains("Runner.exe", StringComparison.OrdinalIgnoreCase)),
                "Setup complete"),
            Times.Once);
    }

    /// MAC32: modal must NOT fire on the "no profile selected" early-return
    /// path — that's a finalize-blocked branch, not a success.
    [Fact]
    public async Task FinalizeCommand_NoSelectedProfile_DoesNotShowCompletionModal()
    {
        SetupDefaultMocks();
        _driveService.Setup(d => d.EnsureWritable(It.IsAny<string>(), It.IsAny<string>(), out It.Ref<string?>.IsAny)).Returns(true);

        var vm = CreateViewModel();
        vm.Initialize();
        // SelectedProfile left null so TryGetFinalizeProfile blocks.

        vm.FinalizeCommand.Execute(null);
        await WaitForCommandAsync(vm.FinalizeCommand);

        _dialogService.Verify(
            d => d.ShowInfo(It.IsAny<string>(), "Setup complete"),
            Times.Never);
    }

    /// MAC32: modal must NOT fire when readiness fails. The existing
    /// readiness-failure ShowWarning is the user's signal in that case.
    [Fact]
    public async Task FinalizeCommand_OnReadinessFailure_DoesNotShowCompletionModal()
    {
        SetupDefaultMocks();
        _driveService.Setup(d => d.EnsureWritable(It.IsAny<string>(), It.IsAny<string>(), out It.Ref<string?>.IsAny)).Returns(true);
        _ollamaPackageService
            .Setup(s => s.EnsureOllamaReadyAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(@"E:\windows\tools\ollama\ollama.exe");
        _prereqService
            .Setup(s => s.StagePrerequisitesAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _artifactStagingService
            .Setup(s => s.StageRunnerAsync(It.IsAny<string>(), It.IsAny<Action<string>>()))
            .Returns(Task.CompletedTask);
        _readinessService
            .Setup(s => s.RunReadinessChecksAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReadinessItem> { ReadinessItem.Fail("Runner payload", "missing") });

        var config = new PortableConfig
        {
            Models =
            {
                new ModelConfigEntry { Name = "llama3.2:3b", Status = ModelInstallStatus.Installed }
            }
        };
        _modelService.Setup(m => m.LoadConfigAsync(It.IsAny<string>())).ReturnsAsync(config);

        var vm = CreateViewModel();
        vm.Initialize();
        vm.SelectedProfile = UserProfile.GeneralAssistant;

        vm.FinalizeCommand.Execute(null);
        await WaitForCommandAsync(vm.FinalizeCommand);

        _dialogService.Verify(
            d => d.ShowInfo(It.IsAny<string>(), "Setup complete"),
            Times.Never);
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

    [Fact]
    public void ModelGridRow_IsSelected_RaisesPropertyChanged()
    {
        var row = new ModelGridRow(
            "llama3:latest",
            "Not downloaded",
            "Recommended",
            "OK",
            "—",
            "—",
            "—",
            isOnDiskOnly: false,
            isPresentOnDrive: false);

        var changed = false;
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModelGridRow.IsSelected))
                changed = true;
        };

        row.IsSelected = true;

        Assert.True(changed);
        Assert.True(row.IsSelected);
    }

    [Fact]
    public async Task RemoveCommand_ConfigOnly_AppliesToAllCheckedRows()
    {
        SetupDefaultMocks();
        _driveService.Setup(d => d.EnsureWritable(It.IsAny<string>(), It.IsAny<string>(), out It.Ref<string?>.IsAny)).Returns(true);
        var config = new PortableConfig();
        config.Models.Add(new ModelConfigEntry { Name = "llama3:8b", Status = ModelInstallStatus.Installed });
        config.Models.Add(new ModelConfigEntry { Name = "qwen2.5:7b", Status = ModelInstallStatus.NotInstalled });
        _modelService.Setup(m => m.LoadConfigAsync(It.IsAny<string>())).ReturnsAsync(config);
        _dialogService.Setup(d => d.PromptRemoveModel(It.Is<string>(s => s.Contains("2 selected models"))))
            .Returns(ModelRemoveChoice.ConfigOnly);

        var vm = CreateViewModel();
        vm.Initialize();
        Thread.Sleep(100);
        vm.ModelRows.First(r => r.Name == "llama3:8b").IsSelected = true;
        vm.ModelRows.First(r => r.Name == "qwen2.5:7b").IsSelected = true;

        vm.RemoveCommand.Execute(null);
        await WaitForCommandAsync(vm.RemoveCommand);

        Assert.Empty(config.Models);
        _modelService.Verify(m => m.SaveConfigAsync(It.IsAny<string>(), config), Times.Once);
    }

    [Fact]
    public async Task RemoveCommand_RecommendedOnlySelection_DoesNotPrompt()
    {
        SetupDefaultMocks();
        _driveService.Setup(d => d.EnsureWritable(It.IsAny<string>(), It.IsAny<string>(), out It.Ref<string?>.IsAny)).Returns(true);

        var vm = CreateViewModel();
        vm.Initialize();
        await vm.SetStarterCatalogAsync(new[]
        {
            new StarterCatalogEntry("llama3.2:3b", "Small", "General chat")
        });

        var row = vm.ModelRows.Single(r => r.Name == "llama3.2:3b");
        row.IsSelected = true;

        vm.RemoveCommand.Execute(null);
        await WaitForCommandAsync(vm.RemoveCommand);

        _dialogService.Verify(d => d.PromptRemoveModel(It.IsAny<string>()), Times.Never);
        Assert.Contains(vm.LogLines, l => l.Contains("Remove only works"));
    }

    [Fact]
    public async Task DownloadCommand_CheckedRowsAlreadyOnDrive_SkipsPull()
    {
        SetupDefaultMocks();
        _driveService.Setup(d => d.EnsureWritable(It.IsAny<string>(), It.IsAny<string>(), out It.Ref<string?>.IsAny)).Returns(true);
        var config = new PortableConfig();
        config.Models.Add(new ModelConfigEntry { Name = "llama3:latest", Status = ModelInstallStatus.Installed });
        _modelService.Setup(m => m.LoadConfigAsync(It.IsAny<string>())).ReturnsAsync(config);
        _modelService.Setup(m => m.DiscoverModelsOnDisk(It.IsAny<string>())).Returns(new[] { "llama3:latest" });

        var vm = CreateViewModel();
        vm.Initialize();
        Thread.Sleep(100);
        vm.ModelRows.Single(r => r.Name == "llama3:latest").IsSelected = true;

        vm.DownloadCommand.Execute(null);
        await WaitForCommandAsync(vm.DownloadCommand);

        _driveService.Verify(d => d.EnsureSsdStructure(It.IsAny<string>()), Times.Never);
        _modelService.Verify(m => m.PullModelAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<Action<string>>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<string?>(),
            It.IsAny<Action<OllamaPullProgress>?>(), It.IsAny<Action?>()), Times.Never);
        Assert.Contains(vm.LogLines, l => l.Contains("nothing to download"));
    }

    [Fact]
    public void ClearSelectionCommand_UnchecksEveryRow()
    {
        SetupDefaultMocks();
        var config = new PortableConfig();
        config.Models.Add(new ModelConfigEntry { Name = "llama3:8b", Status = ModelInstallStatus.NotInstalled });
        config.Models.Add(new ModelConfigEntry { Name = "qwen2.5:7b", Status = ModelInstallStatus.NotInstalled });
        _modelService.Setup(m => m.LoadConfigAsync(It.IsAny<string>())).ReturnsAsync(config);

        var vm = CreateViewModel();
        vm.Initialize();
        Thread.Sleep(100);
        vm.ModelRows.First().IsSelected = true;
        vm.ModelRows.Last().IsSelected = true;

        vm.ClearSelectionCommand.Execute(null);

        Assert.All(vm.ModelRows, row => Assert.False(row.IsSelected));
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
        _dialogService.Setup(d => d.ConfirmErase(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        _dialogService.Setup(d => d.Confirm(It.IsAny<string>(), "Administrator required")).Returns(false);

        var vm = CreateViewModel();
        vm.Initialize();
        vm.FormatPrepareCommand.Execute(null);
        Thread.Sleep(100);

        _elevationService.Verify(e => e.TryRelaunchElevated(It.IsAny<IEnumerable<string>?>()), Times.Never);
        _driveService.Verify(d => d.FormatAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
        Assert.Contains(vm.LogLines, l => l.Contains("administrator privileges required"));
    }

    [Fact]
    public void FormatPrepare_NotElevated_UserAccepts_UacDeclined_LogsAndStops()
    {
        SetupFormatPath(elevated: false);
        _dialogService.Setup(d => d.ConfirmErase(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        _dialogService.Setup(d => d.Confirm(It.IsAny<string>(), "Administrator required")).Returns(true);
        _elevationService.Setup(e => e.TryRelaunchElevated(It.IsAny<IEnumerable<string>?>())).Returns(false);

        var vm = CreateViewModel();
        vm.Initialize();
        vm.FormatPrepareCommand.Execute(null);
        Thread.Sleep(100);

        _elevationService.Verify(
            e => e.TryRelaunchElevated(It.Is<IEnumerable<string>>(args =>
                args.Contains("--autoresume-format=E:\\") &&
                args.Contains("--autoresume-label=Portable AI"))),
            Times.Once);
        _driveService.Verify(d => d.FormatAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains(vm.LogLines, l => l.Contains("UAC prompt was declined"));
    }

    [Fact]
    public void FormatPrepare_EraseNotConfirmed_Aborts()
    {
        SetupFormatPath(elevated: true);
        _dialogService.Setup(d => d.ConfirmErase(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(false);

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
        _dialogService.Setup(d => d.ConfirmErase(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(true);
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

    // ───── MAC10a: PrepTargets → filesystem mapping ─────

    [Fact]
    public void ResolveFileSystem_WindowsOnly_IsNtfs()
        => Assert.Equal("NTFS", PrepViewModel.ResolveFileSystem(PrepTargets.Windows));

    [Fact]
    public void ResolveFileSystem_WindowsAndMac_IsExFat()
        => Assert.Equal("exFAT", PrepViewModel.ResolveFileSystem(PrepTargets.Windows | PrepTargets.Mac));

    [Fact]
    public void ResolveFileSystem_MacOnly_IsExFat()
        => Assert.Equal("exFAT", PrepViewModel.ResolveFileSystem(PrepTargets.Mac));

    [Fact]
    public void ResolveFileSystem_None_Throws()
        => Assert.Throws<InvalidOperationException>(() => PrepViewModel.ResolveFileSystem(PrepTargets.None));

    [Fact]
    public void FormatPrepare_WindowsAndMac_FormatsAsExFat()
    {
        SetupFormatPath(elevated: true);
        // PrepareMac setter clamps to false unless Mac artifacts are
        // available — override the SetupDefaultMocks default so the
        // mac checkbox stays on through Initialize and the format flow.
        string? macProblem = null;
        _artifactStagingService.Setup(a => a.AreMacArtifactsAvailable(out macProblem)).Returns(true);
        _dialogService.Setup(d => d.ConfirmErase(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        _driveService.Setup(d => d.FormatAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var vm = CreateViewModel();
        vm.Initialize();
        vm.PrepareWindows = true;
        vm.PrepareMac = true;
        vm.FormatPrepareCommand.Execute(null);
        Thread.Sleep(200);

        _driveService.Verify(d => d.FormatAsync("E:\\", It.IsAny<string>(), "exFAT",
            It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
        _dialogService.Verify(d => d.ConfirmErase("E:\\", It.IsAny<string>(), "exFAT"), Times.Once);
    }

    [Fact]
    public void FormatPrepare_MacOnly_FormatsAsExFat()
    {
        SetupFormatPath(elevated: true);
        string? macProblem = null;
        _artifactStagingService.Setup(a => a.AreMacArtifactsAvailable(out macProblem)).Returns(true);
        _dialogService.Setup(d => d.ConfirmErase(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        _driveService.Setup(d => d.FormatAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var vm = CreateViewModel();
        vm.Initialize();
        vm.PrepareMac = true;
        vm.PrepareWindows = false;
        vm.FormatPrepareCommand.Execute(null);
        Thread.Sleep(200);

        _driveService.Verify(d => d.FormatAsync("E:\\", It.IsAny<string>(), "exFAT",
            It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ───── Auto-resume (B3-Redux phase 2) ─────

    [Fact]
    public async Task TryAutoResumeFormat_NoIntent_IsNoOp()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        vm.Initialize();

        await vm.TryAutoResumeFormatAsync();

        _driveService.Verify(
            d => d.FormatAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    public async Task TryAutoResumeFormat_DriveNoLongerPresent_ShowsWarning_NoFormat()
    {
        // Setup: intent targets G:\ but enumeration only returns E:\.
        // Must log + show warning + never call FormatAsync.
        SetupDefaultMocks(); // returns [E:\]
        var vm = CreateViewModel();
        vm.Initialize();
        vm.ApplyStartupIntent("G:\\", "Portable AI", diagEnabled: false);

        await vm.TryAutoResumeFormatAsync();

        _dialogService.Verify(
            d => d.ShowWarning(It.Is<string>(s => s.Contains("G:\\")), "Drive not found"),
            Times.Once);
        _driveService.Verify(
            d => d.FormatAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Never);
        Assert.Contains(vm.LogLines, l => l.Contains("no longer present"));
    }

    [Fact]
    public async Task TryAutoResumeFormat_ConsumesIntent_SecondCallIsNoOp()
    {
        // Intent must be consumed on attempt so it never fires twice.
        SetupDefaultMocks(); // returns [E:\] — intent for G:\ won't match
        var vm = CreateViewModel();
        vm.Initialize();
        vm.ApplyStartupIntent("G:\\", "label", diagEnabled: false);

        await vm.TryAutoResumeFormatAsync();
        _dialogService.Invocations.Clear();

        await vm.TryAutoResumeFormatAsync();

        _dialogService.Verify(
            d => d.ShowWarning(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task TryAutoResumeFormat_DrivePresent_SelectsAndFiresFormatWithConfirm()
    {
        SetupFormatPath(elevated: true);
        _dialogService.Setup(d => d.ConfirmErase(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        _driveService.Setup(d => d.FormatAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

        var vm = CreateViewModel();
        vm.Initialize();
        vm.ApplyStartupIntent("E:\\", "Resumed Label", diagEnabled: false);

        await vm.TryAutoResumeFormatAsync();

        Assert.Equal("Resumed Label", vm.VolumeLabel);
        _dialogService.Verify(d => d.ConfirmErase("E:\\", It.IsAny<string>(), "NTFS"), Times.Once);
        _driveService.Verify(d => d.FormatAsync("E:\\", "Resumed Label", "NTFS",
            It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>(), false), Times.Once);
    }

    [Fact]
    public async Task TryAutoResumeFormat_UserDeclinesConfirm_NoFormat()
    {
        SetupFormatPath(elevated: true);
        _dialogService.Setup(d => d.ConfirmErase(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        var vm = CreateViewModel();
        vm.Initialize();
        vm.ApplyStartupIntent("E:\\", "label", diagEnabled: false);

        await vm.TryAutoResumeFormatAsync();

        _driveService.Verify(d => d.FormatAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void ElevationBanner_ReflectsIntent()
    {
        SetupDefaultMocks();
        _elevationService.Setup(e => e.IsElevated()).Returns(true);
        var vm = CreateViewModel();

        // No intent → fallback copy.
        Assert.False(vm.HasAutoResumeIntent);
        Assert.Contains("Click Format", vm.ElevationBannerText);

        // Intent applied → "ready to continue" copy.
        vm.ApplyStartupIntent("E:\\", "label", diagEnabled: false);
        Assert.True(vm.HasAutoResumeIntent);
        Assert.Contains("ready to continue", vm.ElevationBannerText);
    }

    [Fact]
    public void FormatPrepare_FormatThrows_LogsAndShowsError()
    {
        SetupFormatPath(elevated: true);
        _dialogService.Setup(d => d.ConfirmErase(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(true);
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

    // ── F2a: picker filter (search + most-popular cap) ──────────────

    [Fact]
    public async Task IsModelRowVisible_EmptySearch_ShowsAllRows()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        vm.Initialize();
        await vm.SetStarterCatalogAsync(F2aCatalogFixture());

        Assert.All(vm.ModelRows, r => Assert.True(vm.IsModelRowVisible(r)));
    }

    [Fact]
    public async Task IsModelRowVisible_SearchFiltersByName()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        vm.Initialize();
        await vm.SetStarterCatalogAsync(F2aCatalogFixture());

        vm.ModelSearchText = "llama";

        var visible = vm.ModelRows.Where(vm.IsModelRowVisible).ToList();
        Assert.NotEmpty(visible);
        Assert.All(visible, r => Assert.Contains("llama", r.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IsModelRowVisible_SearchMatchesBestAtCaseInsensitive()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        vm.Initialize();
        await vm.SetStarterCatalogAsync(F2aCatalogFixture());

        vm.ModelSearchText = "REASONING";   // matches "reasoning, coding" in qwen2.5:7b's BestAt

        var visible = vm.ModelRows.Where(vm.IsModelRowVisible).Select(r => r.Name).ToList();
        Assert.Contains("qwen2.5:7b", visible);
    }

    [Fact]
    public async Task IsModelRowVisible_TrimsSearchWhitespace()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        vm.Initialize();
        await vm.SetStarterCatalogAsync(F2aCatalogFixture());

        vm.ModelSearchText = "   llama   ";
        var trimmed = vm.ModelRows.Where(vm.IsModelRowVisible).Select(r => r.Name).OrderBy(n => n).ToList();

        vm.ModelSearchText = "llama";
        var plain = vm.ModelRows.Where(vm.IsModelRowVisible).Select(r => r.Name).OrderBy(n => n).ToList();

        Assert.Equal(plain, trimmed);
    }

    [Fact]
    public async Task IsModelRowVisible_MostPopular_CapsRecommendedRowsToTopByPullCount()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        vm.Initialize();
        // 18-entry catalog (3 above the 15-cap) so the cap actually
        // excludes someone. Pull counts descend from 100M down by 5M.
        var catalog = Enumerable.Range(0, 18)
            .Select(i => new StarterCatalogEntry(
                Tag: $"popular:{i}",
                SizeTier: "Medium",
                BestAt: $"Variant {i}",
                PullCount: 100_000_000L - i * 5_000_000L))
            .ToList();
        await vm.SetStarterCatalogAsync(catalog);

        vm.ShowOnlyMostPopular = true;

        var visibleRecommended = vm.ModelRows
            .Where(r => string.Equals(r.Source, "Recommended", StringComparison.OrdinalIgnoreCase))
            .Where(vm.IsModelRowVisible)
            .Select(r => r.Name)
            .ToList();

        Assert.Equal(PrepViewModel.DefaultMostPopularLimit, visibleRecommended.Count);
        // The bottom three (popular:15..popular:17) must be hidden.
        Assert.DoesNotContain("popular:15", visibleRecommended);
        Assert.DoesNotContain("popular:16", visibleRecommended);
        Assert.DoesNotContain("popular:17", visibleRecommended);
        // And the top three must be present.
        Assert.Contains("popular:0", visibleRecommended);
        Assert.Contains("popular:1", visibleRecommended);
        Assert.Contains("popular:2", visibleRecommended);
    }

    [Fact]
    public async Task IsModelRowVisible_MostPopular_HidesRecommendedRowsWithoutPullCount()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        vm.Initialize();
        // Mirror the bundled-catalog state: no pull counts at all.
        await vm.SetStarterCatalogAsync(new[]
        {
            new StarterCatalogEntry("bundled-1", "Small", "Bundled entry 1"),
            new StarterCatalogEntry("bundled-2", "Small", "Bundled entry 2"),
        });

        vm.ShowOnlyMostPopular = true;

        var visibleRecommended = vm.ModelRows
            .Where(r => string.Equals(r.Source, "Recommended", StringComparison.OrdinalIgnoreCase))
            .Where(vm.IsModelRowVisible)
            .ToList();
        Assert.Empty(visibleRecommended);
    }

    [Fact]
    public async Task IsModelRowVisible_MostPopular_LeavesConfiguredRowsAlone()
    {
        SetupDefaultMocks();
        var config = new PortableConfig();
        config.Models.Add(new ModelConfigEntry { Name = "llama3:latest", Status = ModelInstallStatus.Installed });
        _modelService.Setup(m => m.LoadConfigAsync(It.IsAny<string>())).ReturnsAsync(config);
        _modelService.Setup(m => m.DiscoverModelsOnDisk(It.IsAny<string>())).Returns(new[] { "llama3:latest" });

        var vm = CreateViewModel();
        vm.Initialize();
        await vm.SetStarterCatalogAsync(F2aCatalogFixture());

        vm.ShowOnlyMostPopular = true;

        var configuredRow = vm.ModelRows.SingleOrDefault(r => r.Name == "llama3:latest");
        Assert.NotNull(configuredRow);
        Assert.True(vm.IsModelRowVisible(configuredRow!),
            "configured rows must always pass the popular filter");
    }

    [Fact]
    public async Task ModelRowsViewInvalidated_FiresWhenSearchTextChanges()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        vm.Initialize();
        await vm.SetStarterCatalogAsync(F2aCatalogFixture());

        var fired = 0;
        vm.ModelRowsViewInvalidated += (_, _) => fired++;

        vm.ModelSearchText = "llama";
        Assert.Equal(1, fired);

        vm.ModelSearchText = "llama";   // no-op set
        Assert.Equal(1, fired);

        vm.ShowOnlyMostPopular = true;
        Assert.Equal(2, fired);
    }

    // M11: integration pin against the perception bug. Toggling
    // ShowOnlyMostPopular has to (a) populate StarterRowCountCaption
    // with a non-empty visible/total string and (b) raise PropertyChanged
    // so the WPF binding refreshes. Without this surface the toggle
    // looks no-op when ollama.com's natural order already has the
    // popular models on top.
    [Fact]
    public async Task StarterRowCountCaption_PopulatedAndRaisesPropertyChanged_WhenMostPopularToggled()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        vm.Initialize();
        await vm.SetStarterCatalogAsync(F2aCatalogFixture());

        var captionChanges = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.StarterRowCountCaption)) captionChanges++;
        };

        Assert.Equal(string.Empty, vm.StarterRowCountCaption);

        vm.ShowOnlyMostPopular = true;
        Assert.True(captionChanges >= 1, $"expected PropertyChanged for caption; got {captionChanges}");
        Assert.Contains("Showing top", vm.StarterRowCountCaption);
        Assert.Contains("by pulls.", vm.StarterRowCountCaption);

        vm.ShowOnlyMostPopular = false;
        Assert.Equal(string.Empty, vm.StarterRowCountCaption);
    }

    [Fact]
    public async Task StarterRowCountCaption_AnnouncesSearchFilter_WhenOnlySearchActive()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        vm.Initialize();
        await vm.SetStarterCatalogAsync(F2aCatalogFixture());

        vm.ModelSearchText = "llama";

        Assert.Contains("matching search.", vm.StarterRowCountCaption);
        Assert.DoesNotContain("by pulls", vm.StarterRowCountCaption);
    }

    private static List<StarterCatalogEntry> F2aCatalogFixture() => new()
    {
        new StarterCatalogEntry("llama3.2:1b", "Small",
            "Lightweight assistant for quick prompts (chat, fast)", 114_000_000L),
        new StarterCatalogEntry("llama3.2:3b", "Small",
            "Balanced small model for everyday Q&A (chat, general)", 90_000_000L),
        new StarterCatalogEntry("qwen2.5:7b", "Medium",
            "Versatile 7B with reasoning + coding support (reasoning, coding)", 50_000_000L),
        new StarterCatalogEntry("gemma2:2b", "Small",
            "Good starter when hardware is limited (cpu-friendly)", 200_000_000L),
        new StarterCatalogEntry("deepseek-r1:70b", "Large",
            "Frontier reasoning model (reasoning)", 25_000_000L),
        new StarterCatalogEntry("bundled-only:1b", "Small",
            "Fallback bundled entry (chat)", null),
    };

    // ── C3 / C4 / C5 picker filter cluster ──────────────────────────

    private static List<StarterCatalogEntry> C3C4C5Fixture() => new()
    {
        // Tools+vision multi-cap entry — the only one that survives an
        // AND filter on {tools, vision}.
        new StarterCatalogEntry("multi-tool:8b", "Medium", "Tool-using vision model",
            PullCount: 50_000_000L,
            Capabilities: new[] { "tools", "vision" },
            ParametersBillion: 8.0,
            LastUpdated: new DateTimeOffset(2026, 5, 8, 0, 0, 0, TimeSpan.Zero)),
        new StarterCatalogEntry("tools-only:7b", "Medium", "Tool-using small model",
            PullCount: 30_000_000L,
            Capabilities: new[] { "tools" },
            ParametersBillion: 7.0,
            LastUpdated: new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero)),
        new StarterCatalogEntry("vision-only:14b", "Large", "Vision-capable mid model",
            PullCount: 20_000_000L,
            Capabilities: new[] { "vision" },
            ParametersBillion: 14.0,
            LastUpdated: new DateTimeOffset(2026, 2, 10, 0, 0, 0, TimeSpan.Zero)),
        new StarterCatalogEntry("deepseek-r1:70b", "Large", "Frontier reasoning",
            PullCount: 25_000_000L,
            Capabilities: new[] { "thinking" },
            ParametersBillion: 70.0,
            LastUpdated: new DateTimeOffset(2026, 3, 20, 0, 0, 0, TimeSpan.Zero)),
        // Bundled-style: empty caps + nil params + nil date — pass-through
        // under every filter, sorts last under newest.
        new StarterCatalogEntry("bundled-only:1b", "Small", "Fallback bundled entry",
            PullCount: null,
            Capabilities: Array.Empty<string>(),
            ParametersBillion: null,
            LastUpdated: null),
    };

    [Fact]
    public async Task IsModelRowVisible_ParameterCap_DropsRowsAboveCap()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        vm.Initialize();
        await vm.SetStarterCatalogAsync(C3C4C5Fixture());

        vm.MaxParametersBillion = 14.0;

        var visible = vm.ModelRows
            .Where(r => string.Equals(r.Source, "Recommended", StringComparison.OrdinalIgnoreCase))
            .Where(vm.IsModelRowVisible)
            .Select(r => r.Name)
            .ToList();

        Assert.DoesNotContain("deepseek-r1:70b", visible);
        Assert.Contains("multi-tool:8b", visible);
        Assert.Contains("tools-only:7b", visible);
        Assert.Contains("vision-only:14b", visible);
        // Null-params entry must pass through (matches the C4 capability
        // and F2a Most-popular pass-through posture).
        Assert.Contains("bundled-only:1b", visible);
    }

    [Fact]
    public async Task IsModelRowVisible_ParameterCap_NullCapIsNoOp()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        vm.Initialize();
        await vm.SetStarterCatalogAsync(C3C4C5Fixture());

        vm.MaxParametersBillion = null;

        var visible = vm.ModelRows.Where(vm.IsModelRowVisible).ToList();
        Assert.Equal(vm.ModelRows.Count, visible.Count);
    }

    [Fact]
    public async Task IsModelRowVisible_Capabilities_AndSemantics()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        vm.Initialize();
        await vm.SetStarterCatalogAsync(C3C4C5Fixture());

        vm.FilterCapabilityTools = true;
        vm.FilterCapabilityVision = true;

        var visibleRecommended = vm.ModelRows
            .Where(r => string.Equals(r.Source, "Recommended", StringComparison.OrdinalIgnoreCase))
            .Where(vm.IsModelRowVisible)
            .Select(r => r.Name)
            .ToList();

        // Only multi-tool:8b carries both tools+vision.
        Assert.Contains("multi-tool:8b", visibleRecommended);
        Assert.DoesNotContain("tools-only:7b", visibleRecommended);
        Assert.DoesNotContain("vision-only:14b", visibleRecommended);
        Assert.DoesNotContain("deepseek-r1:70b", visibleRecommended);
        // Empty-capabilities entry must still pass through.
        Assert.Contains("bundled-only:1b", visibleRecommended);
    }

    [Fact]
    public async Task IsModelRowVisible_Capabilities_EmptySetIsNoOp()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        vm.Initialize();
        await vm.SetStarterCatalogAsync(C3C4C5Fixture());

        Assert.Empty(vm.ActiveCapabilityFilters);
        var visible = vm.ModelRows.Where(vm.IsModelRowVisible).ToList();
        Assert.Equal(vm.ModelRows.Count, visible.Count);
    }

    [Fact]
    public async Task FilterCapabilityToggles_UpdateActiveSetAndRaiseInvalidation()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        vm.Initialize();
        await vm.SetStarterCatalogAsync(C3C4C5Fixture());

        var fired = 0;
        vm.ModelRowsViewInvalidated += (_, _) => fired++;

        vm.FilterCapabilityTools = true;
        Assert.Contains("tools", vm.ActiveCapabilityFilters);
        Assert.Equal(1, fired);

        vm.FilterCapabilityTools = true;   // no-op set
        Assert.Equal(1, fired);

        vm.FilterCapabilityTools = false;
        Assert.DoesNotContain("tools", vm.ActiveCapabilityFilters);
        Assert.Equal(2, fired);
    }

    [Fact]
    public async Task SortMode_ChangeRaisesInvalidation()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        vm.Initialize();
        await vm.SetStarterCatalogAsync(C3C4C5Fixture());

        var fired = 0;
        vm.ModelRowsViewInvalidated += (_, _) => fired++;

        vm.SortMode = ModelSortMode.Newest;
        Assert.Equal(1, fired);
        vm.SortMode = ModelSortMode.Newest;   // no-op
        Assert.Equal(1, fired);
        vm.SortMode = ModelSortMode.Alphabetical;
        Assert.Equal(2, fired);
    }

    [Fact]
    public async Task StarterRowCountCaption_IncludesParameterCap()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        vm.Initialize();
        await vm.SetStarterCatalogAsync(C3C4C5Fixture());

        vm.MaxParametersBillion = 14.0;
        Assert.Contains("≤14B", vm.StarterRowCountCaption);
    }

    // ───── C25: capability pass-through marker ─────
    //
    // Visual cue is rendered by the XAML row style (MultiDataTrigger
    // gated on row.Capabilities.Count == 0 AND VM.HasActiveCapabilityFilter);
    // the unit tests cover the VM signal the row style depends on.

    [Fact]
    public void HasActiveCapabilityFilter_FalseByDefault()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        vm.Initialize();
        Assert.False(vm.HasActiveCapabilityFilter);
    }

    [Fact]
    public async Task HasActiveCapabilityFilter_TogglesWithChips()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        vm.Initialize();
        await vm.SetStarterCatalogAsync(C3C4C5Fixture());

        var changes = new List<string>();
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PrepViewModel.HasActiveCapabilityFilter))
                changes.Add(args.PropertyName!);
        };

        vm.FilterCapabilityTools = true;
        Assert.True(vm.HasActiveCapabilityFilter);
        // Adding a second chip is still "active" — no flip back to false.
        vm.FilterCapabilityVision = true;
        Assert.True(vm.HasActiveCapabilityFilter);
        // Clearing both flips it back to false.
        vm.FilterCapabilityTools = false;
        vm.FilterCapabilityVision = false;
        Assert.False(vm.HasActiveCapabilityFilter);
        // PropertyChanged fired at least once when state crossed boundaries —
        // we don't assert exact count because each chip toggle raises it.
        Assert.NotEmpty(changes);
    }

    // ───── C26: Most-popular limit dropdown ─────

    [Fact]
    public void MostPopularLimit_DefaultMatchesConstant()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        vm.Initialize();
        Assert.Equal(PrepViewModel.DefaultMostPopularLimit, vm.MostPopularLimit);
    }

    [Fact]
    public async Task MostPopularLimit_ChangeRecomputesTopTagsAndInvalidates()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        vm.Initialize();
        // 30 recommended entries with descending pull counts so different
        // top-N caps produce visibly different visible sets.
        var catalog = Enumerable.Range(0, 30)
            .Select(i => new StarterCatalogEntry(
                Tag: $"limit:{i}",
                SizeTier: "Medium",
                BestAt: $"Variant {i}",
                PullCount: 100_000_000L - i * 1_000_000L))
            .ToList();
        await vm.SetStarterCatalogAsync(catalog);
        vm.ShowOnlyMostPopular = true;

        var fired = 0;
        vm.ModelRowsViewInvalidated += (_, _) => fired++;

        vm.MostPopularLimit = 10;
        var visibleAt10 = vm.ModelRows
            .Where(r => string.Equals(r.Source, "Recommended", StringComparison.OrdinalIgnoreCase))
            .Count(vm.IsModelRowVisible);
        Assert.Equal(10, visibleAt10);
        Assert.Equal(1, fired);

        vm.MostPopularLimit = 25;
        var visibleAt25 = vm.ModelRows
            .Where(r => string.Equals(r.Source, "Recommended", StringComparison.OrdinalIgnoreCase))
            .Count(vm.IsModelRowVisible);
        Assert.Equal(25, visibleAt25);
        Assert.Equal(2, fired);

        // No-op set must not fire.
        vm.MostPopularLimit = 25;
        Assert.Equal(2, fired);
    }

    [Fact]
    public void MostPopularLimitOptions_AreSurfaced()
    {
        // C26: the WPF/Mac dropdown read these — pin so a future
        // refactor doesn't silently drop an option.
        Assert.Contains(10, PrepViewModel.MostPopularLimitOptions);
        Assert.Contains(15, PrepViewModel.MostPopularLimitOptions);
        Assert.Contains(25, PrepViewModel.MostPopularLimitOptions);
        Assert.Contains(50, PrepViewModel.MostPopularLimitOptions);
    }

    // ───── C2: embedding-model auto-pull pins ─────

    /// C2 Stage 1b. The user-facing failure mode pre-C2 was: prep a
    /// fresh SSD, pick a chat model from the F2a picker, finalize, then
    /// drop a small PDF into a library — every chunk's /api/embed call
    /// 404s because nothing ever pulled the embedder. This pin covers
    /// the Download path: after the chat-model pull loop, the embedder
    /// MUST be pulled too, sharing the same temp Ollama server.
    [Fact]
    public async Task DownloadCommand_PullsEmbeddingModel_AfterChatModels_WhenMissing()
    {
        SetupDefaultMocks();
        _driveService.Setup(d => d.EnsureWritable(It.IsAny<string>(), It.IsAny<string>(), out It.Ref<string?>.IsAny)).Returns(true);
        // SetupDefaultMocks omits BuildPullSelectionWarnings (the only existing
        // download test short-circuits before ConfirmSizingWarningsIfNeeded);
        // mock here so the real download path reaches PullModelsAsync.
        _modelService.Setup(m => m.BuildPullSelectionWarnings(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>())).Returns(new List<string>());

        var config = new PortableConfig();
        config.Models.Add(new ModelConfigEntry { Name = "llama3.2:3b", Status = ModelInstallStatus.NotInstalled });
        _modelService.Setup(m => m.LoadConfigAsync(It.IsAny<string>())).ReturnsAsync(config);
        _modelService.Setup(m => m.DiscoverModelsOnDisk(It.IsAny<string>())).Returns(Array.Empty<string>());
        _ollamaPackageService
            .Setup(s => s.EnsureOllamaReadyAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(@"E:\windows\tools\ollama\ollama.exe");
        _ollamaPackageService
            .Setup(s => s.StartTemporaryServerAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(new FakeOllamaServerHandle("127.0.0.1:11434"));
        _modelService
            .Setup(m => m.PullModelAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<Action<OllamaPullProgress>?>(), It.IsAny<Action?>()))
            .ReturnsAsync(new ModelPullResult("0123456789abcdef", 270_000_000));

        var vm = CreateViewModel();
        vm.Initialize();
        Thread.Sleep(100);
        vm.ModelRows.Single(r => r.Name == "llama3.2:3b").IsSelected = true;

        vm.DownloadCommand.Execute(null);
        await WaitForCommandAsync(vm.DownloadCommand);

        // Chat model pulled.
        _modelService.Verify(m => m.PullModelAsync(
            It.IsAny<string>(), It.IsAny<string>(), "llama3.2:3b",
            It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(),
            "127.0.0.1:11434", It.IsAny<Action<OllamaPullProgress>?>(), It.IsAny<Action?>()),
            Times.Once);

        // Embedder pulled (default EmbeddingModelName = "nomic-embed-text",
        // normalized to nomic-embed-text:latest by the helper).
        _modelService.Verify(m => m.PullModelAsync(
            It.IsAny<string>(), It.IsAny<string>(), "nomic-embed-text:latest",
            It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(),
            "127.0.0.1:11434", It.IsAny<Action<OllamaPullProgress>?>(), It.IsAny<Action?>()),
            Times.Once);

        // Embedder marked Installed in the on-disk config.
        _modelService.Verify(m => m.UpdateModelStatusAsync(
            It.IsAny<string>(), "nomic-embed-text:latest",
            ModelInstallStatus.Installed,
            It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<DateTime?>()),
            Times.Once);
    }

    /// C2 idempotency. If the embedder is already on the SSD (e.g. the
    /// user ran prep before), DownloadAsync's tail must NOT re-pull it.
    /// Disk-truth check is the source of truth — re-pulling would burn
    /// 270 MB of bandwidth for nothing.
    [Fact]
    public async Task DownloadCommand_SkipsEmbeddingPull_WhenAlreadyOnDisk()
    {
        SetupDefaultMocks();
        _driveService.Setup(d => d.EnsureWritable(It.IsAny<string>(), It.IsAny<string>(), out It.Ref<string?>.IsAny)).Returns(true);
        _modelService.Setup(m => m.BuildPullSelectionWarnings(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>())).Returns(new List<string>());

        var config = new PortableConfig();
        config.Models.Add(new ModelConfigEntry { Name = "llama3.2:3b", Status = ModelInstallStatus.NotInstalled });
        _modelService.Setup(m => m.LoadConfigAsync(It.IsAny<string>())).ReturnsAsync(config);
        // Embedder already present on disk; chat model not yet.
        _modelService.Setup(m => m.DiscoverModelsOnDisk(It.IsAny<string>())).Returns(new[] { "nomic-embed-text:latest" });
        _ollamaPackageService
            .Setup(s => s.EnsureOllamaReadyAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(@"E:\windows\tools\ollama\ollama.exe");
        _ollamaPackageService
            .Setup(s => s.StartTemporaryServerAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(new FakeOllamaServerHandle("127.0.0.1:11434"));
        _modelService
            .Setup(m => m.PullModelAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<Action<OllamaPullProgress>?>(), It.IsAny<Action?>()))
            .ReturnsAsync(new ModelPullResult("abcdefabcdef", 4_000_000_000L));

        var vm = CreateViewModel();
        vm.Initialize();
        Thread.Sleep(100);
        vm.ModelRows.Single(r => r.Name == "llama3.2:3b").IsSelected = true;

        vm.DownloadCommand.Execute(null);
        await WaitForCommandAsync(vm.DownloadCommand);

        _modelService.Verify(m => m.PullModelAsync(
            It.IsAny<string>(), It.IsAny<string>(), "nomic-embed-text:latest",
            It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), It.IsAny<Action<OllamaPullProgress>?>(), It.IsAny<Action?>()),
            Times.Never);
    }

    /// C2 Stage 1c. A user can reach Finalize without going through
    /// Download (e.g. a drive that already had chat models on disk
    /// before they opened PrepApp). The Finalize-time guard must
    /// detect a missing embedder and pull it before readiness checks.
    [Fact]
    public async Task FinalizeCommand_PullsEmbeddingModel_WhenMissing()
    {
        SetupDefaultMocks();
        _driveService.Setup(d => d.EnsureWritable(It.IsAny<string>(), It.IsAny<string>(), out It.Ref<string?>.IsAny)).Returns(true);
        _ollamaPackageService
            .Setup(s => s.EnsureOllamaReadyAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(@"E:\windows\tools\ollama\ollama.exe");
        _ollamaPackageService
            .Setup(s => s.StartTemporaryServerAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(new FakeOllamaServerHandle("127.0.0.1:11434"));
        _prereqService
            .Setup(s => s.StagePrerequisitesAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _artifactStagingService
            .Setup(s => s.StageRunnerAsync(It.IsAny<string>(), It.IsAny<Action<string>>()))
            .Returns(Task.CompletedTask);
        _readinessService
            .Setup(s => s.RunReadinessChecksAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReadinessItem> { ReadinessItem.Pass("Runner payload") });
        _modelService
            .Setup(m => m.PullModelAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<Action<OllamaPullProgress>?>(), It.IsAny<Action?>()))
            .ReturnsAsync(new ModelPullResult("ee11ee11", 270_000_000));

        var config = new PortableConfig
        {
            Models =
            {
                new ModelConfigEntry { Name = "llama3.2:3b", Status = ModelInstallStatus.Installed }
            }
        };
        _modelService.Setup(m => m.LoadConfigAsync(It.IsAny<string>())).ReturnsAsync(config);
        // Chat model on disk so the installedCount==0 guard passes;
        // embedder is NOT on disk, so the helper should pull it.
        _modelService.Setup(m => m.DiscoverModelsOnDisk(It.IsAny<string>())).Returns(new[] { "llama3.2:3b" });

        var vm = CreateViewModel();
        vm.Initialize();
        vm.SelectedProfile = UserProfile.GeneralAssistant;

        vm.FinalizeCommand.Execute(null);
        await WaitForCommandAsync(vm.FinalizeCommand);

        _modelService.Verify(m => m.PullModelAsync(
            It.IsAny<string>(), It.IsAny<string>(), "nomic-embed-text:latest",
            It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(),
            "127.0.0.1:11434", It.IsAny<Action<OllamaPullProgress>?>(), It.IsAny<Action?>()),
            Times.Once);
    }

    /// C2 Finalize idempotency. If the embedder is already present on
    /// disk, the Finalize-time helper must NOT spin up a temp Ollama
    /// server just to discover the no-op. Re-finalize on a fully-prepped
    /// drive should be free.
    [Fact]
    public async Task FinalizeCommand_SkipsEmbeddingPull_WhenAlreadyOnDisk()
    {
        SetupDefaultMocks();
        _driveService.Setup(d => d.EnsureWritable(It.IsAny<string>(), It.IsAny<string>(), out It.Ref<string?>.IsAny)).Returns(true);
        _ollamaPackageService
            .Setup(s => s.EnsureOllamaReadyAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(@"E:\windows\tools\ollama\ollama.exe");
        _ollamaPackageService
            .Setup(s => s.StartTemporaryServerAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(new FakeOllamaServerHandle("127.0.0.1:11434"));
        _prereqService
            .Setup(s => s.StagePrerequisitesAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _artifactStagingService
            .Setup(s => s.StageRunnerAsync(It.IsAny<string>(), It.IsAny<Action<string>>()))
            .Returns(Task.CompletedTask);
        _readinessService
            .Setup(s => s.RunReadinessChecksAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReadinessItem> { ReadinessItem.Pass("Runner payload") });

        var config = new PortableConfig
        {
            Models =
            {
                new ModelConfigEntry { Name = "llama3.2:3b", Status = ModelInstallStatus.Installed }
            }
        };
        _modelService.Setup(m => m.LoadConfigAsync(It.IsAny<string>())).ReturnsAsync(config);
        // Both chat model AND embedder already on disk.
        _modelService.Setup(m => m.DiscoverModelsOnDisk(It.IsAny<string>())).Returns(new[] { "llama3.2:3b", "nomic-embed-text:latest" });

        var vm = CreateViewModel();
        vm.Initialize();
        vm.SelectedProfile = UserProfile.GeneralAssistant;

        vm.FinalizeCommand.Execute(null);
        await WaitForCommandAsync(vm.FinalizeCommand);

        _modelService.Verify(m => m.PullModelAsync(
            It.IsAny<string>(), It.IsAny<string>(), "nomic-embed-text:latest",
            It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), It.IsAny<Action<OllamaPullProgress>?>(), It.IsAny<Action?>()),
            Times.Never);

        // Existing Windows-runner staging path calls StartTemporaryServerAsync
        // 0 times today; the C2 finalize helper must also not spin one up
        // when the embedder is already present.
        _ollamaPackageService.Verify(s => s.StartTemporaryServerAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private sealed class FakeOllamaServerHandle : IOllamaServerHandle
    {
        public string Host { get; }
        public bool Disposed { get; private set; }
        public FakeOllamaServerHandle(string host) { Host = host; }
        public void Dispose() { Disposed = true; }
    }

    // ───── C27 Stage 1: ActiveSource picker source dropdown ─────

    [Fact]
    public void ActiveSource_DefaultsToOllama()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        vm.Initialize();
        Assert.Equal(ModelSource.Ollama, vm.ActiveSource);
    }

    [Fact]
    public void ActiveSource_ChangeRaisesActiveSourceChanged()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        vm.Initialize();

        var fired = 0;
        vm.ActiveSourceChanged += (_, _) => fired++;

        vm.ActiveSource = ModelSource.HuggingFace;
        Assert.Equal(1, fired);
        Assert.Equal(ModelSource.HuggingFace, vm.ActiveSource);

        // No-op set must not fire — consistent with MostPopularLimit etc.
        vm.ActiveSource = ModelSource.HuggingFace;
        Assert.Equal(1, fired);

        vm.ActiveSource = ModelSource.Ollama;
        Assert.Equal(2, fired);
    }

    [Fact]
    public async Task ActiveSource_HuggingFaceCatalog_TagsRowsWithSourceKind()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        vm.Initialize();

        var entries = new List<StarterCatalogEntry>
        {
            new(
                Tag: "hf.co/bartowski/Qwen3-8B-GGUF",
                SizeTier: "Custom",
                BestAt: string.Empty,
                PullCount: 12345L,
                Capabilities: Array.Empty<string>(),
                ParametersBillion: 8.0,
                LastUpdated: DateTimeOffset.UtcNow,
                Source: ModelSource.HuggingFace),
        };
        await vm.SetStarterCatalogAsync(entries);

        var row = vm.ModelRows.Single(r => r.Name == "hf.co/bartowski/Qwen3-8B-GGUF");
        Assert.Equal(ModelSource.HuggingFace, row.SourceKind);
    }

    [Fact]
    public async Task DownloadCommand_HuggingFaceRowWithoutToken_BlocksAndOffersBrowser()
    {
        // 2026-05-12 regression: HF pulls fail without a Bearer token
        // in Ollama's HF_TOKEN env (even for public GGUFs — HF rate-
        // limits anon). DownloadAsync must block on missing token,
        // ask the user via Confirm() whether to open the HF token page,
        // and refuse to invoke PullModelAsync.
        SetupDefaultMocks();
        _driveService.Setup(d => d.EnsureWritable(It.IsAny<string>(), It.IsAny<string>(), out It.Ref<string?>.IsAny)).Returns(true);
        string? capturedTitle = null;
        string? capturedMessage = null;
        _dialogService
            .Setup(d => d.Confirm(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string m, string t) => { capturedMessage = m; capturedTitle = t; return false; });

        var vm = CreateViewModel();
        vm.Initialize();
        await vm.SetStarterCatalogAsync(new[]
        {
            new StarterCatalogEntry(
                Tag: "hf.co/bartowski/Qwen3-8B-GGUF",
                SizeTier: "Custom",
                BestAt: string.Empty,
                PullCount: 100L,
                Capabilities: Array.Empty<string>(),
                ParametersBillion: 8.0,
                LastUpdated: null,
                Source: ModelSource.HuggingFace),
        });
        // No token set on purpose.
        vm.ModelRows.Single(r => r.Name == "hf.co/bartowski/Qwen3-8B-GGUF").IsSelected = true;

        vm.DownloadCommand.Execute(null);
        await WaitForCommandAsync(vm.DownloadCommand);

        Assert.Equal("Hugging Face token required", capturedTitle);
        Assert.Contains("huggingface.co", capturedMessage ?? string.Empty);
        Assert.Equal("Hugging Face token required", vm.StatusText);
        _modelService.Verify(m => m.PullModelAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), It.IsAny<Action<OllamaPullProgress>?>(), It.IsAny<Action?>()),
            Times.Never);
    }

    [Fact]
    public void HuggingFaceSelectionNeedsToken_FlipsWithTokenAndSelectionState()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        vm.Initialize();
        vm.ModelRows.Add(new ModelGridRow(
            "hf.co/bartowski/Qwen3-8B-GGUF", "Not downloaded", "Recommended",
            "OK", "—", "—", "—",
            isOnDiskOnly: false, isPresentOnDrive: false,
            sourceKind: ModelSource.HuggingFace));

        Assert.False(vm.HuggingFaceSelectionNeedsToken);  // nothing selected
        vm.ModelRows[^1].IsSelected = true;
        Assert.True(vm.HuggingFaceSelectionNeedsToken);   // selected + no token
        vm.HuggingFaceTokenInput = "hf_xxx";
        Assert.False(vm.HuggingFaceSelectionNeedsToken);  // token present
        vm.HuggingFaceTokenInput = "  ";
        Assert.True(vm.HuggingFaceSelectionNeedsToken);   // whitespace-only = empty
    }

    [Fact]
    public void ModelGridRow_IsRowSelectable_FalseOnlyForHfParents()
    {
        // 2026-05-12: HF parent rows (IsExpandable=true) get the row
        // checkbox disabled in WPF + Mac via IsRowSelectable. Quant
        // children + Ollama rows stay selectable.
        var ollama = new ModelGridRow(
            "llama3:8b", "Not downloaded", "Recommended", "OK", "—", "—", "—",
            isOnDiskOnly: false, isPresentOnDrive: false);
        Assert.True(ollama.IsRowSelectable);

        var hfParent = new ModelGridRow(
            "hf.co/owner/repo", "Not downloaded", "Recommended", "OK", "—", "—", "—",
            isOnDiskOnly: false, isPresentOnDrive: false,
            sourceKind: ModelSource.HuggingFace, isExpandable: true);
        Assert.False(hfParent.IsRowSelectable);

        var hfChild = new ModelGridRow(
            "hf.co/owner/repo:Q4_K_M", "Not downloaded", "Recommended", "OK", "—", "—", "—",
            isOnDiskOnly: false, isPresentOnDrive: false,
            sourceKind: ModelSource.HuggingFace, isExpandable: false,
            parentRepoId: "owner/repo", quantLabel: "Q4_K_M");
        Assert.True(hfChild.IsRowSelectable);
    }

    [Fact]
    public void ModelGridRow_IsCapabilityFadeEligible_FalseForHuggingFaceSource()
    {
        // 2026-05-12: HF rows never expose capability tags via the HF
        // API, so the C25 pass-through fade was making every HF row
        // look "disabled" under any chip. The XAML trigger now AND's
        // with this getter to suppress the fade on HF rows.
        var ollama = new ModelGridRow(
            "llama3:8b", "Not downloaded", "Recommended", "OK", "—", "—", "—",
            isOnDiskOnly: false, isPresentOnDrive: false);
        Assert.True(ollama.IsCapabilityFadeEligible);

        var hf = new ModelGridRow(
            "hf.co/owner/repo:Q4_K_M", "Not downloaded", "Recommended", "OK", "—", "—", "—",
            isOnDiskOnly: false, isPresentOnDrive: false,
            sourceKind: ModelSource.HuggingFace);
        Assert.False(hf.IsCapabilityFadeEligible);
    }

    [Fact]
    public void StripHuggingFacePrefix_ReturnsBareRepoId_WhenPrefixPresent()
    {
        // C27 Stage 2: helper recovers the bare repoId from a
        // catalog tag for the sizing-warnings hook callers.
        Assert.Equal("Qwen/Qwen3-8B-GGUF", PrepViewModel.StripHuggingFacePrefix("hf.co/Qwen/Qwen3-8B-GGUF"));
        Assert.Equal("Qwen/Qwen3-8B-GGUF", PrepViewModel.StripHuggingFacePrefix("HF.CO/Qwen/Qwen3-8B-GGUF"));
        // Non-HF tags pass through unchanged.
        Assert.Equal("qwen3:8b", PrepViewModel.StripHuggingFacePrefix("qwen3:8b"));
        // Empty/whitespace tags pass through (caller filters those).
        Assert.Equal("", PrepViewModel.StripHuggingFacePrefix(""));
    }

    [Fact]
    public async Task DownloadCommand_HuggingFaceRow_FlowsThroughToPull()
    {
        // C27 Stage 2: HF rows are downloadable. Ollama natively pulls
        // hf.co/owner/repo, so the VM just forwards the tag to the
        // existing PullModelAsync chain — no special-case dispatch.
        SetupDefaultMocks();
        _driveService.Setup(d => d.EnsureWritable(It.IsAny<string>(), It.IsAny<string>(), out It.Ref<string?>.IsAny)).Returns(true);
        _modelService.Setup(m => m.BuildPullSelectionWarnings(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>())).Returns(new List<string>());
        _ollamaPackageService
            .Setup(o => o.EnsureOllamaReadyAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ollama.exe");
        var serverHandle = new Mock<IOllamaServerHandle>();
        serverHandle.Setup(s => s.Host).Returns("http://127.0.0.1:11434");
        _ollamaPackageService
            .Setup(o => o.StartTemporaryServerAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(serverHandle.Object);
        _modelService
            .Setup(m => m.PullModelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(),
                It.IsAny<Action<OllamaPullProgress>?>(), It.IsAny<Action?>()))
            .ReturnsAsync(new ModelPullResult("deadbeef", 4_900_000_000L));

        var vm = CreateViewModel();
        vm.Initialize();

        await vm.SetStarterCatalogAsync(new[]
        {
            new StarterCatalogEntry(
                Tag: "hf.co/bartowski/Qwen3-8B-GGUF",
                SizeTier: "Custom",
                BestAt: string.Empty,
                PullCount: 100L,
                Capabilities: Array.Empty<string>(),
                ParametersBillion: 8.0,
                LastUpdated: null,
                Source: ModelSource.HuggingFace),
        });
        vm.HuggingFaceTokenInput = "hf_test_token";
        vm.ModelRows.Single(r => r.Name == "hf.co/bartowski/Qwen3-8B-GGUF").IsSelected = true;

        vm.DownloadCommand.Execute(null);
        await WaitForCommandAsync(vm.DownloadCommand);

        // Stage 2 fires the pull — no Stage 1-style refusal in StatusText.
        _modelService.Verify(m => m.PullModelAsync(
            It.IsAny<string>(), It.IsAny<string>(), "hf.co/bartowski/Qwen3-8B-GGUF",
            It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), It.IsAny<Action<OllamaPullProgress>?>(), It.IsAny<Action?>()),
            Times.Once);
        Assert.DoesNotContain("Stage 2", vm.StatusText);
    }

    [Fact]
    public async Task DownloadCommand_HuggingFaceRow_InvokesSizingHookWithStrippedRepoId()
    {
        // C27 Stage 2: the view-host-supplied hook receives bare
        // repoIds (no hf.co/ prefix). Hook returning warnings funnels
        // them through ConfirmSizingWarnings; user declining cancels.
        SetupDefaultMocks();
        _driveService.Setup(d => d.EnsureWritable(It.IsAny<string>(), It.IsAny<string>(), out It.Ref<string?>.IsAny)).Returns(true);
        _modelService.Setup(m => m.BuildPullSelectionWarnings(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>())).Returns(new List<string>());
        _dialogService.Setup(d => d.ConfirmSizingWarnings(It.IsAny<IReadOnlyList<string>>())).Returns(false);

        IReadOnlyList<string>? hookInputs = null;
        long hookFreeBytes = -1;
        var vm = CreateViewModel();
        vm.HuggingFaceSizingWarningsHook = (repoIds, freeBytes, ct) =>
        {
            hookInputs = repoIds;
            hookFreeBytes = freeBytes;
            return Task.FromResult<IReadOnlyList<string>>(new[]
            {
                "hf.co/Qwen/Qwen3-8B-GGUF: file ≈ 4.9 GB — leaves under 2× headroom.",
            });
        };
        vm.Initialize();

        await vm.SetStarterCatalogAsync(new[]
        {
            new StarterCatalogEntry(
                Tag: "hf.co/Qwen/Qwen3-8B-GGUF",
                SizeTier: "Custom",
                BestAt: string.Empty,
                PullCount: 100L,
                Capabilities: Array.Empty<string>(),
                ParametersBillion: 8.0,
                LastUpdated: null,
                Source: ModelSource.HuggingFace),
        });
        vm.HuggingFaceTokenInput = "hf_test_token";
        vm.ModelRows.Single(r => r.Name == "hf.co/Qwen/Qwen3-8B-GGUF").IsSelected = true;

        vm.DownloadCommand.Execute(null);
        await WaitForCommandAsync(vm.DownloadCommand);

        Assert.NotNull(hookInputs);
        Assert.Equal(new[] { "Qwen/Qwen3-8B-GGUF" }, hookInputs!);
        // 100 GB free per SetupDefaultMocks → 100 * 1024^3 bytes.
        Assert.Equal(100L * 1024 * 1024 * 1024, hookFreeBytes);
        // User declined the dialog → no pull.
        _modelService.Verify(m => m.PullModelAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), It.IsAny<Action<OllamaPullProgress>?>(), It.IsAny<Action?>()),
            Times.Never);
        _dialogService.Verify(d => d.ConfirmSizingWarnings(
            It.Is<IReadOnlyList<string>>(w => w.Any(s => s.Contains("under 2× headroom")))),
            Times.Once);
    }

    [Fact]
    public async Task DownloadCommand_HuggingFaceRow_HookThrowing_StillProceedsWithPull()
    {
        // C27 Stage 2 R2 (rate-limit posture): a hook failure (HF API
        // 429/5xx, network) is non-fatal — the pull continues without
        // a disk-budget warning.
        SetupDefaultMocks();
        _driveService.Setup(d => d.EnsureWritable(It.IsAny<string>(), It.IsAny<string>(), out It.Ref<string?>.IsAny)).Returns(true);
        _modelService.Setup(m => m.BuildPullSelectionWarnings(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>())).Returns(new List<string>());
        _ollamaPackageService
            .Setup(o => o.EnsureOllamaReadyAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ollama.exe");
        var serverHandle = new Mock<IOllamaServerHandle>();
        serverHandle.Setup(s => s.Host).Returns("http://127.0.0.1:11434");
        _ollamaPackageService
            .Setup(o => o.StartTemporaryServerAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(serverHandle.Object);
        _modelService
            .Setup(m => m.PullModelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(),
                It.IsAny<Action<OllamaPullProgress>?>(), It.IsAny<Action?>()))
            .ReturnsAsync(new ModelPullResult("deadbeef", 4_900_000_000L));

        var vm = CreateViewModel();
        vm.HuggingFaceSizingWarningsHook = (_, _, _) => throw new InvalidOperationException("rate-limited");
        vm.Initialize();

        await vm.SetStarterCatalogAsync(new[]
        {
            new StarterCatalogEntry(
                Tag: "hf.co/Qwen/Qwen3-8B-GGUF",
                SizeTier: "Custom",
                BestAt: string.Empty,
                PullCount: 100L,
                Capabilities: Array.Empty<string>(),
                ParametersBillion: 8.0,
                LastUpdated: null,
                Source: ModelSource.HuggingFace),
        });
        vm.HuggingFaceTokenInput = "hf_test_token";
        vm.ModelRows.Single(r => r.Name == "hf.co/Qwen/Qwen3-8B-GGUF").IsSelected = true;

        vm.DownloadCommand.Execute(null);
        await WaitForCommandAsync(vm.DownloadCommand);

        _modelService.Verify(m => m.PullModelAsync(
            It.IsAny<string>(), It.IsAny<string>(), "hf.co/Qwen/Qwen3-8B-GGUF",
            It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), It.IsAny<Action<OllamaPullProgress>?>(), It.IsAny<Action?>()),
            Times.Once);
        Assert.Contains(vm.LogLines, l => l.Contains("Could not fetch Hugging Face"));
    }

    [Fact]
    public async Task DownloadCommand_HuggingFaceRow_NoHookRegistered_ProceedsWithoutWarning()
    {
        // C27 Stage 2: tests + Stage-3-pending scenarios that don't
        // register the hook still pull successfully. No warnings get
        // surfaced, but no refusal either.
        SetupDefaultMocks();
        _driveService.Setup(d => d.EnsureWritable(It.IsAny<string>(), It.IsAny<string>(), out It.Ref<string?>.IsAny)).Returns(true);
        _modelService.Setup(m => m.BuildPullSelectionWarnings(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>())).Returns(new List<string>());
        _ollamaPackageService
            .Setup(o => o.EnsureOllamaReadyAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ollama.exe");
        var serverHandle = new Mock<IOllamaServerHandle>();
        serverHandle.Setup(s => s.Host).Returns("http://127.0.0.1:11434");
        _ollamaPackageService
            .Setup(o => o.StartTemporaryServerAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(serverHandle.Object);
        _modelService
            .Setup(m => m.PullModelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(),
                It.IsAny<Action<OllamaPullProgress>?>(), It.IsAny<Action?>()))
            .ReturnsAsync(new ModelPullResult("deadbeef", 4_900_000_000L));

        var vm = CreateViewModel();
        // No HuggingFaceSizingWarningsHook set — default null path.
        vm.Initialize();

        await vm.SetStarterCatalogAsync(new[]
        {
            new StarterCatalogEntry(
                Tag: "hf.co/Qwen/Qwen3-8B-GGUF",
                SizeTier: "Custom",
                BestAt: string.Empty,
                PullCount: 100L,
                Capabilities: Array.Empty<string>(),
                ParametersBillion: 8.0,
                LastUpdated: null,
                Source: ModelSource.HuggingFace),
        });
        vm.HuggingFaceTokenInput = "hf_test_token";
        vm.ModelRows.Single(r => r.Name == "hf.co/Qwen/Qwen3-8B-GGUF").IsSelected = true;

        vm.DownloadCommand.Execute(null);
        await WaitForCommandAsync(vm.DownloadCommand);

        _modelService.Verify(m => m.PullModelAsync(
            It.IsAny<string>(), It.IsAny<string>(), "hf.co/Qwen/Qwen3-8B-GGUF",
            It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), It.IsAny<Action<OllamaPullProgress>?>(), It.IsAny<Action?>()),
            Times.Once);
        _dialogService.Verify(d => d.ConfirmSizingWarnings(It.IsAny<IReadOnlyList<string>>()), Times.Never);
    }

    // ── C27 Stage 3: HF token + finalize plumbing ─────────────────────

    [Fact]
    public void HuggingFaceTokenInput_RaisesChangedEvent_WithTrimmedValue()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        string? observed = "<unset>";
        vm.HuggingFaceTokenChanged += (_, t) => observed = t;

        vm.HuggingFaceTokenInput = "  hf_test_abc  ";

        Assert.Equal("hf_test_abc", observed);
    }

    [Fact]
    public void HuggingFaceTokenInput_EmptyValue_RaisesNullToken()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        // First install something so the setter sees a real change.
        vm.HuggingFaceTokenInput = "hf_initial";

        string? observed = "<unset>";
        vm.HuggingFaceTokenChanged += (_, t) => observed = t;

        vm.HuggingFaceTokenInput = "   ";

        Assert.Null(observed);
    }

    [Fact]
    public void IsHuggingFaceTokenFieldVisible_TracksActiveSource()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        Assert.False(vm.IsHuggingFaceTokenFieldVisible);

        vm.ActiveSource = ModelSource.HuggingFace;
        Assert.True(vm.IsHuggingFaceTokenFieldVisible);

        vm.ActiveSource = ModelSource.Ollama;
        Assert.False(vm.IsHuggingFaceTokenFieldVisible);
    }

    [Fact]
    public void IsHuggingFaceTokenPlaintextWarningVisible_OnlyWhenAllConditionsMet()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();

        // No source, no token, no encryption → no warning.
        Assert.False(vm.IsHuggingFaceTokenPlaintextWarningVisible);

        vm.ActiveSource = ModelSource.HuggingFace;
        Assert.False(vm.IsHuggingFaceTokenPlaintextWarningVisible); // empty token

        vm.HuggingFaceTokenInput = "hf_xyz";
        Assert.True(vm.IsHuggingFaceTokenPlaintextWarningVisible); // HF + token + no encryption

        vm.EnableEncryption = true;
        Assert.False(vm.IsHuggingFaceTokenPlaintextWarningVisible); // encryption flips it off

        vm.EnableEncryption = false;
        vm.ActiveSource = ModelSource.Ollama;
        Assert.False(vm.IsHuggingFaceTokenPlaintextWarningVisible); // wrong source
    }

    [Fact]
    public void BuildHuggingFaceEnv_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(PrepViewModel.BuildHuggingFaceEnv(null));
        Assert.Null(PrepViewModel.BuildHuggingFaceEnv(""));
        Assert.Null(PrepViewModel.BuildHuggingFaceEnv("   "));
    }

    [Fact]
    public void BuildHuggingFaceEnv_NonEmpty_SetsBothEnvVars()
    {
        var env = PrepViewModel.BuildHuggingFaceEnv("  hf_abc  ");
        Assert.NotNull(env);
        Assert.Equal("hf_abc", env!["HF_TOKEN"]);
        Assert.Equal("hf_abc", env["HUGGING_FACE_HUB_TOKEN"]);
    }

    // ── C27 Stage 4: lazy quant expansion ────────────────────────────

    private static ModelGridRow MakeHfParent(string repoTag)
        => new(
            name: repoTag,
            status: "Not downloaded",
            source: "Recommended",
            sizingWarning: "OK",
            sizeDisplay: "—",
            shaPreview: "—",
            lastVerifiedDisplay: "—",
            isOnDiskOnly: false,
            isPresentOnDrive: false,
            tier: "Custom",
            sourceKind: ModelSource.HuggingFace,
            isExpandable: true);

    [Fact]
    public async Task ToggleRepoExpansionAsync_FirstExpand_InsertsChildrenBelowParent()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        var parent = MakeHfParent("hf.co/Qwen/Qwen3-8B-GGUF");
        vm.ModelRows.Add(parent);

        vm.HuggingFaceQuantExpansionHook = (repoId, _) =>
        {
            Assert.Equal("Qwen/Qwen3-8B-GGUF", repoId);
            return Task.FromResult<IReadOnlyList<StarterCatalogEntry>>(new[]
            {
                new StarterCatalogEntry(
                    Tag: "hf.co/Qwen/Qwen3-8B-GGUF:Q4_K_M",
                    SizeTier: "Custom",
                    BestAt: "Q4_K_M",
                    PullCount: null,
                    Capabilities: Array.Empty<string>(),
                    ParametersBillion: null,
                    LastUpdated: null,
                    Source: ModelSource.HuggingFace,
                    IsExpandable: false,
                    ParentRepoId: "Qwen/Qwen3-8B-GGUF",
                    QuantLabel: "Q4_K_M",
                    QuantSizeBytes: 4_500_000_000),
            });
        };

        await vm.ToggleRepoExpansionAsync(parent);

        Assert.True(parent.IsExpanded);
        Assert.Equal(2, vm.ModelRows.Count);
        Assert.Equal("hf.co/Qwen/Qwen3-8B-GGUF:Q4_K_M", vm.ModelRows[1].Name);
        Assert.Equal("Qwen/Qwen3-8B-GGUF", vm.ModelRows[1].ParentRepoId);
        Assert.True(vm.ModelRows[1].IsQuantChild);
    }

    [Fact]
    public async Task ToggleRepoExpansionAsync_SecondToggle_CollapsesWithoutFetch()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        var parent = MakeHfParent("hf.co/owner/repo");
        vm.ModelRows.Add(parent);
        var fetchCount = 0;
        vm.HuggingFaceQuantExpansionHook = (_, _) =>
        {
            fetchCount++;
            return Task.FromResult<IReadOnlyList<StarterCatalogEntry>>(new[]
            {
                new StarterCatalogEntry("hf.co/owner/repo:Q4_K_M", "Custom", "Q4_K_M",
                    Source: ModelSource.HuggingFace, IsExpandable: false,
                    ParentRepoId: "owner/repo", QuantLabel: "Q4_K_M", QuantSizeBytes: 1_000_000_000),
            });
        };

        await vm.ToggleRepoExpansionAsync(parent); // expand
        await vm.ToggleRepoExpansionAsync(parent); // collapse

        Assert.False(parent.IsExpanded);
        Assert.Equal(1, fetchCount); // only the expand fired
        // Child row remains in ModelRows; visibility filter hides it.
        Assert.Equal(2, vm.ModelRows.Count);
        Assert.False(vm.IsModelRowVisible(vm.ModelRows[1]));
    }

    [Fact]
    public async Task ToggleRepoExpansionAsync_HookThrows_LogsAndLeavesUncollapsed()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        var parent = MakeHfParent("hf.co/owner/repo");
        vm.ModelRows.Add(parent);
        vm.HuggingFaceQuantExpansionHook = (_, _) =>
            throw new HttpRequestException("simulated 503");

        await vm.ToggleRepoExpansionAsync(parent);

        Assert.False(parent.IsExpanded);
        Assert.Single(vm.ModelRows); // no children inserted
    }

    [Fact]
    public async Task ToggleRepoExpansionAsync_NoHook_LogsAndDoesNothing()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        var parent = MakeHfParent("hf.co/owner/repo");
        vm.ModelRows.Add(parent);
        // HuggingFaceQuantExpansionHook stays null (default).

        await vm.ToggleRepoExpansionAsync(parent);

        Assert.False(parent.IsExpanded);
        Assert.Single(vm.ModelRows);
    }

    [Fact]
    public async Task ToggleRepoExpansionAsync_NonExpandableRow_NoOp()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        var ollamaRow = new ModelGridRow(
            "llama3.2:3b", "Not downloaded", "Recommended", "OK",
            "—", "—", "—", isOnDiskOnly: false, isPresentOnDrive: false,
            tier: "Small", isExpandable: false);
        vm.ModelRows.Add(ollamaRow);
        var hookCalled = false;
        vm.HuggingFaceQuantExpansionHook = (_, _) =>
        {
            hookCalled = true;
            return Task.FromResult<IReadOnlyList<StarterCatalogEntry>>(Array.Empty<StarterCatalogEntry>());
        };

        await vm.ToggleRepoExpansionAsync(ollamaRow);

        Assert.False(hookCalled);
        Assert.False(ollamaRow.IsExpanded);
    }

    [Fact]
    public async Task ToggleRepoExpansionAsync_EmptyChildren_LogsAndMarksExpanded()
    {
        SetupDefaultMocks();
        var vm = CreateViewModel();
        var parent = MakeHfParent("hf.co/owner/repo");
        vm.ModelRows.Add(parent);
        vm.HuggingFaceQuantExpansionHook = (_, _) =>
            Task.FromResult<IReadOnlyList<StarterCatalogEntry>>(Array.Empty<StarterCatalogEntry>());

        await vm.ToggleRepoExpansionAsync(parent);

        Assert.True(parent.IsExpanded); // chevron flips even with no children
        Assert.Single(vm.ModelRows);
    }

    // ─────────────────────────────────────────────────────────────
    // C6 — DriveConfiguration detection + already-configured banner
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a temp directory and optionally seeds it with our config marker
    /// and/or model manifests so the C6 detector returns the desired state.
    /// Returns the root path and a cleanup delegate the test should invoke
    /// in finally.
    /// </summary>
    private static (string root, Action cleanup) MakeTempDriveRoot(
        bool hasPlaintextConfig = false,
        bool hasEncryptedConfig = false,
        int manifestCount = 0)
    {
        var root = Path.Combine(Path.GetTempPath(), "free-ai-ssd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        if (hasPlaintextConfig || hasEncryptedConfig)
        {
            var configDir = Path.Combine(root, SsdLayout.Config);
            Directory.CreateDirectory(configDir);
            if (hasPlaintextConfig)
            {
                File.WriteAllText(
                    Path.Combine(configDir, DriveConfigurationDetector.PlaintextConfigFileName),
                    "{}");
            }
            if (hasEncryptedConfig)
            {
                File.WriteAllText(
                    Path.Combine(configDir, SsdEncryption.EncryptedConfigFileName),
                    "{}");
            }
        }
        if (manifestCount > 0)
        {
            var manifestDir = Path.Combine(
                root, SsdLayout.Models, "manifests", "registry.ollama.ai", "library", "llama3");
            Directory.CreateDirectory(manifestDir);
            for (var i = 0; i < manifestCount; i++)
            {
                File.WriteAllText(Path.Combine(manifestDir, $"m{i}"), "{}");
            }
        }
        return (root, () =>
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        });
    }

    [Fact]
    public void SelectingUnconfiguredDrive_DriveConfigurationIsUnconfigured_NoBanner()
    {
        var (root, cleanup) = MakeTempDriveRoot();
        try
        {
            SetupDefaultMocks(new List<DriveTarget> { MakeDrive(root) });

            var vm = CreateViewModel();
            vm.Initialize();

            Assert.Equal(DriveConfigurationState.Unconfigured, vm.DriveConfiguration.State);
            Assert.False(vm.ShowAlreadyConfiguredBanner);
            Assert.False(vm.ShowManageModelsButton);
            Assert.False(vm.ShowStartOverButton);
            Assert.True(vm.CanInitiateFreshFormat); // format controls below stay enabled
            Assert.Equal(string.Empty, vm.AlreadyConfiguredBannerText);
        }
        finally { cleanup(); }
    }

    [Fact]
    public void SelectingConfiguredEmptyDrive_ShowsBanner_BothButtonsVisible_FreshFormatDisabled()
    {
        var (root, cleanup) = MakeTempDriveRoot(hasPlaintextConfig: true);
        try
        {
            SetupDefaultMocks(new List<DriveTarget> { MakeDrive(root) });

            var vm = CreateViewModel();
            vm.Initialize();

            Assert.Equal(DriveConfigurationState.ConfiguredEmpty, vm.DriveConfiguration.State);
            Assert.True(vm.IsSelectedDriveConfiguredEmpty);
            Assert.True(vm.ShowAlreadyConfiguredBanner);
            Assert.True(vm.ShowManageModelsButton);
            Assert.True(vm.ShowStartOverButton);
            Assert.False(vm.CanInitiateFreshFormat);
            Assert.Equal("This SSD is prepared but has no models yet.", vm.AlreadyConfiguredBannerText);
        }
        finally { cleanup(); }
    }

    [Fact]
    public void SelectingFullyConfiguredDrive_ShowsBanner_WithModelCount()
    {
        var (root, cleanup) = MakeTempDriveRoot(hasPlaintextConfig: true, manifestCount: 3);
        try
        {
            SetupDefaultMocks(new List<DriveTarget> { MakeDrive(root) });

            var vm = CreateViewModel();
            vm.Initialize();

            Assert.Equal(DriveConfigurationState.FullyConfigured, vm.DriveConfiguration.State);
            Assert.True(vm.IsSelectedDriveFullyConfigured);
            Assert.True(vm.ShowAlreadyConfiguredBanner);
            Assert.True(vm.ShowManageModelsButton);
            Assert.True(vm.ShowStartOverButton);
            Assert.False(vm.CanInitiateFreshFormat);
            Assert.Contains("already prepared", vm.AlreadyConfiguredBannerText);
            Assert.Contains("3 models", vm.AlreadyConfiguredBannerText);
        }
        finally { cleanup(); }
    }

    [Fact]
    public void SelectingFullyConfiguredDrive_SingleModel_BannerSaysOneModel()
    {
        var (root, cleanup) = MakeTempDriveRoot(hasPlaintextConfig: true, manifestCount: 1);
        try
        {
            SetupDefaultMocks(new List<DriveTarget> { MakeDrive(root) });

            var vm = CreateViewModel();
            vm.Initialize();

            Assert.Contains("1 model", vm.AlreadyConfiguredBannerText);
            Assert.DoesNotContain("1 models", vm.AlreadyConfiguredBannerText);
        }
        finally { cleanup(); }
    }

    [Fact]
    public void SelectingEncryptedFullyConfiguredDrive_DetectorReportsEncrypted_StartOverDisabled()
    {
        // C6: detector signals IsConfigEncrypted=true from filename presence
        // alone (no decrypt). StartOverCommand respects CanMutateDrive (false
        // on encrypted drives) so the destructive path is gated behind C7's
        // future unlock flow.
        var (root, cleanup) = MakeTempDriveRoot(hasEncryptedConfig: true, manifestCount: 2);
        try
        {
            SetupDefaultMocks(new List<DriveTarget> { MakeDrive(root) }, encrypted: true);

            var vm = CreateViewModel();
            vm.Initialize();

            Assert.True(vm.DriveConfiguration.IsConfigEncrypted);
            Assert.True(vm.ShowAlreadyConfiguredBanner);
            Assert.False(vm.CanMutateDrive);
            Assert.False(vm.StartOverCommand.CanExecute(null));
            // Manage-models stays enabled — it's navigation, not mutation.
            Assert.True(vm.ManageModelsCommand.CanExecute(null));
        }
        finally { cleanup(); }
    }

    [Fact]
    public void SelectingPreconfiguredDrive_RaisesCanExecuteChanged_OnBannerCommands()
    {
        // Regression: 1.3.31 bug #51 — switching the SelectedDrive to a
        // preconfigured SSD made the banner appear (visibility binds to
        // ShowAlreadyConfiguredBanner) but Manage / Start over / Unlock
        // stayed disabled because RaiseAllCommandsCanExecuteChanged
        // omitted the three banner commands. Our RelayCommand does not
        // hook CommandManager.RequerySuggested, so a stale CanExecute=false
        // snapshot persisted until the next focus change. Verify each
        // banner command fires CanExecuteChanged when SelectedDrive
        // changes to a preconfigured drive.
        var (unconfRoot, unconfCleanup) = MakeTempDriveRoot();
        var (confRoot, confCleanup) = MakeTempDriveRoot(hasPlaintextConfig: true, manifestCount: 1);
        try
        {
            var unconf = MakeDrive(unconfRoot, "Blank");
            var conf = MakeDrive(confRoot, "Configured");
            SetupDefaultMocks(new List<DriveTarget> { unconf, conf });

            var vm = CreateViewModel();
            vm.Initialize();
            Assert.False(vm.ShowAlreadyConfiguredBanner);

            var manageFired = 0;
            var startOverFired = 0;
            var unlockFired = 0;
            vm.ManageModelsCommand.CanExecuteChanged += (_, _) => manageFired++;
            vm.StartOverCommand.CanExecuteChanged += (_, _) => startOverFired++;
            vm.UnlockCommand.CanExecuteChanged += (_, _) => unlockFired++;

            vm.SelectedDrive = conf;

            Assert.True(vm.ShowAlreadyConfiguredBanner);
            Assert.True(vm.ManageModelsCommand.CanExecute(null));
            Assert.True(vm.StartOverCommand.CanExecute(null));
            Assert.True(manageFired > 0);
            Assert.True(startOverFired > 0);
            Assert.True(unlockFired > 0);
        }
        finally
        {
            confCleanup();
            unconfCleanup();
        }
    }

    [Fact]
    public void ManageModelsCommand_RaisesModelsTabRequestedEvent()
    {
        var (root, cleanup) = MakeTempDriveRoot(hasPlaintextConfig: true, manifestCount: 1);
        try
        {
            SetupDefaultMocks(new List<DriveTarget> { MakeDrive(root) });

            var vm = CreateViewModel();
            vm.Initialize();
            var raised = 0;
            vm.ModelsTabRequested += (_, _) => raised++;

            vm.ManageModelsCommand.Execute(null);

            Assert.Equal(1, raised);
        }
        finally { cleanup(); }
    }

    [Fact]
    public async Task StartOverCommand_UserDeclinesConfirm_DoesNotInvokeFormat()
    {
        var (root, cleanup) = MakeTempDriveRoot(hasPlaintextConfig: true, manifestCount: 2);
        try
        {
            SetupDefaultMocks(new List<DriveTarget> { MakeDrive(root) });
            _dialogService
                .Setup(d => d.Confirm(It.IsAny<string>(), "Erase already-prepared SSD?"))
                .Returns(false);

            var vm = CreateViewModel();
            vm.Initialize();
            vm.StartOverCommand.Execute(null);
            await WaitForCommandAsync(vm.StartOverCommand);

            // ConfirmErase belongs to the existing FormatPrepareAsync flow —
            // if we never got there, ConfirmErase is never called.
            _dialogService.Verify(
                d => d.ConfirmErase(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
                Times.Never);
            _driveService.Verify(
                d => d.FormatAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
                Times.Never);
        }
        finally { cleanup(); }
    }

    [Fact]
    public async Task StartOverCommand_UserApprovesConfirm_DelegatesToFormatPrepareAsync()
    {
        // Approve C6 pre-confirm → FormatPrepareAsync runs. The first thing
        // it does is call _driveService.EnsureWritable("Format & Prepare Drive")
        // — that invocation is the platform-agnostic proof of delegation.
        // EnsureWritable returns false in this test, so FormatPrepareAsync
        // bails before any destructive work; FormatAsync verifying never-
        // called is the belt-and-braces safety pin.
        var (root, cleanup) = MakeTempDriveRoot(hasPlaintextConfig: true, manifestCount: 2);
        try
        {
            SetupDefaultMocks(new List<DriveTarget> { MakeDrive(root) });
            _dialogService
                .Setup(d => d.Confirm(It.IsAny<string>(), "Erase already-prepared SSD?"))
                .Returns(true);
            string? blockedMessage = null;
            _driveService
                .Setup(d => d.EnsureWritable(It.IsAny<string>(), "Format & Prepare Drive", out blockedMessage))
                .Returns(false);

            var vm = CreateViewModel();
            vm.Initialize();
            vm.StartOverCommand.Execute(null);
            await WaitForCommandAsync(vm.StartOverCommand);

            _dialogService.Verify(
                d => d.Confirm(It.IsAny<string>(), "Erase already-prepared SSD?"),
                Times.Once);
            _driveService.Verify(
                d => d.EnsureWritable(It.IsAny<string>(), "Format & Prepare Drive", out blockedMessage),
                Times.Once);
            _driveService.Verify(
                d => d.FormatAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
                Times.Never);
        }
        finally { cleanup(); }
    }

    [Fact]
    public void ChangingDriveSelection_FromConfiguredToFreshTempDir_BannerHides()
    {
        var (configured, cleanupConfigured) = MakeTempDriveRoot(hasPlaintextConfig: true, manifestCount: 1);
        var (fresh, cleanupFresh) = MakeTempDriveRoot();
        try
        {
            var drives = new List<DriveTarget>
            {
                MakeDrive(configured, "Prepped"),
                MakeDrive(fresh, "Fresh")
            };
            SetupDefaultMocks(drives);

            var vm = CreateViewModel();
            vm.Initialize();
            // Initialize selects Drives[0] = configured.
            Assert.True(vm.ShowAlreadyConfiguredBanner);

            vm.SelectedDrive = drives[1];

            Assert.False(vm.ShowAlreadyConfiguredBanner);
            Assert.True(vm.CanInitiateFreshFormat);
        }
        finally
        {
            cleanupConfigured();
            cleanupFresh();
        }
    }

    [Fact]
    public void DeselectingDrive_DriveConfigurationResetsToEmpty()
    {
        var (root, cleanup) = MakeTempDriveRoot(hasPlaintextConfig: true, manifestCount: 1);
        try
        {
            SetupDefaultMocks(new List<DriveTarget> { MakeDrive(root) });

            var vm = CreateViewModel();
            vm.Initialize();
            Assert.True(vm.ShowAlreadyConfiguredBanner);

            vm.SelectedDrive = null;

            Assert.Same(DriveConfigurationDetector.Empty, vm.DriveConfiguration);
            Assert.False(vm.ShowAlreadyConfiguredBanner);
        }
        finally { cleanup(); }
    }

    // C7 — encrypted-drive Manage Models unlock pins.

    [Fact]
    public void EncryptedDriveAtInit_IsManageSessionUnlockedIsFalse_ShowUnlockButtonIsTrue()
    {
        // Defense-in-depth: a fresh selection of an encrypted drive must
        // start in the locked state. Pairs with the existing C6 pin at
        // SelectingEncryptedFullyConfiguredDrive_DetectorReportsEncrypted_StartOverDisabled.
        var (root, cleanup) = MakeTempDriveRoot(hasEncryptedConfig: true, manifestCount: 2);
        try
        {
            SetupDefaultMocks(new List<DriveTarget> { MakeDrive(root) }, encrypted: true);

            var vm = CreateViewModel();
            vm.Initialize();

            Assert.True(vm.IsSelectedDriveEncrypted);
            Assert.False(vm.IsManageSessionUnlocked);
            Assert.False(vm.CanMutateDrive);
            Assert.True(vm.ShowUnlockButton);
            Assert.False(vm.ShowSessionUnlockedBanner);
            Assert.True(vm.UnlockCommand.CanExecute(null));
        }
        finally { cleanup(); }
    }

    [Fact]
    public void ApplyUnlockResult_FlipsIsManageSessionUnlocked_AndCanMutateDriveBecomesTrue()
    {
        var (root, cleanup) = MakeTempDriveRoot(hasEncryptedConfig: true, manifestCount: 1);
        try
        {
            SetupDefaultMocks(new List<DriveTarget> { MakeDrive(root) }, encrypted: true);

            var vm = CreateViewModel();
            vm.Initialize();
            Assert.False(vm.CanMutateDrive);

            // Synthesize an unlock result without actually invoking PBKDF2.
            // The VM doesn't care how the material was derived; it just
            // takes ownership and flips the gate. The real path (via
            // SsdEncryption.TryUnlockPortableConfigWithMaterial) is
            // covered end-to-end by SsdEncryption's own tests.
            var config = new PortableConfig { HuggingFaceToken = "hf_lifted_token" };
            var material = new UnlockMaterial(
                new byte[32], new byte[16], 210_000, "aes-256-gcm+pbkdf2-sha256-v1");

            vm.ApplyUnlockResult(config, material);

            Assert.True(vm.IsManageSessionUnlocked);
            Assert.True(vm.CanMutateDrive);
            Assert.False(vm.ShowUnlockButton);
            Assert.True(vm.ShowSessionUnlockedBanner);
            // HF token lifted into the picker's inline field — closes the
            // post-finalize re-entry gap.
            Assert.Equal("hf_lifted_token", vm.HuggingFaceTokenInput);
        }
        finally { cleanup(); }
    }

    [Fact]
    public void ApplyUnlockResult_WithEmptyHuggingFaceToken_LeavesTokenInputUntouched()
    {
        var (root, cleanup) = MakeTempDriveRoot(hasEncryptedConfig: true, manifestCount: 1);
        try
        {
            SetupDefaultMocks(new List<DriveTarget> { MakeDrive(root) }, encrypted: true);

            var vm = CreateViewModel();
            vm.Initialize();
            vm.HuggingFaceTokenInput = "hf_prior_user_input"; // user typed before unlocking

            var config = new PortableConfig { HuggingFaceToken = null };
            var material = new UnlockMaterial(
                new byte[32], new byte[16], 210_000, "aes-256-gcm+pbkdf2-sha256-v1");
            vm.ApplyUnlockResult(config, material);

            // Lift path is gated on non-empty config token; pre-existing
            // user input survives.
            Assert.Equal("hf_prior_user_input", vm.HuggingFaceTokenInput);
        }
        finally { cleanup(); }
    }

    [Fact]
    public void LockManageSession_ZeroizesDerivedKeyBuffer_AndFlipsGateClosed()
    {
        var (root, cleanup) = MakeTempDriveRoot(hasEncryptedConfig: true, manifestCount: 1);
        try
        {
            SetupDefaultMocks(new List<DriveTarget> { MakeDrive(root) }, encrypted: true);

            var vm = CreateViewModel();
            vm.Initialize();

            var key = new byte[32];
            new Random(42).NextBytes(key); // non-zero so zeroize is observable
            var keyRef = key; // separate ref so the test can inspect after VM releases
            var config = new PortableConfig();
            var material = new UnlockMaterial(
                key, new byte[16], 210_000, "aes-256-gcm+pbkdf2-sha256-v1");
            vm.ApplyUnlockResult(config, material);
            Assert.True(vm.IsManageSessionUnlocked);

            vm.LockManageSession();

            Assert.False(vm.IsManageSessionUnlocked);
            Assert.False(vm.CanMutateDrive);
            // CryptographicOperations.ZeroMemory wipes in-place; the
            // record holds a reference to the same buffer.
            Assert.All(keyRef, b => Assert.Equal((byte)0, b));
        }
        finally { cleanup(); }
    }

    [Fact]
    public void SelectingDifferentDrive_LocksManageSession()
    {
        // C7 lifecycle: switching drives mid-unlocked-session zeroizes the
        // cached material and resets the banner so the green "Unlocked"
        // state can never render against a different drive's identity.
        var (rootA, cleanupA) = MakeTempDriveRoot(hasEncryptedConfig: true, manifestCount: 1);
        var (rootB, cleanupB) = MakeTempDriveRoot(hasEncryptedConfig: true, manifestCount: 1);
        try
        {
            var driveA = MakeDrive(rootA);
            var driveB = MakeDrive(rootB);
            SetupDefaultMocks(new List<DriveTarget> { driveA, driveB }, encrypted: true);

            var vm = CreateViewModel();
            vm.Initialize();
            vm.SelectedDrive = driveA;
            vm.ApplyUnlockResult(
                new PortableConfig(),
                new UnlockMaterial(new byte[32], new byte[16], 210_000, "aes-256-gcm+pbkdf2-sha256-v1"));
            Assert.True(vm.IsManageSessionUnlocked);

            vm.SelectedDrive = driveB;

            Assert.False(vm.IsManageSessionUnlocked);
            Assert.True(vm.ShowUnlockButton);
            Assert.False(vm.ShowSessionUnlockedBanner);
        }
        finally { cleanupA(); cleanupB(); }
    }

    [Fact]
    public void UnlockCommand_OnUnencryptedDrive_IsNotExecutable()
    {
        // Sanity: the UnlockCommand should never light up on an
        // unencrypted drive. The banner XAML hides the button too, but
        // the CanExecute guard is the second line of defense.
        var (root, cleanup) = MakeTempDriveRoot(hasPlaintextConfig: true, manifestCount: 1);
        try
        {
            SetupDefaultMocks(new List<DriveTarget> { MakeDrive(root) });

            var vm = CreateViewModel();
            vm.Initialize();

            Assert.False(vm.IsSelectedDriveEncrypted);
            Assert.False(vm.UnlockCommand.CanExecute(null));
            Assert.False(vm.ShowUnlockButton);
        }
        finally { cleanup(); }
    }

    [Fact]
    public void UnlockCommand_RaisesUnlockRequestedEvent()
    {
        var (root, cleanup) = MakeTempDriveRoot(hasEncryptedConfig: true, manifestCount: 1);
        try
        {
            SetupDefaultMocks(new List<DriveTarget> { MakeDrive(root) }, encrypted: true);

            var vm = CreateViewModel();
            vm.Initialize();
            var raised = 0;
            vm.UnlockRequested += (_, _) => raised++;

            Assert.True(vm.UnlockCommand.CanExecute(null));
            vm.UnlockCommand.Execute(null);

            Assert.Equal(1, raised);
        }
        finally { cleanup(); }
    }

    // ───── #48 / #49: model-download UX (finalize state + friendly labels) ─────

    /// #49 pin: when an Ollama pull frame arrives with the user-opaque
    /// "pulling &lt;hex&gt;" status, PullProgressLine must render it
    /// with the parent model name + a layer counter — the raw hash is
    /// what surfaced as a 6.9 GB "undisclosed" download to the user.
    [Fact]
    public async Task DownloadCommand_RewritesBlobHashProgress_WithModelNameAndLayerCounter()
    {
        SetupDefaultMocks();
        _driveService.Setup(d => d.EnsureWritable(It.IsAny<string>(), It.IsAny<string>(), out It.Ref<string?>.IsAny)).Returns(true);
        _modelService.Setup(m => m.BuildPullSelectionWarnings(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>())).Returns(new List<string>());

        var config = new PortableConfig();
        config.Models.Add(new ModelConfigEntry { Name = "llama3.2:3b", Status = ModelInstallStatus.NotInstalled });
        _modelService.Setup(m => m.LoadConfigAsync(It.IsAny<string>())).ReturnsAsync(config);
        _ollamaPackageService
            .Setup(s => s.EnsureOllamaReadyAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(@"E:\windows\tools\ollama\ollama.exe");
        _ollamaPackageService
            .Setup(s => s.StartTemporaryServerAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(new FakeOllamaServerHandle("127.0.0.1:11434"));

        // Capture the in-flight progress + finalize callbacks so the
        // test can drive synthetic NDJSON frames through them and watch
        // PullProgressLine end up with a friendly label.
        var renderedLines = new List<string>();
        _modelService
            .Setup(m => m.PullModelAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<Action<OllamaPullProgress>?>(), It.IsAny<Action?>()))
            .Callback<string, string, string, Action<string>, CancellationToken, string?, Action<OllamaPullProgress>?, Action?>(
                (_, _, modelTag, _, _, _, onProgress, onFinalize) =>
                {
                    // Two distinct layer digests + one stage frame — the
                    // shape Ollama emits for a multi-blob model. Repeat
                    // the second digest to confirm the layer counter
                    // doesn't double-count repeat frames.
                    onProgress?.Invoke(new OllamaPullProgress(
                        "pulling 96c415656d37", "sha256:96c415656d37", 4_700_000_000L, 1_000_000_000L));
                    onProgress?.Invoke(new OllamaPullProgress(
                        "pulling e73cc17c7181", "sha256:e73cc17c7181", 6_900_000_000L, 3_450_000_000L));
                    onProgress?.Invoke(new OllamaPullProgress(
                        "pulling e73cc17c7181", "sha256:e73cc17c7181", 6_900_000_000L, 6_900_000_000L));
                    onProgress?.Invoke(new OllamaPullProgress(
                        "verifying sha256 digest", null, null, null));
                    onFinalize?.Invoke();
                })
            .ReturnsAsync(new ModelPullResult("0123456789abcdef", 270_000_000));

        var vm = CreateViewModel();
        vm.Initialize();
        Thread.Sleep(100);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PrepViewModel.PullProgressLine))
                renderedLines.Add(vm.PullProgressLine);
        };
        vm.ModelRows.Single(r => r.Name == "llama3.2:3b").IsSelected = true;

        vm.DownloadCommand.Execute(null);
        await WaitForCommandAsync(vm.DownloadCommand);

        // First layer frame names the model. We deliberately suppress
        // "layer 1 of 1" — it's noise until a second digest appears.
        Assert.Contains(renderedLines, l =>
            l.Contains("pulling llama3.2:3b")
            && !l.Contains("layer")
            && l.Contains("21%"));
        // Layer 2 frame bumps the counter — both digests seen now.
        Assert.Contains(renderedLines, l => l.Contains("llama3.2:3b") && l.Contains("layer 2 of 2"));
        // Repeat of layer-2 digest stays at "layer 2 of 2" (no double-count).
        Assert.DoesNotContain(renderedLines, l => l.Contains("layer 2 of 3"));
        // Stage frame is prefixed with the parent model.
        Assert.Contains(renderedLines, l => l.Contains("llama3.2:3b") && l.Contains("verifying sha256 digest"));
        // #48: finalize hook flipped the line to the "Finalizing…" state.
        Assert.Contains(renderedLines, l => l.Contains("Finalizing llama3.2:3b") && l.Contains("verifying integrity"));
        // None of the rendered lines leak the raw blob hash.
        Assert.DoesNotContain(renderedLines, l => l.Contains("96c415656d37"));
        Assert.DoesNotContain(renderedLines, l => l.Contains("e73cc17c7181"));
    }

    /// #49 pin: the embedding-model pull (the "e73cc17c7181"-style
    /// surprise companion install) must surface an explicit log line
    /// naming the model and explaining why it's being installed, BEFORE
    /// the pull starts.
    [Fact]
    public async Task DownloadCommand_LogsCompanionEmbedderInstallExplicitly_BeforePull()
    {
        SetupDefaultMocks();
        _driveService.Setup(d => d.EnsureWritable(It.IsAny<string>(), It.IsAny<string>(), out It.Ref<string?>.IsAny)).Returns(true);
        _modelService.Setup(m => m.BuildPullSelectionWarnings(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>())).Returns(new List<string>());

        var config = new PortableConfig();
        config.Models.Add(new ModelConfigEntry { Name = "llama3.2:3b", Status = ModelInstallStatus.NotInstalled });
        _modelService.Setup(m => m.LoadConfigAsync(It.IsAny<string>())).ReturnsAsync(config);
        _modelService.Setup(m => m.DiscoverModelsOnDisk(It.IsAny<string>())).Returns(Array.Empty<string>());
        _ollamaPackageService
            .Setup(s => s.EnsureOllamaReadyAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(@"E:\windows\tools\ollama\ollama.exe");
        _ollamaPackageService
            .Setup(s => s.StartTemporaryServerAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(new FakeOllamaServerHandle("127.0.0.1:11434"));
        _modelService
            .Setup(m => m.PullModelAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Action<string>>(), It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<Action<OllamaPullProgress>?>(), It.IsAny<Action?>()))
            .ReturnsAsync(new ModelPullResult("0123456789abcdef", 270_000_000));

        var vm = CreateViewModel();
        vm.Initialize();
        Thread.Sleep(100);
        vm.ModelRows.Single(r => r.Name == "llama3.2:3b").IsSelected = true;

        vm.DownloadCommand.Execute(null);
        await WaitForCommandAsync(vm.DownloadCommand);

        // The pre-pull "Also installing" line is what the user-reported
        // bug specifically asked for — it names the model AND tells the
        // user *why* it's being installed (RAG).
        Assert.Contains(vm.LogLines, l =>
            l.Contains("Also installing")
            && l.Contains("nomic-embed-text")
            && l.Contains("RAG"));
    }
}
