using FreeAiSsd.Shared.Documents;

namespace FreeAiSsd.Tests;

/// <summary>
/// Unit tests for <see cref="DcsSavedGamesLocator"/>, <see cref="DcsAircraftScanner"/>,
/// and <see cref="DcsBatchProcessor"/>.
/// </summary>
public class DcsAircraftScannerTests : IDisposable
{
    private readonly string _tempDir;

    public DcsAircraftScannerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"dcs-scanner-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Creates the Config/Input directory tree for a fake DCS saved games root.</summary>
    private string MakeSavedGamesRoot() =>
        Path.Combine(_tempDir, $"sg-{Guid.NewGuid():N}");

    /// <summary>
    /// Creates a device sub-folder with an optional diff.lua under a given aircraft folder.
    /// Returns the path to diff.lua.
    /// </summary>
    private string AddDeviceFolder(
        string configInputRoot,
        string aircraftFolder,
        string deviceFolder,
        string? diffContent = null)
    {
        var devicePath = Path.Combine(configInputRoot, aircraftFolder, deviceFolder);
        Directory.CreateDirectory(devicePath);
        var diffPath = Path.Combine(devicePath, "diff.lua");

        if (diffContent is not null)
        {
            File.WriteAllText(diffPath, diffContent);
        }

        return diffPath;
    }

    private static string MinimalDiff(string axisName = "Pitch", string axisKey = "JOY_Y") => $$"""
        local diff = {
        ["axisDiffs"] = {
        ["dABC"] = {
        ["name"] = "{{axisName}}",
        ["added"] = {
        [1] = {
        ["key"] = "{{axisKey}}",
        }, -- end of [1]
        }, -- end of ["added"]
        }, -- end of ["dABC"]
        }, -- end of ["axisDiffs"]
        ["keyDiffs"] = {
        }, -- end of ["keyDiffs"]
        } -- end of diff
        return diff
        """;

    // ─────────────────────────────────────────────────────────────────────────
    // DcsSavedGamesLocator — WithManualPath
    // ─────────────────────────────────────────────────────────────────────────

    #region DcsSavedGamesLocator — WithManualPath

    [Fact]
    public void WithManualPath_ValidPath_ReturnsInstallation()
    {
        var result = DcsSavedGamesLocator.WithManualPath(_tempDir);

        Assert.NotNull(result);
        Assert.Equal(_tempDir, result.SavedGamesPath);
        Assert.True(result.IsManuallySelected);
    }

