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
/// Models a model-backend failure: writes a rewrite diagnostic file into the
/// worktree's <c>.relay</c> (mirroring the real runner's
/// <c>stage0-attempt1.killed-output.txt</c>) and returns an invalid result whose
/// error carries the <c>(full output: …)</c> breadcrumb pointing at that file. Used
/// to verify the diagnostic is preserved out of the worktree before it is removed.
/// </summary>
internal sealed class RewriteDiagnosticFailureRunner : ISubagentRunner
{
    public const string DiagnosticContents = "swival exit 1\nAuthenticationError: 401\n";

    public string DiagnosticRelativePath { get; private set; } = string.Empty;

    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken ct)
    {
        // The real runner writes the killed-output artifact next to the trace dir's
        // parent: {worktree}/.relay/{taskId}/stage0-attempt1.killed-output.txt.
        var traceParent = Path.GetDirectoryName(invocation.TraceDirectory)!;
        var diagnosticPath = Path.Combine(traceParent, "stage0-attempt1.killed-output.txt");
        Directory.CreateDirectory(traceParent);
        File.WriteAllText(diagnosticPath, DiagnosticContents);
        DiagnosticRelativePath = Path.GetRelativePath(invocation.TargetRoot, diagnosticPath);

        var error = $"swival exit 1: model call failed — AuthenticationError: 401 " +
            $"(full output: {diagnosticPath})";
        return Task.FromResult(new SubagentResult(string.Empty, null, false, error));
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
