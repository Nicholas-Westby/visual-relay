using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

// Outcome of a single readiness check: ready (backend answered 2xx) or not,
// with a remediation-oriented message when not. Designed to be reused by the
// before-run guard, the startup probe, and the later top-bar status indicator
// without a second probe implementation.
public readonly record struct BackendReadiness(bool IsReady, string? Message);

// Fast, never-throwing reachability check against the model backend's
// readiness endpoint. A cheap up-front probe so a down backend fails in ~1-2s
// instead of burning ~36s of LLM-call retries.
public static class BackendReadinessProbe
{
    // Bounded retry knobs for the pre-run path (ProcessRunners wraps its probe
    // delegate with CheckWithRetryAsync). With connection-refused returning
    // near-instantly, 3 attempts × 500ms backoff is ~1-1.5s worst case; even if
    // every attempt hits the 2s timeout it is ~6-7s — still far below ~36s of
    // LLM-call retries the probe exists to avoid.
    public const int DefaultRetryAttempts = 3;
    public static readonly TimeSpan DefaultRetryBackoff = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    // Shared client (infinite client-level timeout; each call applies its own
    // timeout via a linked CancellationTokenSource). One pooled client avoids
    // socket exhaustion when the later top-bar indicator polls on a timer.
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };

    // Convenience overload using the centralized base URL and a ~2s timeout.
    public static Task<BackendReadiness> CheckAsync(CancellationToken cancellationToken = default) =>
        CheckAsync(ModelBackend.BaseUrl, DefaultTimeout, cancellationToken);

    // Probes GET {baseUrl}/health/readiness with the given timeout. A 2xx
    // response is ready; any failure (connection refused, timeout, non-2xx) is
    // not ready with a remediation message. Never throws.
    public static async Task<BackendReadiness> CheckAsync(
        string baseUrl,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var url = $"{baseUrl.TrimEnd('/')}{ModelBackend.ReadinessPath}";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            using var response = await Client.GetAsync(url, cts.Token);
            return response.IsSuccessStatusCode
                ? new BackendReadiness(true, null)
                : new BackendReadiness(false, NotReadyMessage());
        }
        catch (Exception)
        {
            // HttpRequestException (refused), TaskCanceledException/
            // OperationCanceledException (timeout via CancelAfter), and anything
            // else collapse to a single not-ready outcome so the probe is safe
            // to call from the UI thread path.
            return new BackendReadiness(false, NotReadyMessage());
        }
    }

    // Retries CheckAsync up to maxAttempts times with retryBackoff between
    // attempts. Returns ready as soon as any attempt succeeds (zero added
    // latency on the happy path). On exhaustion it returns the last not-ready
    // result with the same ConnectionHint message the single-shot check
    // produces. Honors cancellation between attempts so a paused/stopped run
    // stops retrying promptly. Never throws.
    public static Task<BackendReadiness> CheckWithRetryAsync(
        string baseUrl,
        TimeSpan timeout,
        int maxAttempts = DefaultRetryAttempts,
        TimeSpan? retryBackoff = null,
        CancellationToken cancellationToken = default)
    {
        return CheckWithRetryAsync(
            ct => CheckAsync(baseUrl, timeout, ct),
            maxAttempts,
            retryBackoff,
            cancellationToken);
    }

    // Convenience overload using the centralized base URL, default timeout,
    // default retry attempts, and default backoff. Mirrors the existing
    // CheckAsync() convenience overload pattern.
    public static Task<BackendReadiness> CheckWithRetryAsync(CancellationToken cancellationToken = default) =>
        CheckWithRetryAsync(ModelBackend.BaseUrl, DefaultTimeout, cancellationToken: cancellationToken);

    // Retries a probe delegate up to maxAttempts times with retryBackoff
    // between attempts. Returns ready as soon as any attempt succeeds (zero
    // added latency on the happy path).  On exhaustion it returns the last
    // not-ready result with the same ConnectionHint message the single-shot
    // check produces. Honors cancellation between attempts so a paused/stopped
    // run stops retrying promptly. Never throws — exceptions from the probe
    // delegate are caught and surface as not-ready.
    public static async Task<BackendReadiness> CheckWithRetryAsync(
        Func<CancellationToken, Task<BackendReadiness>> probe,
        int maxAttempts = DefaultRetryAttempts,
        TimeSpan? retryBackoff = null,
        CancellationToken cancellationToken = default)
    {
        var backoff = retryBackoff ?? DefaultRetryBackoff;
        BackendReadiness lastResult = default;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                lastResult = await probe(cancellationToken);
            }
            catch (Exception)
            {
                lastResult = new BackendReadiness(false, NotReadyMessage());
            }

            if (lastResult.IsReady)
                return lastResult;

            if (attempt < maxAttempts - 1)
            {
                try
                {
                    await Task.Delay(backoff, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return lastResult;
                }
            }
        }

        return lastResult;
    }

    private static string NotReadyMessage() =>
        ErrorHintClassifier.HintFor("connection error")
        ?? $"Model backend not reachable at {ModelBackend.BaseUrl}.";
}
