using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.PrepApp;

/// <summary>
/// Parsed command-line intent for PrepApp. Treats every value as
/// untrusted input arriving across the UAC boundary: the elevated
/// instance is reachable via any privileged shortcut, so values are
/// re-validated with the same guards the Format-Volume path uses.
/// Invalid values degrade to "no intent" — never crash, never format.
/// </summary>
public sealed record PrepStartupArgs(
    string? AutoResumeFormatRoot,
    string AutoResumeLabel,
    bool DiagEnabled)
{
    public static readonly PrepStartupArgs Empty = new(null, string.Empty, false);

    public bool HasAutoResumeIntent =>
        !string.IsNullOrEmpty(AutoResumeFormatRoot);

    public const string AutoResumeFormatFlag = "--autoresume-format";
    public const string AutoResumeLabelFlag = "--autoresume-label";
    public const string DiagFlag = "--diag";

    public static PrepStartupArgs Parse(string[]? args)
    {
        if (args is null || args.Length == 0) return Empty;

        string? rawRoot = null;
        string? rawLabel = null;
        var diag = false;

        foreach (var arg in args)
        {
            if (string.IsNullOrEmpty(arg)) continue;

            if (string.Equals(arg, DiagFlag, StringComparison.OrdinalIgnoreCase))
            {
                diag = true;
                continue;
            }

            if (TryMatchKeyValue(arg, AutoResumeFormatFlag, out var rootValue))
            {
                rawRoot = rootValue;
                continue;
            }

            if (TryMatchKeyValue(arg, AutoResumeLabelFlag, out var labelValue))
            {
                rawLabel = labelValue;
                continue;
            }

            // Unknown arg — ignore silently. PrepApp is launched by
            // end users; a typo shouldn't crash the app or refuse
            // to show the UI.
        }

        // Re-validate root via the same parser Format-Volume uses.
        // Any failure → drop intent. Never accept a partially-valid
        // --autoresume-format (e.g. no label) because the whole
        // auto-resume flow presumes both inputs arrived intact.
        string? validatedRoot = null;
        var validatedLabel = string.Empty;
        if (!string.IsNullOrWhiteSpace(rawRoot))
        {
            try
            {
                var letter = DriveFormatCommand.ParseDriveLetter(rawRoot);
                validatedRoot = $"{letter}:\\";
                validatedLabel = DriveFormatCommand.SanitizeLabel(rawLabel);
            }
            catch (ArgumentException)
            {
                validatedRoot = null;
                validatedLabel = string.Empty;
            }
        }

        return new PrepStartupArgs(validatedRoot, validatedLabel, diag);
    }

    /// <summary>
    /// Builds the arg list to forward when triggering a UAC relaunch.
    /// Consumed by <c>WindowsElevationService.TryRelaunchElevated</c>.
    /// </summary>
    public static IReadOnlyList<string> BuildRelaunchArgs(
        string autoResumeRoot,
        string autoResumeLabel,
        bool includeDiag)
    {
        var list = new List<string>(3)
        {
            $"{AutoResumeFormatFlag}={autoResumeRoot}",
            $"{AutoResumeLabelFlag}={autoResumeLabel ?? string.Empty}"
        };
        if (includeDiag) list.Add(DiagFlag);
        return list;
    }

    private static bool TryMatchKeyValue(string arg, string flag, out string value)
    {
        // Only --flag=value form supported. Not --flag value (two tokens)
        // because keeping value bound to the flag makes the parser
        // tolerant of unknown args appearing between them.
        var prefix = flag + "=";
        if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = arg[prefix.Length..];
            return true;
        }
        value = string.Empty;
        return false;
    }
}
