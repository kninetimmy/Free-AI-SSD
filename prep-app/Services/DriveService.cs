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

    public async Task FormatAsync(string rootPath, string label, string fileSystem, Action<string>? onOutput, CancellationToken ct)
    {
        var built = DriveFormatCommand.Build(rootPath, label, fileSystem);

        // Buffer the last N lines so a non-zero exit surfaces a meaningful
        // error instead of just "exit code 1". ProcessRunner merges stdout
        // and stderr into onOutput so we capture both here.
        var tail = new System.Collections.Generic.Queue<string>(OutputTailLines + 1);
        void Capture(string line)
        {
            tail.Enqueue(line);
            while (tail.Count > OutputTailLines) tail.Dequeue();
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

        if (exitCode != 0)
        {
            var detail = tail.Count == 0 ? "(no output)" : string.Join(Environment.NewLine, tail);
            throw new InvalidOperationException(
                $"Format-Volume failed on {rootPath} (exit {exitCode}).{Environment.NewLine}{detail}");
        }
    }
}
