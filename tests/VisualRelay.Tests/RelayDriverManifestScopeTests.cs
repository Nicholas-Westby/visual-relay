using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayDriverManifestScopeTests
{
    [Fact]
    public async Task RunTaskAsync_CommitsTrackedFilesModifiedOutsideTheManifest()
    {
        // Agents sometimes edit a shared tracked file (e.g. a test double) the
        // stage-4 manifest never listed. Stage 9 verifies the working tree, which
        // has the edit, so it passes — but a manifest-only commit would leave that
        // edit uncommitted, and committed code referencing it would fail to build
        // from a clean checkout. The commit must include every tracked change.
        using var repo = TestRepository.Create();
        repo.WriteConfig("test -f src/status.txt", []);
        repo.WriteTask("ship-status", "batch: 2\n\n# Ship status\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "status.txt"), "old");
        File.WriteAllText(Path.Combine(repo.Root, "src", "shared.txt"), "shared-old");
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "visual-relay@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Visual Relay Tests");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "seed");

        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(
                new OutsideManifestEditingRunner(),
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                new InMemoryRelayEventSink()),
            RelayDriverOptions.Default);

        var outcome = await driver.RunTaskAsync(repo.Root, "ship-status");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var names = TestGit.Run(repo.Root, "show", "--name-only", "--pretty=format:", "HEAD");
        Assert.Contains("src/status.txt", names);
        Assert.Contains("src/shared.txt", names);
    }
}

internal sealed class OutsideManifestEditingRunner : ISubagentRunner
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
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "src", "status.txt"), "new");
            // A tracked file the manifest never declared.
            File.WriteAllText(Path.Combine(invocation.TargetRoot, "src", "shared.txt"), "shared-new");
        }

        var json = invocation.Stage.Number switch
        {
            1 => """{"summary":"framed","options":["small"]}""",
            2 => """{"findings":"found","constraints":[]}""",
            3 => """{"evidence":"no remnants","excerpts":[],"repro":"none"}""",
            4 => """{"plan":"edit files","manifest":["src/status.txt","tests/status.test"]}""",
            5 => """{"testFiles":["tests/status.test"],"rationale":"red first"}""",
            6 => """{"summary":"implemented"}""",
            7 => """{"verdict":"pass","issues":[]}""",
            8 => """{"summary":"fixed"}""",
            9 => """{"summary":"verified","commitMessage":"fix: ship status"}""",
            _ => """{"summary":"ok"}"""
        };
        return Task.FromResult(new SubagentResult(json, json, true, null));
    }
}
