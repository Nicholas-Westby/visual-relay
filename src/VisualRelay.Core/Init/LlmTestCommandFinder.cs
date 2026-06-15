using System.Net.Http.Json;
using System.Text.Json;
using VisualRelay.Domain;

namespace VisualRelay.Core.Init;

// Asks the frontier tier (via the local proxy) for a project's test command.
// The completer seam (prompt -> raw model text) is injectable so prompt assembly
// and response parsing are unit-testable without a network call.
public sealed class LlmTestCommandFinder
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly Func<string, CancellationToken, Task<string>> _complete;

    public LlmTestCommandFinder(Func<string, CancellationToken, Task<string>>? complete = null)
    {
        _complete = complete ?? DefaultCompleteAsync;
    }

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

    private static async Task<string> DefaultCompleteAsync(string prompt, CancellationToken cancellationToken)
    {
        var request = new
        {
            model = "frontier",
            messages = new[] { new { role = "user", content = prompt } }
        };

        using var response = await Client.PostAsJsonAsync(
            $"{ModelBackend.BaseUrl}/v1/chat/completions", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;
    }
}
