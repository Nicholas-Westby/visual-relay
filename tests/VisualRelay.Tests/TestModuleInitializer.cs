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
    }
}
