using System.Text;
using System.Text.RegularExpressions;

namespace VisualRelay.GitSim.State;

/// <summary>
/// A minimal but faithful <c>.gitignore</c> matcher for the repo-root ignore file:
/// literal paths, <c>dir/</c> directory rules, <c>*.ext</c> globs, <c>**</c> spans,
/// anchoring, and <c>!</c> negation (last match wins). Each pattern is translated
/// to a regex over the full repo-relative path; <c>.git</c> is always ignored. This
/// is the subset the suite's fixtures exercise, per the task spec.
/// </summary>
internal sealed class GitIgnore
{
    private readonly List<(Regex Regex, bool Negated)> _rules = [];

    private GitIgnore() { }

    /// <summary>Loads and parses <c>&lt;root&gt;/.gitignore</c> (empty matcher when absent).</summary>
    public static GitIgnore Load(string root)
    {
        var ignore = new GitIgnore();
        var path = Path.Combine(root, ".gitignore");
        if (!File.Exists(path))
            return ignore;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line[0] == '#')
                continue;

            var negated = line[0] == '!';
            if (negated)
                line = line[1..];
            if (line.Length == 0)
                continue;

            ignore._rules.Add((new Regex(Translate(line), RegexOptions.CultureInvariant), negated));
        }

        return ignore;
    }

    /// <summary>True when <paramref name="relativePath"/> (forward-slash, repo-relative) is ignored.</summary>
    public bool IsIgnored(string relativePath)
    {
        var path = relativePath.Replace('\\', '/').TrimStart('/');
        if (path == ".git" || path.StartsWith(".git/", StringComparison.Ordinal))
            return true;

        var ignored = false;
        foreach (var (regex, negated) in _rules)
            if (regex.IsMatch(path))
                ignored = !negated;
        return ignored;
    }

    private static string Translate(string pattern)
    {
        var dirOnly = pattern.EndsWith('/');
        var body = dirOnly ? pattern[..^1] : pattern;

        // Anchored when it starts with '/' or contains an interior '/'.
        var anchored = body.StartsWith('/') || body.TrimEnd('/').Contains('/');
        body = body.TrimStart('/');

        var sb = new StringBuilder("^");
        sb.Append(anchored ? string.Empty : "(?:.*/)?");

        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            switch (c)
            {
                case '*' when i + 1 < body.Length && body[i + 1] == '*':
                    sb.Append(".*");
                    i++;
                    break;
                case '*':
                    sb.Append("[^/]*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        // A matched node's children are ignored too (git ignores an ignored dir's contents).
        sb.Append("(?:/.*)?$");
        return sb.ToString();
    }
}
