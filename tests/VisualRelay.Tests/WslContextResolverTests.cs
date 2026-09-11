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
}
