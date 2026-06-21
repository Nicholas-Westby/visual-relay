using System.Text;
using System.Text.RegularExpressions;
using VisualRelay.Core.Execution;

namespace VisualRelay.Core.CommitLint;

/// <summary>
/// The IO-bearing orchestration behind the commit-msg hook tool: resolves the
/// rule tier, gathers the changed-file basenames and the disallowed-substring
/// list, and formats the reference-style violation output. The pure rules live
/// in <see cref="CommitMessageValidator"/>; everything here is git/file reads
/// only — no network, no writes — so it is sandbox-safe mid-run.
/// </summary>
public static partial class CommitLintRunner
{
    /// <summary>
    /// Decides the rule tier: <see cref="RuleTier.Driver"/> only when an active
    /// run exists (<c>.relay/ACTIVE/info.json</c>) and <paramref name="token"/>
    /// (<c>RELAY_COMMIT_TOKEN</c>) equals its nonce — the same comparison
    /// <c>.githooks/pre-commit</c> makes. Otherwise <see cref="RuleTier.Human"/>.
    /// </summary>
    public static Task<RuleTier> DecideTierAsync(
        string repoRoot, string? token, IGitInvoker git, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token))
            return Task.FromResult(RuleTier.Human);

        var infoPath = Path.Combine(repoRoot, ".relay", "ACTIVE", "info.json");
        if (!File.Exists(infoPath))
            return Task.FromResult(RuleTier.Human);

        var nonce = ExtractNonce(File.ReadAllText(infoPath));
        var tier = nonce is not null && string.Equals(nonce, token, StringComparison.Ordinal)
            ? RuleTier.Driver
            : RuleTier.Human;
        return Task.FromResult(tier);
    }

    private static string? ExtractNonce(string infoJson)
    {
        var match = NonceField.Match(infoJson);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Returns the basenames of the staged changed files
    /// (<c>git diff --cached --name-only</c>).
    /// </summary>
    public static async Task<IReadOnlyList<string>> GatherChangedBasenamesAsync(
        string repoRoot, IGitInvoker git, CancellationToken ct)
    {
        var (exit, output, _) = await git.RunAsync(
            repoRoot, ["diff", "--cached", "--name-only"], ct, TimeSpan.FromSeconds(30));
        if (exit != 0)
            return [];

        var names = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = Path.GetFileName(line.Trim());
            if (name.Length > 0)
                names.Add(name);
        }

        return names;
    }

    /// <summary>
    /// Reads <c>disallowed-commit-messages.txt</c> at the repo root if present:
    /// each non-empty, non-<c>#</c> line (trimmed) is a blocked substring.
    /// Returns empty when the file is absent (opt-in).
    /// </summary>
    public static IReadOnlyList<string> ReadDisallowedSubstrings(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "disallowed-commit-messages.txt");
        if (!File.Exists(path))
            return [];

        var result = new List<string>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            result.Add(line);
        }

        return result;
    }

    /// <summary>
    /// Formats violations the way ai-sorcery's reference does:
    /// <c>check-commit-message: N violation(s)</c>, one <c>  - …</c> per
    /// violation, then a pointer to the rules doc.
    /// </summary>
    public static string FormatViolations(IReadOnlyList<Violation> violations)
    {
        var sb = new StringBuilder();
        sb.Append("check-commit-message: ").Append(violations.Count).AppendLine(" violation(s)");
        foreach (var v in violations)
            sb.Append("  - ").AppendLine(v.Message);
        sb.Append("See ").Append(CommitRules.RulesDoc).AppendLine(" for the full ruleset.");
        return sb.ToString();
    }

    [GeneratedRegex(@"""nonce""\s*:\s*""([^""]*)""")]
    private static partial Regex BuildNonceField();

    private static readonly Regex NonceField = BuildNonceField();
}
