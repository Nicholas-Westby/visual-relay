using System.Diagnostics;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Runner that simulates an agent that SUCCESSFULLY self-commits mid-run (the
/// authorized production case: the agent inherited the run nonce, so the
/// pre-commit guard let its bare commit through). No commit-msg/pre-commit hook
/// is installed in the test repo, so the commit lands. It edits a tracked file
/// at stage 6, commits a bare provenance-less commit at stage 8, then makes a
/// further working-tree edit at stage 9 (which always runs). The driver's
/// Commit stage must squash the bare commit and the later edit into a single
/// sealed commit.
/// </summary>
internal sealed class MidRunSelfCommittingRunner(string root) : ISubagentRunner
{
    /// <summary>True once the agent's mid-run commit succeeded.</summary>
    public bool AgentCommitLanded { get; private set; }

    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == 5)
        {
            Directory.CreateDirectory(Path.Combine(invocation.TargetRoot, "tests"));
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "tests", "status.test"), "red first");
        }
        else if (invocation.Stage.Number == 6)
        {
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "src", "status.cs"), "implemented");
        }
        else if (invocation.Stage.Number == 8)
        {
            // Single-token message so the naive space-split below keeps it intact.
            if (Git("add -A") == 0 && Git("commit -m agent-wip-bare") == 0)
                AgentCommitLanded = true;
        }
        else if (invocation.Stage.Number == 9)
        {
            // A further working-tree change made AFTER the bare commit, left
            // uncommitted — must still land in the single sealed commit. Stage 9
            // always runs (unlike stage 10, which is skipped when verify is green).
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "src", "extra.cs"), "post-commit edit");
        }

        var json = invocation.Stage.Number switch
        {
            1 => """{"summary":"framed","options":["small"]}""",
            2 => """{"findings":"found","constraints":[]}""",
            3 => """{"evidence":"no remnants","excerpts":[],"repro":"none"}""",
            4 => """{"plan":"edit files","manifest":["src/status.cs","tests/status.test","src/extra.cs"]}""",
            5 => """{"testFiles":["tests/status.test"],"rationale":"red first"}""",
            6 => """{"summary":"implemented"}""",
            7 => """{"verdict":"pass","issues":[]}""",
            8 => """{"summary":"fixed"}""",
            9 => """{"summary":"verified","commitMessages":["fix(sample): ship status","fix: include shipping status endpoint","chore(sample): update status module"]}""",
            10 => """{"summary":"reviewed"}""",
            _ => """{"summary":"ok"}"""
        };
        return Task.FromResult(new SubagentResult(json, json, true, null));
    }

    private int Git(string arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment.Remove("DEVELOPER_DIR");
        startInfo.Environment.Remove("SDKROOT");
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(root);
        foreach (var arg in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        return process.ExitCode;
    }
}
