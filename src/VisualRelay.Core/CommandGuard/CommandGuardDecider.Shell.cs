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
        if (tokens.Count == 0 || tokens[0].Text != "git")
            return CommandGuardResult.Allow;

        var stripped = StripShell(tokens, command);
        if (stripped is null)
            return CommandGuardResult.Allow;

        return CommandGuardResult.AllowRewritten("shell", stripped);
    }

    /// <summary>
    /// Returns the surgically stripped command string, or null when no
    /// changes were made.
    /// </summary>
    private static string? StripShell(List<ShellToken> tokens, string original)
    {
        var textList = tokens.Select(t => t.Text).ToList();
        var subIdx = FindGitSubcommandIndex(textList);
        if (subIdx is null)
            return null;

        var isCommit = subIdx.Value < tokens.Count
            && tokens[subIdx.Value].Text == "commit";

        var edits = new List<(int Start, int EndExcl, string? Replacement)>();

        for (var i = 0; i < tokens.Count; i++)
        {
            var tok = tokens[i];

            if (tok.Text == "--no-verify")
            {
                edits.Add(RemovalRange(tokens, i));
                continue;
            }

            if (tok.Text == "-n" && isCommit && i >= subIdx.Value)
            {
                edits.Add(RemovalRange(tokens, i));
                continue;
            }

            if (isCommit && i >= subIdx.Value
                && IsCombinedShortFlag(tok.Text) && tok.Text.Contains('n'))
            {
                var stripped = StripN(tok.Text);
                if (stripped.Length == 0)
                    edits.Add(RemovalRange(tokens, i));
                else if (stripped != tok.Text)
                    edits.Add((tok.Start, tok.Start + tok.Length, stripped));
            }
        }

        if (edits.Count == 0)
            return null;

        edits.Sort((a, b) => b.Start.CompareTo(a.Start));

        var chars = original.ToCharArray();
        foreach (var (start, endExcl, replacement) in edits)
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

        return new string(chars);
    }

    /// <summary>
    /// Returns the range to remove: from the end of the previous token to
    /// the end of this token.
    /// </summary>
    private static (int Start, int EndExcl, string? Replacement) RemovalRange(
        List<ShellToken> tokens, int idx)
    {
        var tok = tokens[idx];
        var start = idx > 0 ? tokens[idx - 1].Start + tokens[idx - 1].Length : 0;
        var end = tok.Start + tok.Length;
        return (start, end, null);
    }

    // ── Tokenizer ────────────────────────────────────────────────────

    internal readonly struct ShellToken(string text, int start)
    {
        public string Text { get; } = text;
        public int Start { get; } = start;
        public int Length => Text.Length;
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
