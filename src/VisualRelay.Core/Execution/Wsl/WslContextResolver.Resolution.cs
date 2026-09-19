namespace VisualRelay.Core.Execution.Wsl;

public static partial class WslContextResolver
{
    /// <summary>
    /// One resolution's state: the memoised probe, the override, and the last probe the
    /// memo ran. The application has one; each <see cref="IsolateForTests"/> scope has
    /// its own, so nothing a test sets can reach another test.
    /// <para>
    /// The memo is fixed when the resolution is built and never replaced, which also
    /// retires two earlier races. It used to be a swappable field read outside the lock
    /// that wrote it (a caller could resolve the previous memo, on Windows a real
    /// wsl.exe probe), and before that the prober lived in a field of its own and was
    /// looked up when the memo first resolved rather than when it was built.
    /// </para>
    /// </summary>
    private sealed class Resolution
    {
        private readonly object _gate = new();
        private WslContext? _override;
        private WslProbe? _lastProbe;

        public Resolution(Func<CancellationToken, Task<WslProbe>> prober, WslContext? @override)
        {
            _override = @override;
            // The one probe, memoized as a TASK both accessors share. It is started on the
            // thread pool because the blocking accessor is reachable from the UI thread:
            // probing inline would make the UI thread the continuation its own awaits are
            // queued to, and the wait would never end.
            // PreferFairness sends it to the pool's global queue. Task.Run from a pool
            // thread queues to that thread's OWN local queue, which other threads take from
            // only when the global queue is empty, and the blocking accessor then parks the
            // very thread that holds it: a stall of seconds under load. The headless UI
            // thread in the test suite is a pool thread, and so is any drain thread here.
            Probed = new Lazy<Task<WslContext?>>(
                () => Task.Factory.StartNew(
                        async () => Record(await prober(CancellationToken.None).ConfigureAwait(false)),
                        CancellationToken.None,
                        TaskCreationOptions.DenyChildAttach | TaskCreationOptions.PreferFairness,
                        TaskScheduler.Default)
                    .Unwrap(),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public Lazy<Task<WslContext?>> Probed { get; }

        public WslContext? OverrideContext
        {
            get { lock (_gate) { return _override; } }
            set { lock (_gate) { _override = value; } }
        }

        public WslProbe? RecordedProbe
        {
            get { lock (_gate) { return _lastProbe; } }
        }

        private WslContext? Record(WslProbe probe)
        {
            lock (_gate)
            {
                _lastProbe = probe;
            }

            return FromProbe(probe);
        }
    }

    /// <summary>
    /// Puts back the resolution the flow saw before the test opened its own, and refuses
    /// when the flow is no longer on this scope's resolution: closed out of order, or from
    /// a flow that did not open it, a restore would install the wrong one silently.
    /// </summary>
    private sealed class IsolationScope(Resolution installed, Resolution? outer) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            if (!ReferenceEquals(Isolated.Value, installed))
                throw new InvalidOperationException(
                    "A WSL test scope was closed out of order, or from a flow that did not open it.");
            _disposed = true;
            Isolated.Value = outer;
        }
    }
}
