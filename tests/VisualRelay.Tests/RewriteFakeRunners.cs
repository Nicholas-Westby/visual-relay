using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// A fake <see cref="ISubagentRunner"/> that writes a rewritten spec
/// into the worktree at the manifest path, and optionally scribbles a
/// stray file outside the task folder to verify copy-back isolation.
/// </summary>
internal sealed class RewriteFakeRunner : ISubagentRunner
{
    public string NewContent { get; init; } = "# Rewritten\n\nBetter spec.\n";
    public bool WriteStrayFile { get; init; }
    public string StrayRelativePath { get; init; } = "src/stray.txt";
    public string StrayContent { get; init; } = "stray data";
    public bool ThrowOnRun { get; init; }
    public StageInvocation? LastInvocation { get; private set; }

    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken ct)
    {
        LastInvocation = invocation;

        if (ThrowOnRun)
            throw new InvalidOperationException("synthetic rewrite failure");

        ct.ThrowIfCancellationRequested();

        foreach (var file in invocation.Manifest)
        {
            var fullPath = Path.Combine(invocation.TargetRoot, file);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, NewContent);
        }

        if (WriteStrayFile)
        {
            var strayPath = Path.Combine(invocation.TargetRoot, StrayRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(strayPath)!);
            File.WriteAllText(strayPath, StrayContent);
        }

        return Task.FromResult(new SubagentResult(
            RawText: "```json\n{\"summary\":\"rewritten\"}\n```",
            Json: "{\"summary\":\"rewritten\"}",
            IsValid: true,
            Error: null));
    }
}

/// <summary>
/// Writes the spec into the worktree (simulating partial work) then throws
/// <see cref="OperationCanceledException"/>, modelling a cancel that fires
/// after the model has written output but before the runner returns.
/// </summary>
internal sealed class PostWriteCancellationRunner(string newContent, CancellationToken cancelAfter)
    : ISubagentRunner
{
    public string WorktreeRoot { get; private set; } = string.Empty;

    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken ct)
    {
        WorktreeRoot = invocation.TargetRoot;

        foreach (var file in invocation.Manifest)
        {
            var fullPath = Path.Combine(invocation.TargetRoot, file);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, newContent);
        }

        cancelAfter.ThrowIfCancellationRequested();
        throw new OperationCanceledException(cancelAfter);
    }
}
