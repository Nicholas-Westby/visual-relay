using System.Text;
using VisualRelay.GitSim.State;

namespace VisualRelay.GitSim;

/// <summary>The <c>--format</c> placeholder interpreter used by <c>log</c>.</summary>
internal static partial class GitSimCommands
{
    /// <summary>
    /// Expands a git <c>--format</c> string for one commit. Supports the placeholders
    /// production uses — <c>%H %h %T %P</c>, author/committer name/email
    /// (<c>%an %ae %aN %aE %cn %ce %cN %cE</c>) and dates (<c>%ai %aI %ci %cI</c>),
    /// subject/body (<c>%s %b %B</c>), and <c>%n</c>/<c>%%</c>. Literal text (including
    /// the \x1e/\x1f separators embedded in the format) passes through untouched.
    /// </summary>
    public static string FormatCommit(GitObjectStore store, string sha, string format)
    {
        if (!store.TryGetCommit(sha, out var c))
            return string.Empty;

        var sb = new StringBuilder();
        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] != '%' || i + 1 >= format.Length)
            {
                sb.Append(format[i]);
                continue;
            }

            i++;
            switch (format[i])
            {
                case 'H': sb.Append(sha); break;
                case 'h': sb.Append(sha[..7]); break;
                case 'T': sb.Append(c.TreeSha); break;
                case 'P': sb.Append(string.Join(' ', c.Parents)); break;
                case 's': sb.Append(Subject(c.Message)); break;
                case 'b': sb.Append(Body(c.Message)); break;
                case 'B': sb.Append(c.Message.TrimEnd('\n')); break;
                case 'n': sb.Append('\n'); break;
                case '%': sb.Append('%'); break;
                case 'a': AppendPerson(sb, c.Author, Next(format, ref i)); break;
                case 'c': AppendPerson(sb, c.Committer, Next(format, ref i)); break;
                default: sb.Append('%').Append(format[i]); break;
            }
        }

        return sb.ToString();
    }

    private static char Next(string format, ref int i)
    {
        if (i + 1 < format.Length)
            return format[++i];
        return '\0';
    }

    private static void AppendPerson(StringBuilder sb, GitPerson person, char field)
    {
        switch (field)
        {
            case 'n' or 'N': sb.Append(person.Name); break;
            case 'e' or 'E': sb.Append(person.Email); break;
            case 'I': sb.Append(person.When.ToString("yyyy-MM-ddTHH:mm:sszzz")); break;
            case 'i': sb.Append(person.When.ToString("yyyy-MM-dd HH:mm:ss ")).Append(FormatTz(person.When.Offset)); break;
            default: sb.Append("%").Append(field); break;
        }
    }

    private static string Body(string message)
    {
        var blank = message.IndexOf("\n\n", StringComparison.Ordinal);
        return blank < 0 ? string.Empty : message[(blank + 2)..].TrimEnd('\n');
    }
}
