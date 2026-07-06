using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class RelayDriverVerifyFixTests
{
    [Fact]
    public async Task RunTaskAsync_SkipTests_Stage5RecordedAsSkipped_NoSubagentInvocation()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """
            {
              "testCmd": "dotnet test",
              "logSources": [],
              "baselineVerify": false,
              "enableFixVerify": true,
              "skipTestsTaskIds": ["readme-only"]
            }
            """);
        repo.WriteTask("readme-only", "# README-only task\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(0, "green"));  // stage 9 verify — green
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "readme-only");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // Stage 5 must be recorded as "Skipped", not "Done".
        var entries = StageStatusRecord.Read(Path.Combine(repo.Root, ".relay", "readme-only"));
        Assert.Equal(11, entries.Count);
        var stage5 = entries.Single(e => e.Stage == 5);
        Assert.Equal("Skipped", stage5.Status);
        Assert.Equal("green", stage5.Check);

        // All other stages must be "Done".
        foreach (var e in entries.Where(e => e.Stage != 5))
        {
            Assert.Equal("Done", e.Status);
        }

        // No subagent invocation for stage 5.
        Assert.DoesNotContain(runner.Invocations, i => i.Stage.Number == 5);

        // Stage 9 still runs.
        Assert.Contains(runner.Invocations, i => i.Stage.Number == 9);
    }
}
