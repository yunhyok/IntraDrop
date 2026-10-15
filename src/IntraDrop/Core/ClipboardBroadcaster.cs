using IntraDrop.Models;

namespace IntraDrop.Core;

internal sealed class ClipboardBroadcaster : IDisposable
{
    private sealed class PendingSend
    {
        public PeerInfo Peer { get; init; } = null!;
        public ClipboardContent Content { get; init; } = null!;
    }

    private sealed class PeerQueue
    {
        public PendingSend? Pending;
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, PeerQueue> _queues = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<PeerInfo, ClipboardContent, CancellationToken, Task> _send;
    private readonly Action<PeerInfo, Exception?> _completed;

    public ClipboardBroadcaster(Func<PeerInfo, ClipboardContent, CancellationToken, Task> send,
        Action<PeerInfo, Exception?> completed)
    {
        _send = send;
        _completed = completed;
    }

    public void Publish(IReadOnlyList<PeerInfo> peers, ClipboardContent content)
    {
        lock (_sync)
        {
            if (_lifetime.IsCancellationRequested) return;
            ClearPending();
            foreach (var peer in peers)
            {
                string? key = ExplorerContextMenu.TokenFor(peer);
                if (key == null) continue;
                bool running = _queues.TryGetValue(key, out var queue);
                if (!running) _queues[key] = queue = new PeerQueue();
                // One in-flight transfer and only the latest pending copy per computer.
                queue!.Pending = new PendingSend { Peer = peer, Content = content };
                if (!running) _ = Task.Run(() => DrainAsync(key, queue));
            }
        }
    }

    public void ClearPending()
    {
        lock (_sync)
            foreach (var queue in _queues.Values) queue.Pending = null;
    }

    private async Task DrainAsync(string key, PeerQueue queue)
    {
        while (true)
        {
            PendingSend? pending;
            lock (_sync)
            {
                pending = queue.Pending;
                queue.Pending = null;
                if (pending == null || _lifetime.IsCancellationRequested)
                {
                    _queues.Remove(key);
                    return;
                }
            }
            Exception? error = null;
            try { await _send(pending.Peer, pending.Content, _lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex) { error = ex; }
            if (!_lifetime.IsCancellationRequested) _completed(pending.Peer, error);
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        ClearPending();
    }
}
