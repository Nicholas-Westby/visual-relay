using System.Text.Json;

namespace VisualRelay.Core.Agent;

/// <summary>What reading a stage's contract out of its answer produced.</summary>
/// <param name="Json">The contract object, or <c>null</c> when none was usable.</param>
/// <param name="Error">Why it could not be read, in terms the model can act on.</param>
public sealed record StageContractResult(string? Json, string? Error)
{
    /// <summary>True when a contract came out.</summary>
    public bool Succeeded => Json is not null;

    /// <summary>
    /// What had to be repaired before the block would parse, empty when it parsed
    /// as written. A stage announces these, because a contract that only survived
    /// repair is a defect worth seeing even though the stage went on.
    /// </summary>
    public IReadOnlyList<string> Repairs { get; init; } = [];
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
/// Where the contract is comes from <see cref="StageContractLocator"/>, which
/// asks the model's own fence before it guesses. Candidates are tried in that
/// order and the first that both parses and satisfies the contract wins, so an
/// example object the model wrote in passing can never displace the real one.
/// </para>
/// <para>
/// Only when nothing in the answer parses as written does
/// <see cref="StageContractRepair"/> get a turn. Across a 27-task run, five
/// stages were discarded whose reasoning and code were correct and whose block
/// was one escape or one comma away from valid; nothing else about them was
/// wrong.
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
        var candidates = StageContractLocator.Candidates(answer);
        if (candidates.Count == 0)
            return new StageContractResult(
                null, "no JSON object found in the answer; the contract block is required");

        // Strict first. Repair is only ever reached when NOTHING in the answer
        // parses as written, so a well-formed answer is never touched.
        return Choose(candidates, required, repair: false)
            ?? Choose(candidates, required, repair: true)
            ?? new StageContractResult(null, "the contract block is not valid JSON");
    }

    /// <summary>
    /// The first candidate that parses and satisfies the contract, or the
    /// complaint about the first that merely parsed. Null when none parsed at
    /// all, which is the caller's cue to try again with repair.
    /// </summary>
    private static StageContractResult? Choose(
        List<string> candidates, List<string> required, bool repair)
    {
        string? parsed = null;
        IReadOnlyList<string> parsedRepairs = [];

        foreach (var candidate in candidates)
        {
            var text = candidate;
            IReadOnlyList<string> repairs = [];
            if (repair) (text, repairs) = StageContractRepair.Apply(candidate);
            if (!TryReadObject(text, out var element)) continue;

            if (required.All(key => element.TryGetProperty(key, out _)))
                return new StageContractResult(text, null) { Repairs = repairs };

            if (parsed is null) (parsed, parsedRepairs) = (text, repairs);
        }

        if (parsed is null) return null;

        // Something parsed but did not fit; name the first key it lacks so the
        // model has something specific to correct.
        TryReadObject(parsed, out var best);
        var missing = required.FirstOrDefault(key => !best.TryGetProperty(key, out _));
        return new StageContractResult(
            null,
            missing is null
                ? "the contract object did not match the required shape"
                : $"the contract is missing the required key \"{missing}\"")
        {
            Repairs = parsedRepairs,
        };
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
}
