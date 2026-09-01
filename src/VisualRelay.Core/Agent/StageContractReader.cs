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
/// This is much smaller than what it replaces. The previous extractor had to
/// fish a fenced block out of a subprocess's STDOUT, which carried sandbox
/// banners, deny advisories and echoes of the prompt itself. In process there is
/// no stdout: this reads the model's own answer.
/// </para>
/// <para>
/// The scan runs FORWARD from the start of the answer, tracking string state, so
/// it only ever considers braces that are genuinely at the top level. Searching
/// backwards from the last brace is what an earlier version did, and it is
/// wrong: the last brace is frequently INSIDE a string value — a plan describing
/// code says things like <c>returns {"a": 1}</c> — and a scan starting there has
/// no idea it is inside a string, so it can lift a fragment out of the middle of
/// the contract and hand back an object that parses but is not the contract. A
/// benchmark run failed exactly that way, reporting a missing key against text
/// that had the key.
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

        var required = RequiredKeys(contract).ToList();
        var candidates = TopLevelObjects(answer);
        if (candidates.Count == 0)
            return new StageContractResult(
                null, "no JSON object found in the answer; the contract block is required");

        // Prefer the LAST candidate that satisfies the contract: the contract is
        // the last thing the model writes, and preferring one that fits means an
        // example object earlier in the prose can never win.
        string? lastParseable = null;
        for (var i = candidates.Count - 1; i >= 0; i--)
        {
            if (!TryReadObject(candidates[i], out var element)) continue;

            lastParseable ??= candidates[i];
            if (required.All(key => element.TryGetProperty(key, out _)))
                return new StageContractResult(candidates[i], null);
        }

        if (lastParseable is null)
            return new StageContractResult(
                null, "the contract block is not valid JSON");

        // Something parsed but did not fit; name the first key it lacks so the
        // model has something specific to correct.
        TryReadObject(lastParseable, out var best);
        var missing = required.FirstOrDefault(key => !best.TryGetProperty(key, out _));
        return new StageContractResult(
            null,
            missing is null
                ? "the contract object did not match the required shape"
                : $"the contract is missing the required key \"{missing}\"");
    }

    private static bool TryReadObject(string json, out JsonElement element)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                element = default;
                return false;
            }

            element = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }

    /// <summary>
    /// The keys a contract line requires. A key written <c>"name"?:</c> is
    /// optional, which is how the fix-verify contract marks its amend list.
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
    /// Every top-level brace-balanced span in the answer, in order. One forward,
    /// string-aware pass, so a brace inside a string value is never mistaken for
    /// the start or end of an object.
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
                    if (depth == 0) start = i;
                    depth++;
                    break;
                case '}' when depth > 0:
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
