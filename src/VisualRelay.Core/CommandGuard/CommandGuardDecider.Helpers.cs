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
        for (var i = 1; i < tokens.Count; i++)
        {
            var tok = tokens[i];

            if ((tok == "-C" || tok == "-c") && i + 1 < tokens.Count)
            {
                i++;
                continue;
            }

            if (tok == "--git-dir")
            {
                if (i + 1 < tokens.Count)
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
