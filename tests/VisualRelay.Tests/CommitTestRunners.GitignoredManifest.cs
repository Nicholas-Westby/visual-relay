using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Stage 4 manifest includes <c>swival.toml</c> — a gitignored runtime artifact.
/// Bypasses the early SwivalSubagentRunner check (this ISubagentRunner is used
/// directly by RelayDriver) so the gitignored path reaches stage 11, exercising
/// the GitCommitter backstop. Otherwise behaves like <see cref="EditingSubagentRunner"/>.
/// </summary>
internal sealed class GitignoredManifestSubagentRunner : ISubagentRunner
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
            4 => """{"plan":"edit files","manifest":["swival.toml","src/status.cs","tests/status.test"]}""",
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
