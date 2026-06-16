using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

internal static class AuthorTestGate
{
    public static async Task<AuthorTestGateResult> RunAsync(
        string rootPath,
        string taskId,
        string runId,
        IReadOnlyList<string> manifest,
        IReadOnlyList<string> testFiles,
        string command,
        ITestRunner testRunner,
        IGitInvoker gitInvoker,
        CancellationToken cancellationToken)
    {
        var tag = RedGate.StashTag(taskId, runId);
        var stripSet = RedGate.ComputeStripSet(manifest, testFiles);
        var stashed = false;
        var restore = RedGateRestoreResult.Absent;
        TestRunResult? result;
        string? error = null;

        try
        {
            stashed = await RedGate.StripToRedAsync(rootPath, stripSet, tag, cancellationToken, gitInvoker);
            result = await testRunner.RunAsync(rootPath, command, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            result = new TestRunResult(-1, ex.Message);
        }
        finally
        {
            if (stashed)
            {
                restore = await RedGate.RestoreStashAsync(rootPath, tag, cancellationToken, gitInvoker);
            }
        }

        return new AuthorTestGateResult(result, stashed, restore, error);
    }
}

internal sealed record AuthorTestGateResult(
    TestRunResult TestResult,
    bool StashedImplementation,
    RedGateRestoreResult RestoreResult,
    string? Error);
