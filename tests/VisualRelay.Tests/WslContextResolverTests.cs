using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// Resolving the WSL context. Every fact that needs a resolution other than this
/// machine's opens its own with <see cref="WslContextResolver.IsolateForTests"/>, which
/// only its own async flow can see, so these run beside any other test.
/// </summary>
public sealed class WslContextResolverTests
{
    private static readonly WslContext Context =
        new(WslProbeFixtures.WslExe, "Ubuntu", "/usr/local/bin/nono", "/home/alice");

    [Fact]
    public void FromProbe_Usable_CarriesTheFourLaunchFacts()
    {
        var context = WslContextResolver.FromProbe(WslProbeFixtures.Usable());

        Assert.Equal(
            new WslContext(WslProbeFixtures.WslExe, "Ubuntu", "/usr/local/bin/nono", "/home/alice"),
            context);
    }

    [Fact]
    public void FromProbe_Unusable_IsNull()
    {
        Assert.Null(WslContextResolver.FromProbe(WslProbeFixtures.NonoMissing()));
        Assert.Null(WslContextResolver.FromProbe(WslProbeFixtures.NoWsl()));
    }

    /// <summary>
    /// The override beats the probe. The probe here finds a different, usable distro,
    /// so on Windows the probe's answer would be a different context, and off Windows
    /// there would be none.
    /// </summary>
    [Fact]
    public void TheOverride_WinsOverTheProbe()
    {
        using var wsl = WslContextResolver.IsolateForTests(@override: Context, prober: DebianMachine);

        Assert.Same(Context, WslContextResolver.TryGetCurrent());
    }

    /// <summary>
    /// The async accessor answers from the same override, so a caller on the UI
    /// thread never reaches the blocking one.
    /// </summary>
    [Fact]
    public async Task TryGetCurrentAsync_AnswersFromTheOverride()
    {
        using var wsl = WslContextResolver.IsolateForTests(@override: Context, prober: DebianMachine);

        Assert.Same(Context, await WslContextResolver.TryGetCurrentAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// With no override and no prober stated there is no context on any platform: off
    /// Windows nothing is probed, and on Windows the scope's machine has no WSL rather
    /// than being the machine running the suite. The memo is resolved too, so a default
    /// that ran the real probe would leave its own probe behind rather than the empty one.
    /// </summary>
    [Fact]
    public async Task AScopeThatStatesNothing_HasNoContextOnAnyPlatform()
    {
        using var wsl = WslContextResolver.IsolateForTests();

        Assert.Null(WslContextResolver.TryGetCurrent());
        Assert.Null(await WslContextResolver.TryGetCurrentAsync(TestContext.Current.CancellationToken));
        Assert.Null(await WslContextResolver.ProbedForTestsAsync());
        Assert.Same(WslProbe.Empty, WslContextResolver.UnusableProbe);
    }

    /// <summary>
    /// Outside any scope a test sees a machine with no WSL, whatever machine runs the
    /// suite: the test assembly sets that before any test runs. Only Windows reads the
    /// memo, so only there can this fail, and there it did in effect: the first live
    /// probe, started by whichever test first asked for this machine's host, took 25 to
    /// 60 s under load and queued everything behind it.
    /// </summary>
    [Fact]
    public async Task OutsideAScope_TheTestProcessHasNoWsl()
    {
        Assert.Null(WslContextResolver.TryGetCurrent());
        Assert.Null(await WslContextResolver.TryGetCurrentAsync(TestContext.Current.CancellationToken));
        Assert.Null(SandboxHost.Current.Wsl);
    }

    /// <summary>
    /// Mapping a probe records nothing. Tests call it outside any scope, so if it wrote
    /// the last probe, the refusal of whichever test read it next would name this one's
    /// failing check.
    /// </summary>
    [Fact]
    public void FromProbe_LeavesTheRefusalWordingAlone()
    {
        using var wsl = WslContextResolver.IsolateForTests();

        WslContextResolver.FromProbe(WslProbeFixtures.NonoMissing());

        Assert.Same(WslProbe.Empty, WslContextResolver.UnusableProbe);
    }

    /// <summary>
    /// What the CLI gate calls. Tests are barred from it because outside a scope it sets
    /// the application's override, which every test running at the time would resolve;
    /// inside one it sets only that scope's, which is the one place it is tested.
    /// </summary>
    [Fact]
    public void Override_SetsTheOverrideOfTheResolutionInUse()
    {
        using var wsl = WslContextResolver.IsolateForTests(prober: DebianMachine);

#pragma warning disable RS0030 // Inside IsolateForTests this writes only this test's resolution.
        WslContextResolver.Override(Context);
#pragma warning restore RS0030

        Assert.Same(Context, WslContextResolver.TryGetCurrent());
    }

    /// <summary>
    /// One probe per resolution, however many callers ask. The callers run on other
    /// threads, started from inside the scope, and still get the scope's memo: work a
    /// test starts is part of the test. The memo's task runs on the thread pool, which
    /// is what keeps a blocked UI thread from being the continuation it waits for.
    /// </summary>
    [Fact]
    public async Task TheProbe_RunsOnceForEveryCaller()
    {
        var probes = 0;
        using var wsl = WslContextResolver.IsolateForTests(prober: _ =>
        {
            Interlocked.Increment(ref probes);
            return Task.FromResult(WslProbeFixtures.Usable());
        });

        var asked = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Task.Run(
                WslContextResolver.ProbedForTestsAsync, TestContext.Current.CancellationToken)));

        // Named, because the two probe-count checks are otherwise indistinguishable in a
        // failure, and this test once failed twice on Windows without anyone being able
        // to say which line went.
        Assert.True(probes == 1, $"eight callers should share one probe; ran {probes}");
        Assert.All(asked, context => Assert.Equal("Ubuntu", context!.Distro));
        var again = await WslContextResolver.ProbedForTestsAsync();
        Assert.True(ReferenceEquals(asked[0], again),
            "a later caller got a different context, so the memo was replaced mid-test");
        Assert.True(probes == 1, $"the memo should still hold; ran {probes} probes in total");
    }

    /// <summary>
    /// Closing a scope gives the flow back the one it had, not no resolution at all, so
    /// a helper that isolates inside a test that already did leaves the test's intact.
    /// </summary>
    [Fact]
    public async Task ClosingANestedScope_RestoresTheOuterOne()
    {
        using var outer = WslContextResolver.IsolateForTests(prober: _ => Task.FromResult(WslProbeFixtures.Usable("Outer")));
        using (WslContextResolver.IsolateForTests(prober: _ => Task.FromResult(WslProbeFixtures.Usable("Inner"))))
            Assert.Equal("Inner", (await WslContextResolver.ProbedForTestsAsync())?.Distro);

        Assert.Equal("Outer", (await WslContextResolver.ProbedForTestsAsync())?.Distro);
    }

    /// <summary>
    /// Outside a scope there is no memo of the test's own to read: the process-wide one
    /// belongs to the process, and in the application it is the real probe of the machine.
    /// The test seam refuses rather than reading it.
    /// </summary>
    [Fact]
    public async Task ReadingTheMemoOutsideAScope_IsRefused()
    {
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(WslContextResolver.ProbedForTestsAsync);

        Assert.Contains("IsolateForTests", refusal.Message, StringComparison.Ordinal);
    }

    private static Task<WslProbe> DebianMachine(CancellationToken _) =>
        Task.FromResult(WslProbeFixtures.Usable("Debian"));
}
