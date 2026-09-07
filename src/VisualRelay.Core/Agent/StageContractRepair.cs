using System.Globalization;
using System.Text;

namespace VisualRelay.Core.Agent;

/// <summary>
/// The last resort before a stage is lost: three deterministic repairs to a
/// block that is JSON in intent and invalid only in its punctuation.
/// <para>
/// Every rule here comes from a live answer whose reasoning and code were
/// correct and whose work was discarded anyway — prose with literal newlines in
/// a string value, a regex or a template literal quoted inside one, a list left
/// with a trailing comma. Nothing here guesses at missing content: a truncated
/// object still fails, because inventing the rest of it would be inventing the
/// model's work.
/// </para>
/// </summary>
internal static class StageContractRepair
{
    /// <summary>The escapes JSON actually defines, after a backslash.</summary>
    private const string ValidEscapes = "\"\\/bfnrtu";

    /// <summary>
    /// Repairs a candidate block. Returns it unchanged, with no repairs named,
    /// when none of the three rules applies.
    /// </summary>
    /// <param name="json">The candidate block, as the model wrote it.</param>
    /// <returns>The repaired text and the names of the repairs applied.</returns>
    public static (string Json, IReadOnlyList<string> Repairs) Apply(string json)
    {
        var builder = new StringBuilder(json.Length + 16);
        var repairs = new List<string>();
        var inString = false;

        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];

            if (!inString)
            {
                if (c == '"')
                {
                    inString = true;
                    builder.Append(c);
                }
                else if (c == ',' && ClosesNext(json, i + 1))
                {
                    Note(repairs, "trailing commas");
                }
                else
                {
                    builder.Append(c);
                }

                continue;
            }

            if (c == '\\')
            {
                var next = i + 1 < json.Length ? json[i + 1] : '\0';
                if (ValidEscapes.Contains(next, StringComparison.Ordinal))
                {
                    builder.Append(c).Append(next);
                    i++;
                    continue;
                }

                // The backslash was meant literally — a regex class, a shell
                // escape, a template literal's backtick. Doubling it is the only
                // reading that keeps what the model wrote.
                Note(repairs, "invalid escapes");
                builder.Append('\\').Append('\\');
                continue;
            }

            if (c == '"')
            {
                inString = false;
                builder.Append(c);
                continue;
            }

            if (c < ' ')
            {
                Note(repairs, "control characters");
                builder.Append(Escaped(c));
                continue;
            }

            builder.Append(c);
        }

        return (builder.ToString(), repairs);
    }

    private static void Note(List<string> repairs, string name)
    {
        if (!repairs.Contains(name, StringComparer.Ordinal)) repairs.Add(name);
    }

    /// <summary>True when the next non-space character closes an object or array.</summary>
    private static bool ClosesNext(string json, int from)
    {
        for (var i = from; i < json.Length; i++)
        {
            if (char.IsWhiteSpace(json[i])) continue;
            return json[i] is '}' or ']';
        }

        return false;
    }

    private static string Escaped(char c) => c switch
    {
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        '\b' => "\\b",
        '\f' => "\\f",
        _ => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
    };
}
