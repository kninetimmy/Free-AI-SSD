using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

public sealed class PrepDriveWriteGuardTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsWriteBlocked_ReflectsEncryptionState(bool isEncrypted)
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
}
