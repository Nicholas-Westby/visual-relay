namespace VisualRelay.Tests;

/// <summary>
/// Behavioral tests for the nono sandbox gate, now owned by VisualRelay.Cli's
/// <c>launch</c> command (re-pointed from the bash <c>Installer5Sandbox2</c>
/// suite). The sandbox is always on, so when nono is absent <c>launch</c> must
/// exit non-zero with an install message and NOT start the backend; and
/// provisioning still pulls the swival base pack when nono is present.
/// </summary>
public sealed class CliNonoGateTests
{
    [Fact]
    public async Task Launch_SandboxEnabled_NonoAbsent_ExitsNonZeroWithInstallMessage()
    {
        var (repo, stub) = CliHarness.NewSandboxRepo();
        try
        {
            CliHarness.WriteStub(stub, "dotnet");
            CliHarness.WriteStub(stub, "swival");
            // nono intentionally absent.
            var (ec, _, err) = await CliHarness.RunAsync(repo, stub, ["launch"],
                new Dictionary<string, string> { ["VR_BACKEND_FLAG"] = Path.Combine(repo, "backend-ran") });

            Assert.NotEqual(0, ec);
            Assert.Contains("nono", err, StringComparison.OrdinalIgnoreCase);
            Assert.Matches("(?i)install|brew|nix", err);
            Assert.False(File.Exists(Path.Combine(repo, "backend-ran")),
                "backend must not run when the nono gate fails");
        }
        finally { TryDelete(repo); }
    }

    /// <summary>
    /// Regression (converted from the old bash <c>BypassSandbox_ReadsConfigFromScriptDir</c>,
    /// which used to prove a <c>bypassSandbox:true</c> config made the launcher SKIP nono):
    /// a stale <c>"bypassSandbox": true</c> key left in <c>.relay/config.json</c> is now
    /// silently ignored. The sandbox is always on, so with nono absent the launch must
    /// STILL hit the nono requirement (exit 127, nono error) and never reach the app's
    /// <c>dotnet run --project …App…</c> / start the backend. Locks the "silently ignore"
    /// decision at the launcher surface.
    /// </summary>
    [Fact]
    public async Task Launch_StaleBypassSandboxKey_StillRequiresNono()
    {
        var (repo, stub) = CliHarness.NewSandboxRepo();
        // Inject the stale opt-out key the loader/gate must now ignore.
        File.WriteAllText(Path.Combine(repo, ".relay", "config.json"),
            "{\"testCmd\":\"true\",\"bypassSandbox\":true}");
        var backendRan = Path.Combine(repo, "backend-ran");
        try
        {
            CliHarness.WriteStub(stub, "dotnet", CliHarness.BackendAwareDotnetStub);
            CliHarness.WriteStub(stub, "swival");
            // nono intentionally absent — the stale bypass key must NOT skip the gate.
            var (ec, _, err) = await CliHarness.RunAsync(repo, stub, ["launch"],
                new Dictionary<string, string> { ["VR_BACKEND_FLAG"] = backendRan });

            Assert.NotEqual(0, ec);
            Assert.Contains("nono", err, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(backendRan),
                "a stale bypassSandbox:true must not skip the nono gate — backend must not run");
        }
        finally { TryDelete(repo); }
    }

    [Fact]
    public async Task Launch_SandboxEnabled_NonoPresent_PullsSwivalPack()
    {
        var (repo, stub) = CliHarness.NewSandboxRepo();
        var nonoArgv = Path.Combine(repo, "nono-argv");
        try
        {
            CliHarness.WriteStub(stub, "dotnet");
            CliHarness.WriteStub(stub, "swival");
            CliHarness.WriteStub(stub, "nono", $"printf '%s ' \"$@\" >> '{nonoArgv}'; printf '\\n' >> '{nonoArgv}'\nexit 0");
            await CliHarness.RunAsync(repo, stub, ["launch"], UpgradeSuppressed(repo));

            Assert.True(File.Exists(nonoArgv), "nono should have been invoked");
            Assert.Contains("pull jedisct1/swival", File.ReadAllText(nonoArgv), StringComparison.Ordinal);
        }
        finally { TryDelete(repo); }
    }

    private static Dictionary<string, string> UpgradeSuppressed(string repo) => new()
    {
        ["VR_BACKEND_FLAG"] = Path.Combine(repo, "backend-ran"),
        ["XDG_STATE_HOME"] = Path.Combine(repo, "state"),
        ["VISUAL_RELAY_SWIVAL_LATEST_CMD"] = "true", // empty stdout ⇒ no upgrade noise
    };

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (Exception) { /* best-effort */ }
    }
}
