using Typr.Windows;
using Xunit;

namespace Typr.Windows.Tests;

// Pure worker-policy tests: no WPF app/window, clipboard access or native input.
public sealed class ClipboardTransactionTests
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    static ClipboardReplacement Success() => new(true, new(new Dictionary<uint, byte[]> { [13] = [1, 2] }), 123, "");

    [Fact]
    public async Task ActiveCaptureDefersBeforeInspectingAnUnconfirmedDestination()
    {
        var result = await new ClipboardDelivery().DeliverAsync("Queued words", new(0, null, null, false, false),
            CancellationToken.None, captureActive: () => true);
        Assert.True(result.Deferred);
        Assert.False(result.Confirmed);
    }

    [Theory]
    [InlineData(0L, 0U, true)]
    [InlineData(1L, 0U, false)]
    [InlineData(0L, 1U, false)]
    [InlineData(1L, 4U, false)]
    public void DeferralRequiresNoInputAttemptOrSentKeys(long attemptedAt, uint sent, bool expected)
        => Assert.Equal(expected, ClipboardDelivery.CanDeferPaste(attemptedAt, sent));

    [Theory]
    [InlineData(ClipboardRestoreResult.Restored, true)]
    [InlineData(ClipboardRestoreResult.ChangedByUser, true)]
    [InlineData(ClipboardRestoreResult.BusyOrFailed, false)]
    internal async Task DeferralWaitsForOwnedClipboardCleanupAndFailedCleanupCannotRetry(ClipboardRestoreResult restoration, bool deferred)
    {
        var cleanup = new TaskCompletionSource<ClipboardRestoreResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int restores = 0;
        var pending = ClipboardDelivery.CompleteDeferredPasteAsync(() => { ++restores; return cleanup.Task; });
        Assert.Equal(1, restores);
        Assert.False(pending.IsCompleted);
        cleanup.SetResult(restoration);
        var result = await pending.WaitAsync(Timeout);
        Assert.Equal(deferred, result.Deferred);
        Assert.False(result.Confirmed);
        if (!deferred) Assert.Contains("couldn't be restored", result.Message);
        Assert.Equal(1, restores);
    }

    [Fact]
    public async Task DelayedReadTimesOutAndCannotWriteWhenItFinallyReturns()
    {
        using var release = new ManualResetEventSlim();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int writes = 0, rollbacks = 0;
        var transaction = SafeClipboard.RunTransactionAsync(permission => {
            reading.TrySetResult();
            try {
                if (!release.Wait(Timeout)) throw new TimeoutException("Synthetic renderer didn't release.");
                if (permission.TryBeginWrite()) Interlocked.Increment(ref writes);
                return Success();
            } catch (Exception error) { finished.TrySetException(error); throw; }
            finally { finished.TrySetResult(); }
        }, _ => { Interlocked.Increment(ref rollbacks); return ClipboardRestoreResult.Restored; }, CancellationToken.None, 100);
        try {
            await reading.Task.WaitAsync(Timeout);
            var result = await transaction.WaitAsync(Timeout);
            Assert.False(result.Replaced);
            Assert.Contains("in time", result.Reason);
            Assert.False(finished.Task.IsCompleted);
        } finally { release.Set(); }
        await finished.Task.WaitAsync(Timeout);
        Assert.Equal(0, writes);
        Assert.Equal(0, rollbacks);
    }

    [Fact]
    public async Task CancelledReadReturnsPromptlyAndCannotOverwriteClipboardLater()
    {
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int writes = 0;
        var transaction = SafeClipboard.RunTransactionAsync(permission => {
            reading.TrySetResult();
            try {
                if (!release.Wait(Timeout)) throw new TimeoutException();
                Assert.False(permission.CanRead);
                if (permission.TryBeginWrite()) Interlocked.Increment(ref writes);
                return Success();
            } catch (Exception error) { finished.TrySetException(error); throw; }
            finally { finished.TrySetResult(); }
        }, _ => throw new InvalidOperationException("An uncommitted read must never restore."), cancellation.Token, 2000);
        try {
            await reading.Task.WaitAsync(Timeout);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transaction.WaitAsync(Timeout));
            Assert.False(finished.Task.IsCompleted);
        } finally { release.Set(); }
        await finished.Task.WaitAsync(Timeout);
        Assert.Equal(0, writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartedMutationFinishesAndRestoresBeforeTimeoutOrCancellationReturns(bool cancel)
    {
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int rollbacks = 0;
        var transaction = SafeClipboard.RunTransactionAsync(permission => {
            Assert.True(permission.TryBeginWrite());
            writing.TrySetResult();
            if (!release.Wait(Timeout)) throw new TimeoutException();
            return Success();
        }, result => {
            Assert.Equal((uint)123, result.Sequence);
            Assert.Equal(new byte[] { 1, 2 }, result.Previous.Formats[13]);
            Interlocked.Increment(ref rollbacks);
            return ClipboardRestoreResult.Restored;
        }, cancellation.Token, cancel ? 2000 : 500);
        try {
            await writing.Task.WaitAsync(Timeout);
            if (cancel) cancellation.Cancel();
            await Task.Delay(cancel ? 100 : 650);
            Assert.False(transaction.IsCompleted); // No failure is reported before committed work is cleaned up.
        } finally { release.Set(); }
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transaction.WaitAsync(Timeout));
        else Assert.False((await transaction.WaitAsync(Timeout)).Replaced);
        Assert.Equal(1, rollbacks);
    }

    [Fact]
    public async Task FastReplacementKeepsSnapshotForNormalDeliveryWithoutRollback()
    {
        int rollbacks = 0;
        var result = await SafeClipboard.RunTransactionAsync(permission => {
            Assert.True(permission.CanRead);
            Assert.True(permission.TryBeginWrite());
            return Success();
        }, _ => { Interlocked.Increment(ref rollbacks); return ClipboardRestoreResult.Restored; }, CancellationToken.None, 2000);
        Assert.True(result.Replaced);
        Assert.Equal((uint)123, result.Sequence);
        Assert.Equal(0, rollbacks);
    }

    [Theory]
    [InlineData(ClipboardRestoreResult.BusyOrFailed)]
    [InlineData(ClipboardRestoreResult.Restored)]
    [InlineData(ClipboardRestoreResult.ChangedByUser)]
    internal async Task CancelledWriteRetainsFailedCleanupAndReportsTheRecoveryOutcome(ClipboardRestoreResult recovery)
    {
        using var cancellation = new CancellationTokenSource();
        int rollbacks = 0, retries = 0;
        var error = await Assert.ThrowsAsync<ClipboardCleanupCanceledException>(() => SafeClipboard.RunTransactionAsync(permission => {
            Assert.True(permission.TryBeginWrite());
            cancellation.Cancel();
            return Success();
        }, _ => { Interlocked.Increment(ref rollbacks); return ClipboardRestoreResult.BusyOrFailed; }, cancellation.Token, 2000));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal((uint)123, error.Replacement.Sequence);
        Assert.Equal(new byte[] { 1, 2 }, error.Replacement.Previous.Formats[13]);
        var report = await ClipboardDelivery.RecoverCancelledClipboardAsync(error, replacement => {
            Assert.Same(error.Replacement, replacement);
            Interlocked.Increment(ref retries);
            return Task.FromResult(recovery);
        });
        Assert.False(report.Result.Confirmed);
        Assert.Contains("cancelled", report.Result.Message);
        if (recovery == ClipboardRestoreResult.BusyOrFailed) {
            Assert.Contains("previous clipboard couldn't be restored", report.Result.Message);
            Assert.Equal("cancelled-clipboard-restore-failed", report.Outcome);
        } else Assert.DoesNotContain("couldn't be restored", report.Result.Message);
        Assert.Equal(1, rollbacks); Assert.Equal(1, retries);
    }
}
