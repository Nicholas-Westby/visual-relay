using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// The no-op placeholder test command is silent by design — it exits 0 so a greenfield
/// repo is runnable. On a real repo whose detection failed it makes Verify report a
/// green gate having run nothing, so both the run log and <c>GET /state</c> say it out
/// loud.
/// </summary>
public sealed class PlaceholderTestCommandVisibilityTests
{
    [Fact]
    public async Task RunTaskAsync_PlaceholderTestCommand_WarnsAtRunStart()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig(ProjectBootstrapper.PlaceholderTestCommand, [], baselineVerify: false);
        repo.WriteTask("vacuous", "# Placeholder gate\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                sink, new NullGitInvoker()),
            RelayDriverOptions.NoGitCommit);

        await driver.RunTaskAsync(repo.Root, "vacuous");

        var warning = Assert.Single(sink.Events, e =>
            e is { Level: "warn", EventName: "test_command_placeholder" });
        Assert.Contains("placeholder", warning.Data!["message"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunTaskAsync_RealTestCommand_DoesNotWarn()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: false);
        repo.WriteTask("real", "# Real gate\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                sink, new NullGitInvoker()),
            RelayDriverOptions.NoGitCommit);

        await driver.RunTaskAsync(repo.Root, "real");

        Assert.DoesNotContain(sink.Events, e => e.EventName == "test_command_placeholder");
    }
}
