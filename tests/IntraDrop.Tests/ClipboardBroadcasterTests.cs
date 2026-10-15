using System.Collections.Concurrent;
using IntraDrop.Core;
using IntraDrop.Models;
using Xunit;

namespace IntraDrop.Tests;

public sealed class ClipboardBroadcasterTests
{
    [Fact]
    public async Task SlowPeerDoesNotDelayHealthyPeer()
    {
        var slowStarted = Signal();
        var releaseSlow = Signal();
        var healthySent = Signal();
        var healthySentAgain = Signal();
        var slow = Peer("slow");
        var healthy = Peer("healthy");
        using var broadcaster = new ClipboardBroadcaster(async (peer, content, _) =>
        {
            if (peer == slow)
            {
                slowStarted.TrySetResult();
                await releaseSlow.Task;
            }
            else if (content.Data[0] == 1) healthySent.TrySetResult();
            else healthySentAgain.TrySetResult();
        }, (_, _) => { });

        broadcaster.Publish([slow, healthy], Content(1));

        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await healthySent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        broadcaster.Publish([slow, healthy], Content(2));
        await healthySentAgain.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseSlow.TrySetResult();
    }

    [Fact]
    public async Task FailedPeerDoesNotStopOtherPeer()
    {
        var completed = new ConcurrentDictionary<string, Exception?>();
        var bothCompleted = Signal();
        var failed = Peer("failed");
        var healthy = Peer("healthy");
        using var broadcaster = new ClipboardBroadcaster((peer, _, _) =>
            peer == failed ? Task.FromException(new InvalidOperationException("offline")) : Task.CompletedTask,
            (peer, error) =>
            {
                completed[peer.Host] = error;
                if (completed.Count == 2) bothCompleted.TrySetResult();
            });

        broadcaster.Publish([failed, healthy], Content(1));
        await bothCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsType<InvalidOperationException>(completed[failed.Host]);
        Assert.Null(completed[healthy.Host]);
    }

    [Fact]
    public async Task BusyPeerSendsCurrentThenLatestPendingCopy()
    {
        var firstStarted = Signal();
        var releaseFirst = Signal();
        var twoSent = Signal();
        var sent = new ConcurrentQueue<byte>();
        var peer = Peer("peer");
        using var broadcaster = new ClipboardBroadcaster(async (_, content, _) =>
        {
            sent.Enqueue(content.Data[0]);
            if (content.Data[0] == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            }
            if (sent.Count == 2) twoSent.TrySetResult();
        }, (_, _) => { });

        broadcaster.Publish([peer], Content(1));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        broadcaster.Publish([peer], Content(2));
        broadcaster.Publish([peer], Content(3));
        releaseFirst.TrySetResult();
        await twoSent.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([1, 3], sent.ToArray());
    }

    [Fact]
    public async Task ClearPendingDropsCopyQueuedBehindActiveSend()
    {
        var firstStarted = Signal();
        var releaseFirst = Signal();
        var firstCompleted = Signal();
        int sends = 0;
        var peer = Peer("peer");
        using var broadcaster = new ClipboardBroadcaster(async (_, _, _) =>
        {
            Interlocked.Increment(ref sends);
            firstStarted.TrySetResult();
            await releaseFirst.Task;
        }, (_, _) => firstCompleted.TrySetResult());

        broadcaster.Publish([peer], Content(1));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        broadcaster.Publish([peer], Content(2));
        broadcaster.ClearPending();
        releaseFirst.TrySetResult();
        await firstCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);

        Assert.Equal(1, Volatile.Read(ref sends));
    }

    [Fact]
    public async Task DisposeCancelsActiveSendAndIgnoresFutureCopies()
    {
        var started = Signal();
        var cancelled = Signal();
        int sends = 0;
        var peer = Peer("peer");
        var broadcaster = new ClipboardBroadcaster(async (_, _, cancellationToken) =>
        {
            Interlocked.Increment(ref sends);
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
        }, (_, _) => { });

        broadcaster.Publish([peer], Content(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        broadcaster.Dispose();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        broadcaster.Publish([peer], Content(2));
        await Task.Delay(100);

        Assert.Equal(1, Volatile.Read(ref sends));
    }

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static PeerInfo Peer(string host) => new()
    {
        Host = host,
        DeviceId = Guid.NewGuid().ToString("N")
    };

    private static ClipboardContent Content(byte value) => new()
    {
        Format = "text",
        Data = [value]
    };
}
