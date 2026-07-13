using System.Net;
using System.Net.Sockets;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class BackendReadinessProbeTests
{
    // The fake backend is a raw TcpListener rather than HttpListener: the listener
    // holds its own port for its whole lifetime (no release-then-rebind race), and
    // it sidesteps HttpListener's process-wide static HttpEndPointManager, which
    // throws "Address already in use" during Dispose on macOS when several
    // listeners churn in one test process (as the full suite does during a
    // pipeline's stage-9 verify). A handful of header bytes plus a fixed 200 line
    // is all the probe's HttpClient needs to see a reachable backend.
    private const int PortRollAttempts = 25;

    [Fact]
    public async Task CheckAsync_BackendListeningAndReturns200_IsReady()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        // ReSharper disable once AccessToDisposedClosure — the finally block stops the
        // listener and 'await serve' joins this task before 'listener' (using var) is
        // disposed at method exit; the catch handles any stop-while-serving race.
        var serve = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                // Drain a chunk of the request (count used to satisfy CA2022) so the
                // response is not RST'd; one read covers a small GET's headers.
                var read = await stream.ReadAsync(new byte[1024]);
                if (read == 0)
                {
                    return;
                }

                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
                await stream.FlushAsync();
            }
            catch (Exception)
            {
                // Listener stopped before/while serving; nothing to do.
            }
        });

        try
        {
            var result = await BackendReadinessProbe.CheckAsync(
                $"http://127.0.0.1:{port}",
                TimeSpan.FromSeconds(2));

            Assert.True(result.IsReady);
            Assert.Null(result.Message);
        }
        finally
        {
            listener.Stop();
            await serve;
        }
    }

    [Fact]
    public async Task CheckAsync_NothingListening_IsNotReadyWithMessage()
    {
        for (var attempt = 0; ; attempt++)
        {
            var closedPort = FreePort();

            var result = await BackendReadinessProbe.CheckAsync(
                $"http://127.0.0.1:{closedPort}",
                TimeSpan.FromSeconds(2));

            // If something grabbed the just-freed port it would answer as "ready";
            // re-roll onto a new closed port rather than assert on a stolen one.
            if (!result.IsReady || attempt >= PortRollAttempts)
            {
                Assert.False(result.IsReady);
                Assert.False(string.IsNullOrWhiteSpace(result.Message));
                return;
            }
        }
    }

    // ── CheckWithRetryAsync tests ──────────────────────────────────────────

    [Fact]
    public async Task CheckWithRetryAsync_FirstAttemptSucceeds_ReturnsReadyImmediately()
    {
        var callCount = 0;
        Task<BackendReadiness> Probe(CancellationToken ct)
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(new BackendReadiness(true, null));
        }

        var result = await BackendReadinessProbe.CheckWithRetryAsync(Probe);

        Assert.True(result.IsReady);
        Assert.Null(result.Message);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task CheckWithRetryAsync_TransientFailureRecovers_ReturnsReady()
    {
        var callCount = 0;
        Task<BackendReadiness> Probe(CancellationToken ct)
        {
            var count = Interlocked.Increment(ref callCount);
            return Task.FromResult(
                count > 1
                    ? new BackendReadiness(true, null)
                    : new BackendReadiness(false, "transient blip"));
        }

        var time = new ManualTimeProvider();
        var probeTask = BackendReadinessProbe.CheckWithRetryAsync(Probe, timeProvider: time);
        while (!probeTask.IsCompleted)
        {
            time.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Yield();
        }
        var result = await probeTask;

        Assert.True(result.IsReady);
        Assert.Null(result.Message);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task CheckWithRetryAsync_AllFailures_ReturnsNotReadyWithMessage()
    {
        var callCount = 0;
        Task<BackendReadiness> Probe(CancellationToken ct)
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(new BackendReadiness(false, "backend down"));
        }

        var time = new ManualTimeProvider();
        var probeTask = BackendReadinessProbe.CheckWithRetryAsync(Probe, timeProvider: time);
        while (!probeTask.IsCompleted)
        {
            time.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Yield();
        }
        var result = await probeTask;

        Assert.False(result.IsReady);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.Equal(BackendReadinessProbe.DefaultRetryAttempts, callCount);
    }

    [Fact]
    public async Task CheckWithRetryAsync_CancellationStopsRetries()
    {
        var callCount = 0;
        using var cts = new CancellationTokenSource();
        Task<BackendReadiness> Probe(CancellationToken ct)
        {
            Interlocked.Increment(ref callCount);
            // Cancel DURING the first attempt so the inter-attempt backoff is cut short
            // deterministically — the retry stops by causality, not by a wall-clock race.
            cts.Cancel();
            return Task.FromResult(new BackendReadiness(false, "still down"));
        }

        var result = await BackendReadinessProbe.CheckWithRetryAsync(
            Probe,
            maxAttempts: BackendReadinessProbe.DefaultRetryAttempts,
            retryBackoff: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        Assert.False(result.IsReady);
        // Cancellation cut retries short: far fewer than the full attempt budget, and
        // the backoff's Task.Delay observed the cancelled token and returned at once.
        Assert.True(callCount < BackendReadinessProbe.DefaultRetryAttempts,
            $"Expected fewer than {BackendReadinessProbe.DefaultRetryAttempts} calls, got {callCount}");
    }

    [Fact]
    public async Task CheckWithRetryAsync_BaseUrlOverload_ProbesRealPort()
    {
        // Own the port for the whole test — a bound listener no parallel test can
        // steal — and answer every probe with 503, so "not ready" is deterministic:
        // no closed-port reuse race (the old FreePort approach) and no wall-clock
        // assertion (macOS drops SYNs for a bound-unlistened port, so a served 503
        // is the reliable fast negative). The overload must call CheckAsync in a
        // retry loop and return not-ready with a non-blank message after exhausting.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource();
        var serve = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(cts.Token);
                    await using var stream = client.GetStream();
                    // Count read to satisfy CA2022; a client that sent nothing just re-loops.
                    var read = await stream.ReadAsync(new byte[1024], cts.Token);
                    if (read == 0)
                        continue;
                    await stream.WriteAsync(
                        "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(),
                        cts.Token);
                    await stream.FlushAsync(cts.Token);
                }
            }
            catch (Exception)
            {
                // Listener stopped / token cancelled while accepting or serving.
            }
        });

        try
        {
            var result = await BackendReadinessProbe.CheckWithRetryAsync(
                $"http://127.0.0.1:{port}",
                TimeSpan.FromSeconds(2),
                retryBackoff: TimeSpan.Zero);

            Assert.False(result.IsReady);
            Assert.False(string.IsNullOrWhiteSpace(result.Message));
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            await serve;
        }
    }

    [Fact]
    public async Task CheckWithRetryAsync_DelegateNeverThrows_WhenProbeThrows()
    {
        var callCount = 0;
        Task<BackendReadiness> Probe(CancellationToken ct)
        {
            Interlocked.Increment(ref callCount);
            throw new InvalidOperationException("boom");
        }

        // Must not throw — the delegate overload catches exceptions from the
        // probe and returns not-ready.
        var time = new ManualTimeProvider();
        var probeTask = BackendReadinessProbe.CheckWithRetryAsync(Probe, timeProvider: time);
        while (!probeTask.IsCompleted)
        {
            time.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Yield();
        }
        var result = await probeTask;

        Assert.False(result.IsReady);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.Equal(BackendReadinessProbe.DefaultRetryAttempts, callCount);
    }

    [Fact]
    public void CheckWithRetryAsync_WorstCaseBudget_StaysUnder36Seconds()
    {
        // The probe exists to avoid ~36 s of LLM-call retries. Even the
        // worst-case retry budget must stay well below that.
        var worstCase = BackendReadinessProbe.DefaultRetryAttempts * 2.0  // max attempts × 2s timeout each
            + (BackendReadinessProbe.DefaultRetryAttempts - 1) * BackendReadinessProbe.DefaultRetryBackoff.TotalSeconds  // backoffs
            + 1.0;  // fudge for overhead

        Assert.True(worstCase < 36.0,
            $"Worst-case probe budget ({worstCase:F1}s) must stay well below 36s");
    }

    // Bind to port 0 to let the OS assign a free port, read it, then release it so
    // the caller can probe a port with nothing listening (connection refused).
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
