using System.Text.Json;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// A fake <see cref="ISubagentRunner"/> for the fix-task-author path.
/// Returns a JSON payload with markdown, summary, and slug so the
/// <c>FixTaskAuthorRunner</c> can parse it.
/// </summary>
internal sealed class FixTaskFakeRunner : ISubagentRunner
{
    public string Markdown { get; init; } = "# Fix\n\nMake it work.\n";
    public string Summary { get; init; } = "Fix the issue";
    public string Slug { get; init; } = "fix-issue";
    public bool ThrowOnRun { get; init; }
    public bool WasCalled { get; private set; }
    public StageInvocation? LastInvocation { get; private set; }

    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken ct)
    {
        LastInvocation = invocation;
        WasCalled = true;

        if (ThrowOnRun)
            throw new InvalidOperationException("synthetic fix-task-author failure");

        ct.ThrowIfCancellationRequested();

        var payload = new Dictionary<string, string>
        {
            ["markdown"] = Markdown,
            ["summary"] = Summary,
            ["slug"] = Slug,
        };
        var json = JsonSerializer.Serialize(payload);

        return Task.FromResult(new SubagentResult(
            RawText: $"```json\n{json}\n```",
            Json: json,
            IsValid: true,
            Error: null));
    }
}

/// <summary>
/// Parks inside <see cref="RunAsync"/> until an external gate completes, then
/// returns the authored payload — letting a view-model test observe the
/// in-flight <c>IsCreatingFixTask</c> state deterministically.
/// </summary>
internal sealed class GatedFixTaskRunner : ISubagentRunner
{
    private readonly string _markdown;
    private readonly string _summary;
    private readonly string _slug;
    private readonly Task _gate;

    public StageInvocation? LastInvocation { get; private set; }

    public GatedFixTaskRunner(string markdown, string summary, string slug, Task gate)
    {
        _markdown = markdown;
        _summary = summary;
        _slug = slug;
        _gate = gate;
    }

    public async Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken ct)
    {
        LastInvocation = invocation;
        await _gate.WaitAsync(ct);

        var payload = new Dictionary<string, string>
        {
            ["markdown"] = _markdown,
            ["summary"] = _summary,
            ["slug"] = _slug,
        };
        var json = JsonSerializer.Serialize(payload);

        return new SubagentResult(
            RawText: $"```json\n{json}\n```",
            Json: json,
            IsValid: true,
            Error: null);
    }
}
