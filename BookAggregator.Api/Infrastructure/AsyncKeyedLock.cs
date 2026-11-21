namespace BookAggregator.Api.Infrastructure;

// References include both holders and waiters, so gates can safely be removed after use.
internal sealed class AsyncKeyedLock
{
    private readonly Dictionary<string, Gate> _gates = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public async Task<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken)
    {
        Gate gate;
        lock (_sync)
        {
            if (!_gates.TryGetValue(key, out gate!)) _gates[key] = gate = new Gate();
            gate.References++;
        }

        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken);
            return new Lease(() => Release(key, gate, acquired: true));
        }
        catch
        {
            Release(key, gate, acquired: false);
            throw;
        }
    }

    private void Release(string key, Gate gate, bool acquired)
    {
        lock (_sync)
        {
            if (acquired) gate.Semaphore.Release();
            if (--gate.References != 0) return;
            _gates.Remove(key);
            gate.Semaphore.Dispose();
        }
    }

    private sealed class Gate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int References;
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
