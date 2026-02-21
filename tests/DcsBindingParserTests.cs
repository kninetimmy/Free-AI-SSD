using FreeAiSsd.Shared.Documents;

namespace FreeAiSsd.Tests;

/// <summary>
/// Unit tests for <see cref="DcsBindingParser"/> and supporting models.
/// Tests cover: Lua parsing, multi-device merging, output formatting,
/// aircraft name mapping, category inference, and edge cases.
/// </summary>
public class DcsBindingParserTests : IDisposable
{
    private readonly string _tempDir;

    public DcsBindingParserTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"dcs-parser-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private string WriteDiff(string fileName, string content)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    // ---------------------------------------------------------------------------
    // Representative diff.lua fixtures
    // ---------------------------------------------------------------------------

    private const string StickDiff = """
        local diff = {
        ["axisDiffs"] = {
        ["d3001abc"] = {
        ["name"] = "Pitch",
        ["added"] = {
        [1] = {
        ["key"] = "JOY_Y",
        ["filter"] = {
        ["curvature"] = 0.2,
        ["deadzone"] = 0.05,
        ["invert"] = false,
        ["saturationX"] = 1,
        ["saturationY"] = 1,
        ["slip"] = 0,
        }, -- end of ["filter"]
        }, -- end of [1]
        }, -- end of ["added"]
        }, -- end of ["d3001abc"]
        ["d3002def"] = {
        ["name"] = "Roll",
        ["added"] = {
        [1] = {
        ["key"] = "JOY_X",
        ["filter"] = {
        ["curvature"] = 0.2,
        ["deadzone"] = 0,
        ["invert"] = false,
        ["saturationX"] = 1,
        ["saturationY"] = 1,
        ["slip"] = 0,
        }, -- end of ["filter"]
        }, -- end of [1]
        }, -- end of ["added"]
        }, -- end of ["d3002def"]
        }, -- end of ["axisDiffs"]
        ["keyDiffs"] = {
        ["k3001abc"] = {
        ["name"] = "Gun Trigger - SECOND DETENT",
        ["added"] = {
        [1] = {
        ["key"] = "JOY_BTN1",
        }, -- end of [1]
        }, -- end of ["added"]
        }, -- end of ["k3001abc"]
        ["k3002def"] = {
        ["name"] = "Select Sparrow",
        ["added"] = {
        [1] = {
        ["key"] = "JOY_BTN9",
        }, -- end of [1]
        }, -- end of ["added"]
        }, -- end of ["k3002def"]
        ["k3003ghi"] = {
        ["name"] = "Trimmer - Climb",
        ["added"] = {
        [1] = {
        ["key"] = "JOY_BTN13",
        }, -- end of [1]
        }, -- end of ["added"]
        }, -- end of ["k3003ghi"]
        }, -- end of ["keyDiffs"]
        } -- end of diff
        return diff
        """;

    private const string ThrottleDiff = """
        local diff = {
        ["axisDiffs"] = {
        ["d9001aaa"] = {
        ["name"] = "Left Engine Throttle",
        ["added"] = {
        [1] = {
        ["key"] = "JOY_Z",
        ["filter"] = {
        ["curvature"] = 0,
        ["deadzone"] = 0,
        ["invert"] = false,
        ["saturationX"] = 1,
        ["saturationY"] = 1,
        ["slip"] = 0,
        }, -- end of ["filter"]
        }, -- end of [1]
        }, -- end of ["added"]
        }, -- end of ["d9001aaa"]
        }, -- end of ["axisDiffs"]
        ["keyDiffs"] = {
        ["k9001bbb"] = {
        ["name"] = "Weapon Release",
        ["added"] = {
        [1] = {
        ["key"] = "JOY_BTN2",
        }, -- end of [1]
        }, -- end of ["added"]
        }, -- end of ["k9001bbb"]
        }, -- end of ["keyDiffs"]
        } -- end of diff
        return diff
        """;

