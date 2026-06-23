using System.Text.Json;
using VisualRelay.Core.CommandGuard;

// VisualRelay.CommandGuard — Swival command-middleware that strips git
// hook-bypass flags (--no-verify / -n) so the per-repo authority hook
// re-engages. Reads a JSON payload from stdin, writes the verdict to stdout.
//
// Fail-open for non-git commands; fail-CLOSED for git commit so a broken
// guard cannot silently re-enable the bypass.

string stdin = "";
try
{
    stdin = await Console.In.ReadToEndAsync();
    using var doc = JsonDocument.Parse(stdin);
    var result = CommandGuardDecider.Decide(doc.RootElement, stdin);
    var json = SerializeResult(result);
    await Console.Out.WriteAsync(json);
    return 0;
}
catch
{
    // Fail-closed for git commit: a broken guard must not silently
    // re-enable the hook-bypass.  Non-git commands still fail-open.
    if (LooksLikeGitCommit(stdin))
        await Console.Out.WriteAsync("""{"action":"deny","reason":"command-guard internal error for git commit"}""");
    else
        await Console.Out.WriteAsync("""{"action":"allow"}""");
    return 0;
}

static string SerializeResult(CommandGuardResult result)
{
    if (result.IsPassThrough)
        return """{"action":"allow"}""";

    if (result.IsRewritten)
    {
        var mode = result.Mode!;
        var cmdJson = JsonSerializer.Serialize(result.Command);
        return $$"""{"action":"allow","mode":"{{mode}}","command":{{cmdJson}}}""";
    }

    if (result.IsDeny)
    {
        var reason = JsonEncodedText.Encode(result.Reason ?? "blocked by command guard");
        return $$"""{"action":"deny","reason":{{reason}}}""";
    }

    // Fallback (should not be reached).
    return """{"action":"allow"}""";
}

// Best-effort check: does the raw JSON string look like a git-commit
// command?  Quick substring scan — avoids full re-parse in the error path.
// Searches for plain substrings "git" and "commit" (not quoted JSON
// tokens) so both argv-mode (["git","commit"]) and shell-mode
// ("git commit") payloads are detected.
static bool LooksLikeGitCommit(string rawJson)
{
    var gitIdx = rawJson.IndexOf("git", StringComparison.Ordinal);
    if (gitIdx < 0) return false;
    var commitIdx = rawJson.IndexOf("commit", StringComparison.Ordinal);
    return commitIdx >= 0 && Math.Abs(commitIdx - gitIdx) < 200;
}
