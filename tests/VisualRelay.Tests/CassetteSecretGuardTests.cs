using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// Guard-as-test for <see cref="CassetteSecretGuard"/>. The guard scans every
/// committed file under <c>tests/VisualRelay.Tests/Cassettes/</c> for API-key
/// prefixes, the literal <c>Bearer </c>, the provider key names from
/// <c>.env.example</c>, the live values of any of those currently exported, and
/// key-shaped base64url runs outside the known-safe fields.
/// </summary>
public sealed class CassetteSecretGuardTests
{
    private const string CassettesDir = "tests/VisualRelay.Tests/Cassettes";

    // ── Inline-snippet unit tests ──────────────────────────────────────────

    /// <summary>
    /// Teeth: an <c>sk-</c> prefix followed by a key-shaped tail is flagged.
    /// </summary>
    [Fact]
    public void KeyPrefix_WithKeyShapedTail_IsFlagged()
    {
        var files = new[] { (CassettesDir + "/deepseek/chat/a.json", "  \"auth\": \"sk-0123456789abcdef\"") };

        var violations = CassetteSecretGuard.FindViolations(files);

        var v = Assert.Single(violations);
        Assert.Contains("API-key prefix 'sk-'", v.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>hf_</c> prefix is flagged on the same terms — a token boundary
    /// plus a key-shaped tail.
    /// </summary>
    [Fact]
    public void HuggingFaceKeyPrefix_IsFlagged()
    {
        var files = new[] { (CassettesDir + "/hf/chat/a.json", "  \"auth\": \"hf_QwErTyUiOpAsDf\"") };

        var violations = CassetteSecretGuard.FindViolations(files);

        var v = Assert.Single(violations);
        Assert.Contains("API-key prefix 'hf_'", v.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A prefix sitting mid-word is NOT flagged. Without the token-boundary rule
    /// every <c>task-</c>, <c>disk-</c> and <c>risk-</c> in a recorded prompt
    /// would fire and the guard would be switched off.
    /// </summary>
    [Fact]
    public void KeyPrefix_MidWord_IsNotFlagged()
    {
        var files = new[]
        {
            (CassettesDir + "/deepseek/chat/a.json",
                "  \"prompt\": \"the task-runner writes to disk-cache without risk-taking\""),
        };

        var violations = CassetteSecretGuard.FindViolations(files);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Teeth: the literal <c>Bearer </c> is flagged on any occurrence — the
    /// canonicalizer elides <c>Authorization</c>, so the token has no business
    /// in a cassette at all.
    /// </summary>
    [Fact]
    public void BearerLiteral_IsFlagged()
    {
        var files = new[] { (CassettesDir + "/zai/chat/a.json", "  \"authorization\": \"Bearer xyz\"") };

        var violations = CassetteSecretGuard.FindViolations(files);

        var v = Assert.Single(violations);
        Assert.Contains("literal 'Bearer '", v.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Teeth: a provider key NAME from <c>.env.example</c> is flagged — its
    /// presence means an environment dump reached the cassette.
    /// </summary>
    [Fact]
    public void ProviderKeyName_IsFlagged()
    {
        var files = new[] { (CassettesDir + "/moonshot/chat/a.json", "  \"env\": { \"MOONSHOT_API_KEY\": \"\" }") };

        var violations = CassetteSecretGuard.FindViolations(files);

        var v = Assert.Single(violations);
        Assert.Contains("MOONSHOT_API_KEY", v.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Teeth: a live environment value is flagged, and neither the reason nor the
    /// snippet echoes it — guard failures are printed into CI logs.
    /// </summary>
    [Fact]
    public void LiveKeyValue_IsFlagged_AndNeverEchoed()
    {
        const string liveValue = "Zq7RmVt3PkLdN8Ha";
        var live = new Dictionary<string, string>(StringComparer.Ordinal) { ["ZAI_API_KEY"] = liveValue };
        var files = new[] { (CassettesDir + "/zai/chat/a.json", "  \"token\": \"" + liveValue + "\"") };

        var violations = CassetteSecretGuard.FindViolations(files, live);

        var v = Assert.Single(violations);
        Assert.Contains("live value of 'ZAI_API_KEY'", v.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(liveValue, v.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(liveValue, v.Snippet, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unset key contributes no rule: the same content with no live values
    /// supplied produces nothing.
    /// </summary>
    [Fact]
    public void UnsetKeys_ContributeNoRule()
    {
        var files = new[] { (CassettesDir + "/zai/chat/a.json", "  \"token\": \"Zq7RmVt3PkLdN8Ha\"") };

        var violations = CassetteSecretGuard.FindViolations(files, liveKeyValues: null);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Teeth: a 32+ character base64url run under an ordinary field is flagged,
    /// and the snippet reports its length and column rather than its bytes.
    /// </summary>
    [Fact]
    public void Base64UrlRun_OutsideSafeField_IsFlagged()
    {
        const string run = "aB3dE6gH9jK2mN5pQ8sT1vW4yZ7bC0eF3hJ6lM9o";
        var files = new[] { (CassettesDir + "/deepseek/chat/a.json", "  \"note\": \"" + run + "\"") };

        var violations = CassetteSecretGuard.FindViolations(files);

        var v = Assert.Single(violations);
        Assert.Contains("base64url run", v.Reason, StringComparison.Ordinal);
        Assert.Contains("under field 'note'", v.Snippet, StringComparison.Ordinal);
        Assert.DoesNotContain(run, v.Snippet, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same run under a default-safe field — the cassette's own
    /// content-addressed key — is NOT flagged.
    /// </summary>
    [Fact]
    public void Base64UrlRun_UnderDefaultSafeField_IsNotFlagged()
    {
        var files = new[]
        {
            (CassettesDir + "/deepseek/chat/a.json",
                "  \"key\": \"aB3dE6gH9jK2mN5pQ8sT1vW4yZ7bC0eF3hJ6lM9o\""),
        };

        var violations = CassetteSecretGuard.FindViolations(files);

        Assert.Empty(violations);
    }

    /// <summary>
    /// The safe-field list is a parameter, not a constant buried in the matcher:
    /// a caller-supplied list exempts its own field and re-arms the defaults.
    /// </summary>
    [Fact]
    public void SafeFieldList_IsConfigurablePerCall()
    {
        const string run = "aB3dE6gH9jK2mN5pQ8sT1vW4yZ7bC0eF3hJ6lM9o";
        var files = new[]
        {
            (CassettesDir + "/vision/chat/a.json", "  \"imageChunk\": \"" + run + "\""),
            (CassettesDir + "/vision/chat/b.json", "  \"key\": \"" + run + "\""),
        };

        var violations = CassetteSecretGuard.FindViolations(files, safeFieldNames: ["imageChunk"]);

        var v = Assert.Single(violations);
        Assert.EndsWith("b.json", v.Path, StringComparison.Ordinal);
    }

    /// <summary>
    /// A run on a continuation line inherits the field carried from the line that
    /// opened the array, so a chunk list under a safe field stays exempt.
    /// </summary>
    [Fact]
    public void Base64UrlRun_InheritsFieldFromEnclosingArray()
    {
        var files = new[]
        {
            (CassettesDir + "/deepseek/stream/a.json",
                "  \"chunks\": [\n    \"aB3dE6gH9jK2mN5pQ8sT1vW4yZ7bC0eF3hJ6lM9o\"\n  ]"),
        };

        var violations = CassetteSecretGuard.FindViolations(files);

        Assert.Empty(violations);
    }

    /// <summary>
    /// Happy path: a properly redacted cassette produces zero violations.
    /// </summary>
    [Fact]
    public void RedactedCassette_ReportsZero()
    {
        var files = new[]
        {
            (CassettesDir + "/deepseek/chat/a.json", """
                {
                  "canonicalizerVersion": 1,
                  "key": "3f2a9c1d4e6b8a0f2c4e6a8b0d2f4a6c8e0b2d4f6a8c0e2b4d6f8a0c2e4b6d8f",
                  "request": { "model": "deepseek-chat", "path": "/v1/chat/completions" },
                  "response": { "status": 200, "body": "{\"choices\":[]}" }
                }
                """),
        };

        var violations = CassetteSecretGuard.FindViolations(files, CassetteSecretGuard.ReadLiveKeyValues());

        Assert.Empty(violations);
    }

    /// <summary>
    /// No cassettes at all — the state before the first recording — is a
    /// vacuous pass, not an error.
    /// </summary>
    [Fact]
    public void NoCassettes_ReportsZero()
    {
        var violations = CassetteSecretGuard.FindViolations([]);

        Assert.Empty(violations);
    }

    // ── Live-directory test ────────────────────────────────────────────────

    /// <summary>
    /// The live enforcing gate: every committed file under
    /// <c>tests/VisualRelay.Tests/Cassettes/</c> is scanned with the live
    /// environment's key values folded in. Passes vacuously while the directory
    /// does not exist.
    /// </summary>
    [Fact]
    public void LiveCassettes_CarryNoSecrets()
    {
        var root = RepoSetup.Root;
        var cassettes = Path.Combine(root, "tests", "VisualRelay.Tests", "Cassettes");

        var files = new List<(string Path, string Content)>();
        if (Directory.Exists(cassettes))
        {
            foreach (var file in Directory.EnumerateFiles(cassettes, "*", SearchOption.AllDirectories))
                files.Add((Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllText(file)));
        }

        var violations = CassetteSecretGuard.FindViolations(files, CassetteSecretGuard.ReadLiveKeyValues());

        Assert.True(violations.Count == 0,
            $"CassetteSecretGuard found secret-shaped material in {files.Count} committed cassette(s) " +
            "(redact at record time; never commit a live key):\n" +
            string.Join("\n", violations.Select(v => $"{v.Path}:{v.Line}: {v.Reason} — {v.Snippet}")));
    }
}
