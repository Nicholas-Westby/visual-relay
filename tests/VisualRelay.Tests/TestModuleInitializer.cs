using System.Runtime.CompilerServices;

[assembly: AssemblyFixture(typeof(VisualRelay.Tests.PipelineTestFixture))]
[assembly: AssemblyFixture(typeof(VisualRelay.Tests.CachedSyntaxTreesFixture))]

namespace VisualRelay.Tests;

/// <summary>
/// Runs once when the test assembly loads — before ANY test — and redirects
/// <c>XDG_CONFIG_HOME</c> to a unique temp directory so that even a test
/// that constructs <c>MainWindowViewModel</c> with a null accessor can never
/// resolve the real per-user <c>~/.config/visual-relay/.env</c>.
/// <para>
/// The suite's other hermeticity seam — no outbound sockets — lives in
/// <see cref="HermeticHttpHandler"/> rather than here. .NET exposes no
/// process-global handler factory (<c>ConnectCallback</c> is per-handler, and
/// <c>HttpClient.DefaultProxy</c> swallows exceptions thrown from
/// <c>IWebProxy</c>), so the gate is a single composition root plus
/// <c>HttpClientConstructionGuard</c>, which fails the build if any other file
/// under <c>src/</c>, <c>tools/</c> or <c>tests/</c> constructs a client,
/// invoker or handler of its own.
/// </para>
/// </summary>
internal static class TestModuleInitializer
{
    /// <summary>
    /// The temp directory this initializer redirected <c>XDG_CONFIG_HOME</c> to,
    /// captured at module load. Hermeticity tests assert against this recorded
    /// value rather than the live env var: a concurrent test in another collection
    /// transiently nulls and restores the process-wide var under a try/finally, and
    /// a live read racing that window would spuriously see the null.
    /// </summary>
    internal static string? RedirectedXdgConfigHome { get; private set; }

    [ModuleInitializer]
    public static void Initialize()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "vr-test-xdg-config",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", tempDir);
        RedirectedXdgConfigHome = tempDir;
        RaiseThreadPoolFloor();
    }

    /// <summary>
    /// Lets the thread pool add threads immediately instead of at its injection rate.
    /// <para>
    /// Below the minimum the pool creates a thread on demand; above it, it adds roughly
    /// two per second while it hill-climbs. The suite runs 2.0x processor count in
    /// parallel and much of what it does BLOCKS a pool thread — file IO, waiting on a
    /// spawned process, an Avalonia async-void continuation posted back from a pool
    /// thread. Once every thread is blocked, work that would release them sits in the
    /// queue for seconds, which is a deadlock in all but name and resolves only as the
    /// pool trickles threads in.
    /// </para>
    /// <para>
    /// That is the shape already recorded in <c>IsolatedCollectionDefinition</c>:
    /// measured on Windows in a parallel full run, a child process that exited 0.55 s in
    /// was reported back at 6.1 s. The same starvation expires a 5 s wait for a settings
    /// window and a 5 s loopback request whose own listener needs a pool thread to
    /// accept. Roughly one Windows full run in five fails one of five tests this way,
    /// and none of them reproduces in isolation, because isolation removes the cause.
    /// </para>
    /// <para>
    /// Measured, 18 runs per condition on Windows and 6 on macOS, columns declared usable
    /// from their own noise before comparing: a small, consistent improvement and no harm
    /// (7% fewer tests over two seconds on Windows; macOS, with nothing to relieve, lost
    /// nothing). It is kept on that basis. It does not explain the deadline failures by
    /// itself: those tests now run in the Isolated collection, where nothing competes for
    /// the pool. Raising the floor only, never lowering it, so a host that already asked
    /// for more keeps what it asked for.
    /// </para>
    /// </summary>
    private static void RaiseThreadPoolFloor()
    {
        ThreadPool.GetMinThreads(out var workers, out var completionPorts);
        var wanted = Math.Max(workers, Environment.ProcessorCount * 8);
        var wantedPorts = Math.Max(completionPorts, Environment.ProcessorCount * 8);
        ThreadPool.SetMinThreads(wanted, wantedPorts);
    }
}
