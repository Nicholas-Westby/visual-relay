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
    /// On ANY internal error, returns <see cref="CommandGuardResult.Allow"/>
    /// (fail-open — never break the agent).
    /// </summary>
    public static CommandGuardResult Decide(JsonElement payload)
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
            return CommandGuardResult.Allow;
        }
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
