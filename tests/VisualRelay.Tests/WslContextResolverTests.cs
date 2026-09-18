using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// The process-wide resolved WSL context. Its override is static state, so the
/// facts that touch it share one collection and always clear it again.
/// </summary>
[Collection("WslContext")]
public sealed class WslContextResolverTests
{
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

    [Fact]
    public void Override_WinsUntilCleared()
    {
        var context = new WslContext(WslProbeFixtures.WslExe, "Ubuntu", "/usr/local/bin/nono", "/home/alice");
        try
        {
            WslContextResolver.Override(context);

            Assert.Same(context, WslContextResolver.TryGetCurrent());
        }
        finally
        {
            WslContextResolver.Override(null);
        }

        // Off Windows there is nothing to probe, so a cleared override yields null.
        // (On Windows TryGetCurrent would probe the real machine once.)
        if (!OperatingSystem.IsWindows())
            Assert.Null(WslContextResolver.TryGetCurrent());
    }

    /// <summary>
    /// The async accessor answers from the same override, so a caller on the UI
    /// thread never reaches the blocking one.
    /// </summary>
    [Fact]
    public async Task TryGetCurrentAsync_AnswersFromTheOverride()
    {
        var context = new WslContext(WslProbeFixtures.WslExe, "Ubuntu", "/usr/local/bin/nono", "/home/alice");
        try
        {
            WslContextResolver.Override(context);

            Assert.Same(context, await WslContextResolver.TryGetCurrentAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            WslContextResolver.Override(null);
        }

        if (!OperatingSystem.IsWindows())
            Assert.Null(await WslContextResolver.TryGetCurrentAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// One probe per process, however many callers ask and whichever accessor they
    /// use. The sync accessor blocks on the SAME task the async one awaits, and that
    /// task runs on the thread pool — which is what keeps a blocked UI thread from
    /// being the continuation the probe is waiting for.
    /// </summary>
    [Fact]
    public async Task TheProbe_RunsOnceForEveryCaller_OnBothAccessors()
    {
        var probes = 0;
        WslContextResolver.UseProberForTests(_ =>
        {
            Interlocked.Increment(ref probes);
            return Task.FromResult(WslProbeFixtures.Usable());
        });
        try
        {
            var asked = await Task.WhenAll(
                Enumerable.Range(0, 8).Select(_ => Task.Run(
                    WslContextResolver.ProbedForTestsAsync, TestContext.Current.CancellationToken)));

            Assert.Equal(1, probes);
            Assert.All(asked, context => Assert.Equal("Ubuntu", context!.Distro));
            Assert.Same(asked[0], await WslContextResolver.ProbedForTestsAsync());
            Assert.Equal(1, probes);
        }
        finally
        {
            WslContextResolver.UseProberForTests(null);
        }
    }

    /// <summary>
    /// A prober installed while callers are already asking must be the one they get.
    /// The memo field was written under the lock and read outside it, so a reader could
    /// resolve the PREVIOUS Lazy — on Windows the real wsl.exe probe — after a fixture
    /// had replaced it. This drives the swap and the reads together; it cannot make a
    /// stale read certain, so it is a guard on the contract rather than a reproduction.
    /// </summary>
    [Fact]
    public async Task AProberInstalledWhileCallersAsk_IsTheOneTheyGet()
    {
        try
        {
            for (var round = 0; round < 40; round++)
            {
                var distro = $"Round{round}";
                WslContextResolver.UseProberForTests(
                    _ => Task.FromResult(WslProbeFixtures.Usable(distro)));

                var asked = await Task.WhenAll(Enumerable.Range(0, 8)
                    .Select(_ => Task.Run(WslContextResolver.ProbedForTestsAsync)));

                Assert.All(asked, context => Assert.Equal(distro, context!.Distro));
            }
        }
        finally
        {
            WslContextResolver.UseProberForTests(null);
        }
    }
}