    private const string ArrayCurvatureDiff = """
        local diff = {
        ["axisDiffs"] = {
        ["d0001zzz"] = {
        ["name"] = "Pitch",
        ["added"] = {
        [1] = {
        ["key"] = "JOY_Y",
        ["filter"] = {
        ["curvature"] = {0, 0, 0, 0.1, 0.3, 0.1, 0, 0, 0},
        ["deadzone"] = 0.03,
        ["invert"] = true,
        ["saturationX"] = 0.9,
        ["saturationY"] = 0.8,
        ["slip"] = 0,
        }, -- end of ["filter"]
        }, -- end of [1]
        }, -- end of ["added"]
        }, -- end of ["d0001zzz"]
        }, -- end of ["axisDiffs"]
        ["keyDiffs"] = {
        }, -- end of ["keyDiffs"]
        } -- end of diff
        return diff
        """;

    private const string ChangedEntryDiff = """
        local diff = {
        ["axisDiffs"] = {
        }, -- end of ["axisDiffs"]
        ["keyDiffs"] = {
        ["kCHANGED"] = {
        ["name"] = "Autopilot/NWS Disengage Paddle",
        ["changed"] = {
        [1] = {
        ["key"] = "JOY_BTN6",
        }, -- end of [1]
        }, -- end of ["changed"]
        }, -- end of ["kCHANGED"]
        }, -- end of ["keyDiffs"]
        } -- end of diff
        return diff
        """;

    private const string RemovedEntryDiff = """
        local diff = {
        ["axisDiffs"] = {
        }, -- end of ["axisDiffs"]
        ["keyDiffs"] = {
        ["kREMOVED"] = {
        ["name"] = "Select Gun",
        ["removed"] = {
        [1] = {
        ["key"] = "JOY_BTN7",
        }, -- end of [1]
        }, -- end of ["removed"]
        }, -- end of ["kREMOVED"]
        }, -- end of ["keyDiffs"]
        } -- end of diff
        return diff
        """;

    // ---------------------------------------------------------------------------
    // ParseFile — file system edge cases
    // ---------------------------------------------------------------------------

    #region ParseFile — file system edge cases

    [Fact]
    public void ParseFile_MissingFile_ReturnsNull()
    {
        var result = DcsBindingParser.ParseFile("/nonexistent/path/diff.lua", "TestDevice");
        Assert.Null(result);
    }

    [Fact]
    public void ParseFile_EmptyFile_ReturnsEmptyBindings()
    {
        var path = WriteDiff("empty.lua", "");
        var result = DcsBindingParser.ParseFile(path, "TestDevice");

        Assert.NotNull(result);
        Assert.Empty(result.Axes);
        Assert.Empty(result.Buttons);
    }

    [Fact]
    public void ParseFile_WhitespaceOnlyFile_ReturnsEmptyBindings()
    {
        var path = WriteDiff("blank.lua", "   \n\t\r\n   ");
        var result = DcsBindingParser.ParseFile(path, "TestDevice");

        Assert.NotNull(result);
        Assert.Empty(result.Axes);
        Assert.Empty(result.Buttons);
    }

    [Fact]
    public void ParseFile_DeviceNamePreserved()
    {
        var path = WriteDiff("stick.lua", StickDiff);
        var result = DcsBindingParser.ParseFile(path, "Saitek X-56 Rhino Stick");

        Assert.NotNull(result);
        Assert.Equal("Saitek X-56 Rhino Stick", result.DeviceName);
    }

    [Fact]
    public void ParseFile_SourceFilePathPreserved()
    {
        var path = WriteDiff("stick.lua", StickDiff);
        var result = DcsBindingParser.ParseFile(path, "Stick");

        Assert.NotNull(result);
        Assert.Equal(path, result.SourceFilePath);
    }

    #endregion

    // ---------------------------------------------------------------------------
    // ParseFile — axis bindings
    // ---------------------------------------------------------------------------

    #region ParseFile — axis bindings

    [Fact]
    public void ParseFile_StickDiff_ParsesTwoAxes()
    {
        var path = WriteDiff("stick.lua", StickDiff);
        var result = DcsBindingParser.ParseFile(path, "Stick")!;

        Assert.Equal(2, result.Axes.Count);
    }

