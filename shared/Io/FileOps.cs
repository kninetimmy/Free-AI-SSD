namespace FreeAiSsd.Shared.Io;

public static class FileOps
{
    // Wraps File.Replace with a short retry. On Windows CI, Defender / the file
    // indexer can briefly hold a handle on the freshly-written source (or the
    // backup target) and cause a spurious sharing violation. File.Replace is a
    // rename — on failure the filesystem state is unchanged, so retry is safe.
    public static void ReplaceWithRetry(string source, string destination, string? backup)
    {
        const int maxAttempts = 5;
        var delayMs = 25;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Replace(source, destination, backup);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
            }
            Thread.Sleep(delayMs);
            delayMs *= 2;
        }
    }
}
