using System.Text.Json;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Review (7) and Visual-review (8) run as a pair on their own recording path,
/// separate from the sequential one every other stage uses. That path must still
/// record what the stage actually ran on: the concrete model and the turn count
/// come from the stage report, and dropping them leaves the two review stages as
/// the only blanks in status.json — so the stage card cannot show the model and
/// per-model token attribution silently loses the reviews.
/// </summary>
public sealed class ReviewPairStageMetricTests
{
    [Fact]
    public async Task RunTaskAsync_ReviewPairStages_RecordModelAndTurnsLikeSequentialStages()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("pair-metrics", "# Pair metrics\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        runner.SeedCostReports();
        var driver = new RelayDriver(
            RelayDriverTestHelpers.DepsFor(repo, runner, new ScriptedTestRunner(new TestRunResult(0, "green")),
                new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "pair-metrics");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        var entries = ReadStatusEntries(repo.Root, "pair-metrics");

        // Stage 6 (Implement) goes through the sequential path and is the control:
        // whatever it records, the pair path must record too.
        var implement = entries.Single(e => e.Stage == 6);
        Assert.False(string.IsNullOrEmpty(implement.Model),
            "control: the sequential path should record a model");
        Assert.NotNull(implement.Turns);

        var review = entries.Single(e => e.Stage == 7);
        Assert.Equal("frontier", review.Model);
        Assert.Equal(implement.Turns, review.Turns);
    }

    private sealed record StatusEntry(int Stage, string? Model, int? Turns);

    private static IReadOnlyList<StatusEntry> ReadStatusEntries(string root, string taskId)
    {
        var path = Path.Combine(root, ".relay", taskId, "status.json");
        Assert.True(File.Exists(path), $"status.json missing at {path}");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.EnumerateArray().Select(e => new StatusEntry(
            e.GetProperty("stage").GetInt32(),
            e.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null,
            e.TryGetProperty("turns", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : null))];
    }
}
