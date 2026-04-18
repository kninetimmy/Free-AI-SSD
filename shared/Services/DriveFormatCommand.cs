using System.Collections.Generic;
using System.IO;

namespace FreeAiSsd.Shared.Services;

/// <summary>
/// Pure builder that turns a drive root / label / filesystem into a
/// safe powershell.exe invocation. Label is passed via environment
/// variable (not string-concat into the command) so arbitrary user
/// input can never be interpreted as PowerShell.
/// </summary>
public static class DriveFormatCommand
{
    public const string LabelEnvVar = "FREEAI_FORMAT_LABEL";
    public const int MaxLabelLength = 32;
    public const string DefaultFileSystem = "NTFS";

    public readonly record struct Built(
        string FileName,
        IReadOnlyList<string> Arguments,
        IReadOnlyDictionary<string, string> Environment,
        char DriveLetter);

    /// <summary>
    /// Builds the command. Throws ArgumentException on invalid input.
    /// </summary>
    /// <param name="rootPath">Drive root like "D:\" or "D:".</param>
    /// <param name="label">Volume label (will be sanitized; empty allowed).</param>
    /// <param name="fileSystem">"NTFS" (the only currently supported value).</param>
    public static Built Build(string rootPath, string label, string fileSystem)
    {
        var driveLetter = ParseDriveLetter(rootPath);
        var normalizedFs = NormalizeFileSystem(fileSystem);
        var sanitizedLabel = SanitizeLabel(label);

        var args = new List<string>
        {
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy", "Bypass",
            "-Command",
            "$ErrorActionPreference='Stop'; " +
            $"Format-Volume -DriveLetter {driveLetter} " +
            $"-FileSystem {normalizedFs} " +
            $"-NewFileSystemLabel $env:{LabelEnvVar} " +
            "-Confirm:$false -Force | Out-Null"
        };

        var env = new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            [LabelEnvVar] = sanitizedLabel
        };

        // Absolute path to System32\powershell.exe — never a bare name.
        // With UseShellExecute=false (what ProcessRunner uses), Windows
        // CreateProcess searches the app's load directory before System32,
        // so a bare "powershell.exe" could resolve to an attacker-planted
        // binary co-located with PrepApp and run with administrator
        // privileges (Format & Prepare requires UAC elevation).
        var powershellPath = Path.Combine(System.Environment.SystemDirectory,
            "WindowsPowerShell", "v1.0", "powershell.exe");

        return new Built(powershellPath, args, env, driveLetter);
    }

    /// <summary>
    /// Diagnostic-only helper. Renders a Built command as a human-readable
    /// multi-line string safe to write to the UI log or a diagnostic file.
    /// Env values are quoted so empty strings / whitespace are visible.
    /// </summary>
    public static string Describe(Built built)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("FileName     : ").AppendLine(built.FileName);
        sb.Append("DriveLetter  : ").Append(built.DriveLetter).AppendLine();
        sb.Append("Arguments    : ").AppendLine(string.Join(" ", built.Arguments));
        sb.AppendLine("Environment  :");
        foreach (var kv in built.Environment)
        {
            sb.Append("  ").Append(kv.Key).Append(" = \"").Append(kv.Value).AppendLine("\"");
        }
        return sb.ToString().TrimEnd();
    }

    public static char ParseDriveLetter(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new System.ArgumentException("Drive root path is empty.", nameof(rootPath));

        var trimmed = rootPath.Trim();
        if (trimmed.Length < 2 || trimmed[1] != ':')
            throw new System.ArgumentException($"Drive root must start with a drive letter followed by ':' (got '{rootPath}').", nameof(rootPath));

        var letter = char.ToUpperInvariant(trimmed[0]);
        if (letter < 'A' || letter > 'Z')
            throw new System.ArgumentException($"Drive letter '{trimmed[0]}' is not A–Z.", nameof(rootPath));

        return letter;
    }

    internal static string NormalizeFileSystem(string fileSystem)
    {
        if (string.IsNullOrWhiteSpace(fileSystem))
            return DefaultFileSystem;

        var upper = fileSystem.Trim().ToUpperInvariant();
        if (upper != "NTFS")
            throw new System.ArgumentException($"Unsupported file system '{fileSystem}'. Only NTFS is supported.", nameof(fileSystem));

        return upper;
    }

    public static string SanitizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return string.Empty;

        var trimmed = label.Trim();
        var cleaned = new System.Text.StringBuilder(trimmed.Length);
        foreach (var c in trimmed)
        {
            if (char.IsControl(c)) continue;
            cleaned.Append(c);
            if (cleaned.Length >= MaxLabelLength) break;
        }
        return cleaned.ToString();
    }
}
