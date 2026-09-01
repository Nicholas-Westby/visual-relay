using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class ErrorHintClassifierTests
{
    [Fact]
    public void HintFor_ConnectionError_SuggestsProviderIsUnreachable()
    {
        // Reaches the classifier from whatever client the target project used.
        const string raw = "Connection error.";

        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.NotNull(hint);
        Assert.Contains("provider", hint, StringComparison.OrdinalIgnoreCase);
        // Nothing local to start any more, so the hint must never say so.
        Assert.DoesNotContain("backend", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HintFor_ConnectionRefused_SuggestsProviderIsUnreachable()
    {
        // What the runner writes when a route's socket dies.
        const string raw = "the connection to frontier failed: Connection refused (api.z.ai:443)";

        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.NotNull(hint);
        Assert.Contains("provider", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HintFor_Timeout_SuggestsRaisingTimeoutOrCheckingLatency()
    {
        const string raw = "command timed out after 240s";

        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.NotNull(hint);
        Assert.Contains("maxTurns", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HintFor_AuthFailure_SuggestsProviderKey()
    {
        // ProviderErrorReader's own wording when a 401 carries no message.
        const string raw = "provider returned HTTP 401 with an empty body";

        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.NotNull(hint);
        Assert.Contains("key", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HintFor_ProviderAuthMessageWithoutAStatusCode_SuggestsProviderKey()
    {
        // A message-carrying body is surfaced verbatim with no status code, so
        // the classifier cannot rely on a 401. DeepSeek's measured auth body.
        const string raw = "Authentication Fails, Your api key: ****tall is invalid";

        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.NotNull(hint);
        Assert.Contains("key", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HintFor_Forbidden_SuggestsProviderKey()
    {
        const string raw = "provider returned HTTP 403 with an empty body";

        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.NotNull(hint);
        Assert.Contains("key", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HintFor_AuthFailureWrappedInRetries_PrefersAuthOverConnection()
    {
        // The hint is appended to a stage's test, guard and bootstrap output, so
        // the wording here is the target project's client, not ours. One retrying
        // an auth failure prints an auth code and a retry-exhaustion phrase on the
        // same line; the key is still the fix, so auth wins. Keeps the auth
        // branch's needles live: authenticationerror, 401 and api_key.
        const string raw =
            "AuthenticationError: Error code: 401 - invalid api_key " +
            "(Max retries exceeded with url: /chat/completions)";

        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.NotNull(hint);
        Assert.Contains("key", hint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("backend", hint, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The loop's own failure wordings name no key, connection or
    /// timeout, so the classifier stays silent rather than misdirect.</summary>
    [Theory]
    [InlineData("provider call failed")]
    [InlineData("provider ended the stream without its terminator")]
    [InlineData("model produced only reasoning within its output budget")]
    public void HintFor_LoopFailureWordings_ReturnNoHint(string raw)
    {
        Assert.Null(ErrorHintClassifier.HintFor(raw));
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
        // nono set up the sandbox fine, then could not exec the program the stage
        // asked for. The fix is installing that tool, not a sandbox rule.
        const string raw = "nono: Command execution failed: dotnet: cannot find binary path";

        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.NotNull(hint);
        Assert.Contains("install", hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nono", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HintFor_CommandNotFound_SuggestsInstallingTheTool()
    {
        const string raw = "/bin/sh: shellcheck: command not found";

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
            "nono is not installed or not on PATH on this machine — Visual Relay can't run tasks here.";

        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.NotNull(hint);
        Assert.Contains("install", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HintFor_MissingBinary_DoesNotShadowConnectionOrAuth()
    {
        // The missing-binary branch must not accidentally swallow a connection
        // error that happens to mention nothing about a binary.
        var connectionHint = ErrorHintClassifier.HintFor("Connection error.");
        Assert.NotNull(connectionHint);
        Assert.Contains("provider", connectionHint, StringComparison.OrdinalIgnoreCase);

        var authHint = ErrorHintClassifier.HintFor("Invalid API key provided.");
        Assert.NotNull(authHint);
        Assert.Contains("key", authHint, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every watchdog kill must still land on the timeout hint. The subprocess agent
    /// phrased these as "… timed out after …", which the generic timeout branch
    /// matched. The in-process watchdog phrases all four as "the stage stalled: …",
    /// which contains neither "timed out" nor "timeout", so the cutover silently left
    /// a killed stage with no actionable hint at all.
    /// </summary>
    [Theory]
    [InlineData("the stage stalled: nothing happened for the inactivity window")]
    [InlineData("the stage stalled: a request was in flight but produced no output while work continued")]
    [InlineData("the stage stalled: the model produced nothing for the silence window")]
    [InlineData("the stage stalled: the stage hit its absolute ceiling")]
    public void HintFor_WatchdogKill_ReturnsTimeoutHint(string raw)
    {
        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.NotNull(hint);
        Assert.Contains("maxTurns", hint, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A stalled stage is not a hanging test suite, so it must not get the
    /// test-subset guidance meant for <c>test command timed out</c>.
    /// </summary>
    [Fact]
    public void HintFor_WatchdogKill_IsNotTheTestSubsetHint()
    {
        var hint = ErrorHintClassifier.HintFor("the stage stalled: the stage hit its absolute ceiling");

        Assert.DoesNotContain("targeted subset", hint!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The missing-binary branch keys on <c>exit 127</c>, but the command tool now
    /// reports <c>exit code 127</c>. Both spellings must reach the same hint.
    /// </summary>
    [Fact]
    public void HintFor_ExitCode127_SuggestsInstallingTheTool()
    {
        var hint = ErrorHintClassifier.HintFor("the command exited with code 127");

        Assert.NotNull(hint);
        Assert.Contains("install", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HintFor_UnrecognizedError_ReturnsNull()
    {
        const string raw = "the stage reported an entirely novel failure mode nobody has seen";

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
        const string raw = "Connection error.";

        var combined = ErrorHintClassifier.WithHint(raw);

        // Exact contract: raw text verbatim, blank-line separator, then the hint.
        // Three later tasks render this combined string, so pin the format.
        Assert.Equal($"{raw}\n\n{ErrorHintClassifier.HintFor(raw)}", combined);
        Assert.Contains("provider", combined, StringComparison.OrdinalIgnoreCase);
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
        // A non-test timeout must still match the TimeoutHint (LLM-tuning
        // advice), not the test-subset hint.
        const string raw = "guard timed out";

        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.NotNull(hint);
        Assert.Contains("maxTurns", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HintFor_DifferentlyWordedTimeout_StillReturnsTimeoutHint()
    {
        // The branch keys on the phrase, not on one caller's exact wording.
        const string raw = "Render command timed out: the render produced no output";

        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.NotNull(hint);
        Assert.Contains("maxTurns", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithHint_UnrecognizedError_ReturnsRawUnchanged()
    {
        const string raw = "the stage reported an entirely novel failure mode nobody has seen";

        var combined = ErrorHintClassifier.WithHint(raw);

        Assert.Equal(raw, combined);
    }
}
