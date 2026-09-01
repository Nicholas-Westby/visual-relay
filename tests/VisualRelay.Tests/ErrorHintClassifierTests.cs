using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class ErrorHintClassifierTests
{
    [Fact]
    public void HintFor_ConnectionError_SuggestsBackendIsUnreachable()
    {
        // A failed model call surfaces the provider's own message verbatim.
        const string raw = "Connection error.";

        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.NotNull(hint);
        Assert.Contains("provider", hint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("4000", hint);
    }

    [Fact]
    public void HintFor_ConnectionRefused_SuggestsBackendIsUnreachable()
    {
        const string raw = "the connection to balanced failed: Connection refused (api.z.ai:443)";

        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.NotNull(hint);
        Assert.Contains("provider", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HintFor_Timeout_SuggestsRaisingTimeoutOrCheckingLatency()
    {
        const string raw = "command timed out after 240s, the timeout that was actually applied.";

        var hint = ErrorHintClassifier.HintFor(raw);

        Assert.NotNull(hint);
        Assert.Contains("maxTurns", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HintFor_AuthFailure_SuggestsProviderKey()
    {
        const string raw = "provider returned HTTP 401 with an empty body";

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
        // nono set up the sandbox fine, then could not exec the program the stage
        // asked for because it isn't on PATH. The actionable fix is installing
        // that tool, not bypassing a sandbox-permission rule.
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
