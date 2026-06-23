using System.Text.Json;

namespace VisualRelay.Core.CommandGuard;

/// <summary>
/// Pure, IO-free strip logic. Reads a swival command-middleware payload and
/// returns a verdict: pass-through allow, rewritten allow (with hook-bypass
/// flags stripped), or deny.
/// </summary>
public static partial class CommandGuardDecider
{
    /// <summary>
    /// Decides the verdict for a swival command-middleware payload.
    /// On internal error, fails OPEN for non-commit commands and fails
    /// CLOSED (deny) for git commit commands when <paramref name="rawJson"/>
    /// is supplied so the payload can be identified.
    /// </summary>
    /// <param name="payload">The swival middleware JSON payload.</param>
    /// <param name="rawJson">
    /// The raw JSON string (available from Program.cs stdin). Used only in
    /// the catch path for fail-closed git-commit detection. Optional;
    /// callers that don't have the raw string (unit tests without it) get
    /// fail-open unconditionally.
    /// </param>
    public static CommandGuardResult Decide(JsonElement payload, string? rawJson = null)
    {
        try
        {
            if (payload.ValueKind != JsonValueKind.Object)
                return CommandGuardResult.Allow;

            if (!payload.TryGetProperty("mode", out var modeEl) || modeEl.ValueKind != JsonValueKind.String)
                return CommandGuardResult.Allow;
            var mode = modeEl.GetString()!;

            if (!payload.TryGetProperty("command", out var cmdEl) || cmdEl.ValueKind == JsonValueKind.Null)
                return CommandGuardResult.Allow;

            return mode switch
            {
                "argv" => DecideArgv(cmdEl),
                "shell" => DecideShell(cmdEl),
                _ => CommandGuardResult.Allow
            };
        }
        catch
        {
            // Fail-closed for git commit: a broken guard must not silently
            // re-enable the hook-bypass it exists to block.  Non-git commands
            // still fail-open so the agent's other work isn't wedged.
            if (rawJson is not null && LooksLikeGitCommitFromJson(rawJson))
                return CommandGuardResult.Deny("command-guard internal error for git commit");

            return CommandGuardResult.Allow;
        }
    }

    /// <summary>
    /// Best-effort check: does <paramref name="rawJson"/> look like a
    /// git-commit command?  Scans the raw JSON string for <c>git</c>
    /// near <c>commit</c> as plain substrings (not quoted JSON tokens)
    /// so both argv-mode and shell-mode payloads are detected.
    /// Only used in the catch path when the payload is not safely
    /// inspectable.
    /// </summary>
    private static bool LooksLikeGitCommitFromJson(string rawJson)
    {
        // Quick substring scan: look for "git" and "commit" as plain
        // words in the JSON.  Avoids full re-parse in the error path.
        var gitIdx = rawJson.IndexOf("git", StringComparison.Ordinal);
        if (gitIdx < 0) return false;
        var commitIdx = rawJson.IndexOf("commit", StringComparison.Ordinal);
        return commitIdx >= 0 && Math.Abs(commitIdx - gitIdx) < 200;
    }

    // ── argv mode ──────────────────────────────────────────────────────

    private static CommandGuardResult DecideArgv(JsonElement cmdEl)
    {
        if (cmdEl.ValueKind != JsonValueKind.Array)
            return CommandGuardResult.Allow;

        var tokens = new List<string>();
        foreach (var el in cmdEl.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String)
                return CommandGuardResult.Allow;
            tokens.Add(el.GetString()!);
        }

        if (tokens.Count == 0 || tokens[0] != "git")
            return CommandGuardResult.Allow;

        var result = StripArgv(tokens);
        if (result is null)
            return CommandGuardResult.Allow;

        return CommandGuardResult.AllowRewritten("argv", result);
    }

    /// <summary>
    /// Returns the filtered list when stripping was applied, or null when
    /// nothing changed (pass-through).
    /// </summary>
    private static string[]? StripArgv(List<string> tokens)
    {
        var subIdx = FindGitSubcommandIndex(tokens);
        if (subIdx is null)
            return null;

        var isCommit = subIdx.Value < tokens.Count
            && tokens[subIdx.Value] == "commit";

        var changed = false;
        var result = new List<string>(tokens.Count);

        for (var i = 0; i < tokens.Count; i++)
        {
            var tok = tokens[i];

            // Defect 5 (deferred): --no-verify is stripped unconditionally
            // even when it sits in a value position (e.g. git commit -F --no-verify).
            // Scoping to option position is low-priority; the threat model makes
            // filenames named --no-verify vanishingly unlikely.
            if (tok == "--no-verify")
            {
                changed = true;
                continue;
            }

            if (tok == "-n" && isCommit && i >= subIdx.Value)
            {
                changed = true;
                continue;
            }

            if (isCommit && i >= subIdx.Value
                && IsCombinedShortFlag(tok) && tok.Contains('n'))
            {
                var stripped = StripN(tok);
                if (stripped.Length == 0)
                {
                    changed = true;
                    continue;
                }

                if (stripped != tok)
                {
                    changed = true;
                    result.Add(stripped);
                    continue;
                }
            }

            result.Add(tok);
        }

        return changed ? result.ToArray() : null;
    }
}
