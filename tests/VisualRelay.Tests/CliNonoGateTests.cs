namespace VisualRelay.Tests;

/// <summary>
/// Behavioral tests for the nono sandbox gate, now owned by VisualRelay.Cli's
/// <c>launch</c> command (re-pointed from the bash <c>Installer5Sandbox2</c>
/// suite). The sandbox is always on, so when nono is absent <c>launch</c> must
/// exit non-zero with an install message and never reach the app; and when nono
/// is present the launch must reach the app without pulling any profile pack
/// first — vr-guard inherits nono's built-in default and nothing else.
/// </summary>
public sealed class CliNonoGateTests
{
    [Fact]
    public async Task Launch_SandboxEnabled_NonoAbsent_ExitsNonZeroWithInstallMessage()
    {
        var (repo, stub) = CliHarness.NewSandboxRepo();
        var dotnetArgv = Path.Combine(repo, "dotnet-argv");
        try
        {
            CliHarness.WriteStub(stub, "dotnet", CliHarness.ArgvRecordingDotnetStub(dotnetArgv));
            // nono intentionally absent.
            var (ec, _, err) = await CliHarness.RunAsync(repo, stub, ["launch"], LaunchEnv(repo));

            Assert.NotEqual(0, ec);
            Assert.Contains("nono", err, StringComparison.OrdinalIgnoreCase);
            Assert.Matches("(?i)install|brew|nix", err);
            Assert.False(File.Exists(dotnetArgv),
                "launch must not run the app when the nono gate fails");
        }
        finally { TryDelete(repo); }
    }

    /// <summary>
    /// Regression (converted from the old bash <c>BypassSandbox_ReadsConfigFromScriptDir</c>,
    /// which used to prove a <c>bypassSandbox:true</c> config made the launcher SKIP nono):
    /// a stale <c>"bypassSandbox": true</c> key left in <c>.relay/config.json</c> is now
    /// silently ignored. The sandbox is always on, so with nono absent the launch must
    /// STILL hit the nono requirement (exit 127, nono error) and never reach the app's
    /// <c>dotnet run --project …App…</c>. Locks the "silently ignore" decision at the
    /// launcher surface.
    /// </summary>
    [Fact]
    public async Task Launch_StaleBypassSandboxKey_StillRequiresNono()
    {
        var (repo, stub) = CliHarness.NewSandboxRepo();
        // Inject the stale opt-out key the loader/gate must now ignore.
        File.WriteAllText(Path.Combine(repo, ".relay", "config.json"),
            "{\"testCmd\":\"true\",\"bypassSandbox\":true}");
        var dotnetArgv = Path.Combine(repo, "dotnet-argv");
        try
        {
            CliHarness.WriteStub(stub, "dotnet", CliHarness.ArgvRecordingDotnetStub(dotnetArgv));
            // nono intentionally absent — the stale bypass key must NOT skip the gate.
            var (ec, _, err) = await CliHarness.RunAsync(repo, stub, ["launch"], LaunchEnv(repo));

            Assert.NotEqual(0, ec);
            Assert.Contains("nono", err, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(dotnetArgv),
                "a stale bypassSandbox:true must not skip the nono gate — the app must not run");
        }
        finally { TryDelete(repo); }
    }

    /// <summary>
    /// The launch used to pull a third-party nono pack every time, because
    /// vr-guard extended it. vr-guard is self-contained now, so the launch must
    /// not shell out to nono at all — the pack's "already at x.y.z" line was the
    /// only thing still naming an agent Visual Relay had deleted.
    /// </summary>
    [Fact]
    public async Task Launch_SandboxEnabled_NonoPresent_PullsNoProfilePack()
    {
        var (repo, stub) = CliHarness.NewSandboxRepo();
        var nonoArgv = Path.Combine(repo, "nono-argv");
        try
        {
            CliHarness.WriteStub(stub, "dotnet");
            CliHarness.WriteStub(stub, "nono", $"printf '%s ' \"$@\" >> '{nonoArgv}'; printf '\\n' >> '{nonoArgv}'\nexit 0");
            await CliHarness.RunAsync(repo, stub, ["launch"], LaunchEnv(repo));

            Assert.False(File.Exists(nonoArgv),
                "launch must not invoke nono: " +
                (File.Exists(nonoArgv) ? File.ReadAllText(nonoArgv) : ""));
        }
        finally { TryDelete(repo); }
    }

    private static Dictionary<string, string> LaunchEnv(string repo) => new()
    {
        ["XDG_STATE_HOME"] = Path.Combine(repo, "state"),
    };

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (Exception) { /* best-effort */ }
    }
}
