namespace FreeAiSsd.Shared;

/// <summary>
/// Security utility that prevents path traversal attacks by ensuring file paths
/// remain within an expected root directory. Also detects symbolic links and
/// junction points (reparse points) that could redirect access outside the root.
/// </summary>
public static class PathGuards
{
    /// <summary>
    /// Validates and normalizes a path, ensuring it resides under the specified root.
    /// Throws if the path escapes the root directory or traverses a reparse point (symlink/junction).
    /// </summary>
    /// <param name="root">The trusted root directory that the path must be under.</param>
    /// <param name="path">The path to validate.</param>
    /// <returns>The fully normalized, validated path.</returns>
    /// <exception cref="ArgumentException">Thrown if root or path is empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the path escapes the root or contains a reparse point.</exception>
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

        // Check that the normalized path starts with the root directory.
        if (!IsPathUnderRoot(normalizedRoot, normalizedPath, OperatingSystem.IsWindows()))
        {
            throw new InvalidOperationException("Path escapes expected root.");
        }

        // Walk up the directory tree to detect any symlinks or junction points.
        EnsureNoReparsePoint(normalizedPath, normalizedRoot);
        return normalizedPath;
    }

    /// <summary>
    /// Checks whether a normalized path starts with a normalized root prefix.
    /// Uses case-insensitive comparison on Windows and case-sensitive on other platforms
    /// to match the filesystem's case sensitivity behavior.
    /// </summary>
    /// <param name="normalizedRoot">The fully resolved root directory path.</param>
    /// <param name="normalizedPath">The fully resolved path to check.</param>
    /// <param name="isWindows">Whether to use Windows case-insensitive comparison.</param>
    /// <returns>True if the path is a descendant of the root directory.</returns>
    internal static bool IsPathUnderRoot(string normalizedRoot, string normalizedPath, bool isWindows)
    {
        var comparison = isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        // Ensure root ends with a directory separator to prevent
        // "/root" matching "/root2/file" (sibling directory, not child).
        var rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;

        return normalizedPath.StartsWith(rootWithSeparator, comparison);
    }

    /// <summary>
    /// Walks the directory hierarchy from the target path up to the root,
    /// checking each directory (and the file itself) for reparse points
    /// (symlinks, junctions) that could redirect access outside the root.
    /// </summary>
    /// <param name="path">The target file or directory path to check.</param>
    /// <param name="root">The root boundary; traversal stops here.</param>
    /// <exception cref="InvalidOperationException">Thrown if a reparse point is detected.</exception>
    private static void EnsureNoReparsePoint(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var current = new DirectoryInfo(Path.GetDirectoryName(path) ?? root);

        // Walk up from the parent directory to the root, checking each level.
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

        // Also check the file itself for reparse point attributes.
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
