using System;
using System.Linq;
using System.Threading.Tasks;
using FreeAiSsd.Runner.Services;
using Xunit;

namespace FreeAiSsd.Tests;

/// <summary>
/// Unit tests for <see cref="IndexingActivity"/>, the in-flight indexing signal
/// that lets the chat path warn callers and /health report a mid-index library
/// (task #99).
/// </summary>
public sealed class IndexingActivityTests
{
    [Fact]
    public void FreshTracker_IsNotInProgress()
    {
        var activity = new IndexingActivity();
        Assert.False(activity.InProgress);
        Assert.Equal(0, activity.ActiveCount);
    }

    [Fact]
    public void Begin_MarksInProgress_DisposeClearsIt()
    {
        var activity = new IndexingActivity();

        var scope = activity.Begin();
        Assert.True(activity.InProgress);
        Assert.Equal(1, activity.ActiveCount);

        scope.Dispose();
        Assert.False(activity.InProgress);
        Assert.Equal(0, activity.ActiveCount);
    }

    [Fact]
    public void OverlappingScopes_StayInProgressUntilLastDisposes()
    {
        var activity = new IndexingActivity();

        var first = activity.Begin();
        var second = activity.Begin();
        Assert.Equal(2, activity.ActiveCount);

        first.Dispose();
        Assert.True(activity.InProgress);
        Assert.Equal(1, activity.ActiveCount);

        second.Dispose();
        Assert.False(activity.InProgress);
    }

    [Fact]
    public void DoubleDispose_IsIdempotent_DoesNotGoNegative()
    {
        var activity = new IndexingActivity();

        var scope = activity.Begin();
        scope.Dispose();
        scope.Dispose(); // second dispose must be a no-op

        Assert.Equal(0, activity.ActiveCount);
        Assert.False(activity.InProgress);
    }

    [Fact]
    public async Task ConcurrentBeginDispose_BalancesToZero()
    {
        var activity = new IndexingActivity();

        var tasks = Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                using var _ = activity.Begin();
            }
        }));

        await Task.WhenAll(tasks);

        Assert.Equal(0, activity.ActiveCount);
        Assert.False(activity.InProgress);
    }
}
