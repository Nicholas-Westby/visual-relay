using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// The generalized in-process escalation in <see cref="SwivalSubagentRunner.RunAsync"/>:
/// ANY in-process failure (contract/shape reject, nonzero exit, persistent stall)
/// re-runs the stage with an escalated tier (cheap→balanced→frontier, capped) AND a
/// doubled turn budget, up to <c>MaxStageFailures</c> runs (original + escalations),
/// then fails. The 10× boost holds turns flat while the tier still escalates. Hard
/// infra aborts (absolute ceiling) do not escalate. Driven through a fake swival that
/// records the <c>--profile</c> (tier) and <c>--max-turns</c> it was launched with on
/// each attempt, wrapped by the transparent passthrough nono.
/// </summary>
public sealed class SwivalSubagentRunnerEscalationTests
{
    // Fake swival that records "<attemptDir> profile=<p> turns=<t>" to ladder.log,
    // then either always contract-fails (no JSON), nonzero-exits, persistently stalls
    // (no output, then blocks forever 0-CPU via `tail -f /dev/null` so the first-output
    // watchdog fires), or recovers at a chosen attempt. Mode is baked into the script.
    private static Task<string> WriteLadderSwivalAsync(string root, string mode) =>
        SwivalTestHelpers.WriteExecutableAsync(root, $"fake-swival-ladder-{mode}",
            $$"""
            #!/usr/bin/env bash
            profile=""; turns=""; trace_dir=""
            while [[ $# -gt 0 ]]; do
              case "$1" in
                --profile) profile="$2"; shift 2;;
                --max-turns) turns="$2"; shift 2;;
                --trace-dir) trace_dir="$2"; shift 2;;
                *) shift;;
              esac
            done
            echo "$(basename "$trace_dir") profile=$profile turns=$turns" >> ladder.log
            case "{{mode}}" in
              contract) echo "no fenced json here"; exit 0;;
              nonzero)  echo "boom" >&2; exit 7;;
              stall)    exec tail -f /dev/null;;
              recover2)
                if [[ "$trace_dir" == *attempt2* ]]; then
                  printf '```json\n{"summary":"recovered at run 2","options":["x"]}\n```\n'; exit 0
                fi
                echo "no fenced json here"; exit 0;;
            esac
            """);

    // firstOutputMs > 0 pins a tiny first-output window across all tiers so a
    // no-output stall fires the watchdog deterministically fast (the stall-ladder test).
    private static RelayConfig EscalationConfig(int maxStageFailures = 3, int maxContractRetries = 0, int maxStallRetries = 0, int maxTurns = 200, int firstOutputMs = 0) =>
        new(
            TasksDir: "llm-tasks",
            TestCommand: "true",
            TestFileCommand: "true",
            LogSources: [],
            TierProfiles: new Dictionary<string, string> { ["cheap"] = "cheap", ["balanced"] = "balanced", ["frontier"] = "frontier" },
            EnableFixVerify: true,
            MaxStageFailures: maxStageFailures,
            MaxTurns: maxTurns,
            BaselineVerify: false,
            ArchiveOnDone: true,
            SubagentTimeoutMilliseconds: 30_000,
            TestTimeoutMilliseconds: 300_000,
            FirstOutputTimeoutMsByTier: firstOutputMs > 0
                ? new Dictionary<string, int> { ["cheap"] = firstOutputMs, ["balanced"] = firstOutputMs, ["frontier"] = firstOutputMs }
                : new Dictionary<string, int> { ["cheap"] = 90_000, ["balanced"] = 120_000, ["frontier"] = 660_000 },
            FirstOutputTimeoutMs: firstOutputMs > 0 ? firstOutputMs : 660_000,
            MaxStallRetries: maxStallRetries,
            MaxContractRetries: maxContractRetries,
            InactivityTimeoutMsByTier: null,
            InactivityTimeoutMs: 600_000);

