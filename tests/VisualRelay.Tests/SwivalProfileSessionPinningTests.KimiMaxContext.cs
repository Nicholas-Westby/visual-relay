using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed partial class SwivalProfileSessionPinningTests
{
    // ── Kimi K2.7 Code context window ─────────────────────────────────────

    /// <summary>
    /// Extracts <c>max_context_tokens = …</c> for each profile from a
    /// swival.toml string, keyed by profile name.
    /// </summary>
    private static Dictionary<string, int> ParseSwivalProfileMaxContextTokens(string toml)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        string? currentProfile = null;

        foreach (var raw in toml.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.StartsWith("[profiles.", StringComparison.Ordinal) && line.EndsWith(']'))
            {
                currentProfile = line["[profiles.".Length..^1];
            }
            else if (currentProfile != null
                     && line.StartsWith("max_context_tokens = ", StringComparison.Ordinal))
            {
                var val = line["max_context_tokens = ".Length..].Trim();
                if (int.TryParse(val, out var tokens))
                    result[currentProfile] = tokens;

                currentProfile = null; // consume the line
            }
        }

        return result;
    }

    /// <summary>
    /// Kimi K2.7 Code has a 256 K-token context window (262 144 tokens).
    /// The <c>[profiles.kimi]</c> block in DefaultToml must reflect the
    /// true upstream ceiling so swival never under-budgets its context.
    /// </summary>
    [Fact]
    public void DefaultToml_KimiProfile_MaxContextTokensIs256000()
    {
        var toml = SwivalProfileSession.DefaultToml;
        var maxTokens = ParseSwivalProfileMaxContextTokens(toml);

        Assert.True(maxTokens.TryGetValue("kimi", out var kimiMax),
            "DefaultToml must contain a [profiles.kimi] block with max_context_tokens");
        Assert.Equal(256000, kimiMax);
    }
}
