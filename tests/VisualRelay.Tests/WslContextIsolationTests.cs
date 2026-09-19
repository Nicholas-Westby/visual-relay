using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// One test's WSL resolution cannot be seen by another test running at the same time.
/// The resolver used to hold the override, the memoised probe and the last probe in
/// process-wide statics, and on 2026-09-18 six Windows facts failed in 12 ms each
/// because another test's "no distro" was in place when they resolved a host.
/// <para>
/// Each fact runs two flows and orders them by hand, so the bad interleaving happens on
/// every run rather than when the scheduler allows it. Each fails against the old statics.
/// </para>
/// </summary>
public sealed class WslContextIsolationTests
{
    private static readonly WslContext Context =
        new(@"C:\Windows\System32\wsl.exe", "Ubuntu", "/usr/local/bin/nono", "/home/alice");

    [Fact]
    public async Task AnOverrideSetByOneTest_IsNotSeenByAnotherResolvingMeanwhile()
    {
        var ct = TestContext.Current.CancellationToken;
        var readerIsolated = NewSignal();
        var writerIsolated = NewSignal();
        var readerResolved = NewSignal();

        var reader = Task.Run(async () =>
        {
            using var wsl = WslContextResolver.IsolateForTests();
            readerIsolated.SetResult();
            await writerIsolated.Task.WaitAsync(ct);
            var host = await SandboxHost.ResolveAsync(isWindows: true, ct);
            readerResolved.SetResult();
            return host;
        }, ct);
        var writer = Task.Run(async () =>
        {
            await readerIsolated.Task.WaitAsync(ct);
            using var wsl = WslContextResolver.IsolateForTests(@override: Context);
            writerIsolated.SetResult();
            await readerResolved.Task.WaitAsync(ct);
            return await SandboxHost.ResolveAsync(isWindows: true, ct);
        }, ct);

        Assert.Null((await reader).Wsl);
        Assert.Same(Context, (await writer).Wsl);
    }

    /// <summary>
    /// Off Windows the accessors answer from the override alone and never read the memo,
    /// so the memo is read directly here, which is the same on every platform.
    /// </summary>
    [Fact]
    public async Task AProberInstalledByOneTest_IsNotTheOneAnotherResolves()
    {
        var ct = TestContext.Current.CancellationToken;
        var alphaIsolated = NewSignal();
        var betaIsolated = NewSignal();
        var alphaResolved = NewSignal();

        var alpha = Task.Run(async () =>
        {
            using var wsl = WslContextResolver.IsolateForTests(prober: _ => Task.FromResult(WslProbeFixtures.Usable("Alpha")));
            alphaIsolated.SetResult();
            await betaIsolated.Task.WaitAsync(ct);
            var context = await WslContextResolver.ProbedForTestsAsync();
            alphaResolved.SetResult();
            return context;
        }, ct);
        var beta = Task.Run(async () =>
        {
            await alphaIsolated.Task.WaitAsync(ct);
            using var wsl = WslContextResolver.IsolateForTests(prober: _ => Task.FromResult(WslProbeFixtures.Usable("Beta")));
            betaIsolated.SetResult();
            await alphaResolved.Task.WaitAsync(ct);
            return await WslContextResolver.ProbedForTestsAsync();
        }, ct);

        Assert.Equal("Alpha", (await alpha)?.Distro);
        Assert.Equal("Beta", (await beta)?.Distro);
    }

    /// <summary>
    /// A refusal is worded from the last probe, so a test whose machine lacks nono must
    /// not make another test's refusal say so. The quiet test never probed, so its
    /// refusal has only the empty probe to explain it.
    /// </summary>
    [Fact]
    public async Task AnUnusableProbeRunByOneTest_DoesNotWordAnotherTestsRefusal()
    {
        var ct = TestContext.Current.CancellationToken;
        var noisyProbed = NewSignal();
        var quietRead = NewSignal();

        var noisy = Task.Run(async () =>
        {
            using var wsl = WslContextResolver.IsolateForTests(prober: _ => Task.FromResult(WslProbeFixtures.NonoMissing()));
            await WslContextResolver.ProbedForTestsAsync();
            noisyProbed.SetResult();
            await quietRead.Task.WaitAsync(ct);
            return WslContextResolver.UnusableProbe;
        }, ct);
        var quiet = Task.Run(async () =>
        {
            await noisyProbed.Task.WaitAsync(ct);
            using var wsl = WslContextResolver.IsolateForTests();
            var probe = WslContextResolver.UnusableProbe;
            quietRead.SetResult();
            return probe;
        }, ct);

        Assert.Same(WslProbe.Empty, await quiet);
        var own = await noisy;
        Assert.Equal("Ubuntu", own.DistroName);
        Assert.Null(own.NonoPath);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
