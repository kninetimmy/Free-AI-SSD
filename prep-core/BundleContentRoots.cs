namespace FreeAiSsd.PrepApp;

/// <summary>Yields candidate directory roots that may contain bundled artifacts.</summary>
internal static class BundleContentRoots
{
    /// <summary>
    /// Yields candidate roots starting at <paramref name="baseDirectory"/>, including the
    /// immediate "dependencies" (post-restructure) and legacy "payload" subdirectories, then
    /// repeats that trio for up to 6 ancestor levels. The ancestor walk handles the Mac
    /// PrepApp sidecar case where the process runs from inside
    /// PrepApp.app/Contents/Resources/prep-host/ but bundled artifacts live at the bundle root.
    /// Windows finds artifacts on an early candidate and never enters the ancestor loop.
    /// </summary>
    internal static IEnumerable<string> Enumerate(string baseDirectory)
    {
        yield return baseDirectory;
        yield return Path.Combine(baseDirectory, "dependencies");
        yield return Path.Combine(baseDirectory, "payload");

        DirectoryInfo? cursor;
        try { cursor = new DirectoryInfo(baseDirectory); }
        catch { yield break; }

        for (var i = 0; i < 6 && cursor?.Parent is not null; i++)
        {
            cursor = cursor.Parent;
            yield return cursor.FullName;
            yield return Path.Combine(cursor.FullName, "dependencies");
            yield return Path.Combine(cursor.FullName, "payload");
        }
    }
}
