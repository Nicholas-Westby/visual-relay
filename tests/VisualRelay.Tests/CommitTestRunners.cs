using System.Diagnostics;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Editing runner: modifies src/status.cs and returns a standard
/// commitMessages array.  Used by the basic happy-path commit test.
/// </summary>
internal sealed class EditingSubagentRunner : ISubagentRunner
{
    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == 5)
        {
            Directory.CreateDirectory(Path.Combine(invocation.TargetRoot, "tests"));
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "tests", "status.test"), "red first");
        }
        else if (invocation.Stage.Number == 6)
        {
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "src", "status.cs"), "new");
        }

        var json = invocation.Stage.Number switch
        {
            1 => """{"summary":"framed","options":["small"]}""",
            2 => """{"findings":"found","constraints":[]}""",
            3 => """{"evidence":"no remnants","excerpts":[],"repro":"none"}""",
            4 => """{"plan":"edit files","manifest":["src/status.cs","tests/status.test","src/ghost.cs"]}""",
            5 => """{"testFiles":["tests/status.test"],"rationale":"red first"}""",
            6 => """{"summary":"implemented"}""",
            7 => """{"verdict":"pass","issues":[]}""",
            8 => """{"summary":"fixed"}""",
            9 => """{"summary":"verified","commitMessages":["fix(sample): ship status","fix: include shipping status endpoint","chore(sample): update status module"]}""",
            _ => """{"summary":"ok"}"""
        };
        return Task.FromResult(new SubagentResult(json, json, true, null));
    }
}

/// <summary>
/// Runner that simulates an agent trying to git-commit at stage 8 (mid-run).
/// The pre-commit hook should reject it because RELAY_COMMIT_TOKEN is absent.
/// </summary>
internal sealed class MidRunCommittingSubagentRunner(string root) : ISubagentRunner
{
    /// <summary>True after the hook rejected the agent's git commit at stage 8.</summary>
    public bool AgentCommitRejected { get; private set; }

    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == 5)
        {
            Directory.CreateDirectory(Path.Combine(invocation.TargetRoot, "tests"));
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "tests", "status.test"), "red first");
        }
        else if (invocation.Stage.Number == 6)
        {
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "src", "status.cs"), "new");
        }
        else if (invocation.Stage.Number == 8)
        {
            var exitCode = Git("add -A");
            if (exitCode == 0)
            {
                exitCode = Git("commit -m \"agent: premature commit of work in progress\"");
                if (exitCode != 0)
                {
                    AgentCommitRejected = true;
                }
            }
        }

        var json = invocation.Stage.Number switch
        {
            1 => """{"summary":"framed","options":["small"]}""",
            2 => """{"findings":"found","constraints":[]}""",
            3 => """{"evidence":"no remnants","excerpts":[],"repro":"none"}""",
            4 => """{"plan":"edit files","manifest":["src/status.cs","tests/status.test"]}""",
            5 => """{"testFiles":["tests/status.test"],"rationale":"red first"}""",
            6 => """{"summary":"implemented"}""",
            7 => """{"verdict":"pass","issues":[]}""",
            8 => """{"summary":"fixed"}""",
            9 => """{"summary":"verified","commitMessages":["fix(sample): ship status","fix: include shipping status","chore(sample): update status module"]}""",
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
        // Strip DEVELOPER_DIR/SDKROOT so xcrun shim cannot resurrect a stale
        // nix-store path inherited from the shell environment.
        startInfo.Environment.Remove("DEVELOPER_DIR");
        startInfo.Environment.Remove("SDKROOT");
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(root);
        foreach (var arg in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        return process.ExitCode;
    }
}

/// <summary>
/// Runner that deletes the data/ directory contents at stage 6 and verifies
/// deletions are committed.  Uses the new commitMessages array.
/// </summary>
internal sealed class DeletingDirectorySubagentRunner : ISubagentRunner
{
    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == 5)
        {
            Directory.CreateDirectory(Path.Combine(invocation.TargetRoot, "tests"));
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "tests", "data.test"), "red first");
        }
        else if (invocation.Stage.Number == 6)
        {
            File.Delete(Path.Combine(invocation.TargetRoot, "data", "company.json"));
            File.Delete(Path.Combine(invocation.TargetRoot, "data", "http_log.json"));
        }

        var json = invocation.Stage.Number switch
        {
            1 => """{"summary":"framed","options":["delete"]}""",
            2 => """{"findings":"found","constraints":[]}""",
            3 => """{"evidence":"stale fixtures","excerpts":[],"repro":"none"}""",
            4 => """{"plan":"delete stale data","manifest":["src/delete.cs","data","tests/data.test"]}""",
            5 => """{"testFiles":["tests/data.test"],"rationale":"red first"}""",
            6 => """{"summary":"deleted stale data"}""",
            7 => """{"verdict":"pass","issues":[]}""",
            8 => """{"summary":"fixed"}""",
            9 => """{"summary":"verified","commitMessages":["fix(sample): remove stale data","chore: clean stale data files","docs: document data cleanup"]}""",
            _ => """{"summary":"ok"}"""
        };
        return Task.FromResult(new SubagentResult(json, json, true, null));
    }
}