    [Fact]
    public void ParseFile_StickDiff_PitchAxisHasCorrectKey()
    {
        var path = WriteDiff("stick.lua", StickDiff);
        var result = DcsBindingParser.ParseFile(path, "Stick")!;

        var pitch = result.Axes.FirstOrDefault(a => a.Name == "Pitch");
        Assert.NotNull(pitch);
        Assert.Equal("JOY_Y", pitch.KeyCode);
    }

    [Fact]
    public void ParseFile_StickDiff_PitchAxisHasFilter()
    {
        var path = WriteDiff("stick.lua", StickDiff);
        var result = DcsBindingParser.ParseFile(path, "Stick")!;

        var pitch = result.Axes.First(a => a.Name == "Pitch");
        Assert.NotNull(pitch.Filter);
        Assert.Equal(0.2, pitch.Filter!.Curvature, precision: 5);
        Assert.Equal(0.05, pitch.Filter.Deadzone, precision: 5);
        Assert.False(pitch.Filter.Invert);
    }

    [Fact]
    public void ParseFile_StickDiff_RollAxisHasCurvatureZeroDeadzone()
    {
        var path = WriteDiff("stick.lua", StickDiff);
        var result = DcsBindingParser.ParseFile(path, "Stick")!;

        var roll = result.Axes.First(a => a.Name == "Roll");
        Assert.NotNull(roll.Filter);
        Assert.Equal(0.2, roll.Filter!.Curvature, precision: 5);
        Assert.Equal(0.0, roll.Filter.Deadzone, precision: 5);
    }

    [Fact]
    public void ParseFile_ArrayCurvature_ParsedToSingleValue()
    {
        var path = WriteDiff("array.lua", ArrayCurvatureDiff);
        var result = DcsBindingParser.ParseFile(path, "Stick")!;

        Assert.Single(result.Axes);
        var pitch = result.Axes[0];
        Assert.NotNull(pitch.Filter);
        // Midpoint of {0, 0, 0, 0.1, 0.3, 0.1, 0, 0, 0} (9 values) is index 4 = 0.3
        Assert.Equal(0.3, pitch.Filter!.Curvature, precision: 5);
    }

    [Fact]
    public void ParseFile_ArrayCurvature_InvertAndSaturationParsed()
    {
        var path = WriteDiff("array.lua", ArrayCurvatureDiff);
        var result = DcsBindingParser.ParseFile(path, "Stick")!;

        var filter = result.Axes[0].Filter!;
        Assert.True(filter.Invert);
        Assert.Equal(0.9, filter.SaturationX, precision: 5);
        Assert.Equal(0.8, filter.SaturationY, precision: 5);
    }

    [Fact]
    public void ParseFile_AxisDeviceNamePropagated()
    {
        var path = WriteDiff("stick.lua", StickDiff);
        var result = DcsBindingParser.ParseFile(path, "X-56 Stick")!;

        Assert.All(result.Axes, a => Assert.Equal("X-56 Stick", a.DeviceName));
    }

    #endregion

    // ---------------------------------------------------------------------------
    // ParseFile — button bindings
    // ---------------------------------------------------------------------------

    #region ParseFile — button bindings

    [Fact]
    public void ParseFile_StickDiff_ParsesThreeButtons()
    {
        var path = WriteDiff("stick.lua", StickDiff);
        var result = DcsBindingParser.ParseFile(path, "Stick")!;

        Assert.Equal(3, result.Buttons.Count);
    }

    [Fact]
    public void ParseFile_StickDiff_GunTriggerHasCorrectKey()
    {
        var path = WriteDiff("stick.lua", StickDiff);
        var result = DcsBindingParser.ParseFile(path, "Stick")!;

        var btn = result.Buttons.FirstOrDefault(b => b.Name == "Gun Trigger - SECOND DETENT");
        Assert.NotNull(btn);
        Assert.Equal("JOY_BTN1", btn!.KeyCode);
    }

    [Fact]
    public void ParseFile_StickDiff_ButtonDeviceNamePropagated()
    {
        var path = WriteDiff("stick.lua", StickDiff);
        var result = DcsBindingParser.ParseFile(path, "X-56 Stick")!;

        Assert.All(result.Buttons, b => Assert.Equal("X-56 Stick", b.DeviceName));
    }