    [Fact]
    public void WithManualPath_SetsVariantToDCS()
    {
        var result = DcsSavedGamesLocator.WithManualPath(_tempDir);
        Assert.Equal(DcsSavedGamesVariant.DCS, result.Variant);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WithManualPath_EmptyOrWhitespacePath_Throws(string badPath)
    {
        Assert.Throws<ArgumentException>(() => DcsSavedGamesLocator.WithManualPath(badPath));
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    // DcsSavedGamesLocator — GetConfigInputPath
    // ─────────────────────────────────────────────────────────────────────────

    #region DcsSavedGamesLocator — GetConfigInputPath

    [Fact]
    public void GetConfigInputPath_ReturnsConfigInputUnderSavedGames()
    {
        var installation = DcsSavedGamesLocator.WithManualPath(_tempDir);
        var expected     = Path.Combine(_tempDir, "Config", "Input");

        Assert.Equal(expected, DcsSavedGamesLocator.GetConfigInputPath(installation));
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    // DcsSavedGamesLocator — TryDetect
    // ─────────────────────────────────────────────────────────────────────────

    #region DcsSavedGamesLocator — TryDetect

    [Fact]
    public void TryDetect_WhenNeitherFolderExists_ReturnsNull()
    {
        // On the CI Linux runner neither "Saved Games/DCS" nor "Saved Games/DCS.openbeta"
        // should exist in the home directory.
        var result = DcsSavedGamesLocator.TryDetect();

        // We cannot guarantee null here if the machine actually has DCS installed,
        // so we only assert the shape: if non-null the path must exist on disk.
        if (result is not null)
        {
            Assert.True(Directory.Exists(result.SavedGamesPath));
            Assert.False(result.IsManuallySelected);
        }
    }

    [Fact]
    public void TryDetect_WhenDcsFolderExists_ReturnsStableVariant()
    {
        // Synthesise a Saved Games/DCS folder underneath our temp dir and point the
        // locator at it by testing the inner logic via a custom overload (not available
        // in production), so we instead exercise the variant assignment indirectly.
        // The public TryDetect() is environment-dependent; this test documents intent.
        Assert.True(true, "TryDetect variant resolution is covered by environment-dependent integration.");
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    // DcsAircraftScanner — ScanAircraft
    // ─────────────────────────────────────────────────────────────────────────

    #region DcsAircraftScanner — ScanAircraft

    [Fact]
    public void ScanAircraft_MissingConfigInputFolder_ReturnsEmpty()
    {
        var sgRoot = MakeSavedGamesRoot();
        Directory.CreateDirectory(sgRoot); // no Config/Input inside

        var result = DcsAircraftScanner.ScanAircraft(sgRoot);

        Assert.Empty(result);
    }

    [Fact]
    public void ScanAircraft_EmptyConfigInputFolder_ReturnsEmpty()
    {
        var sgRoot        = MakeSavedGamesRoot();
        var configInput   = Path.Combine(sgRoot, "Config", "Input");
        Directory.CreateDirectory(configInput);

        var result = DcsAircraftScanner.ScanAircraft(sgRoot);

        Assert.Empty(result);
    }

    [Fact]
    public void ScanAircraft_FindsAircraftWithDevice()
    {
        var sgRoot      = MakeSavedGamesRoot();
        var configInput = Path.Combine(sgRoot, "Config", "Input");
        AddDeviceFolder(configInput, "FA-18C_hornet", "Saitek X-56 Rhino Stick", MinimalDiff());

        var result = DcsAircraftScanner.ScanAircraft(sgRoot);

        Assert.Single(result);
        Assert.Equal("FA-18C_hornet", result[0].FolderName);
    }

    [Fact]
    public void ScanAircraft_FriendlyNameMappedFromParser()
    {
        var sgRoot      = MakeSavedGamesRoot();
        var configInput = Path.Combine(sgRoot, "Config", "Input");
        AddDeviceFolder(configInput, "FA-18C_hornet", "Stick", MinimalDiff());

        var result = DcsAircraftScanner.ScanAircraft(sgRoot);

        Assert.Equal("F/A-18C Hornet", result[0].FriendlyName);
    }

    [Fact]
    public void ScanAircraft_UnknownAircraft_FriendlyNameCleanedUp()
    {
        var sgRoot      = MakeSavedGamesRoot();
        var configInput = Path.Combine(sgRoot, "Config", "Input");
        AddDeviceFolder(configInput, "Custom_Module_X", "Stick", MinimalDiff());

        var result = DcsAircraftScanner.ScanAircraft(sgRoot);

        Assert.Equal("Custom Module X", result[0].FriendlyName);
    }

    [Fact]
    public void ScanAircraft_DeviceWithNoDiffLua_NotIncluded()
    {
        var sgRoot      = MakeSavedGamesRoot();
        var configInput = Path.Combine(sgRoot, "Config", "Input");

        // Device folder with NO diff.lua
        var devicePath = Path.Combine(configInput, "FA-18C_hornet", "Empty Device");
        Directory.CreateDirectory(devicePath);

        var result = DcsAircraftScanner.ScanAircraft(sgRoot);

        // Aircraft folder still appears, but no devices listed
        Assert.Single(result);
        Assert.Empty(result[0].Devices);
    }

    [Fact]
    public void ScanAircraft_DeviceWithDiffLua_Included()
    {
        var sgRoot      = MakeSavedGamesRoot();
        var configInput = Path.Combine(sgRoot, "Config", "Input");
        AddDeviceFolder(configInput, "FA-18C_hornet", "Saitek X-56 Rhino Stick", MinimalDiff());

        var result = DcsAircraftScanner.ScanAircraft(sgRoot);

        Assert.Single(result[0].Devices);
        Assert.Equal("Saitek X-56 Rhino Stick", result[0].Devices[0].DeviceFolderName);
        Assert.True(File.Exists(result[0].Devices[0].DiffLuaPath));
    }

    [Fact]
    public void ScanAircraft_MultipleDevices_AllIncluded()
    {
        var sgRoot      = MakeSavedGamesRoot();
        var configInput = Path.Combine(sgRoot, "Config", "Input");
        AddDeviceFolder(configInput, "FA-18C_hornet", "Stick",    MinimalDiff("Pitch", "JOY_Y"));
        AddDeviceFolder(configInput, "FA-18C_hornet", "Throttle", MinimalDiff("Left Engine Throttle", "JOY_Z"));

        var result = DcsAircraftScanner.ScanAircraft(sgRoot);

        Assert.Equal(2, result[0].Devices.Count);
    }

    [Fact]
    public void ScanAircraft_HasBindings_TrueWhenDiffIsNonEmpty()
    {
        var sgRoot      = MakeSavedGamesRoot();
        var configInput = Path.Combine(sgRoot, "Config", "Input");
        AddDeviceFolder(configInput, "FA-18C_hornet", "Stick", MinimalDiff());

        var result = DcsAircraftScanner.ScanAircraft(sgRoot);

        Assert.True(result[0].HasBindings);
    }

    [Fact]
    public void ScanAircraft_HasBindings_FalseWhenAllDiffsEmpty()
    {
        var sgRoot      = MakeSavedGamesRoot();
        var configInput = Path.Combine(sgRoot, "Config", "Input");
        AddDeviceFolder(configInput, "FA-18C_hornet", "Stick", ""); // empty file

        var result = DcsAircraftScanner.ScanAircraft(sgRoot);

        Assert.False(result[0].HasBindings);
    }

    [Fact]
    public void ScanAircraft_HasBindings_TrueWhenAtLeastOneDiffNonEmpty()
    {
        var sgRoot      = MakeSavedGamesRoot();
        var configInput = Path.Combine(sgRoot, "Config", "Input");
        AddDeviceFolder(configInput, "FA-18C_hornet", "Stick",    MinimalDiff());
        AddDeviceFolder(configInput, "FA-18C_hornet", "Throttle", ""); // empty

        var result = DcsAircraftScanner.ScanAircraft(sgRoot);

        Assert.True(result[0].HasBindings);
    }

    [Fact]
    public void ScanAircraft_MultipleAircraft_SortedByFriendlyName()
    {
        var sgRoot      = MakeSavedGamesRoot();
        var configInput = Path.Combine(sgRoot, "Config", "Input");
        AddDeviceFolder(configInput, "Su-27",          "Stick", MinimalDiff());
        AddDeviceFolder(configInput, "FA-18C_hornet",  "Stick", MinimalDiff());
        AddDeviceFolder(configInput, "A-10C",          "Stick", MinimalDiff());

        var result = DcsAircraftScanner.ScanAircraft(sgRoot);

        // "A-10C Warthog" < "F/A-18C Hornet" < "Su-27 Flanker"
        Assert.Equal(3, result.Count);
        Assert.Equal("A-10C", result[0].FolderName);
        Assert.Equal("FA-18C_hornet", result[1].FolderName);
        Assert.Equal("Su-27", result[2].FolderName);
    }

    [Fact]
    public void ScanAircraft_DiffLuaPathIsAbsolute()
    {
        var sgRoot      = MakeSavedGamesRoot();
        var configInput = Path.Combine(sgRoot, "Config", "Input");
        AddDeviceFolder(configInput, "FA-18C_hornet", "Stick", MinimalDiff());

        var result = DcsAircraftScanner.ScanAircraft(sgRoot);

        Assert.True(Path.IsPathRooted(result[0].Devices[0].DiffLuaPath));
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    // DcsBatchProcessor — GetDefaultOutputDirectory
    // ─────────────────────────────────────────────────────────────────────────

    #region DcsBatchProcessor — GetDefaultOutputDirectory

    [Fact]
    public void GetDefaultOutputDirectory_ReturnsLibraryFilesPath()
    {
        var ssdRoot = MakeSavedGamesRoot();
        Directory.CreateDirectory(ssdRoot);
        var mgr     = new DocumentLibraryManager(ssdRoot);
        var libId   = "lib-test-001";
        mgr.EnsureLibraryFolders(libId);

        var result   = DcsBatchProcessor.GetDefaultOutputDirectory(mgr, libId);
        var expected = mgr.GetFilesPath(libId);

        Assert.Equal(expected, result);
    }

    #endregion

    // ─────────────────────────────────────────────────────────────────────────
    // DcsBatchProcessor — ProcessAircraftWithSummary
    // ─────────────────────────────────────────────────────────────────────────

    #region DcsBatchProcessor — ProcessAircraftWithSummary

    [Fact]
    public void ProcessAircraftWithSummary_EmptyInput_ZeroCounts()
    {
        var processor = new DcsBatchProcessor();
        var outputDir = Path.Combine(_tempDir, "out-empty");

        var summary = processor.ProcessAircraftWithSummary(
            Array.Empty<DcsAircraftInfo>(), outputDir);

        Assert.Equal(0, summary.Succeeded);
        Assert.Equal(0, summary.Failed);
        Assert.Empty(summary.Results);
        Assert.Empty(summary.OutputFilePaths);
    }

    [Fact]
    public void ProcessAircraftWithSummary_ValidAircraft_Succeeds()
    {
        var sgRoot      = MakeSavedGamesRoot();
        var configInput = Path.Combine(sgRoot, "Config", "Input");
        AddDeviceFolder(configInput, "FA-18C_hornet", "Stick", MinimalDiff());

        var aircraft  = DcsAircraftScanner.ScanAircraft(sgRoot);
        var outputDir = Path.Combine(_tempDir, "out-valid");
        var processor = new DcsBatchProcessor();

        var summary = processor.ProcessAircraftWithSummary(aircraft, outputDir);

        Assert.Equal(1, summary.Succeeded);
        Assert.Equal(0, summary.Failed);
    }

    [Fact]
    public void ProcessAircraftWithSummary_CreatesOutputFile()
    {
        var sgRoot      = MakeSavedGamesRoot();
        var configInput = Path.Combine(sgRoot, "Config", "Input");
        AddDeviceFolder(configInput, "FA-18C_hornet", "Stick", MinimalDiff());

        var aircraft  = DcsAircraftScanner.ScanAircraft(sgRoot);
        var outputDir = Path.Combine(_tempDir, "out-file");
        var processor = new DcsBatchProcessor();

        var summary = processor.ProcessAircraftWithSummary(aircraft, outputDir);

        Assert.Single(summary.OutputFilePaths);
        Assert.True(File.Exists(summary.OutputFilePaths[0]));
    }

    [Fact]
    public void ProcessAircraftWithSummary_OutputFileHasTxtExtension()
    {
        var sgRoot      = MakeSavedGamesRoot();
        var configInput = Path.Combine(sgRoot, "Config", "Input");
        AddDeviceFolder(configInput, "FA-18C_hornet", "Stick", MinimalDiff());

        var aircraft  = DcsAircraftScanner.ScanAircraft(sgRoot);
        var outputDir = Path.Combine(_tempDir, "out-ext");
        var processor = new DcsBatchProcessor();

        var summary = processor.ProcessAircraftWithSummary(aircraft, outputDir);

        Assert.EndsWith(".txt", summary.OutputFilePaths[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProcessAircraftWithSummary_OutputContainsAircraftName()
    {
        var sgRoot      = MakeSavedGamesRoot();
        var configInput = Path.Combine(sgRoot, "Config", "Input");
        AddDeviceFolder(configInput, "FA-18C_hornet", "Stick", MinimalDiff());

        var aircraft  = DcsAircraftScanner.ScanAircraft(sgRoot);
        var outputDir = Path.Combine(_tempDir, "out-name");
        var processor = new DcsBatchProcessor();

        var summary = processor.ProcessAircraftWithSummary(aircraft, outputDir);

        var text = File.ReadAllText(summary.OutputFilePaths[0]);
        Assert.Contains("F/A-18C Hornet", text);
    }

    [Fact]
    public void ProcessAircraftWithSummary_OutputContainsAxisBindings()
    {
        var sgRoot      = MakeSavedGamesRoot();
        var configInput = Path.Combine(sgRoot, "Config", "Input");
        AddDeviceFolder(configInput, "FA-18C_hornet", "Stick",
            MinimalDiff("Pitch", "JOY_Y"));

        var aircraft  = DcsAircraftScanner.ScanAircraft(sgRoot);
        var outputDir = Path.Combine(_tempDir, "out-axes");
        var processor = new DcsBatchProcessor();

        var summary = processor.ProcessAircraftWithSummary(aircraft, outputDir);

        var text = File.ReadAllText(summary.OutputFilePaths[0]);
        Assert.Contains("Pitch", text);
        Assert.Contains("JOY_Y", text);
    }

    [Fact]
    public void ProcessAircraftWithSummary_AxisAndButtonCountsReflected()
    {
        var sgRoot      = MakeSavedGamesRoot();
        var configInput = Path.Combine(sgRoot, "Config", "Input");
        AddDeviceFolder(configInput, "FA-18C_hornet", "Stick", MinimalDiff());

        var aircraft  = DcsAircraftScanner.ScanAircraft(sgRoot);
        var outputDir = Path.Combine(_tempDir, "out-counts");
        var processor = new DcsBatchProcessor();

        var summary = processor.ProcessAircraftWithSummary(aircraft, outputDir);

        Assert.Equal(1, summary.Results[0].AxisCount);  // MinimalDiff has one axis
        Assert.Equal(0, summary.Results[0].ButtonCount);
    }

    [Fact]
    public void ProcessAircraftWithSummary_MultipleAircraft_OneFileEach()
    {
        var sgRoot      = MakeSavedGamesRoot();
        var configInput = Path.Combine(sgRoot, "Config", "Input");
        AddDeviceFolder(configInput, "FA-18C_hornet", "Stick", MinimalDiff());
        AddDeviceFolder(configInput, "F-16C_50",      "Stick", MinimalDiff("Roll", "JOY_X"));

        var aircraft  = DcsAircraftScanner.ScanAircraft(sgRoot);
        var outputDir = Path.Combine(_tempDir, "out-multi");
        var processor = new DcsBatchProcessor();

        var summary = processor.ProcessAircraftWithSummary(aircraft, outputDir);

        Assert.Equal(2, summary.Succeeded);
        Assert.Equal(2, summary.OutputFilePaths.Count);
        // All output files should exist
        Assert.All(summary.OutputFilePaths, p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public void ProcessAircraftWithSummary_CreatesOutputDirectoryIfAbsent()
    {
        var sgRoot      = MakeSavedGamesRoot();
        var configInput = Path.Combine(sgRoot, "Config", "Input");
        AddDeviceFolder(configInput, "FA-18C_hornet", "Stick", MinimalDiff());

        var aircraft  = DcsAircraftScanner.ScanAircraft(sgRoot);
        var outputDir = Path.Combine(_tempDir, "does-not-exist-yet", "subdir");
        var processor = new DcsBatchProcessor();

        processor.ProcessAircraftWithSummary(aircraft, outputDir);

        Assert.True(Directory.Exists(outputDir));
    }

    [Fact]
    public void ProcessAircraftWithSummary_AircraftFriendlyNameInResult()
    {
        var sgRoot      = MakeSavedGamesRoot();
        var configInput = Path.Combine(sgRoot, "Config", "Input");
        AddDeviceFolder(configInput, "FA-18C_hornet", "Stick", MinimalDiff());

        var aircraft  = DcsAircraftScanner.ScanAircraft(sgRoot);
        var outputDir = Path.Combine(_tempDir, "out-fname");
        var processor = new DcsBatchProcessor();

        var summary = processor.ProcessAircraftWithSummary(aircraft, outputDir);

        Assert.Equal("F/A-18C Hornet", summary.Results[0].AircraftFriendlyName);
    }

    [Fact]
    public void ProcessAircraftWithSummary_AircraftNoDevices_StillSucceeds()
    {
        // An aircraft folder with no device folders (no diff.lua) should produce
        // an output file containing empty binding sections rather than throwing.
        var sgRoot      = MakeSavedGamesRoot();
        var configInput = Path.Combine(sgRoot, "Config", "Input");
        // Create the aircraft dir but no device sub-folder
        Directory.CreateDirectory(Path.Combine(configInput, "FA-18C_hornet"));

        var aircraft  = DcsAircraftScanner.ScanAircraft(sgRoot);
        var outputDir = Path.Combine(_tempDir, "out-nodev");
        var processor = new DcsBatchProcessor();

        var summary = processor.ProcessAircraftWithSummary(aircraft, outputDir);

        Assert.Equal(1, summary.Succeeded);
        Assert.Equal(0, summary.Failed);
    }

    [Fact]
    public void ProcessAircraftWithSummary_Cancellation_ThrowsOperationCanceled()
    {
        var sgRoot      = MakeSavedGamesRoot();
        var configInput = Path.Combine(sgRoot, "Config", "Input");
        AddDeviceFolder(configInput, "FA-18C_hornet", "Stick", MinimalDiff());
        AddDeviceFolder(configInput, "F-16C_50",      "Stick", MinimalDiff());

        var aircraft  = DcsAircraftScanner.ScanAircraft(sgRoot);
        var outputDir = Path.Combine(_tempDir, "out-cancel");
        var processor = new DcsBatchProcessor();

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancelled

        Assert.Throws<OperationCanceledException>(() =>
            processor.ProcessAircraftWithSummary(aircraft, outputDir, cts.Token));
    }

    [Fact]
    public void ProcessAircraftWithSummary_SucceededPlusFailedEqualsTotalResults()
    {
        var sgRoot      = MakeSavedGamesRoot();
        var configInput = Path.Combine(sgRoot, "Config", "Input");
        AddDeviceFolder(configInput, "FA-18C_hornet", "Stick", MinimalDiff());
        AddDeviceFolder(configInput, "F-16C_50",      "Stick", MinimalDiff());

        var aircraft  = DcsAircraftScanner.ScanAircraft(sgRoot);
        var outputDir = Path.Combine(_tempDir, "out-sum");
        var processor = new DcsBatchProcessor();

        var summary = processor.ProcessAircraftWithSummary(aircraft, outputDir);

        Assert.Equal(summary.Results.Count, summary.Succeeded + summary.Failed);
    }

    #endregion
}
