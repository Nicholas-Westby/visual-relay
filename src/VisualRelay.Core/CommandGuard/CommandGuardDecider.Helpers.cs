namespace VisualRelay.Core.CommandGuard;

public static partial class CommandGuardDecider
{
    // ── Shared helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Returns the index of the git subcommand in <paramref name="tokens"/>,
    /// skipping git's own option arguments (-C, -c, --git-dir, etc.), or
    /// null if no subcommand is found.
    /// </summary>
    private static int? FindGitSubcommandIndex(List<string> tokens)
    {
        return FindGitSubcommandIndexInTextRange(tokens, 0, tokens.Count);
    }

    /// <summary>
    /// Scoped variant: searches for the git subcommand within
    /// <paramref name="startIdx"/>..<paramref name="endIdxExcl"/>.
    /// </summary>
    private static int? FindGitSubcommandIndexInTextRange(
        List<string> tokens, int startIdx, int endIdxExcl)
    {
        for (var i = startIdx + 1; i < endIdxExcl; i++)
        {
            var tok = tokens[i];

            if ((tok == "-C" || tok == "-c") && i + 1 < endIdxExcl)
            {
                i++;
                continue;
            }

            if (tok == "--git-dir")
            {
                if (i + 1 < endIdxExcl)
                    i++;
                continue;
            }

            if (tok.StartsWith("--git-dir=", StringComparison.Ordinal))
                continue;

            if (tok.StartsWith('-'))
                continue;

            return i;
        }

        return null;
    }

    // ── Shell segment helpers ─────────────────────────────────────────

    /// <summary>
    /// A logical token with its position in the original command string.
    /// Operators like <c>&&</c>, <c>||</c>, <c>;</c>, <c>|</c>, <c>(</c>,
    /// <c>)</c> are split out from glued tokens.
    /// </summary>
    internal readonly struct SubToken(string text, int start, bool isOperator)
    {
        public string Text { get; } = text;
        public int Start { get; } = start;
        public int Length => Text.Length;
        public bool IsOperator { get; } = isOperator;
    }

    /// <summary>
    /// Splits shell tokens into sub-tokens, separating shell control
    /// operators from glued neighbors (e.g. <c>true&amp;&amp;git</c> →
    /// <c>true</c>, <c>&amp;&amp;</c>, <c>git</c>).
    /// </summary>
    internal static List<SubToken> SplitOperators(List<ShellToken> tokens)
    {
        // Shell control operators in longest-match-first order.
        // |  must come after || so || is matched before |.
        string[] operators = ["&&", "||", ";", "|", "(", ")"];

        var result = new List<SubToken>();

        foreach (var tok in tokens)
        {
            var remaining = tok.Text;
            var offset = tok.Start;

            while (remaining.Length > 0)
            {
                // Find the earliest operator occurrence.
                var earliestIdx = remaining.Length;
                string? earliestOp = null;
                foreach (var op in operators)
                {
                    var idx = remaining.IndexOf(op, StringComparison.Ordinal);
                    if (idx >= 0 && idx < earliestIdx)
                    {
                        earliestIdx = idx;
                        earliestOp = op;
                    }
                }

                if (earliestIdx > 0)
                {
                    // Text before the operator.
                    result.Add(new SubToken(
                        remaining[..earliestIdx], offset, false));
                    offset += earliestIdx;
                    remaining = remaining[earliestIdx..];
                }

                if (earliestOp is not null)
                {
                    result.Add(new SubToken(earliestOp, offset, true));
                    offset += earliestOp.Length;
                    remaining = remaining[earliestOp.Length..];
                }
                else
                {
                    // No operator in the remaining text — it was already
                    // added above (when earliestIdx > 0), so we're done.
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Returns segment ranges (start, endExcl) into <paramref name="subTokens"/>.
    /// Each segment is a contiguous run of non-operator sub-tokens separated by
    /// shell control operators.
    /// </summary>
    internal static List<(int Start, int EndExcl)> FindShellSegments(
        List<SubToken> subTokens)
    {
        var segments = new List<(int Start, int EndExcl)>();
        var segStart = -1;

        for (var i = 0; i < subTokens.Count; i++)
        {
            if (subTokens[i].IsOperator)
            {
                if (segStart >= 0)
                {
                    segments.Add((segStart, i));
                    segStart = -1;
                }
            }
            else if (segStart < 0)
            {
                segStart = i;
            }
        }

        if (segStart >= 0)
            segments.Add((segStart, subTokens.Count));

        return segments;
    }

    /// <summary>
    /// Scoped git-subcommand search on sub-tokens.  Returns the index
    /// (into sub-tokens) of the subcommand, or null.
    /// </summary>
    private static int? FindGitSubcommandIndexInSubRange(
        List<SubToken> subTokens, int startIdx, int endIdxExcl)
    {
        for (var i = startIdx + 1; i < endIdxExcl; i++)
        {
            var tok = subTokens[i];

            if ((tok.Text == "-C" || tok.Text == "-c") && i + 1 < endIdxExcl)
            {
                i++;
                continue;
            }

            if (tok.Text == "--git-dir")
            {
                if (i + 1 < endIdxExcl)
                    i++;
                continue;
            }

            if (tok.Text.StartsWith("--git-dir=", StringComparison.Ordinal))
                continue;

            if (tok.Text.StartsWith('-'))
                continue;

            return i;
        }

        return null;
    }

    /// <summary>
    /// Skips leading env-var assignment tokens like <c>FOO=1</c> within a
    /// segment. Returns the index of the first non-env-var sub-token.
    /// </summary>
    internal static int SkipEnvPrefix(
        List<SubToken> subTokens, int startIdx, int endIdxExcl)
    {
        var i = startIdx;
        while (i < endIdxExcl)
        {
            var text = subTokens[i].Text;
            var eqIdx = text.IndexOf('=');
            if (eqIdx <= 0) break; // no '=', or '=' at position 0
            // Must look like a valid shell identifier before '='.
            if (!IsValidShellIdentifier(text[..eqIdx])) break;
            i++;
        }

        return i;
    }

    private static bool IsValidShellIdentifier(string s)
    {
        if (s.Length == 0) return false;
        if (s[0] != '_' && s[0] is < 'A' or > 'Z' and < 'a' or > 'z')
            return false;
        for (var j = 1; j < s.Length; j++)
        {
            var c = s[j];
            if (c != '_' && c is < '0' or > '9' and < 'A' or > 'Z' and < 'a' or > 'z')
                return false;
        }

        return true;
    }

    /// <summary>
    /// Returns true when <paramref name="tok"/> looks like a combined short
    /// flag: starts with '-' followed by two or more lowercase letters.
    /// </summary>
    private static bool IsCombinedShortFlag(string tok)
    {
        if (tok.Length < 3 || tok[0] != '-')
            return false;
        for (var i = 1; i < tok.Length; i++)
        {
            if (tok[i] is < 'a' or > 'z')
                return false;
        }

        return true;
    }

    /// <summary>
    /// Strips 'n' from a combined short flag like "-nm" → "-m".
    /// Returns the remaining flag string, or "" when no flags remain.
    /// </summary>
    private static string StripN(string tok)
    {
        var chars = new List<char> { '-' };
        for (var i = 1; i < tok.Length; i++)
        {
            if (tok[i] != 'n')
                chars.Add(tok[i]);
        }

        return chars.Count == 1 ? "" : new string(chars.ToArray());
    }
}
