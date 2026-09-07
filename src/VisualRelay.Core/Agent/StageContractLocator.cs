using VisualRelay.Core.Execution;

namespace VisualRelay.Core.Agent;

/// <summary>
/// Every span of an answer that could be the contract object, best candidate
/// first.
/// <para>
/// The fence is the model's own declaration of where the contract is, so a block
/// tagged <c>json</c> is asked first, then any other fenced block, and only then
/// the bare-brace scan. Preferring the fence is what rescues an answer whose
/// prose the scan cannot read: the block is right there, tagged, and parses.
/// </para>
/// </summary>
internal static class StageContractLocator
{
    /// <summary>Candidate blocks, the likeliest contract first.</summary>
    /// <param name="answer">The model's final answer.</param>
    /// <returns>Brace-matched spans, deduplicated, in preference order.</returns>
    public static List<string> Candidates(string answer)
    {
        var candidates = new List<string>();
        var blocks = FencedBlocks(answer);

        // Last fence first: the contract is the last thing the model writes, and
        // an earlier block is an example it was reasoning about.
        for (var i = blocks.Count - 1; i >= 0; i--)
            if (IsJson(blocks[i].Tag))
                Add(candidates, blocks[i].Body);

        for (var i = blocks.Count - 1; i >= 0; i--)
            if (!IsJson(blocks[i].Tag))
                Add(candidates, blocks[i].Body);

        var bare = TopLevelObjects(answer);
        for (var i = bare.Count - 1; i >= 0; i--) Add(candidates, bare[i]);

        return candidates;
    }

    private static bool IsJson(string tag) =>
        tag.Equals("json", StringComparison.OrdinalIgnoreCase);

    private static void Add(List<string> candidates, string body)
    {
        // Brace-match rather than trust the fence: models close a fence on the
        // content line as often as on its own, and a body carrying the stray
        // backticks would never parse.
        var value = FencedJsonExtractor.ExtractFirstJsonValue(body, 0);
        if (value is not null && !candidates.Contains(value, StringComparer.Ordinal))
            candidates.Add(value);
    }

    /// <summary>Every fenced block in the answer, in order, with its info tag.</summary>
    private static List<(string Tag, string Body)> FencedBlocks(string answer)
    {
        var blocks = new List<(string, string)>();
        var from = 0;
        while (true)
        {
            var open = answer.IndexOf("```", from, StringComparison.Ordinal);
            if (open < 0) return blocks;

            var lineEnd = answer.IndexOf('\n', open);
            if (lineEnd < 0) return blocks;

            var close = answer.IndexOf("```", lineEnd + 1, StringComparison.Ordinal);
            if (close < 0) return blocks;

            blocks.Add((answer[(open + 3)..lineEnd].Trim(), answer[(lineEnd + 1)..close]));
            from = close + 3;
        }
    }

    /// <summary>
    /// Every top-level brace-balanced span in the answer, in order.
    /// <para>
    /// String state is tracked only INSIDE an object. Outside one, every
    /// character is prose and a quote is a quotation mark: an answer that quotes
    /// C source, or writes an apostrophe-wrapped <c>'"'</c>, used to flip the
    /// scan into string mode at depth 0, swallow the contract's opening brace,
    /// and cost the whole stage. Tracking from the brace also means the scan
    /// never mistakes a brace inside a string value for the end of an object,
    /// which is the one real ambiguity that remains.
    /// </para>
    /// </summary>
    private static List<string> TopLevelObjects(string answer)
    {
        var found = new List<string>();
        var depth = 0;
        var start = -1;
        var inString = false;
        var escaped = false;

        for (var i = 0; i < answer.Length; i++)
        {
            var c = answer[i];

            if (depth == 0)
            {
                if (c != '{') continue;
                (start, depth, inString, escaped) = (i, 1, false, false);
                continue;
            }

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        found.Add(answer[start..(i + 1)]);
                        start = -1;
                    }

                    break;
            }
        }

        return found;
    }
}
