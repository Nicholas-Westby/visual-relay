using System.Text.Json;

namespace VisualRelay.Core.Execution;

// Pulls the JSON contract out of a stage subagent's raw output. The contract is
// emitted inside a ```json fence, but models format the closing fence
// inconsistently: usually on its own line, sometimes appended directly to the
// JSON line with no preceding newline. Rather than depend on fence placement, we
// brace-match the first JSON value after the opening fence — string- and
// escape-aware, so backticks or braces inside string values never confuse it.
//
// The "```json" marker itself can also appear INSIDE the contract's string
// values (stages whose subject matter is fencing/contracts quote it — this
// killed the stage-contract-retry task twice). A single LastIndexOf anchor
// lands on such an embedded marker and fails, so we walk candidate markers
// last-to-first and accept the first whose value actually parses: the last
// *parseable* block still wins, embedded markers are skipped by construction.
internal static class FencedJsonExtractor
{
    public static string? Extract(string text)
    {
        const string marker = "```json";
        var index = text.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var json = ExtractParseableAt(text, index);
            if (json is not null)
            {
                return json;
            }

            index = index > 0
                ? text.LastIndexOf(marker, index - 1, StringComparison.OrdinalIgnoreCase)
                : -1;
        }

        return null;
    }

    private static string? ExtractParseableAt(string text, int markerIndex)
    {
        var start = text.IndexOf('\n', markerIndex);
        if (start < 0)
        {
            return null;
        }

        var json = ExtractFirstJsonValue(text, start + 1);
        if (json is null)
        {
            return null;
        }

        try
        {
            // Every stage contract is a JSON OBJECT. Requiring an object root
            // here (not just parseable JSON) rejects array fragments that an
            // embedded marker can anchor — e.g. a quoted manifest list — so the
            // marker walk continues to the real block instead of handing the
            // driver a wrong-shaped root it would throw on.
            using var parsed = JsonDocument.Parse(json);
            return parsed.RootElement.ValueKind == JsonValueKind.Object ? json : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractFirstJsonValue(string text, int from)
    {
        var openIndex = -1;
        var open = '\0';
        var close = '\0';
        for (var i = from; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                (open, close, openIndex) = ('{', '}', i);
                break;
            }

            if (text[i] == '[')
            {
                (open, close, openIndex) = ('[', ']', i);
                break;
            }
        }

        if (openIndex < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = openIndex; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == open)
            {
                depth++;
            }
            else if (c == close && --depth == 0)
            {
                return text[openIndex..(i + 1)];
            }
        }

        return null;
    }
}
