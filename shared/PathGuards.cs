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

        var rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;

        if (!normalizedPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Path escapes expected root.");
        }

        EnsureNoReparsePoint(normalizedPath, normalizedRoot);
        return normalizedPath;
    }

    private static void EnsureNoReparsePoint(string path, string root)
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(path) ?? root);
        while (current is not null)
        {
            if (current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException($"Path contains a reparse point: {current.FullName}");
            }

            if (string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
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