    private static async Task<IReadOnlyList<(string Attempt, string Profile, string Turns)>> ReadLadderAsync(string root)
    {
        var path = Path.Combine(root, "ladder.log");
        if (!File.Exists(path)) return [];
        var rows = new List<(string, string, string)>();
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            var parts = line.Split(' ');
            rows.Add((parts[0], parts[1]["profile=".Length..], parts[2]["turns=".Length..]));
        }
        return rows;
    }

    private static StageInvocation InvocationFor(string root, RelayStageDefinition stage, int maxTurns, bool boosted = false) =>
        new(stage, stage.Tier, "run-1", root, "task", "# Task", string.Empty, [], [],
            Path.Combine(root, ".relay", "task", $"stage{stage.Number}-attempt1"),
            Path.Combine(root, ".relay", "task", $"stage{stage.Number}-attempt1.report.json"),
            maxTurns, IsTurnBoosted: boosted);

    [Fact]
    public async Task RunAsync_ContractFailure_EscalatesTierAndDoublesTurns_ThreeRunsThenFails()
    {
        using var repo = TestRepository.Create();
        var script = await WriteLadderSwivalAsync(repo.Root, "contract");
        var runner = new SwivalSubagentRunner(EscalationConfig(), script, backendProbe: SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        var result = await runner.RunAsync(InvocationFor(repo.Root, RelayStages.All[0], maxTurns: 200));

        Assert.False(result.IsValid);
        var ladder = await ReadLadderAsync(repo.Root);
        Assert.Equal(3, ladder.Count);
        Assert.Equal(("stage1-attempt1", "cheap", "200"), ladder[0]);
        Assert.Equal(("stage1-attempt2", "balanced", "400"), ladder[1]);
        Assert.Equal(("stage1-attempt3", "frontier", "800"), ladder[2]);
        // No fourth run beyond the 3-run cap.
        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".relay", "task", "stage1-attempt4")));
    }

    [Fact]
    public async Task RunAsync_NonzeroExit_AlsoEscalates_NotJustContractFailures()
    {
        using var repo = TestRepository.Create();
        var script = await WriteLadderSwivalAsync(repo.Root, "nonzero");
        var runner = new SwivalSubagentRunner(EscalationConfig(), script, backendProbe: SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        var result = await runner.RunAsync(InvocationFor(repo.Root, RelayStages.All[0], maxTurns: 200));

        Assert.False(result.IsValid);
        var ladder = await ReadLadderAsync(repo.Root);
        Assert.Equal(["cheap", "balanced", "frontier"], ladder.Select(r => r.Profile));
        Assert.Equal(["200", "400", "800"], ladder.Select(r => r.Turns));
    }

    [Fact]
    public async Task RunAsync_PersistentStall_AlsoEscalates_ThreeRunsThenFails()
    {
        using var repo = TestRepository.Create();
        // A no-output stall: each run writes its ladder row then blocks forever (0-CPU),
        // so the first-output watchdog (pinned tiny) fires. maxStallRetries:0 means the
        // first stall escalates immediately, so the stall path drives the full ladder.
        var script = await WriteLadderSwivalAsync(repo.Root, "stall");
        var runner = new SwivalSubagentRunner(EscalationConfig(firstOutputMs: 2000), script, backendProbe: SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        var result = await runner.RunAsync(InvocationFor(repo.Root, RelayStages.All[0], maxTurns: 200));

        Assert.False(result.IsValid);
        var ladder = await ReadLadderAsync(repo.Root);
        Assert.Equal(3, ladder.Count);
        Assert.Equal(["cheap", "balanced", "frontier"], ladder.Select(r => r.Profile));
        Assert.Equal(["200", "400", "800"], ladder.Select(r => r.Turns));
        // No fourth run beyond the 3-run cap.
        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".relay", "task", "stage1-attempt4")));
    }

    [Fact]
    public async Task RunAsync_FrontierDefaultStage_StaysFrontier_ButTurnsStillDouble()
    {
        using var repo = TestRepository.Create();
        var script = await WriteLadderSwivalAsync(repo.Root, "contract");
        var review = RelayStages.All[6]; // stage 7 Review — frontier default
        var runner = new SwivalSubagentRunner(EscalationConfig(), script, backendProbe: SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        await runner.RunAsync(InvocationFor(repo.Root, review, maxTurns: 200));

        var ladder = await ReadLadderAsync(repo.Root);
        Assert.Equal(["frontier", "frontier", "frontier"], ladder.Select(r => r.Profile));
        Assert.Equal(["200", "400", "800"], ladder.Select(r => r.Turns));
    }

    [Fact]
    public async Task RunAsync_TenXBoost_TurnsStayFlat_ButTierStillEscalates()
    {
        using var repo = TestRepository.Create();
        var script = await WriteLadderSwivalAsync(repo.Root, "contract");
        var runner = new SwivalSubagentRunner(EscalationConfig(), script, backendProbe: SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        // boosted: the effective run-1 budget is already 10× (2000); doubling is suppressed.
        await runner.RunAsync(InvocationFor(repo.Root, RelayStages.All[0], maxTurns: 2000, boosted: true));

        var ladder = await ReadLadderAsync(repo.Root);
        Assert.Equal(["cheap", "balanced", "frontier"], ladder.Select(r => r.Profile));
        Assert.Equal(["2000", "2000", "2000"], ladder.Select(r => r.Turns));
    }

    [Fact]
    public async Task RunAsync_RecoversOnEscalatedRun_ReturnsSuccess_AndLogsEscalation()
    {
        using var repo = TestRepository.Create();
        var script = await WriteLadderSwivalAsync(repo.Root, "recover2");
        var sink = new InMemoryRelayEventSink();
        var runner = new SwivalSubagentRunner(EscalationConfig(), script, sink, SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        var result = await runner.RunAsync(InvocationFor(repo.Root, RelayStages.All[0], maxTurns: 200));

        Assert.True(result.IsValid);
        Assert.Contains("recovered at run 2", result.Json!, StringComparison.Ordinal);
        var escalation = Assert.Single(sink.Events, e => e.EventName == "stage_escalated");
        var message = escalation.Data!["message"];
        Assert.Contains("run 2/3", message, StringComparison.Ordinal);
        Assert.Contains("cheap", message, StringComparison.Ordinal);
        Assert.Contains("balanced", message, StringComparison.Ordinal);
        Assert.Contains("200", message, StringComparison.Ordinal);
        Assert.Contains("400", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_MaxStageFailuresOne_DoesNotEscalate()
    {
        using var repo = TestRepository.Create();
        var script = await WriteLadderSwivalAsync(repo.Root, "contract");
        var runner = new SwivalSubagentRunner(EscalationConfig(maxStageFailures: 1), script, backendProbe: SwivalTestHelpers.AlwaysReady,
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        await runner.RunAsync(InvocationFor(repo.Root, RelayStages.All[0], maxTurns: 200));

        var ladder = await ReadLadderAsync(repo.Root);
        Assert.Single(ladder);
        Assert.Equal("cheap", ladder[0].Profile);
    }
}
