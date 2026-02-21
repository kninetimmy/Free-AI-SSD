using System.Text;
using System.Text.RegularExpressions;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.Shared.Documents;

/// <summary>
/// Parses DCS World controller binding diff files (diff.lua) into structured binding data
/// and formats them as clean, human-readable text suitable for RAG ingestion.
/// <para>
/// DCS stores bindings at: Saved Games/DCS/Config/Input/{aircraft}/{device-name}/diff.lua
/// Each file covers one physical device; call <see cref="MergeDevices"/> to combine them.
/// </para>
/// </summary>
public static class DcsBindingParser
{
    #region Aircraft name map

    /// <summary>
    /// Maps DCS internal aircraft folder names to readable display names.
    /// Extend this dictionary to support additional modules.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> AircraftNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FA-18C_hornet"]      = "F/A-18C Hornet",
            ["F-16C_50"]           = "F-16C Viper",
            ["A-10C"]              = "A-10C Warthog",
            ["A-10C_2"]            = "A-10C II Warthog",
            ["F-14B"]              = "F-14B Tomcat",
            ["F-14A-135-GR"]       = "F-14A Tomcat",
            ["AV8BNA"]             = "AV-8B Harrier II N/A",
            ["M-2000C"]            = "Mirage 2000C",
            ["MiG-21Bis"]          = "MiG-21bis",
            ["F-5E-3"]             = "F-5E Tiger II",
            ["Su-25T"]             = "Su-25T Frogfoot",
            ["Su-27"]              = "Su-27 Flanker",
            ["MiG-29A"]            = "MiG-29A Fulcrum",
            ["Ka-50"]              = "Ka-50 Black Shark",
            ["Ka-50_3"]            = "Ka-50 Black Shark III",
            ["UH-1H"]              = "UH-1H Huey",
            ["Mi-8MT"]             = "Mi-8MTV2 Magnificent Eight",
            ["SA342M"]             = "SA-342M Gazelle",
            ["SpitfireLFMkIX"]     = "Spitfire LF Mk IX",
            ["P-51D"]              = "P-51D Mustang",
            ["P-51D-30-NA"]        = "P-51D-30 Mustang",
            ["Fw-190A8"]           = "Fw 190 A-8",
            ["Fw-190D9"]           = "Fw 190 D-9",
            ["Bf-109K-4"]          = "Bf 109 K-4",
            ["C-101CC"]            = "C-101CC Aviojet",
            ["L-39C"]              = "L-39C Albatros",
            ["L-39ZA"]             = "L-39ZA Albatros",
            ["AJS37"]              = "AJS-37 Viggen",
            ["JF-17"]              = "JF-17 Thunder",
            ["I-16"]               = "I-16 Ishak",
            ["MosquitoFBMkVI"]     = "de Havilland Mosquito FB Mk VI",
            ["A-4E-C"]             = "A-4E Skyhawk (Community)",
            ["F-86F Sabre"]        = "F-86F Sabre",
            ["MiG-15bis"]          = "MiG-15bis",
            ["MiG-19P"]            = "MiG-19P Farmer",
            ["Yak-52"]             = "Yak-52",
            ["TF-51D"]             = "TF-51D Mustang",
            ["F-4E-45MC"]          = "F-4E Phantom II",
            ["OH-58D"]             = "OH-58D Kiowa Warrior",
            ["AH-64D_BLK_II"]      = "AH-64D Apache",
            ["Mi-24P"]             = "Mi-24P Hind",
            ["F-15ESE"]            = "F-15E Strike Eagle",
            ["common"]             = "Common (Shared Bindings)",
        };

    #endregion

    #region Category keyword map

    /// <summary>
    /// Keyword sets used to infer the display category of a button binding from its name.
    /// Matched case-insensitively against the binding name. First match wins.
    /// </summary>
    private static readonly (string Category, string[] Keywords)[] CategoryRules = new[]
    {
        ("Weapons", new[]
        {
            "gun", "trigger", "weapon", "missile", "bomb", "pickle", "jettison",
            "arm", "safe", "sparrow", "amraam", "sidewinder", "harm", "maverick",
            "agm", "aim", "gbu", "release", "ripple", "selective", "fuze",
        }),
        ("Navigation / Sensors", new[]
        {
            "designator", "undesignate", "radar", "sensor", "hmd",
            "tdc", "waypoint", "nav ", "ils", "tacan", "hud", "iff",
            "helmet", "flir", "eo", "laser", "lasing", "pave", "targeting pod",
        }),
        ("Flight Controls", new[]
        {
            "trimmer", "trim ", "flap", "gear", "brake", "autopilot",
            "speedbrake", "launch bar", "hook", "paddle", "disengage",
            "nosewheel", "anti-skid",
        }),
        ("Communications", new[]
        {
            "comm", "radio", "ptt", "intercom", "transponder", "squawk",
        }),
        ("Systems", new[]
        {
            "eject", "fuel", "engine", "apu", "light", "oxygen", "canopy",
            "startup", "bleed", "generator", "apu", "fire", "extinguish",
            "emergency", "master caution", "warning",
        }),
        ("View Controls", new[]
        {
            "view", "look", "snap", "zoom", "cockpit", "padlock", "head",
        }),
    };

    #endregion

    #region Regex patterns (compiled once)

    // Matches a named string field:  ["fieldName"] = "value"
    private static readonly Regex StringFieldRx = new(
        @"\[""(?<field>[^""]+)""\]\s*=\s*""(?<value>[^""]*)""",
        RegexOptions.Compiled);

    // Matches a named numeric field: ["fieldName"] = 1.23
    private static readonly Regex NumberFieldRx = new(
        @"\[""(?<field>[^""]+)""\]\s*=\s*(?<value>-?[\d]+(?:\.[\d]+)?)",
        RegexOptions.Compiled);

    // Matches a named bool field:    ["fieldName"] = true
    private static readonly Regex BoolFieldRx = new(
        @"\[""(?<field>[^""]+)""\]\s*=\s*(?<value>true|false)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches a named array field:   ["fieldName"] = { ... }  (single-line, values captured)
    private static readonly Regex ArrayFieldRx = new(
        @"\[""(?<field>[^""]+)""\]\s*=\s*\{(?<values>[^}]*)\}",
        RegexOptions.Compiled);

    // Matches a block header (string key): ["someKey"] = {
    private static readonly Regex StringKeyHeaderRx = new(
        @"\[""(?<key>[^""]+)""\]\s*=\s*\{",
        RegexOptions.Compiled);

    // Matches a block header (numeric key): [N] = {
    private static readonly Regex NumericKeyHeaderRx = new(
        @"\[(?<idx>\d+)\]\s*=\s*\{",
        RegexOptions.Compiled);

    #endregion

    #region Public API

    /// <summary>
    /// Parses a single DCS diff.lua file and returns structured binding data for one device.
    /// Returns <see langword="null"/> if the file cannot be parsed, logging the reason via <paramref name="log"/>.
    /// </summary>
    /// <param name="filePath">Absolute path to the diff.lua file.</param>
    /// <param name="deviceName">Friendly name of the physical device (e.g. "Saitek X-56 Rhino Stick").</param>
    /// <param name="log">Optional log service for error and warning messages.</param>
    public static DcsDeviceBindings? ParseFile(string filePath, string deviceName, ILogService? log = null)
    {
        if (!File.Exists(filePath))
        {
            log?.Error($"[DcsBindingParser] File not found: {filePath}");
            return null;
        }

        string content;
        try
        {
            content = File.ReadAllText(filePath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            log?.Error($"[DcsBindingParser] Cannot read '{filePath}': {ex.Message}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            log?.Info($"[DcsBindingParser] Empty file, no bindings: {filePath}");
            return new DcsDeviceBindings { DeviceName = deviceName, SourceFilePath = filePath };
        }

        try
        {
            return ParseContent(content, filePath, deviceName, log);
        }
        catch (Exception ex)
        {
            log?.Error($"[DcsBindingParser] Parse error in '{filePath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Merges bindings from multiple diff.lua files (one per physical device) into a
    /// single <see cref="DcsAircraftBindings"/> object for the given aircraft.
    /// </summary>
    /// <param name="aircraftFolderName">
    /// The DCS internal folder name (e.g. "FA-18C_hornet").
    /// Used to derive a readable aircraft name.
    /// </param>
    /// <param name="devices">
    /// Collection of <c>(filePath, deviceName)</c> pairs — one per diff.lua file.
    /// </param>
    /// <param name="log">Optional log service for error and warning messages.</param>
    public static DcsAircraftBindings MergeDevices(
        string aircraftFolderName,
        IEnumerable<(string FilePath, string DeviceName)> devices,
        ILogService? log = null)
    {
        var result = new DcsAircraftBindings
        {
            AircraftName = MapAircraftName(aircraftFolderName),
            GeneratedUtc = DateTime.UtcNow,
        };

        foreach (var (filePath, deviceName) in devices)
        {
            var parsed = ParseFile(filePath, deviceName, log);
            if (parsed is null)
            {
                continue;
            }

            if (!result.SourceDevices.Contains(parsed.DeviceName))
            {
                result.SourceDevices.Add(parsed.DeviceName);
            }

            result.Axes.AddRange(parsed.Axes);
            result.Buttons.AddRange(parsed.Buttons);
        }

        return result;
    }

    /// <summary>
    /// Formats a <see cref="DcsAircraftBindings"/> object into a clean plain-text report
    /// grouped by section (axes, then buttons by category).
    /// The output is optimised for RAG chunking and embedding.
    /// </summary>
    /// <param name="bindings">The merged aircraft bindings to format.</param>
    public static string FormatOutput(DcsAircraftBindings bindings)
    {
        var sb = new StringBuilder();

        // Header
        sb.AppendLine($"Aircraft: {bindings.AircraftName}");
        sb.AppendLine($"Generated: {bindings.GeneratedUtc:yyyy-MM-dd}");
        sb.AppendLine(bindings.SourceDevices.Count > 0
            ? $"Sources: {string.Join(", ", bindings.SourceDevices)}"
            : "Sources: (none)");

        // Axes section
        sb.AppendLine();
        sb.AppendLine("=== AXES ===");
        if (bindings.Axes.Count == 0)
        {
            sb.AppendLine("(no axis bindings)");
        }
        else
        {
            foreach (var axis in bindings.Axes)
            {
                var filterPart = FormatFilter(axis.Filter);
                sb.AppendLine(filterPart.Length > 0
                    ? $"{axis.Name} = {axis.KeyCode} on {axis.DeviceName} ({filterPart})"
                    : $"{axis.Name} = {axis.KeyCode} on {axis.DeviceName}");
            }
        }

        // Buttons section — grouped by category
        sb.AppendLine();
        sb.AppendLine("=== BUTTONS ===");
        if (bindings.Buttons.Count == 0)
        {
            sb.AppendLine("(no button bindings)");
        }
        else
        {
            var grouped = bindings.Buttons
                .GroupBy(b => string.IsNullOrEmpty(b.Category) ? "Uncategorized" : b.Category)
                .OrderBy(g => g.Key == "Uncategorized" ? 1 : 0)
                .ThenBy(g => g.Key);

            foreach (var group in grouped)
            {
                sb.AppendLine();
                sb.AppendLine($"[{group.Key}]");
                foreach (var btn in group)
                {
                    sb.AppendLine($"{btn.Name} = {btn.KeyCode} on {btn.DeviceName}");
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Maps a DCS internal aircraft folder name to a human-readable display name.
    /// Falls back to a lightly cleaned version of the folder name if no mapping exists.
    /// </summary>
    /// <param name="folderName">Raw DCS folder name (e.g. "FA-18C_hornet").</param>
    public static string MapAircraftName(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return "Unknown Aircraft";
        }

        if (AircraftNames.TryGetValue(folderName, out var mapped))
        {
            return mapped;
        }

        // Best-effort cleanup: replace underscores with spaces, trim
        return folderName.Replace('_', ' ').Trim();
    }

    #endregion

    #region Core parse logic

    private static DcsDeviceBindings ParseContent(
        string content, string filePath, string deviceName, ILogService? log)
    {
        var result = new DcsDeviceBindings
        {
            DeviceName = deviceName,
            SourceFilePath = filePath,
        };

        // Extract the axisDiffs block and parse it
        var axisSection = ExtractNamedBlock(content, "axisDiffs");
        if (axisSection is not null)
        {
            result.Axes.AddRange(ParseAxisSection(axisSection, deviceName, log));
        }

        // Extract the keyDiffs block and parse it
        var keySection = ExtractNamedBlock(content, "keyDiffs");
        if (keySection is not null)
        {
            result.Buttons.AddRange(ParseKeySection(keySection, deviceName, log));
        }

        return result;
    }

    private static List<DcsAxisBinding> ParseAxisSection(
        string sectionContent, string deviceName, ILogService? log)
    {
        var results = new List<DcsAxisBinding>();
        foreach (var (_, bindingContent) in ExtractTopLevelStringBlocks(sectionContent))
        {
            var name = ExtractStringField(bindingContent, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            // Gather key+filter from "added" and "changed" sections (skip "removed")
            foreach (var actionName in new[] { "added", "changed" })
            {
                var actionContent = ExtractNamedBlock(bindingContent, actionName);
                if (actionContent is null)
                {
                    continue;
                }

                foreach (var (_, entryContent) in ExtractTopLevelNumericBlocks(actionContent))
                {
                    var keyCode = ExtractStringField(entryContent, "key");
                    if (string.IsNullOrWhiteSpace(keyCode))
                    {
                        continue;
                    }

                    DcsAxisFilter? filter = null;
                    var filterContent = ExtractNamedBlock(entryContent, "filter");
                    if (filterContent is not null)
                    {
                        filter = ParseAxisFilter(filterContent);
                    }

                    results.Add(new DcsAxisBinding
                    {
                        Name = name,
                        DeviceName = deviceName,
                        KeyCode = keyCode,
                        Category = InferAxisCategory(name),
                        Filter = filter,
                    });
                }
            }
        }

        return results;
    }

    private static List<DcsButtonBinding> ParseKeySection(
        string sectionContent, string deviceName, ILogService? log)
    {
        var results = new List<DcsButtonBinding>();
        foreach (var (_, bindingContent) in ExtractTopLevelStringBlocks(sectionContent))
        {
            var name = ExtractStringField(bindingContent, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            foreach (var actionName in new[] { "added", "changed" })
            {
                var actionContent = ExtractNamedBlock(bindingContent, actionName);
                if (actionContent is null)
                {
                    continue;
                }

                foreach (var (_, entryContent) in ExtractTopLevelNumericBlocks(actionContent))
                {
                    var keyCode = ExtractStringField(entryContent, "key");
                    if (string.IsNullOrWhiteSpace(keyCode))
                    {
                        continue;
                    }

                    results.Add(new DcsButtonBinding
                    {
                        Name = name,
                        DeviceName = deviceName,
                        KeyCode = keyCode,
                        Category = InferButtonCategory(name),
                    });
                }
            }
        }

        return results;
    }

    private static DcsAxisFilter ParseAxisFilter(string filterContent)
    {
        var filter = new DcsAxisFilter();

        // Deadzone: ["deadzone"] = 0.05
        var dz = ExtractNumberField(filterContent, "deadzone");
        if (dz.HasValue)
        {
            filter.Deadzone = dz.Value;
        }

        // Invert: ["invert"] = true
        var inv = ExtractBoolField(filterContent, "invert");
        if (inv.HasValue)
        {
            filter.Invert = inv.Value;
        }

        // SaturationX/Y
        var sx = ExtractNumberField(filterContent, "saturationX");
        if (sx.HasValue)
        {
            filter.SaturationX = sx.Value;
        }

        var sy = ExtractNumberField(filterContent, "saturationY");
        if (sy.HasValue)
        {
            filter.SaturationY = sy.Value;
        }

        // Curvature — may be a single value or an array of control points
        filter.Curvature = ParseCurvature(filterContent);

        return filter;
    }

    private static double ParseCurvature(string filterContent)
    {
        // Try array first: ["curvature"] = { v1, v2, ... }
        var arrayMatch = ArrayFieldRx.Match(filterContent);
        while (arrayMatch.Success)
        {
            if (string.Equals(arrayMatch.Groups["field"].Value, "curvature", StringComparison.Ordinal))
            {
                var raw = arrayMatch.Groups["values"].Value;
                var values = raw
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => double.TryParse(s, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var d) ? (double?)d : null)
                    .Where(d => d.HasValue)
                    .Select(d => d!.Value)
                    .ToArray();

                if (values.Length == 0)
                {
                    return 0;
                }

                // DCS stores 5 control points (x,y pairs = 10 values) or just y-values.
                // The midpoint y-value is the most representative "curvature" for display.
                return values[values.Length / 2];
            }

            arrayMatch = arrayMatch.NextMatch();
        }

        // Fall back to single numeric value: ["curvature"] = 0.2
        var single = ExtractNumberField(filterContent, "curvature");
        return single ?? 0;
    }

    #endregion

    #region Block extraction helpers

    /// <summary>
    /// Extracts the content between the braces of a named string-keyed block,
    /// e.g. finds <c>["name"] = { ... }</c> and returns the content inside the braces.
    /// Returns <see langword="null"/> if the block is not present.
    /// </summary>
    private static string? ExtractNamedBlock(string content, string name)
    {
        var searchToken = $"[\"{name}\"]";
        var tokenIdx = content.IndexOf(searchToken, StringComparison.Ordinal);
        if (tokenIdx < 0)
        {
            return null;
        }

        // Find the '=' and then the opening '{'
        var equalsIdx = content.IndexOf('=', tokenIdx + searchToken.Length);
        if (equalsIdx < 0)
        {
            return null;
        }

        var braceIdx = content.IndexOf('{', equalsIdx);
        if (braceIdx < 0)
        {
            return null;
        }

        var closeIdx = FindMatchingBrace(content, braceIdx);
        if (closeIdx < 0)
        {
            return null;
        }

        return content[(braceIdx + 1)..closeIdx];
    }

    /// <summary>
    /// Iterates over top-level string-keyed blocks (<c>["key"] = { ... }</c>) in
    /// <paramref name="content"/>. Only processes the immediate top level —
    /// nested blocks are included in each block's content but not iterated separately.
    /// </summary>
    private static IEnumerable<(string Key, string Content)> ExtractTopLevelStringBlocks(string content)
    {
        var list = new List<(string, string)>();
        int searchPos = 0;

        while (searchPos < content.Length)
        {
            var m = StringKeyHeaderRx.Match(content, searchPos);
            if (!m.Success)
            {
                break;
            }

            var key = m.Groups["key"].Value;
            var braceIdx = m.Index + m.Length - 1; // the '{' is the last char of the match

            var closeIdx = FindMatchingBrace(content, braceIdx);
            if (closeIdx < 0)
            {
                break;
            }

            list.Add((key, content[(braceIdx + 1)..closeIdx]));
            searchPos = closeIdx + 1; // jump past this block to stay at top level
        }

        return list;
    }

    /// <summary>
    /// Iterates over top-level numeric-keyed blocks (<c>[N] = { ... }</c>) in
    /// <paramref name="content"/>, used for entries inside added/changed action blocks.
    /// </summary>
    private static IEnumerable<(string Key, string Content)> ExtractTopLevelNumericBlocks(string content)
    {
        var list = new List<(string, string)>();
        int searchPos = 0;

        while (searchPos < content.Length)
        {
            var m = NumericKeyHeaderRx.Match(content, searchPos);
            if (!m.Success)
            {
                break;
            }

            var key = m.Groups["idx"].Value;
            var braceIdx = m.Index + m.Length - 1;

            var closeIdx = FindMatchingBrace(content, braceIdx);
            if (closeIdx < 0)
            {
                break;
            }

            list.Add((key, content[(braceIdx + 1)..closeIdx]));
            searchPos = closeIdx + 1;
        }

        return list;
    }

    /// <summary>
    /// Finds the index of the closing brace that matches the opening brace at
    /// <paramref name="openBracePos"/>. Correctly skips Lua line comments and string literals.
    /// Returns -1 if no matching brace is found.
    /// </summary>
    private static int FindMatchingBrace(string content, int openBracePos)
    {
        int depth = 1;
        bool inString = false;
        bool inComment = false;

        for (int i = openBracePos + 1; i < content.Length; i++)
        {
            char c = content[i];

            if (inComment)
            {
                if (c == '\n')
                {
                    inComment = false;
                }

                continue;
            }

            // Detect start of Lua line comment (--)
            if (!inString && c == '-' && i + 1 < content.Length && content[i + 1] == '-')
            {
                inComment = true;
                i++; // consume second '-'
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                // Handle Lua escape sequences inside strings so a \" doesn't end the string
                if (c == '\\' && i + 1 < content.Length)
                {
                    i++; // skip escaped character
                }

                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    #endregion

    #region Field extraction helpers

    /// <summary>Returns the string value of <c>["fieldName"] = "value"</c>, or null.</summary>
    private static string? ExtractStringField(string content, string fieldName)
    {
        var m = StringFieldRx.Match(content);
        while (m.Success)
        {
            if (string.Equals(m.Groups["field"].Value, fieldName, StringComparison.Ordinal))
            {
                return m.Groups["value"].Value;
            }

            m = m.NextMatch();
        }

        return null;
    }

    /// <summary>Returns the numeric value of <c>["fieldName"] = 1.23</c>, or null.</summary>
    private static double? ExtractNumberField(string content, string fieldName)
    {
        var m = NumberFieldRx.Match(content);
        while (m.Success)
        {
            if (string.Equals(m.Groups["field"].Value, fieldName, StringComparison.Ordinal))
            {
                if (double.TryParse(m.Groups["value"].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var d))
                {
                    return d;
                }
            }

            m = m.NextMatch();
        }

        return null;
    }

    /// <summary>Returns the bool value of <c>["fieldName"] = true</c>, or null.</summary>
    private static bool? ExtractBoolField(string content, string fieldName)
    {
        var m = BoolFieldRx.Match(content);
        while (m.Success)
        {
            if (string.Equals(m.Groups["field"].Value, fieldName, StringComparison.Ordinal))
            {
                return string.Equals(m.Groups["value"].Value, "true", StringComparison.OrdinalIgnoreCase);
            }

            m = m.NextMatch();
        }

        return null;
    }

    #endregion

    #region Category and formatting helpers

    /// <summary>
    /// Infers a display category for a button binding name using keyword matching.
    /// Returns an empty string when no category can be determined.
    /// </summary>
    private static string InferButtonCategory(string name)
    {
        var lower = name.ToLowerInvariant();
        foreach (var (category, keywords) in CategoryRules)
        {
            if (keywords.Any(kw => lower.Contains(kw)))
            {
                return category;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Infers a display category for an axis binding name.
    /// Most axes are self-explanatory so this uses a simple match.
    /// </summary>
    private static string InferAxisCategory(string name)
    {
        var lower = name.ToLowerInvariant();

        // Check Navigation/Sensors before Flight Controls: "controller" contains the
        // substring "roll", so designator/TDC axes must be matched first.
        if (lower.Contains("designator") || lower.Contains("tdc") || lower.Contains("sensor"))
        {
            return "Navigation / Sensors";
        }

        if (lower.Contains("pitch") || lower.Contains("roll") || lower.Contains("rudder") ||
            lower.Contains("trim") || lower.Contains("collective") || lower.Contains("cyclic"))
        {
            return "Flight Controls";
        }

        if (lower.Contains("throttle") || lower.Contains("thrust"))
        {
            return "Throttle";
        }

        return string.Empty;
    }

    /// <summary>
    /// Produces a compact, human-readable summary of axis filter settings,
    /// omitting defaults (curvature = 0, deadzone = 0, not inverted).
    /// </summary>
    private static string FormatFilter(DcsAxisFilter? filter)
    {
        if (filter is null)
        {
            return string.Empty;
        }

        var parts = new List<string>();

        if (filter.Curvature != 0)
        {
            parts.Add($"curvature: {filter.Curvature:G4}");
        }

        if (filter.Deadzone != 0)
        {
            parts.Add($"deadzone: {filter.Deadzone:G4}");
        }

        if (filter.Invert)
        {
            parts.Add("inverted");
        }

        if (filter.SaturationX < 1.0)
        {
            parts.Add($"satX: {filter.SaturationX:G4}");
        }

        if (filter.SaturationY < 1.0)
        {
            parts.Add($"satY: {filter.SaturationY:G4}");
        }

        return string.Join(", ", parts);
    }

    #endregion
}