/// <summary>
/// First candidate contains a file-name pattern (foo.cs); later candidates
/// avoid file names.  Used to test commit-msg hook fallback.
/// </summary>
internal sealed class FileNameFirstCandidateRunner : ISubagentRunner
{
    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == 5)
        {
            Directory.CreateDirectory(Path.Combine(invocation.TargetRoot, "tests"));
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "tests", "status.test"), "red first");
        }
        else if (invocation.Stage.Number == 6)
        {
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "src", "status.cs"), "new");
        }

        var json = invocation.Stage.Number switch
        {
            1 => """{"summary":"framed","options":["small"]}""",
            2 => """{"findings":"found","constraints":[]}""",
            3 => """{"evidence":"no remnants","excerpts":[],"repro":"none"}""",
            4 => """{"plan":"edit files","manifest":["src/status.cs","tests/status.test"]}""",
            5 => """{"testFiles":["tests/status.test"],"rationale":"red first"}""",
            6 => """{"summary":"implemented"}""",
            7 => """{"verdict":"pass","issues":[]}""",
            8 => """{"summary":"fixed"}""",
            9 => """{"summary":"verified","commitMessages":["fix(src): update foo.cs logic","fix: correct update logic","refactor: improve control flow"]}""",
            _ => """{"summary":"ok"}"""
        };
        return Task.FromResult(new SubagentResult(json, json, true, null));
    }
}

/// <summary>
/// Stage 9 returns only the legacy single <c>commitMessage</c> string,
/// not the <c>commitMessages</c> array.  The driver must treat the legacy
/// field as a one-element list.
/// </summary>
internal sealed class LegacyCommitMessageRunner : ISubagentRunner
{
    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == 5)
        {
            Directory.CreateDirectory(Path.Combine(invocation.TargetRoot, "tests"));
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "tests", "status.test"), "red first");
        }
        else if (invocation.Stage.Number == 6)
        {
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "src", "status.cs"), "new");
        }

        var json = invocation.Stage.Number switch
        {
            1 => """{"summary":"framed","options":["small"]}""",
            2 => """{"findings":"found","constraints":[]}""",
            3 => """{"evidence":"no remnants","excerpts":[],"repro":"none"}""",
            4 => """{"plan":"edit files","manifest":["src/status.cs","tests/status.test"]}""",
            5 => """{"testFiles":["tests/status.test"],"rationale":"red first"}""",
            6 => """{"summary":"implemented"}""",
            7 => """{"verdict":"pass","issues":[]}""",
            8 => """{"summary":"fixed"}""",
            9 => """{"summary":"verified","commitMessage":"fix(legacy): use old field"}""",
            _ => """{"summary":"ok"}"""
        };
        return Task.FromResult(new SubagentResult(json, json, true, null));
    }
}

/// <summary>
/// Stage 5 authors a new test file (<c>tests/regression-tests.cs</c>) that the
/// stage-4 manifest does NOT list. The manifest only declares the implementation
/// file <c>src/app.cs</c>. The commit must auto-include the untracked test file
/// so it is not silently dropped.
/// </summary>
internal sealed class NewTestFileNotInManifestRunner : ISubagentRunner
{
    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == 5)
        {
            Directory.CreateDirectory(Path.Combine(invocation.TargetRoot, "tests"));
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "tests", "regression-tests.cs"), "// regression test");
        }
        else if (invocation.Stage.Number == 6)
        {
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "src", "app.cs"), "updated implementation");
        }

        var json = invocation.Stage.Number switch
        {
            1 => """{"summary":"framed","options":["small"]}""",
            2 => """{"findings":"found","constraints":[]}""",
            3 => """{"evidence":"no remnants","excerpts":[],"repro":"none"}""",
            4 => """{"plan":"add regression tests","manifest":["src/app.cs"]}""",
            5 => """{"testFiles":["tests/regression-tests.cs"],"rationale":"regression test for new behavior"}""",
            6 => """{"summary":"implemented"}""",
            7 => """{"verdict":"pass","issues":[]}""",
            8 => """{"summary":"fixed"}""",
            9 => """{"summary":"verified","commitMessages":["test: add regression coverage for new behavior","test: cover edge case in regression suite","chore: update test infrastructure"]}""",
            _ => """{"summary":"ok"}"""
        };
        return Task.FromResult(new SubagentResult(json, json, true, null));
    }
}

/// <summary>
/// Stage 9 returns neither <c>commitMessages</c> nor <c>commitMessage</c>.
/// The driver must fall back to <c>chore(relay): &lt;slug&gt;</c>.
/// </summary>
internal sealed class NoCommitMessageRunner : ISubagentRunner
{
    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation.Stage.Number == 5)
        {
            Directory.CreateDirectory(Path.Combine(invocation.TargetRoot, "tests"));
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "tests", "status.test"), "red first");
        }
        else if (invocation.Stage.Number == 6)
        {
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "src", "status.cs"), "new");
        }

        var json = invocation.Stage.Number switch
        {
            1 => """{"summary":"framed","options":["small"]}""",
            2 => """{"findings":"found","constraints":[]}""",
            3 => """{"evidence":"no remnants","excerpts":[],"repro":"none"}""",
            4 => """{"plan":"edit files","manifest":["src/status.cs","tests/status.test"]}""",
            5 => """{"testFiles":["tests/status.test"],"rationale":"red first"}""",
            6 => """{"summary":"implemented"}""",
            7 => """{"verdict":"pass","issues":[]}""",
            8 => """{"summary":"fixed"}""",
            9 => """{"summary":"verified"}""",
            _ => """{"summary":"ok"}"""
        };
        return Task.FromResult(new SubagentResult(json, json, true, null));
    }
}
