using System.Text.Json;

namespace VisualRelay.Core.Agent;

/// <summary>What reading a stage's contract out of its answer produced.</summary>
/// <param name="Json">The contract object, or <c>null</c> when none was usable.</param>
/// <param name="Error">Why it could not be read, in terms the model can act on.</param>
public sealed record StageContractResult(string? Json, string? Error)
{
    /// <summary>True when a contract came out.</summary>
    public bool Succeeded => Json is not null;
}

/// <summary>
/// Reads a stage's contract object out of the model's final answer.
/// <para>
/// This is much smaller than what it replaces, and deliberately so. The previous
/// extractor had to fish a fenced block out of a subprocess's STDOUT, which
/// carried sandbox banners, deny advisories and echoes of the prompt itself, and
/// needed a distiller, a prompt-echo heuristic and a last-to-first fence walk to
/// cope. In process there is no stdout: this reads the model's own answer, and
/// the only real ambiguity left is a fence marker appearing inside a string
/// value, which the last-block-first walk still handles.
/// </para>
/// </summary>
public static class StageContractReader
{
    /// <summary>
    /// Reads the contract from an answer and checks it has the keys the stage
    /// asked for.
    /// </summary>
    /// <param name="answer">The model's final answer.</param>
    /// <param name="contract">The stage's contract line, naming the required keys.</param>
    /// <returns>The contract JSON, or the reason it could not be read.</returns>
    public static StageContractResult Read(string answer, string contract)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return new StageContractResult(null, "the model returned an empty answer");

        var json = ExtractObject(answer);
        if (json is null)
            return new StageContractResult(
                null, "no JSON object found in the answer; the contract block is required");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return new StageContractResult(null, $"the contract block is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new StageContractResult(
                    null, $"the contract must be a JSON object, but got {document.RootElement.ValueKind}");

            foreach (var key in RequiredKeys(contract))
                if (!document.RootElement.TryGetProperty(key, out _))
                    return new StageContractResult(
                        null, $"the contract is missing the required key \"{key}\"");
        }

        return new StageContractResult(json, null);
    }

    /// <summary>
    /// The keys a contract line requires. A key written <c>"name"?:</c> is
    /// optional, which is how the stage-11 contract marks its amend list.
    /// </summary>
    private static IEnumerable<string> RequiredKeys(string contract)
    {
        for (var i = 0; i < contract.Length; i++)
        {
            if (contract[i] != '"') continue;
            var end = contract.IndexOf('"', i + 1);
            if (end < 0) yield break;

            var name = contract[(i + 1)..end];
            var after = end + 1;
            while (after < contract.Length && contract[after] == ' ') after++;

            // A colon marks a key; a question mark before it marks it optional.
            if (after < contract.Length && contract[after] == ':'
                && name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c == '_'))
                yield return name;

            i = end;
        }
    }

    /// <summary>
    /// The last JSON object in the answer that actually parses.
    /// <para>
    /// Both halves matter. Walking from the END finds the contract, which is the
    /// last thing the model writes. Requiring the candidate to PARSE is what
    /// stops a brace in prose from winning: a sentence like "returns {file, line}"
    /// balances perfectly and is not JSON, and accepting it reported a parse
    /// error against prose instead of finding the contract sitting just above it.
    /// </para>
    /// </summary>
    private static string? ExtractObject(string answer)
    {
        for (var start = answer.LastIndexOf('{'); start >= 0; start = answer.LastIndexOf('{', start - 1))
        {
            var candidate = MatchObject(answer, start);
            if (candidate is not null && ParsesAsObject(candidate)) return candidate;
            if (start == 0) break;
        }

        return null;
    }

    /// <summary>Whether a candidate is a JSON object, not merely balanced text.</summary>
    private static bool ParsesAsObject(string candidate)
    {
        try
        {
            using var document = JsonDocument.Parse(candidate);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Scans a balanced object from an opening brace, string-aware.</summary>
    private static string? MatchObject(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return text[start..(i + 1)];
        }

        return null;
    }
}