    [Fact]
    public void ParseFile_ChangedEntry_IsIncluded()
    {
        var path = WriteDiff("changed.lua", ChangedEntryDiff);
        var result = DcsBindingParser.ParseFile(path, "Stick")!;

        Assert.Single(result.Buttons);
        Assert.Equal("Autopilot/NWS Disengage Paddle", result.Buttons[0].Name);
        Assert.Equal("JOY_BTN6", result.Buttons[0].KeyCode);
    }

    [Fact]
    public void ParseFile_RemovedEntry_IsExcluded()
    {
        var path = WriteDiff("removed.lua", RemovedEntryDiff);
        var result = DcsBindingParser.ParseFile(path, "Stick")!;

        // "removed" entries should not appear in the output
        Assert.Empty(result.Buttons);
    }

    #endregion

    // ---------------------------------------------------------------------------
    // Category inference
    // ---------------------------------------------------------------------------

    #region Category inference

    [Theory]
    [InlineData("Gun Trigger - SECOND DETENT", "Weapons")]
    [InlineData("Select Sparrow", "Weapons")]
    [InlineData("Weapon Release", "Weapons")]
    [InlineData("Jettison All", "Weapons")]
    [InlineData("Trimmer - Climb", "Flight Controls")]
    [InlineData("Autopilot/NWS Disengage Paddle", "Flight Controls")]
    [InlineData("Throttle Designator Controller - Depress", "Navigation / Sensors")]
    [InlineData("Undesignate/NWS", "Navigation / Sensors")]
    [InlineData("Comm 1 PTT", "Communications")]
    [InlineData("Engine Left Start", "Systems")]
    [InlineData("Cockpit View", "View Controls")]
    public void ButtonCategory_KnownNames_InferredCorrectly(string name, string expectedCategory)
    {
        var path = WriteDiff($"cat-{Guid.NewGuid():N}.lua", BuildSingleButtonDiff(name, "JOY_BTN1"));
        var result = DcsBindingParser.ParseFile(path, "Device")!;

        Assert.Single(result.Buttons);
        Assert.Equal(expectedCategory, result.Buttons[0].Category);
    }

    [Fact]
    public void ButtonCategory_UnknownName_IsEmpty()
    {
        var path = WriteDiff("unknown.lua", BuildSingleButtonDiff("Some Obscure Function", "JOY_BTN5"));
        var result = DcsBindingParser.ParseFile(path, "Device")!;

        Assert.Single(result.Buttons);
        Assert.Equal(string.Empty, result.Buttons[0].Category);
    }

    [Theory]
    [InlineData("Pitch", "Flight Controls")]
    [InlineData("Roll", "Flight Controls")]
    [InlineData("Rudder", "Flight Controls")]
    [InlineData("Left Engine Throttle", "Throttle")]
    [InlineData("Throttle", "Throttle")]
    [InlineData("Throttle Designator Controller - Vertical", "Navigation / Sensors")]
    public void AxisCategory_KnownNames_InferredCorrectly(string name, string expectedCategory)
    {
        var path = WriteDiff($"axcat-{Guid.NewGuid():N}.lua", BuildSingleAxisDiff(name, "JOY_Y"));
        var result = DcsBindingParser.ParseFile(path, "Device")!;

        Assert.Single(result.Axes);
        Assert.Equal(expectedCategory, result.Axes[0].Category);
    }

    #endregion

    // ---------------------------------------------------------------------------
    // Aircraft name mapping
    // ---------------------------------------------------------------------------

    #region Aircraft name mapping

    [Theory]
    [InlineData("FA-18C_hornet", "F/A-18C Hornet")]
    [InlineData("F-16C_50", "F-16C Viper")]
    [InlineData("A-10C", "A-10C Warthog")]
    [InlineData("A-10C_2", "A-10C II Warthog")]
    [InlineData("F-14B", "F-14B Tomcat")]
    [InlineData("AV8BNA", "AV-8B Harrier II N/A")]
    [InlineData("M-2000C", "Mirage 2000C")]
    [InlineData("Ka-50", "Ka-50 Black Shark")]
    [InlineData("AJS37", "AJS-37 Viggen")]
    [InlineData("common", "Common (Shared Bindings)")]
    public void MapAircraftName_KnownFolderNames_ReturnReadableName(string folder, string expected)
    {
        Assert.Equal(expected, DcsBindingParser.MapAircraftName(folder));
    }

