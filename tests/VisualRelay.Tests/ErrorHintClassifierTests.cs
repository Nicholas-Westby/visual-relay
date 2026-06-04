using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class ErrorHintClassifierTests
{
    [Fact]
    public void HintFor_ConnectionError_SuggestsBackendIsUnreachable()
    {
        const string raw =
            "swival exit 1: Error: LLM call failed (model: cheap-kimi): " +
            "litellm.InternalServerError: InternalServerError: OpenAIException - Connection error.";

        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.NotNull(hint);
        Assert.Contains("http://127.0.0.1:4000", hint);
        Assert.Contains("LiteLLM", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HintFor_ConnectionRefused_SuggestsBackendIsUnreachable()
    {
        const string raw = "swival exit 1: ConnectionRefusedError: [Errno 61] Connection refused";

        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.NotNull(hint);
        Assert.Contains("http://127.0.0.1:4000", hint);
    }

    [Fact]
    public void HintFor_Timeout_SuggestsRaisingTimeoutOrCheckingLatency()
    {
        const string raw = "swival timed out after 600000ms";

        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.NotNull(hint);
        Assert.Contains("maxTurns", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HintFor_AuthFailure_SuggestsProviderKey()
    {
        const string raw =
            "swival exit 1: litellm.AuthenticationError: AuthenticationError: " +
            "OpenAIException - Error code: 401 - invalid api_key";

        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.NotNull(hint);
        Assert.Contains("key", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HintFor_Forbidden_SuggestsProviderKey()
    {
        const string raw = "swival exit 1: OpenAIException - Error code: 403 - forbidden";

        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.NotNull(hint);
        Assert.Contains("key", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HintFor_AuthFailureWrappedInRetries_PrefersAuthOverConnection()
    {
        // An auth error surfaced after the SDK exhausts retries carries both an
        // auth code and "Max retries exceeded"; the actionable fix is the key.
        const string raw =
            "litellm.AuthenticationError: Error code: 401 - invalid api_key " +
            "(Max retries exceeded with url: /chat/completions)";

        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.NotNull(hint);
        Assert.Contains("key", hint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("4000", hint);
    }

    [Fact]
    public void HintFor_MissingFencedJson_SuggestsModelDidNotReturnContract()
    {
        const string raw = "no valid fenced json block";

        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.NotNull(hint);
        Assert.Contains("JSON", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HintFor_UnrecognizedError_ReturnsNull()
    {
        const string raw = "swival exit 7: some entirely novel failure mode nobody has seen";

        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.Null(hint);
    }

    [Fact]
    public void HintFor_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(ErrorHintClassifier.HintFor(null));
        Assert.Null(ErrorHintClassifier.HintFor(""));
        Assert.Null(ErrorHintClassifier.HintFor("   "));
    }

    [Fact]
    public void WithHint_RecognizedError_AppendsHintKeepingRawText()
    {
        const string raw = "swival exit 1: OpenAIException - Connection error.";

        var combined = ErrorHintClassifier.WithHint(raw);

        // Exact contract: raw text verbatim, blank-line separator, then the hint.
        // Three later tasks render this combined string, so pin the format.
        Assert.Equal($"{raw}\n\n{ErrorHintClassifier.HintFor(raw)}", combined);
        Assert.Contains("http://127.0.0.1:4000", combined);
    }

    [Fact]
    public void WithHint_UnrecognizedError_ReturnsRawUnchanged()
    {
        const string raw = "swival exit 7: some entirely novel failure mode nobody has seen";

        var combined = ErrorHintClassifier.WithHint(raw);

        Assert.Equal(raw, combined);
    }
}
