using System.Collections.Generic;

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

        return new Built("powershell.exe", args, env, driveLetter);
    }

    internal static char ParseDriveLetter(string rootPath)
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

    internal static string SanitizeLabel(string? label)
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
