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
            It.IsAny<Action<string>?>()), Times.Never);
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
}
