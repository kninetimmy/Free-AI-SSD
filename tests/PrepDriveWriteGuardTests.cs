using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

public sealed class PrepDriveWriteGuardTests
{
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsWriteBlocked_ReflectsExplicitEncryptionState(bool isEncrypted)
    {
        Assert.Equal(isEncrypted, PrepDriveWriteGuard.IsWriteBlocked(isEncrypted));
    }

    [Fact]
    public void BuildBlockedOperationMessage_UsesOperationName()
    {
        var message = PrepDriveWriteGuard.BuildBlockedOperationMessage("Finalize");

        Assert.Equal($"Finalize blocked: {PrepDriveWriteGuard.ReadOnlyReason}", message);
    }

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
