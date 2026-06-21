namespace VisualRelay.Tests;

/// <summary>
/// Behavioral tests for the nono sandbox gate, now owned by VisualRelay.Cli's
/// <c>launch</c> command (re-pointed from the bash <c>Installer5Sandbox2</c>
/// suite). When the sandbox is enabled and nono is absent, <c>launch</c> must
/// exit non-zero with an install message and NOT start the backend; bypassing
/// the sandbox skips the nono check; and provisioning still pulls the swival
/// base pack.
/// </summary>
public sealed class CliNonoGateTests
{
    [Fact]
    public async Task Launch_SandboxEnabled_NonoAbsent_ExitsNonZeroWithInstallMessage()
    {
        var (repo, stub) = CliHarness.NewSandboxRepo(bypassSandbox: false);
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

    [Fact]
    public async Task Launch_BypassSandbox_NonoAbsent_ProceedsWithoutNonoError()
    {
        var (repo, stub) = CliHarness.NewSandboxRepo(bypassSandbox: true);
        try
        {
            CliHarness.WriteStub(stub, "dotnet");
            CliHarness.WriteStub(stub, "swival");
            var (_, _, err) = await CliHarness.RunAsync(repo, stub, ["launch"],
                UpgradeSuppressed(repo));

            Assert.DoesNotContain("nono", err, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(repo); }
    }

    [Fact]
    public async Task Launch_SandboxEnabled_NonoPresent_PullsSwivalPack()
    {
        var (repo, stub) = CliHarness.NewSandboxRepo(bypassSandbox: false);
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