    [Fact]
    public void MapAircraftName_UnknownFolder_CleanedUp()
    {
        // Unknown folder name: underscores replaced with spaces
        var result = DcsBindingParser.MapAircraftName("Some_Custom_Module");
        Assert.Equal("Some Custom Module", result);
    }

    [Fact]
    public void MapAircraftName_EmptyString_ReturnsUnknown()
    {
        Assert.Equal("Unknown Aircraft", DcsBindingParser.MapAircraftName(""));
    }

    [Fact]
    public void MapAircraftName_CaseInsensitiveMatch()
    {
        Assert.Equal("F/A-18C Hornet", DcsBindingParser.MapAircraftName("FA-18C_HORNET"));
    }

    #endregion

    // ---------------------------------------------------------------------------
    // MergeDevices
    // ---------------------------------------------------------------------------

    #region MergeDevices

    [Fact]
    public void MergeDevices_TwoDevices_CombinesAllAxesAndButtons()
    {
        var stickPath = WriteDiff("stick.lua", StickDiff);
        var throttlePath = WriteDiff("throttle.lua", ThrottleDiff);

        var merged = DcsBindingParser.MergeDevices(
            "FA-18C_hornet",
            new[]
            {
                (stickPath, "X-56 Stick"),
                (throttlePath, "X-56 Throttle"),
            });

        // Stick: 2 axes, Throttle: 1 axis
        Assert.Equal(3, merged.Axes.Count);
        // Stick: 3 buttons, Throttle: 1 button
        Assert.Equal(4, merged.Buttons.Count);
    }

    [Fact]
    public void MergeDevices_AircraftNameMapped()
    {
        var path = WriteDiff("stick.lua", StickDiff);
        var merged = DcsBindingParser.MergeDevices("FA-18C_hornet", new[] { (path, "Stick") });

        Assert.Equal("F/A-18C Hornet", merged.AircraftName);
    }

    [Fact]
    public void MergeDevices_SourceDevicesPopulated()
    {
        var stickPath = WriteDiff("stick.lua", StickDiff);
        var throttlePath = WriteDiff("throttle.lua", ThrottleDiff);

        var merged = DcsBindingParser.MergeDevices(
            "FA-18C_hornet",
            new[]
            {
                (stickPath, "X-56 Stick"),
                (throttlePath, "X-56 Throttle"),
            });

        Assert.Contains("X-56 Stick", merged.SourceDevices);
        Assert.Contains("X-56 Throttle", merged.SourceDevices);
    }

    [Fact]
    public void MergeDevices_DuplicateDeviceName_ListedOnce()
    {
        var path1 = WriteDiff("diff1.lua", StickDiff);
        var path2 = WriteDiff("diff2.lua", StickDiff);

        var merged = DcsBindingParser.MergeDevices(
            "FA-18C_hornet",
            new[]
            {
                (path1, "SameDevice"),
                (path2, "SameDevice"),
            });

        Assert.Single(merged.SourceDevices);
    }

    [Fact]
    public void MergeDevices_InvalidFilePath_SkippedGracefully()
    {
        var valid = WriteDiff("stick.lua", StickDiff);

        var merged = DcsBindingParser.MergeDevices(
            "FA-18C_hornet",
            new[]
            {
                (valid, "Good Device"),
                ("/does/not/exist/diff.lua", "Bad Device"),
            });

        // Should still have the good device's bindings
        Assert.Single(merged.SourceDevices);
        Assert.Equal("Good Device", merged.SourceDevices[0]);
    }

    [Fact]
    public void MergeDevices_EmptyDeviceList_ReturnsEmptyBindings()
    {
        var merged = DcsBindingParser.MergeDevices("FA-18C_hornet", Array.Empty<(string, string)>());

        Assert.Empty(merged.Axes);
        Assert.Empty(merged.Buttons);
        Assert.Empty(merged.SourceDevices);
    }

