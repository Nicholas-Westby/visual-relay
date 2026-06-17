using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class ErrorHintClassifierTests
{
    [Fact]
    public void HintFor_ConnectionError_SuggestsBackendIsUnreachable()
    {
        const string raw =
            "swival exit 1: Error: LLM call failed (model: cheap): " +
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
    public void HintFor_MissingBinary_SuggestsInstallingTheTool()
    {
        // nono set up the sandbox fine, then could not exec swival because it
        // isn't on PATH. The actionable fix is installing swival, not bypassing
        // a sandbox-permission rule.
        const string raw = "nono: Command execution failed: swival: cannot find binary path";

        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.NotNull(hint);
        Assert.Contains("install", hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("swival", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HintFor_CommandNotFound_SuggestsInstallingTheTool()
    {
        const string raw = "swival exit 127: nono: command not found";

        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.NotNull(hint);
        Assert.Contains("install", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HintFor_PreflightNotOnPath_SuggestsInstallingTheTool()
    {
        // The fail-fast pre-flight refusal phrases the failure as "not on PATH";
        // the same actionable hint must fire there as at process exit.
        const string raw =
            "swival is not installed or not on PATH on this machine — Visual Relay can't run tasks here.";

        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.NotNull(hint);
        Assert.Contains("install", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HintFor_MissingBinary_DoesNotShadowConnectionOrAuth()
    {
        // The missing-binary branch must not accidentally swallow a connection
        // error that happens to mention nothing about a binary.
        var connectionHint = ErrorHintClassifier.HintFor(
            "swival exit 1: OpenAIException - Connection error.");
        Assert.NotNull(connectionHint);
        Assert.Contains("http://127.0.0.1:4000", connectionHint);

        var authHint = ErrorHintClassifier.HintFor(
            "swival exit 1: Error code: 401 - invalid api_key");
        Assert.NotNull(authHint);
        Assert.Contains("key", authHint, StringComparison.OrdinalIgnoreCase);
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
    public void HintFor_TestCommandTimedOut_ReturnsSubsetGuidance()
    {
        const string raw = "test command timed out after 300000ms";

        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.NotNull(hint);
        // Must be the distinct TestTimeoutHint with subset-guidance, not the
        // generic TimeoutHint that mentions maxTurns / model latency.
        Assert.Contains("targeted subset", hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("{files}", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("maxTurns", hint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model backend", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HintFor_GenericTimedOut_StillReturnsTimeoutHint()
    {
        // The generic "swival timed out" pattern must still match the
        // existing TimeoutHint (LLM-tuning advice), not the test-subset hint.
        const string raw = "swival timed out after 600000ms";

        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.NotNull(hint);
        Assert.Contains("maxTurns", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithHint_UnrecognizedError_ReturnsRawUnchanged()
    {
        const string raw = "swival exit 7: some entirely novel failure mode nobody has seen";

        var combined = ErrorHintClassifier.WithHint(raw);

        Assert.Equal(raw, combined);
    }
}
