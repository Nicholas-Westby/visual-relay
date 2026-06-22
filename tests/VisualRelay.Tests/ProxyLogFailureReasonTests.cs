using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

// Unit tests for the litellm-proxy-log consultation seam. When swival's own
// merged stdout/stderr carries NO failure marker (a model-backend error lives
// only in the proxy log, never in swival's output), the diagnostics layer reads
// the proxy log tail and surfaces the auth/HTTP cause instead of echoing the
// prompt. Pure string-in/string-out — no live proxy, no filesystem.
public sealed class ProxyLogFailureReasonTests
{
    [Fact]
    public void ExtractProxyLogReason_AuthenticationError_NamesTheAuthCause()
    {
        // A 401 from the provider, as litellm logs it (JSON line, the real shape).
        var log = string.Join('\n', new[]
        {
            "{\"message\": \"127.0.0.1:54606 - \\\"POST /v1/chat/completions HTTP/1.1\\\" 200\", \"level\": \"INFO\"}",
            "{\"message\": \"litellm.AuthenticationError: AuthenticationError: HuggingfaceException - " +
                "Invalid credentials in Authorization header\", \"level\": \"ERROR\"}",
            "{\"message\": \"127.0.0.1:54606 - \\\"POST /v1/chat/completions HTTP/1.1\\\" 401\", \"level\": \"INFO\"}",
        });

        var reason = SwivalSubagentRunner.ExtractProxyLogReason(log);

        Assert.NotNull(reason);
        // The surfaced cause names the auth failure, not a generic line.
        Assert.Contains("AuthenticationError", reason, StringComparison.Ordinal);
        Assert.Contains("401", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractProxyLogReason_OnlyHealthyAccessLines_ReturnsNull()
    {
        // A proxy log with nothing but successful readiness/completions traffic
        // has no failure to surface — the consultation must stay silent so a
        // healthy proxy never invents a cause.
        var log = string.Join('\n', new[]
        {
            "{\"message\": \"127.0.0.1:54583 - \\\"GET /health/readiness HTTP/1.1\\\" 200\", \"level\": \"INFO\"}",
            "{\"message\": \"127.0.0.1:54606 - \\\"POST /v1/chat/completions HTTP/1.1\\\" 200\", \"level\": \"INFO\"}",
        });

        var reason = SwivalSubagentRunner.ExtractProxyLogReason(log);

        Assert.Null(reason);
    }

    [Fact]
    public void ExtractProxyLogReason_EmptyOrWhitespace_ReturnsNull()
    {
        Assert.Null(SwivalSubagentRunner.ExtractProxyLogReason(string.Empty));
        Assert.Null(SwivalSubagentRunner.ExtractProxyLogReason("   \n\n  "));
    }

    // ── Composed nonzero-exit reason (what the user actually sees) ──────────

    // The exact prompt scaffold the runner sends; the incident's "diagnostic" was
    // just this echoed back. Detecting that swival's tail is contained in the prompt
    // is how the builder knows the output is a prompt echo, not a real diagnostic.
    private const string SentPrompt =
        "# Relay stage 0: Rewrite\nTask: improve-live-tiers-ui\n\n## Task input\n" +
        "Rewrite this task spec in place. Overwrite only the file at the path.\n\n## Manifest\nspec.md";

    [Fact]
    public void BuildNonzeroExitReason_PromptEcho_FoldsInProxyAuthError_NotPromptEcho()
    {
        // The ground-truth incident: swival's merged output is just the echoed
        // PROMPT (no failure marker) while the real 401 lives in the proxy log. The
        // surfaced reason must name the model/auth error and must NOT echo the prompt.
        const string swivalOutput = "## Task input\nRewrite this task spec in place. Overwrite only the file at the path.";
        const string proxyLog =
            "{\"message\": \"litellm.AuthenticationError: HuggingfaceException - " +
            "Invalid credentials in Authorization header\", \"level\": \"ERROR\"}\n" +
            "{\"message\": \"127.0.0.1:54606 - \\\"POST /v1/chat/completions HTTP/1.1\\\" 401\"}";

        var reason = SwivalSubagentRunner.BuildNonzeroExitReason(
            exitCode: 1, swivalOutput, SentPrompt, proxyLog,
            killedOutputPath: "/tmp/x/stage0-attempt1.killed-output.txt");

        Assert.Contains("model call failed", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AuthenticationError", reason, StringComparison.Ordinal);
        // The prompt echo must NOT be the surfaced cause.
        Assert.DoesNotContain("Rewrite this task spec in place", reason, StringComparison.Ordinal);
        // The breadcrumb to the preserved full output is retained.
        Assert.Contains("stage0-attempt1.killed-output.txt", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildNonzeroExitReason_SwivalHasOwnMarker_KeepsItAndDoesNotConsultProxy()
    {
        // When swival's output already carries a real failure marker, that wins —
        // a stale/unrelated proxy-log error must not override the genuine cause.
        const string swivalOutput = "nono: Command execution failed: swival: cannot find binary path";
        const string proxyLog =
            "{\"message\": \"litellm.AuthenticationError: stale prior error\", \"level\": \"ERROR\"}";

        var reason = SwivalSubagentRunner.BuildNonzeroExitReason(
            exitCode: 1, swivalOutput, SentPrompt, proxyLog, killedOutputPath: null);

        Assert.StartsWith("swival exit 1:", reason, StringComparison.Ordinal);
        Assert.Contains("cannot find binary path", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("stale prior error", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildNonzeroExitReason_GenuineNonMarkerOutput_KeepsItAndDoesNotConsultProxy()
    {
        // swival emitted a real (non-marker, non-echo) line. It is a usable
        // diagnostic, so it is surfaced verbatim and the proxy log is NOT consulted —
        // the result must not depend on whatever the machine's proxy log holds.
        const string swivalOutput = "profile was available";
        const string proxyLog =
            "{\"message\": \"litellm.AuthenticationError: unrelated prior error\", \"level\": \"ERROR\"}";

        var reason = SwivalSubagentRunner.BuildNonzeroExitReason(
            exitCode: 2, swivalOutput, SentPrompt, proxyLog, killedOutputPath: null);

        Assert.Equal("swival exit 2: profile was available", reason);
    }

    [Fact]
    public void BuildNonzeroExitReason_PromptEchoAndHealthyProxy_PointsAtProxyLogAndModelCall()
    {
        // No usable diagnostic (prompt echo) AND the proxy log shows only healthy
        // traffic: still better than a prompt echo — say the diagnostic was unusable
        // and point at a likely failed model call rather than parroting the prompt.
        const string swivalOutput = "## Task input\nRewrite this task spec in place. Overwrite only the file at the path.";
        const string proxyLog =
            "{\"message\": \"127.0.0.1:54606 - \\\"POST /v1/chat/completions HTTP/1.1\\\" 200\"}";

        var reason = SwivalSubagentRunner.BuildNonzeroExitReason(
            exitCode: 1, swivalOutput, SentPrompt, proxyLog, killedOutputPath: null);

        Assert.Contains("model call", reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Rewrite this task spec in place", reason, StringComparison.Ordinal);
    }
}
