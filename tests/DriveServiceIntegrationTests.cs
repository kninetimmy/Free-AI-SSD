using System.IO;
using FreeAiSsd.PrepApp.Services;
using FreeAiSsd.Shared;
using Xunit;

namespace FreeAiSsd.Tests;

/// <summary>
/// Real-process integration test for <see cref="DriveService.FormatAsync"/>.
/// Creates a VHDX via diskpart, mounts it, and runs the production
/// format path end-to-end through <c>ProcessRunner</c> — no mocks.
/// The mock-only suite masked the v1.2.1 B3 regression (see the
/// <c>feedback_integration_tests_for_shellouts</c> memory); this test
/// is the guard against that class of bug slipping through again.
///
/// Skips cleanly on non-admin / non-Windows agents via
/// <see cref="AdminFactAttribute"/>.
/// </summary>
public sealed class DriveServiceIntegrationTests : IDisposable
{
    private readonly string? _vhdPath;
    private readonly char _letter;
    private readonly bool _mounted;

    public DriveServiceIntegrationTests()
    {
        if (!OperatingSystem.IsWindows() || !IsCurrentProcessAdmin())
        {
            // AdminFact skips the test methods, but xUnit still runs
            // the constructor. Bail out here so we don't touch disk
            // on non-admin CI agents.
            _letter = '\0';
            return;
        }

        _letter = FindFreeDriveLetter();
        _vhdPath = Path.Combine(Path.GetTempPath(), $"freeai-format-int-{Guid.NewGuid():N}.vhdx");

        // Script is written to a temp file and invoked via diskpart /s
        // rather than piping stdin — diskpart is finicky about input.
        // VHDX is 128 MB: large enough for NTFS, small enough to not
        // thrash the test agent's disk.
        var initScript = $@"create vdisk file=""{_vhdPath}"" maximum=128 type=expandable
select vdisk file=""{_vhdPath}""
attach vdisk
create partition primary
format fs=ntfs label=""Initial"" quick
assign letter={_letter}
exit
";

        try
        {
            RunDiskpart(initScript);
            _mounted = true;
        }
        catch
        {
            // Best-effort cleanup: if attach partially succeeded
            // before a later command failed, try to detach so we
            // don't leak a mount across test runs.
            if (_vhdPath is not null)
            {
                TryDetach(_vhdPath);
                if (File.Exists(_vhdPath)) File.Delete(_vhdPath);
            }
            throw;
        }
    }

    public void Dispose()
    {
        if (_mounted && _vhdPath is not null)
        {
            TryDetach(_vhdPath);
        }
        if (_vhdPath is not null && File.Exists(_vhdPath))
        {
            try { File.Delete(_vhdPath); } catch { /* best-effort */ }
        }
    }

    [AdminFact]
    public async Task FormatAsync_RealProcessRunner_ChangesLabelOnDisk()
    {
        // Pre-condition: diskpart set the initial label to "Initial".
        // DriveInfo reads the label from the filesystem — this is the
        // exact same data path PrepApp uses after re-enumeration.
        var root = $"{_letter}:\\";
        var preInfo = new DriveInfo(root);
        Assert.Equal("Initial", preInfo.VolumeLabel);

        var svc = new DriveService();
        const string newLabel = "FreeAiTest";
        await svc.FormatAsync(
            root,
            newLabel,
            fileSystem: "NTFS",
            onOutput: _ => { },
            ct: CancellationToken.None);

        // Re-read via a fresh DriveInfo — NTFS label change is visible
        // immediately without re-mount. A mock that returns
        // Task.CompletedTask would leave the label unchanged and fail
        // this assertion, which is precisely the regression guard.
        var postInfo = new DriveInfo(root);
        Assert.Equal(newLabel, postInfo.VolumeLabel);
    }

    [AdminFact]
    public async Task FormatAsync_RealProcessRunner_RespectsSanitizedLabel()
    {
        // Labels are capped at DriveFormatCommand.MaxLabelLength (32).
        // Pass a longer input and confirm the on-disk label is the
        // truncated form — verifies end-to-end that SanitizeLabel
        // survives the trip through the env-var indirection.
        var root = $"{_letter}:\\";
        var longLabel = new string('x', 50);
        var expected = new string('x', 32);

        var svc = new DriveService();
        await svc.FormatAsync(
            root,
            longLabel,
            fileSystem: "NTFS",
            onOutput: _ => { },
            ct: CancellationToken.None);

        Assert.Equal(expected, new DriveInfo(root).VolumeLabel);
    }

    // ────────── helpers ──────────

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool IsCurrentProcessAdmin()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(id);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static char FindFreeDriveLetter()
    {
        var used = DriveInfo.GetDrives()
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .ToHashSet();
        // Start high and walk down to avoid collisions with typical
        // system (C), network share (N/Z), and optical-drive letters.
        for (var c = 'Y'; c >= 'H'; c--)
        {
            if (!used.Contains(c)) return c;
        }
        throw new InvalidOperationException("No free drive letter available for integration test.");
    }

    private static void RunDiskpart(string script)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"freeai-diskpart-{Guid.NewGuid():N}.txt");
        File.WriteAllText(scriptPath, script);
        var output = new List<string>();
        try
        {
            var diskpartPath = Path.Combine(Environment.SystemDirectory, "diskpart.exe");
            var exit = ProcessRunner.RunAsync(
                diskpartPath,
                new[] { "/s", scriptPath },
                workingDirectory: Environment.SystemDirectory,
                env: null,
                onOutput: output.Add,
                ct: CancellationToken.None).GetAwaiter().GetResult();

            if (exit != 0)
            {
                throw new InvalidOperationException(
                    $"diskpart exited with code {exit}. Output:{Environment.NewLine}" +
                    string.Join(Environment.NewLine, output));
            }
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* best-effort */ }
        }
    }

    private static void TryDetach(string vhdPath)
    {
        try
        {
            RunDiskpart($@"select vdisk file=""{vhdPath}""
detach vdisk
exit
");
        }
        catch { /* best-effort teardown — don't mask the test failure */ }
    }
}
