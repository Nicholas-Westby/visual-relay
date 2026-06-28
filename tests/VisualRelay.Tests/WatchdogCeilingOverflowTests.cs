using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Fix H: the 10× boost multiply must not overflow <see cref="int"/>. A configured
/// <c>subagentTimeoutMs</c> &gt; ~214_748_364 times 10 overflows to a negative value,
/// which silently defeats the boost (a negative ceiling reads as "disabled"
/// downstream). <see cref="RelayDriver.SaturatingBoost"/> computes in
/// <see cref="long"/> and saturates to <see cref="int.MaxValue"/>.
/// </summary>
public sealed class WatchdogCeilingOverflowTests
{
    [Fact]
    public void SaturatingBoost_NormalValue_MultipliesBy10()
    {
        Assert.Equal(2000, RelayDriver.SaturatingBoost(200));
    }

    [Fact]
    public void SaturatingBoost_Zero_StaysZero()
    {
        Assert.Equal(0, RelayDriver.SaturatingBoost(0));
    }

    [Fact]
    public void SaturatingBoost_AtIntBoundary_DoesNotOverflow_SaturatesToMax()
    {
        // 300_000_000 * 10 = 3_000_000_000 > int.MaxValue → would wrap negative in int.
        Assert.Equal(int.MaxValue, RelayDriver.SaturatingBoost(300_000_000));
        // Just over the ~214M threshold the reviewer named.
        Assert.Equal(int.MaxValue, RelayDriver.SaturatingBoost(214_748_365));
    }

    [Fact]
    public async Task BoostedTask_LargeSubagentTimeout_CeilingSaturates_NotNegative()
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
              "maxVerifyLoops": 1,
              "subagentTimeoutMs": 300000000,
              "boostTurnsTaskIds": ["overflow-task"]
            }
            """);
        repo.WriteTask("overflow-task", "# Overflow task\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),     // stage 5 author gate
            new TestRunResult(0, "green"));  // stage 9 verify — green
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "overflow-task");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        Assert.NotEmpty(runner.Invocations);
        foreach (var inv in runner.Invocations)
        {
            // The boosted ceiling saturates positive instead of overflowing negative.
            Assert.Equal(int.MaxValue, inv.AbsoluteCeilingMs);
            // The turns boost (200 * 10) does not overflow either.
            Assert.Equal(2000, inv.MaxTurns);
        }
    }
}