    [Fact]
    public void MergeDevices_SameBindingOnTwoDevices_BothIncluded()
    {
        // When the same function is bound on two different devices, both entries are kept.
        var dev1 = WriteDiff("dev1.lua", BuildSingleButtonDiff("Weapon Release", "JOY_BTN2"));
        var dev2 = WriteDiff("dev2.lua", BuildSingleButtonDiff("Weapon Release", "JOY_BTN5"));

        var merged = DcsBindingParser.MergeDevices(
            "FA-18C_hornet",
            new[]
            {
                (dev1, "Device 1"),
                (dev2, "Device 2"),
            });

        var weaponRelease = merged.Buttons.Where(b => b.Name == "Weapon Release").ToList();
        Assert.Equal(2, weaponRelease.Count);
        Assert.Contains(weaponRelease, b => b.KeyCode == "JOY_BTN2");
        Assert.Contains(weaponRelease, b => b.KeyCode == "JOY_BTN5");
    }

    [Fact]
    public void MergeDevices_GeneratedUtcIsRecent()
    {
        var merged = DcsBindingParser.MergeDevices("FA-18C_hornet", Array.Empty<(string, string)>());
        Assert.True((DateTime.UtcNow - merged.GeneratedUtc).TotalSeconds < 5);
    }

    #endregion

    // ---------------------------------------------------------------------------
    // FormatOutput
    // ---------------------------------------------------------------------------

    #region FormatOutput

    [Fact]
    public void FormatOutput_ContainsAircraftName()
    {
        var path = WriteDiff("stick.lua", StickDiff);
        var merged = DcsBindingParser.MergeDevices("FA-18C_hornet", new[] { (path, "X-56 Stick") });
        var output = DcsBindingParser.FormatOutput(merged);

        Assert.Contains("F/A-18C Hornet", output);
    }

    [Fact]
    public void FormatOutput_ContainsGeneratedDate()
    {
        var path = WriteDiff("stick.lua", StickDiff);
        var merged = DcsBindingParser.MergeDevices("FA-18C_hornet", new[] { (path, "X-56 Stick") });
        var output = DcsBindingParser.FormatOutput(merged);

        Assert.Contains(DateTime.UtcNow.ToString("yyyy-MM-dd"), output);
    }

    [Fact]
    public void FormatOutput_ContainsSourceDevice()
    {
        var path = WriteDiff("stick.lua", StickDiff);
        var merged = DcsBindingParser.MergeDevices("FA-18C_hornet", new[] { (path, "X-56 Stick") });
        var output = DcsBindingParser.FormatOutput(merged);

        Assert.Contains("X-56 Stick", output);
    }

    [Fact]
    public void FormatOutput_ContainsAxisSectionHeader()
    {
        var path = WriteDiff("stick.lua", StickDiff);
        var merged = DcsBindingParser.MergeDevices("FA-18C_hornet", new[] { (path, "Stick") });
        var output = DcsBindingParser.FormatOutput(merged);

        Assert.Contains("=== AXES ===", output);
    }

    [Fact]
    public void FormatOutput_ContainsButtonSectionHeader()
    {
        var path = WriteDiff("stick.lua", StickDiff);
        var merged = DcsBindingParser.MergeDevices("FA-18C_hornet", new[] { (path, "Stick") });
        var output = DcsBindingParser.FormatOutput(merged);

        Assert.Contains("=== BUTTONS ===", output);
    }

    [Fact]
    public void FormatOutput_AxisLineIncludesKeyAndDevice()
    {
        var path = WriteDiff("stick.lua", StickDiff);
        var merged = DcsBindingParser.MergeDevices("FA-18C_hornet", new[] { (path, "X-56 Stick") });
        var output = DcsBindingParser.FormatOutput(merged);

        Assert.Contains("Pitch = JOY_Y on X-56 Stick", output);
    }

    [Fact]
    public void FormatOutput_AxisLineIncludesNonZeroFilter()
    {
        var path = WriteDiff("stick.lua", StickDiff);
        var merged = DcsBindingParser.MergeDevices("FA-18C_hornet", new[] { (path, "Stick") });
        var output = DcsBindingParser.FormatOutput(merged);

        // Pitch has curvature 0.2 and deadzone 0.05 — both non-zero, so filter should appear
        Assert.Contains("curvature", output);
        Assert.Contains("deadzone", output);
    }

