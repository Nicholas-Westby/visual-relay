using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    private static HashSet<string> ExtractFailureIds(string? output)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(output)) return ids;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            if (line.Trim().StartsWith("Failed ", StringComparison.Ordinal))
                ids.Add(line.Trim()["Failed ".Length..].Trim());
        return ids;
    }
    private static async Task<string?> GetNewFailuresAsync(
        string rootPath, string taskId, string runId,
        ITestRunner testRunner, string testCommand,
        TestRunResult workingResult, IGitInvoker gitInvoker, CancellationToken ct)
    {
        var tag = RedGate.StashTag(taskId, runId);
        var stashed = await RedGate.StashAllAsync(rootPath, tag, ct, gitInvoker);
        try
        {
            if (!stashed) return "verify failed";
            var baseline = await testRunner.RunAsync(rootPath, testCommand, ct);
            if (baseline.TimedOut) return "verify failed";
            var current = ExtractFailureIds(workingResult.Output);
            if (current.Count == 0 && workingResult.ExitCode != 0)
                return "verify failed";
            current.ExceptWith(ExtractFailureIds(baseline.Output));
            return current.Count == 0 ? null
                : string.Join(", ", current.Order(StringComparer.Ordinal));
        }
        finally
        {
            if (stashed && await RedGate.RestoreStashAsync(rootPath, tag, ct, gitInvoker)
                == RedGateRestoreResult.Conflict)
            {
                throw new InvalidOperationException(
                    $"Red gate restore conflict after baseline verify for tag '{tag}'.");
            }
        }
    }
}
