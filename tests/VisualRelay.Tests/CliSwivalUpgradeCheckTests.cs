namespace VisualRelay.Tests;

/// <summary>
/// Behavioral tests for the weekly swival upgrade check, now owned by
/// VisualRelay.Cli's <c>launch</c> command (re-pointed from the bash
/// <c>Installer5Bootstrap5</c> suite). The probe runs at most once per 7 days
/// (per-machine XDG-state stamp), always rewrites the stamp after a check, stays
/// non-fatal on a failing probe, and in a non-TTY context prints an
/// upgrade-available hint without prompting or upgrading. The probe/upgrader are
/// overridable via VISUAL_RELAY_SWIVAL_LATEST_CMD / _UPGRADER.
/// </summary>
public sealed class CliSwivalUpgradeCheckTests
{
    [Fact]
    public async Task FreshStamp_SkipsProbe_LaunchProceeds()
    {
        await RunUpgradeScenario(
            stampAgeSecs: 86_400, // 1 day — inside the window
            probeBody: ProbeRecording(emit: "swival 2.0.0"),
            assert: ctx =>
            {
                Assert.False(File.Exists(ctx.ProbeRan), "probe must not run inside the 7-day window");
                Assert.True(File.Exists(ctx.BackendRan), "launch should proceed");
            });
    }

    [Fact]
    public async Task StaleStamp_RunsProbe_RewritesStamp()
    {
        await RunUpgradeScenario(
            stampAgeSecs: 8 * 86_400,
            probeBody: ProbeRecording(emit: ""),
            assert: ctx =>
            {
                Assert.True(File.Exists(ctx.ProbeRan), "probe must run for a stale stamp");
                var last = long.Parse(File.ReadAllText(ctx.StampFile).Trim());
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                Assert.True(now - last < 60, "stamp must be rewritten after the check");
                Assert.True(File.Exists(ctx.BackendRan), "launch should proceed");
            });
    }

    [Fact]
    public async Task NoStamp_RunsProbe_CreatesStamp()
    {
        await RunUpgradeScenario(
            stampAgeSecs: -1,
            probeBody: ProbeRecording(emit: ""),
            assert: ctx =>
            {
                Assert.True(File.Exists(ctx.ProbeRan), "probe must run on first launch");
                Assert.True(File.Exists(ctx.StampFile), "stamp must be created");
                Assert.True(File.Exists(ctx.BackendRan), "launch should proceed");
            });
    }

    [Fact]
    public async Task UpgradeAvailable_NonTty_PrintsHint_NoUpgrade_Proceeds()
    {
        await RunUpgradeScenario(
            stampAgeSecs: 8 * 86_400,
            probeBody: ProbeRecording(emit: "swival 2.0.0"),
            assert: ctx =>
            {
                Assert.Equal(0, ctx.ExitCode);
                Assert.Matches("(?i)newer swival is available", ctx.Stderr);
                Assert.False(File.Exists(ctx.UpgraderRan), "upgrader must not run in a non-TTY context");
                Assert.DoesNotContain("[y/N]", ctx.Stderr, StringComparison.Ordinal);
                Assert.True(File.Exists(ctx.BackendRan), "launch should proceed");
            });
    }

    [Fact]
    public async Task FailingProbe_NonFatal_Proceeds_StampUpdates()
    {
        await RunUpgradeScenario(
            stampAgeSecs: 8 * 86_400,
            probeBody: "echo ran > \"$VR_PROBE_RAN\"\nexit 3",
            assert: ctx =>
            {
                Assert.Equal(0, ctx.ExitCode);
                Assert.True(File.Exists(ctx.ProbeRan), "probe must run");
                var last = long.Parse(File.ReadAllText(ctx.StampFile).Trim());
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                Assert.True(now - last < 60, "stamp must update even after a failing probe");
                Assert.True(File.Exists(ctx.BackendRan), "launch should proceed");
            });
    }

    private sealed record Ctx(
        int ExitCode, string Stderr, string ProbeRan, string UpgraderRan,
        string BackendRan, string StampFile);

    private static string ProbeRecording(string emit) =>
        $"echo ran > \"$VR_PROBE_RAN\"\nprintf '%s' '{emit}'\nexit 0";

    private static async Task RunUpgradeScenario(long stampAgeSecs, string probeBody, Action<Ctx> assert)
    {
        var (repo, stub) = CliHarness.NewSandboxRepo(bypassSandbox: true);
        var xdgState = Path.Combine(repo, "xdg-state");
        var stampFile = Path.Combine(xdgState, "visual-relay", "swival-upgrade-check");
        var probeRan = Path.Combine(repo, "probe-ran");
        var upgraderRan = Path.Combine(repo, "upgrader-ran");
        var backendRan = Path.Combine(repo, "backend-ran");
        try
        {
            CliHarness.WriteStub(stub, "dotnet");
            CliHarness.WriteStub(stub, "swival", "echo 'swival 1.0.0'\nexit 0");
            CliHarness.WriteStub(stub, "vr-swival-probe", probeBody);
            CliHarness.WriteStub(stub, "vr-swival-upgrader", $"echo ran > '{upgraderRan}'\nexit 0");

            if (stampAgeSecs >= 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(stampFile)!);
                var seeded = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - stampAgeSecs;
                File.WriteAllText(stampFile, seeded + "\n");
            }

            var (ec, _, err) = await CliHarness.RunAsync(repo, stub, ["launch"], new Dictionary<string, string>
            {
                ["VR_BACKEND_FLAG"] = backendRan,
                ["VR_PROBE_RAN"] = probeRan,
                ["XDG_STATE_HOME"] = xdgState,
                ["VISUAL_RELAY_SWIVAL_LATEST_CMD"] = Path.Combine(stub, "vr-swival-probe"),
                ["VISUAL_RELAY_SWIVAL_UPGRADER"] = Path.Combine(stub, "vr-swival-upgrader"),
            });

            assert(new Ctx(ec, err, probeRan, upgraderRan, backendRan, stampFile));
        }
        finally { TryDelete(repo); }
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (Exception) { /* best-effort */ }
    }
}
