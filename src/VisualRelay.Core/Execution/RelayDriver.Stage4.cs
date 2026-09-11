using System.Text;
using System.Text.Json;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

// Stage 4 (Plan) post-processing, split out of RelayDriver.cs to keep that file
// under the size guard.
public sealed partial class RelayDriver
{
    /// <summary>What stage 4 leaves behind for the stages after it.</summary>
    /// <param name="Body">The plan body, possibly rewritten by the completeness retry.</param>
    /// <param name="CostDelta">USD the completeness retry added.</param>
    /// <param name="UnknownCostDelta">Unpriced stages the retry added.</param>
    /// <param name="ImplementationFrontLoaded">
    /// True when the implementation is already underway, which downshifts stage 6.
    /// </param>
    private sealed record Stage4Result(
        string Body,
        double CostDelta,
        int UnknownCostDelta,
        bool ImplementationFrontLoaded);

    /// <summary>
    /// Adopts the plan's manifest: task-directory entries are dropped (a plan that
    /// edits its own task file produces a manifest the commit stage cannot
    /// honour), the rest are normalized the way every later reader reads a path —
    /// the <c>+</c> prefix, a leading <c>./</c>, backslashes and a trailing slash
    /// all gone — and the narrowed test command is rebuilt from them. Stage 5
    /// normalizes its declared test files the same way, and the red gate compares
    /// the two Ordinal: an entry left unnormalized here is a second name for one
    /// file, and the gate strips a file stage 5 declared as a test.
    /// </summary>
    private async Task<Stage4Result> HandleStage4Async(
        string rootPath,
        string runId,
        string taskId,
        string taskDirectory,
        RelayConfig config,
        RelayStageDefinition stage,
        RelayTaskInput input,
        StringBuilder ledger,
        List<string> manifest,
        JsonElement json,
        string body,
        bool implementationFrontLoaded,
        CancellationToken cancellationToken)
    {
        manifest.Clear();
        var raw = ReadStringArray(json, "manifest").Distinct(StringComparer.Ordinal).ToList();
        var dropped = new List<string>();
        var clean = new List<string>();
        foreach (var e in raw)
        {
            if (IsPathUnderDirectory(rootPath, e, config.TasksDir))
                dropped.Add(e);
            else if (WorktreeFilter.NormalizeRepoRelativePath(e) is { Length: > 0 } path)
                clean.Add(path);
        }

        manifest.AddRange(clean.Distinct(StringComparer.Ordinal));
        if (dropped.Count > 0)
        {
            var note = dropped.Count == 1
                ? $"> **Note**: dropped 1 task-dir entry from manifest: `{dropped[0]}`"
                : $"> **Note**: dropped {dropped.Count} task-dir entries from manifest: {string.Join(", ", dropped.Select(d => $"`{d}`"))}";
            ledger.AppendLine(note);
            ledger.AppendLine();
        }

        await WriteManifestAsync(taskDirectory, manifest, cancellationToken);
        var (retriedBody, costDelta, unknownDelta) = await TryPlanCompletenessRetryAsync(
            body, json, manifest, rootPath, runId, taskId, taskDirectory, config, stage, input,
            ledger, cancellationToken);

        return new Stage4Result(
            retriedBody,
            costDelta,
            unknownDelta,
            config.DownshiftOnEarlyImplementation
                ? await EarlyImplementationDetector.ImplementationAlreadyUnderwayAsync(
                    rootPath, manifest, IsImpl, _dependencies.GitInvoker, cancellationToken,
                    isTestFile: f => TestPathClassifier.IsTestRelated(f, config.TestPaths))
                : implementationFrontLoaded);
    }
}
