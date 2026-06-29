using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Wiring tests: the driver emits a <c>testfilecmd_no_files_token</c> warn event
/// at run start when <c>testFileCmd</c> lacks the <c>{files}</c> token (the
/// silent full-suite degrade), and stays quiet when it is present.
/// </summary>
public sealed class RelayDriverTestFileCmdWarningTests
{
    [Fact]
    public async Task RunTask_TestFileCmdWithoutFilesToken_EmitsWarning()
    {
        var sink = await RunHappyPathAsync(
            "dotnet test tests/MyProject.Tests/MyProject.Tests.csproj");

        Assert.Contains(sink.Events, e =>
            e is { EventName: "testfilecmd_no_files_token", Level: "warn" });
    }

    [Fact]
    public async Task RunTask_TestFileCmdWithFilesToken_NoWarning()
    {
        var sink = await RunHappyPathAsync("bun test {files}");

        Assert.DoesNotContain(sink.Events, e => e.EventName == "testfilecmd_no_files_token");
    }

    private static async Task<InMemoryRelayEventSink> RunHappyPathAsync(string testFileCmd)
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            $$"""
            {
              "testCmd": "dotnet test",
              "testFileCmd": {{System.Text.Json.JsonSerializer.Serialize(testFileCmd)}},
              "logSources": [],
              "baselineVerify": false,
              "enableFixVerify": true
            }
            """);
        repo.WriteTask("warn-task", "# Warn task\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "config.json");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),    // stage 5 author gate
            new TestRunResult(0, "green")); // stage 9 verify
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, sink),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "warn-task");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        return sink;
    }
}
