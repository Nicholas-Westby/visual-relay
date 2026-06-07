namespace VisualRelay.Domain;

// Maps known subagent/backend failure signatures to a short, actionable hint.
// Pure (no side effects) so it is trivially unit-testable and reusable by the
// pre-flight probe and any UI error surface. The raw error is never replaced;
// callers use WithHint to append the hint and keep the original text.
public static class ErrorHintClassifier
{
    // User-facing copy. The base URL comes from the centralized ModelBackend so
    // the port lives in exactly one place; here it is display text.
    private const string ConnectionHint =
        "Hint: Can't reach the model backend at " + ModelBackend.BaseUrl +
        " — is the LiteLLM proxy running? Start it (see " +
        "autostart-model-backend-on-launch.md) and re-run.";

    private const string TimeoutHint =
        "Hint: The stage timed out. Try raising maxTurns/the timeout, " +
        "or check the model backend's latency.";

    private const string TestTimeoutHint =
        "Hint: The test command exceeded the configured time cap and was halted. " +
        "This likely means the test suite is hanging or running pathologically long — " +
        "fix the hang, or in the interim re-run only the specific tests you need " +
        "rather than the whole suite. Use a targeted subset invocation for this " +
        "project (e.g. a filter expression, specific file paths, or the " +
        "TestFileCommand \"{files}\" pattern) to narrow the scope.";

    private const string AuthHint =
        "Hint: Provider key missing or invalid — check the backend's provider config.";

    private const string MissingJsonHint =
        "Hint: The model didn't return the required JSON contract — usually a " +
        "model/prompt issue, retry or try a stronger tier.";

    // Returns an actionable hint for a recognized failure signature, or null
    // when the error is unrecognized (so callers never show a misleading hint).
    public static string? HintFor(string? rawError)
    {
        if (string.IsNullOrWhiteSpace(rawError))
        {
            return null;
        }

        // Test-command timeout first: a hung test suite needs a targeted subset,
        // not LLM-tuning advice. Must precede the generic timeout check because
        // both match "timed out".
        if (Contains(rawError, "test command timed out"))
        {
            return TestTimeoutHint;
        }

        // Timeout: timeouts can read as connection issues but have a distinct,
        // more specific remedy.
        if (Contains(rawError, "timed out") || Contains(rawError, "timeout"))
        {
            return TimeoutHint;
        }

        // Auth before connection: an auth failure can be surfaced after the SDK
        // exhausts retries (so it carries "max retries exceeded"), but the real
        // fix is the provider key, not the backend reachability.
        if (Contains(rawError, "401") ||
            Contains(rawError, "403") ||
            Contains(rawError, "api_key") ||
            Contains(rawError, "api key") ||
            Contains(rawError, "authenticationerror") ||
            Contains(rawError, "unauthorized") ||
            Contains(rawError, "forbidden"))
        {
            return AuthHint;
        }

        if (Contains(rawError, "connection error") ||
            Contains(rawError, "connection refused") ||
            Contains(rawError, "connectionrefusederror") ||
            Contains(rawError, "max retries exceeded") ||
            Contains(rawError, "failed to establish a new connection"))
        {
            return ConnectionHint;
        }

        if (Contains(rawError, "no valid fenced json block"))
        {
            return MissingJsonHint;
        }

        return null;
    }

    // Returns the raw error with the matching hint appended, or the raw error
    // unchanged when unrecognized. Keeps the original text in all cases.
    public static string WithHint(string? rawError)
    {
        var raw = rawError ?? string.Empty;
        var hint = HintFor(raw);
        return hint is null ? raw : $"{raw}\n\n{hint}";
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
