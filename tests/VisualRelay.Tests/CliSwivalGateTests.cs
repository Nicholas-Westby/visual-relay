namespace VisualRelay.Tests;

/// <summary>
/// Behavioral tests for the swival prerequisite gate, now owned by
/// VisualRelay.Cli's <c>launch</c> command (re-pointed from the bash
/// <c>Installer5Bootstrap4</c> suite). swival is hard-required and not
/// sandbox-gated. With swival present, launch proceeds and starts the backend;
/// with swival absent in a non-TTY context, launch prints the Homebrew-tap
/// instructions, does NOT prompt or run the installer, exits non-zero, and never
/// starts the backend. (The TTY consent-accept path is covered by the pure
/// <c>CliSwivalUpgradeDecisionTests</c> / <c>Tty</c> unit tests.)
/// </summary>
public sealed class CliSwivalGateTests
{
    [Fact]
    public async Task Launch_SwivalPresent_BackendRuns()
    {
        var (repo, stub) = CliHarness.NewSandboxRepo(bypassSandbox: true);
        var backendFlag = Path.Combine(repo, "backend-ran");
        try
        {
            CliHarness.WriteStub(stub, "dotnet", CliHarness.BackendAwareDotnetStub);
            CliHarness.WriteStub(stub, "swival");
            await CliHarness.RunAsync(repo, stub, ["launch"], new Dictionary<string, string>
            {
                ["VR_BACKEND_FLAG"] = backendFlag,
                ["XDG_STATE_HOME"] = Path.Combine(repo, "state"),
                ["VISUAL_RELAY_SWIVAL_LATEST_CMD"] = "true",
            });

            Assert.True(File.Exists(backendFlag), "backend should run once swival is present");
        }
        finally { TryDelete(repo); }
    }

    [Fact]
    public async Task Launch_SwivalMissing_NonTty_PrintsInstructions_NoInstaller_NonZero_NoBackend()
    {
        var (repo, stub) = CliHarness.NewSandboxRepo(bypassSandbox: true);
        var backendFlag = Path.Combine(repo, "backend-ran");
        var installerRan = Path.Combine(repo, "installer-ran");
        try
        {
            CliHarness.WriteStub(stub, "dotnet");
            CliHarness.WriteStub(stub, "brew");
            CliHarness.WriteStub(stub, "vr-swival-installer", $"echo ran > '{installerRan}'\nexit 0");
            // swival intentionally absent.
            var (ec, _, err) = await CliHarness.RunAsync(repo, stub, ["launch"], new Dictionary<string, string>
            {
                ["VR_BACKEND_FLAG"] = backendFlag,
                ["VISUAL_RELAY_SWIVAL_INSTALLER"] = Path.Combine(stub, "vr-swival-installer"),
            });

            Assert.NotEqual(0, ec);
            Assert.Contains("swival/tap/swival", err, StringComparison.Ordinal);
            Assert.DoesNotContain("[y/N]", err, StringComparison.Ordinal);
            Assert.False(File.Exists(installerRan), "installer must not run in a non-TTY context");
            Assert.False(File.Exists(backendFlag), "backend must not run before the swival gate passes");
        }
        finally { TryDelete(repo); }
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (Exception) { /* best-effort */ }
    }
}
