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
            Probed = new Lazy<Task<WslContext?>>(
                () => Task.Run(async () => Record(await prober(CancellationToken.None).ConfigureAwait(false))),
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

    /// <summary>Puts back the resolution the flow saw before the test opened its own.</summary>
    private sealed class IsolationScope(Resolution? outer) : IDisposable
    {
        public void Dispose() => Isolated.Value = outer;
    }
}
