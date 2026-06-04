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

    private static string NotReadyMessage() =>
        ErrorHintClassifier.HintFor("connection error")
        ?? $"Model backend not reachable at {ModelBackend.BaseUrl}.";
}
