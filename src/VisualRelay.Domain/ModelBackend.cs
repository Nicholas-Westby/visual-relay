namespace VisualRelay.Domain;

// Single source of truth for the local model backend endpoint. The Swival
// profile generator, the readiness probe, and the error-hint copy all read the
// base URL from here so the port lives in exactly one place.
public static class ModelBackend
{
    public const string BaseUrl = "http://127.0.0.1:4000";
    public const string ReadinessPath = "/health/readiness";
}
