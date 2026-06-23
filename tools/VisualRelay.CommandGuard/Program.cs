using System.Text.Json;
using VisualRelay.Core.CommandGuard;

// VisualRelay.CommandGuard — Swival command-middleware that strips git
// hook-bypass flags (--no-verify / -n) so the per-repo authority hook
// re-engages. Reads a JSON payload from stdin, writes the verdict to stdout.
//
// Fail-open: any internal error emits {"action":"allow"}.

try
{
    var stdin = await Console.In.ReadToEndAsync();
    using var doc = JsonDocument.Parse(stdin);
    var result = CommandGuardDecider.Decide(doc.RootElement);
    var json = SerializeResult(result);
    await Console.Out.WriteAsync(json);
    return 0;
}
catch
{
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

    // Deny (not used for strip path, but supported).
    return """{"action":"allow"}""";
}