    [Fact]
    public void FormatOutput_AxisLineOmitsDefaultFilter()
    {
        // Roll has curvature 0.2 but deadzone 0, invert false — deadzone line should be absent
        var path = WriteDiff("stick.lua", StickDiff);
        var merged = DcsBindingParser.MergeDevices("FA-18C_hornet", new[] { (path, "Stick") });
        var output = DcsBindingParser.FormatOutput(merged);

        // Verify the Roll line doesn't show "deadzone: 0"
        var rollLine = output.Split('\n').FirstOrDefault(l => l.StartsWith("Roll ="));
        Assert.NotNull(rollLine);
        Assert.DoesNotContain("deadzone", rollLine);
    }

    [Fact]
    public void FormatOutput_ButtonsGroupedByCategory()
    {
        var path = WriteDiff("stick.lua", StickDiff);
        var merged = DcsBindingParser.MergeDevices("FA-18C_hornet", new[] { (path, "Stick") });
        var output = DcsBindingParser.FormatOutput(merged);

        // Weapons category header should precede Gun Trigger line
        var weaponsIdx = output.IndexOf("[Weapons]", StringComparison.Ordinal);
        var gunIdx = output.IndexOf("Gun Trigger - SECOND DETENT", StringComparison.Ordinal);
        Assert.True(weaponsIdx >= 0, "Expected [Weapons] category header");
        Assert.True(gunIdx > weaponsIdx, "Gun Trigger should appear after [Weapons] header");
    }

    [Fact]
    public void FormatOutput_ButtonLineIncludesKeyAndDevice()
    {
        var path = WriteDiff("stick.lua", StickDiff);
        var merged = DcsBindingParser.MergeDevices("FA-18C_hornet", new[] { (path, "X-56 Stick") });
        var output = DcsBindingParser.FormatOutput(merged);

        Assert.Contains("Gun Trigger - SECOND DETENT = JOY_BTN1 on X-56 Stick", output);
    }

    [Fact]
    public void FormatOutput_EmptyBindings_NoBindingsMessages()
    {
        var merged = new DcsAircraftBindings { AircraftName = "F/A-18C Hornet" };
        var output = DcsBindingParser.FormatOutput(merged);

        Assert.Contains("(no axis bindings)", output);
        Assert.Contains("(no button bindings)", output);
    }

    [Fact]
    public void FormatOutput_InvertedAxis_ShowsInvertedInFilterPart()
    {
        var path = WriteDiff("inverted.lua", ArrayCurvatureDiff);
        var merged = DcsBindingParser.MergeDevices("FA-18C_hornet", new[] { (path, "Stick") });
        var output = DcsBindingParser.FormatOutput(merged);

        Assert.Contains("inverted", output);
    }

    #endregion

    // ---------------------------------------------------------------------------
    // Helpers for building minimal fixture diffs
    // ---------------------------------------------------------------------------

    #region Fixture helpers

    private static string BuildSingleButtonDiff(string name, string key) => $$"""
        local diff = {
        ["axisDiffs"] = {
        }, -- end of ["axisDiffs"]
        ["keyDiffs"] = {
        ["kTEST001"] = {
        ["name"] = "{{name}}",
        ["added"] = {
        [1] = {
        ["key"] = "{{key}}",
        }, -- end of [1]
        }, -- end of ["added"]
        }, -- end of ["kTEST001"]
        }, -- end of ["keyDiffs"]
        } -- end of diff
        return diff
        """;

    private static string BuildSingleAxisDiff(string name, string key) => $$"""
        local diff = {
        ["axisDiffs"] = {
        ["dTEST001"] = {
        ["name"] = "{{name}}",
        ["added"] = {
        [1] = {
        ["key"] = "{{key}}",
        }, -- end of [1]
        }, -- end of ["added"]
        }, -- end of ["dTEST001"]
        }, -- end of ["axisDiffs"]
        ["keyDiffs"] = {
        }, -- end of ["keyDiffs"]
        } -- end of diff
        return diff
        """;

    #endregion
}
