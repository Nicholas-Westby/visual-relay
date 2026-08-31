namespace VisualRelay.Core.Agent;

/// <summary>
/// Turns a call to a tool that does not exist into a correction the model can
/// act on. Fired 13 times across the recorded corpus, which is why it is ported
/// and the tool-call scavenger (0 firings) is not.
/// <para>
/// The old error text listed every tool name, which is noise. Naming the closest
/// match is what lets the model fix the call on its next turn.
/// </para>
/// </summary>
public static class UnknownToolRecovery
{
    /// <summary>
    /// Builds the message for an unknown tool name.
    /// </summary>
    /// <param name="requested">The name the model asked for.</param>
    /// <param name="available">Every tool name that does exist.</param>
    /// <returns>Text naming the nearest match, or listing the options.</returns>
    public static string Describe(string requested, IReadOnlyList<string> available)
    {
        var nearest = NearestMatch(requested, available);
        return nearest is null
            ? $"No tool named '{requested}'. Available tools: {string.Join(", ", available.Order(StringComparer.Ordinal))}."
            : $"No tool named '{requested}'. Did you mean '{nearest}'?";
    }

    /// <summary>
    /// The closest available name, or <c>null</c> when nothing is close enough
    /// to be worth suggesting.
    /// </summary>
    /// <param name="requested">The name the model asked for.</param>
    /// <param name="available">Every tool name that does exist.</param>
    /// <returns>The nearest name, or <c>null</c>.</returns>
    public static string? NearestMatch(string requested, IReadOnlyList<string> available)
    {
        if (string.IsNullOrWhiteSpace(requested) || available.Count == 0) return null;

        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in available)
        {
            var distance = Distance(requested, candidate);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = candidate;
        }

        // A suggestion is only useful when it is genuinely close. A third of the
        // longer name is far enough to catch a typo or a plural without
        // proposing an unrelated tool.
        var limit = Math.Max(1, Math.Max(requested.Length, best?.Length ?? 0) / 3);
        return bestDistance <= limit ? best : null;
    }

    /// <summary>Levenshtein distance, case-insensitive.</summary>
    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
