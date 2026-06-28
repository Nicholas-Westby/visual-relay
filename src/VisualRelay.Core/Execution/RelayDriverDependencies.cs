using VisualRelay.Core.Configuration;
using VisualRelay.Core.Logging;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Collaborators a <see cref="RelayDriver"/> needs. <see cref="EnvironmentAccessor"/>
/// is threaded into <see cref="NonoProfileEnsurer.EnsureAsync"/> (the once-per-run
/// vr-guard profile self-heal) so the profile path resolves through an injectable
/// environment. Production leaves it <c>null</c> — real process env, real
/// <c>~/.config</c> — while <see cref="ForTests"/> defaults it to a hermetic temp
/// XDG accessor so the integration suite never writes the user's real profile (and
/// runs cleanly under the always-on vr-guard nono sandbox, which denies that write).
/// </summary>
public sealed record RelayDriverDependencies(
    ISubagentRunner SubagentRunner,
    ITestRunner TestRunner,
    IRelayEventSink EventSink,
    IGitInvoker GitInvoker,
    IEnvironmentAccessor? EnvironmentAccessor = null)
{
    public static RelayDriverDependencies ForTests(
        ISubagentRunner subagentRunner,
        ITestRunner testRunner,
        IRelayEventSink eventSink,
        IGitInvoker? gitInvoker = null,
        IEnvironmentAccessor? environmentAccessor = null) =>
        new(subagentRunner, testRunner, eventSink, gitInvoker ?? new GitInvoker(),
            environmentAccessor ?? new TempXdgEnvironmentAccessor());
}

/// <summary>
/// Test-support <see cref="IEnvironmentAccessor"/> that pins <c>XDG_CONFIG_HOME</c>
/// to ONE shared process-temp directory (<c>…/vr-test-xdg</c>), so the vr-guard
/// profile self-healed by <see cref="RelayDriver.RunTaskAsync"/> through
/// <see cref="NonoProfileEnsurer.EnsureAsync"/> lands under a sandbox-writable temp
/// dir instead of the real <c>~/.config</c>. It is the default accessor for
/// <see cref="RelayDriverDependencies.ForTests"/>, giving every test-built driver
/// hermetic isolation without editing each call site. Only <c>XDG_CONFIG_HOME</c>
/// is overridden; any other key falls through to the real environment — a set
/// <c>XDG_CONFIG_HOME</c> already short-circuits <c>HOME</c> in
/// <see cref="XdgConfig"/>, so the real <c>HOME</c> is never consulted for the path.
///
/// <para>The directory is <b>shared and stable</b>, not per-instance, so a full
/// <c>dotnet test</c> reuses one tree instead of leaking a fresh GUID directory per
/// <see cref="RelayDriverDependencies.ForTests"/> call (the suite builds 100+). To
/// keep that single shared file safe under xUnit's parallel collections — dozens of
/// driver tests invoke the <b>non-atomic</b>
/// <see cref="NonoProfileEnsurer.EnsureAsync"/> writer concurrently — the canonical
/// profile is seeded into the dir exactly once, thread-safely (via
/// <see cref="Lazy{T}"/>), before any accessor hands out the path. Every later
/// <see cref="NonoProfileEnsurer.EnsureAsync"/> then reads byte-identical content and
/// skips its write, so no two threads ever write the shared file.</para>
/// </summary>
internal sealed class TempXdgEnvironmentAccessor : IEnvironmentAccessor
{
    /// <summary>
    /// The shared temp directory pinned as <c>XDG_CONFIG_HOME</c>, materialized only
    /// after its vr-guard profile has been seeded once (thread-safe via
    /// <see cref="Lazy{T}"/>), so the non-atomic <see cref="NonoProfileEnsurer.EnsureAsync"/>
    /// never races a concurrent first write on the shared file.
    /// </summary>
    private static readonly Lazy<string> SeededConfigHome = new(SeedSharedProfile);

    /// <summary>The shared temp directory pinned as <c>XDG_CONFIG_HOME</c>.</summary>
    private string ConfigHome => SeededConfigHome.Value;

    public string? GetEnvironmentVariable(string name) =>
        name == "XDG_CONFIG_HOME" ? ConfigHome : null;

    // Creates the shared dir and writes the canonical vr-guard profile exactly once,
    // before any test's EnsureAsync inspects it, so the on-disk bytes already match
    // and every EnsureAsync skips its (non-atomic) write — no concurrent first write.
    private static string SeedSharedProfile()
    {
        var configHome = Path.Combine(Path.GetTempPath(), "vr-test-xdg");
        var profilePath = NonoProfileEnsurer.ResolveProfilePath(new FixedConfigHomeAccessor(configHome));
        Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
        File.WriteAllText(profilePath, NonoProfileEnsurer.EmbeddedContent);
        return configHome;
    }

    // Non-recursive accessor used only while seeding: resolving the profile path with
    // `this` instead would re-enter the Lazy that is still under construction.
    private sealed class FixedConfigHomeAccessor(string configHome) : IEnvironmentAccessor
    {
        public string? GetEnvironmentVariable(string name) =>
            name == "XDG_CONFIG_HOME" ? configHome : null;
    }
}

