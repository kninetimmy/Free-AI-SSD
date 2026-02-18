namespace FreeAiSsd.Shared;

public static class PathGuards
{
    public static string EnsureUnderRoot(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("Root path is required.", nameof(root));
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path is required.", nameof(path));
        }

        var normalizedRoot = Path.GetFullPath(root);
        var normalizedPath = Path.GetFullPath(path);
        if (!IsPathUnderRoot(normalizedRoot, normalizedPath, OperatingSystem.IsWindows()))
        {
            throw new InvalidOperationException("Path escapes expected root.");
        }

        EnsureNoReparsePoint(normalizedPath, normalizedRoot);
        return normalizedPath;
    }

    internal static bool IsPathUnderRoot(string normalizedRoot, string normalizedPath, bool isWindows)
    {
        var comparison = isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;

        return normalizedPath.StartsWith(rootWithSeparator, comparison);
    }

    private static void EnsureNoReparsePoint(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var current = new DirectoryInfo(Path.GetDirectoryName(path) ?? root);
        while (current is not null)
        {
            if (current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException($"Path contains a reparse point: {current.FullName}");
            }

            if (string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), comparison))
            {
                break;
            }

            current = current.Parent;
        }

        if (File.Exists(path))
        {
            var fileAttributes = File.GetAttributes(path);
            if ((fileAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("File is a reparse point.");
            }
        }
    }
}
