using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

/// <summary>
/// Tests for PrepDriveWriteGuard — the security gate that prevents PrepApp
/// from modifying encrypted SSDs. Covers the "fail closed" security model:
/// corrupt or missing metadata with encrypted artifacts blocks writes;
/// only an explicit "disabled" state with no artifacts allows writes.
/// </summary>
public sealed class PrepDriveWriteGuardTests
{
    /// <summary>
    /// Corrupt state file + encrypted artifact → write blocked (fail closed).
    /// Protects against metadata corruption scenarios.
    /// </summary>
    [Fact]
    public void IsWriteBlocked_WhenMetadataCorruptAndEncryptedArtifactExists_Blocks()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            File.WriteAllText(StatePath(root), "{corrupt");
            File.WriteAllText(EncryptedPath(root), "{}");

            Assert.True(PrepDriveWriteGuard.IsWriteBlocked(root));
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    /// <summary>
    /// Missing state file + encrypted artifact → write blocked (fail closed).
    /// Prevents writes when encryption status is ambiguous.
    /// </summary>
    [Fact]
    public void IsWriteBlocked_WhenMetadataMissingAndEncryptedArtifactExists_Blocks()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            File.WriteAllText(EncryptedPath(root), "{}");

            Assert.True(PrepDriveWriteGuard.IsWriteBlocked(root));
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    /// <summary>
    /// Explicitly disabled state + no encrypted artifacts → writes allowed.
    /// This is the normal unencrypted drive state.
    /// </summary>
    [Fact]
    public void IsWriteBlocked_WhenValidDisabledWithoutEncryptedArtifact_Allows()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            File.WriteAllText(StatePath(root), "{\"enabled\":false}");

            Assert.False(PrepDriveWriteGuard.IsWriteBlocked(root));
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    /// <summary>
    /// Explicitly enabled state → write blocked.
    /// </summary>
    [Fact]
    public void IsWriteBlocked_WhenValidEnabledState_Blocks()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            File.WriteAllText(StatePath(root), "{\"enabled\":true}");

            Assert.True(PrepDriveWriteGuard.IsWriteBlocked(root));
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    /// <summary>
    /// Tests the simplified boolean overload used by PrepApp when encryption
    /// state is already cached in memory.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsWriteBlocked_ReflectsExplicitEncryptionState(bool isEncrypted)
    {
        Assert.Equal(isEncrypted, PrepDriveWriteGuard.IsWriteBlocked(isEncrypted));
    }

    /// <summary>
    /// Verifies the blocked operation message format includes the operation name.
    /// </summary>
    [Fact]
    public void BuildBlockedOperationMessage_UsesOperationName()
    {
        var message = PrepDriveWriteGuard.BuildBlockedOperationMessage("Finalize");

        Assert.Equal($"Finalize blocked: {PrepDriveWriteGuard.ReadOnlyReason}", message);
    }

    /// <summary>
    /// When the operation name is blank, falls back to "Operation blocked".
    /// </summary>
    [Fact]
    public void BuildBlockedOperationMessage_FallsBackWhenOperationMissing()
    {
        var message = PrepDriveWriteGuard.BuildBlockedOperationMessage("   ");

        Assert.Equal($"Operation blocked: {PrepDriveWriteGuard.ReadOnlyReason}", message);
    }

    private static string StatePath(string root) => Path.Combine(root, SsdLayout.Config, SsdEncryption.StateFileName);
    private static string EncryptedPath(string root) => Path.Combine(root, SsdLayout.Config, SsdEncryption.EncryptedConfigFileName);

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "free-ai-ssd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void CleanupTempRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
