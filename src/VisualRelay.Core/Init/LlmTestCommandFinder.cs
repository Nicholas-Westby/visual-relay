namespace VisualRelay.Core.Init;

// Asks a model for a project's test command. The completer seam
// (prompt -> raw model text) is injectable so prompt assembly and response
// parsing are unit-testable without a network call.
//
// It no longer holds an HttpClient of its own: the default completer goes
// through the provider transport seam, so this needs no proxy and can be
// exercised offline like everything else.
public sealed class LlmTestCommandFinder(Func<string, CancellationToken, Task<string>>? complete = null)
{
    private readonly Func<string, CancellationToken, Task<string>> _complete = complete ?? NoCompleter;

    public async Task<string> FindAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        var raw = await _complete(BuildPrompt(rootPath), cancellationToken);
        return ExtractCommand(raw);
    }

    // public (not internal) so the test assembly — which only has InternalsVisibleTo
    // for VisualRelay.App, not Core — can exercise prompt assembly directly.
    public static string BuildPrompt(string rootPath)
    {
        var entries = Directory.EnumerateFileSystemEntries(rootPath)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Take(100);

        return "You are configuring a CI test command for a project. Given its "
            + "top-level entries, reply with ONLY the shell command that runs its "
            + "test suite — no prose, no code fence.\n\nEntries:\n- "
            + string.Join("\n- ", entries);
    }

    public static string ExtractCommand(string raw)
    {
        // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
        // Defensive: tolerate a null LLM response even though the param is non-nullable.
        var text = (raw ?? string.Empty).Trim();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            return line.Trim().Trim('"', '`', '\'');
        }

        return string.Empty;
    }

    /// <summary>
    /// The completer used when none was injected. It asks nothing: a caller that
    /// wants a model guess supplies a real completer, and one that does not falls
    /// back to marker-based detection, which is what runs on a machine with no
    /// provider key at all.
    /// </summary>
    private static Task<string> NoCompleter(string prompt, CancellationToken cancellationToken) =>
        Task.FromResult(string.Empty);
}
