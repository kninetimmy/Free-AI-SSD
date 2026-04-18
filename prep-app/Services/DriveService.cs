using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.PrepApp.Services;

public sealed class DriveService : IDriveService
{
    private const int OutputTailLines = 10;

    public IReadOnlyList<DriveTarget> GetCandidateDrives(bool includeFixed)
        => DriveInspector.GetCandidateDrives(includeFixed);

    public bool IsDriveEncrypted(string rootPath)
        => SsdEncryption.IsEffectivelyEncryptedForWriteGuard(rootPath);

    public bool EnsureWritable(string rootPath, string operationName, out string? blockedMessage)
    {
        var isEncrypted = SsdEncryption.IsEffectivelyEncryptedForWriteGuard(rootPath);
        if (!PrepDriveWriteGuard.IsWriteBlocked(isEncrypted))
        {
            blockedMessage = null;
            return true;
        }

        blockedMessage = PrepDriveWriteGuard.BuildBlockedOperationMessage(operationName);
        return false;
    }

    public int? GetFreeDiskSpaceGb(string rootPath)
        => SystemResources.GetFreeDiskSpaceGb(rootPath);

    public void EnsureSsdStructure(string rootPath)
        => SsdLayout.EnsureStructure(rootPath);

    public async Task FormatAsync(
        string rootPath,
        string label,
        string fileSystem,
        Action<string>? onOutput,
        CancellationToken ct,
        bool verboseDiagnostics = false)
    {
        var built = DriveFormatCommand.Build(rootPath, label, fileSystem);

        // Verbose diagnostic prelude — only emitted when the caller opted
        // in via --diag. Default runs keep the log clean; the per-line
        // stdout/stderr forwarding and exit-code line below always fire
        // regardless, so non-zero exits still get useful context.
        if (verboseDiagnostics)
        {
            onOutput?.Invoke("--- B3-Redux diagnostic: Format-Volume invocation ---");
            onOutput?.Invoke(DriveFormatCommand.Describe(built));
            onOutput?.Invoke($"WorkingDir   : {Environment.SystemDirectory}");
            onOutput?.Invoke($"RootPath arg : {rootPath}");
            onOutput?.Invoke($"Label arg    : \"{label}\" (length {label?.Length ?? 0})");
            onOutput?.Invoke($"FileSystem   : {fileSystem}");
            onOutput?.Invoke("--- Launching process; stdout+stderr follow ---");
        }

        // Capture every line so non-zero exits can surface a meaningful
        // tail. Always-on — the capture is in-memory and cheap.
        var allOutput = new System.Collections.Generic.List<string>();
        void Capture(string line)
        {
            allOutput.Add(line);
            onOutput?.Invoke(line);
        }

        var envDict = new Dictionary<string, string>(built.Environment, StringComparer.Ordinal);
        var exitCode = await ProcessRunner.RunAsync(
            built.FileName,
            built.Arguments,
            workingDirectory: Environment.SystemDirectory,
            env: envDict,
            onOutput: Capture,
            ct: ct);

        // Log exit code explicitly on every path — the previous code only
        // surfaced it on non-zero, so a silent success masked the fact
        // that Format-Volume returned 0 without actually formatting.
        if (verboseDiagnostics)
        {
            onOutput?.Invoke($"--- Process exited with code {exitCode}. {allOutput.Count} output line(s) captured. ---");
        }

        if (exitCode != 0)
        {
            var tail = allOutput.Count <= OutputTailLines
                ? allOutput
                : allOutput.GetRange(allOutput.Count - OutputTailLines, OutputTailLines);
            var detail = tail.Count == 0 ? "(no output)" : string.Join(Environment.NewLine, tail);
            throw new InvalidOperationException(
                $"Format-Volume failed on {rootPath} (exit {exitCode}).{Environment.NewLine}{detail}");
        }
    }
}
