namespace VisualRelay.Core.CommandGuard;

public static partial class CommandGuardDecider
{
    // ── shell mode ─────────────────────────────────────────────────────

    private static CommandGuardResult DecideShell(System.Text.Json.JsonElement cmdEl)
    {
        if (cmdEl.ValueKind != System.Text.Json.JsonValueKind.String)
            return CommandGuardResult.Allow;

        var command = cmdEl.GetString()!;
        if (string.IsNullOrWhiteSpace(command))
            return CommandGuardResult.Allow;

        var tokens = Tokenize(command);
        if (tokens.Count == 0)
            return CommandGuardResult.Allow;

        // Split operators and find segments.
        var subTokens = SplitOperators(tokens);
        var segments = FindShellSegments(subTokens);

        // Collect all edits from all segments, then apply once.
        var allEdits = new List<(int Start, int EndExcl, string? Replacement)>();

        foreach (var seg in segments)
        {
            CollectSegmentEdits(subTokens, seg, allEdits);
        }

        if (allEdits.Count == 0)
            return CommandGuardResult.Allow;

        // Apply edits in descending start order (byte-exact surgical removal).
        allEdits.Sort((a, b) => b.Start.CompareTo(a.Start));

        var chars = command.ToCharArray();
        foreach (var (start, endExcl, replacement) in allEdits)
        {
            var removeLen = endExcl - start;
            if (replacement is null)
            {
                Array.Copy(chars, endExcl, chars, start, chars.Length - endExcl);
                Array.Resize(ref chars, chars.Length - removeLen);
            }
            else
            {
                var replLen = replacement.Length;
                var newLen = chars.Length - removeLen + replLen;
                var newChars = new char[newLen];
                Array.Copy(chars, 0, newChars, 0, start);
                replacement.CopyTo(0, newChars, start, replLen);
                Array.Copy(chars, endExcl, newChars, start + replLen, chars.Length - endExcl);
                chars = newChars;
            }
        }

        return CommandGuardResult.AllowRewritten("shell", new string(chars));
    }

    /// <summary>
    /// Adds removal/replacement edits for a single segment to
    /// <paramref name="edits"/>.  Does nothing if the segment is not a
    /// git commit.
    /// </summary>
    private static void CollectSegmentEdits(
        List<SubToken> subTokens,
        (int Start, int EndExcl) segment,
        List<(int Start, int EndExcl, string? Replacement)> edits)
    {
        var segStart = segment.Start;
        var segEnd = segment.EndExcl;
        if (segStart >= segEnd)
            return;

        // Skip env-var prefix to find the actual command.
        var cmdStart = SkipEnvPrefix(subTokens, segStart, segEnd);
        if (cmdStart >= segEnd)
            return;

        if (subTokens[cmdStart].Text != "git")
            return;

        var subIdx = FindGitSubcommandIndexInSubRange(subTokens, cmdStart, segEnd);
        if (subIdx is null)
            return;

        var isCommit = subTokens[subIdx.Value].Text == "commit";

        for (var i = segStart; i < segEnd; i++)
        {
            var tok = subTokens[i];

            // Defect 5 (deferred): --no-verify is stripped unconditionally
            // even in value position.  Low priority; see argv path comment.
            if (tok.Text == "--no-verify")
            {
                edits.Add(SubRemovalRange(subTokens, i, segStart));
                continue;
            }

            if (tok.Text == "-n" && isCommit && i >= subIdx.Value)
            {
                edits.Add(SubRemovalRange(subTokens, i, segStart));
                continue;
            }

            if (isCommit && i >= subIdx.Value
                && IsCombinedShortFlag(tok.Text) && tok.Text.Contains('n'))
            {
                var stripped = StripN(tok.Text);
                if (stripped.Length == 0)
                    edits.Add(SubRemovalRange(subTokens, i, segStart));
                else if (stripped != tok.Text)
                    edits.Add((tok.Start, tok.Start + tok.Length, stripped));
            }
        }
    }

    /// <summary>
    /// Returns the range to remove for a sub-token: from the end of the
    /// previous sub-token (or segment start's sub-token start) to the end
    /// of this sub-token.
    /// </summary>
    private static (int Start, int EndExcl, string? Replacement) SubRemovalRange(
        List<SubToken> subTokens, int idx, int segStart)
    {
        var tok = subTokens[idx];
        var start = idx > segStart
            ? subTokens[idx - 1].Start + subTokens[idx - 1].Length
            : subTokens[segStart].Start;
        var end = tok.Start + tok.Length;
        return (start, end, null);
    }

    // ── Tokenizer ────────────────────────────────────────────────────

    internal readonly struct ShellToken(string text, int start)
    {
        public string Text { get; } = text;
        public int Start { get; } = start;
    }

    /// <summary>
    /// Tokenizes a shell command string, respecting single and double
    /// quotes.
    /// </summary>
    internal static List<ShellToken> Tokenize(string command)
    {
        var tokens = new List<ShellToken>();
        var i = 0;
        while (i < command.Length)
        {
            if (char.IsWhiteSpace(command[i]))
            {
                i++;
                continue;
            }

            var start = i;

            if (command[i] == '"')
            {
                i++;
                while (i < command.Length)
                {
                    if (command[i] == '\\' && i + 1 < command.Length)
                    {
                        i += 2;
                        continue;
                    }

                    if (command[i] == '"')
                    {
                        i++;
                        break;
                    }

                    i++;
                }

                tokens.Add(new ShellToken(command[start..i], start));
            }
            else if (command[i] == '\'')
            {
                i++;
                while (i < command.Length && command[i] != '\'')
                    i++;
                if (i < command.Length)
                    i++;
                tokens.Add(new ShellToken(command[start..i], start));
            }
            else
            {
                while (i < command.Length && !char.IsWhiteSpace(command[i]))
                    i++;
                tokens.Add(new ShellToken(command[start..i], start));
            }
        }

        return tokens;
    }
}
