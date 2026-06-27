using VisualRelay.App.Services;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Guards that a <see cref="RelayDriver"/> built via
/// <see cref="RelayDriverDependencies.ForTests"/> resolves the vr-guard sandbox
/// profile under a hermetic TEMP directory — never the real <c>$HOME/.config</c>
/// — so the integration suite never writes (or, under the always-on vr-guard
/// nono sandbox, fails to write) the user's real
/// <c>~/.config/visual-relay/vr-guard.json</c>. Production (the real accessor)
/// must still resolve the canonical <c>${XDG_CONFIG_HOME:-$HOME/.config}</c> path.
/// </summary>
public sealed class RelayDriverProfileIsolationTests
{
    [Fact]
    public void ForTests_ResolvesVrGuardProfileUnderTempDir_NeverRealHomeConfig()
    {
        // The real environment (== the production default) resolves the canonical
        // ~/.config profile; the test-built driver must NOT.
        var realEnv = new ProcessEnvironmentAccessor();
        var realConfigDir = XdgConfig.ResolveConfigDir(realEnv);

        var deps = RelayDriverDependencies.ForTests(
            new ScriptedSubagentRunner(), new ScriptedTestRunner(), new InMemoryRelayEventSink());
        var isolated = NonoProfileEnsurer.ResolveProfilePath(deps.EnvironmentAccessor);

        // Lands under the process temp dir, in a visual-relay/vr-guard.json leaf …
        Assert.StartsWith(Path.GetTempPath(), isolated, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("visual-relay", "vr-guard.json"), isolated, StringComparison.Ordinal);
        // … and never under the real config dir ($XDG_CONFIG_HOME or $HOME/.config).
        Assert.DoesNotContain(realConfigDir, isolated, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionAccessor_ResolvesVrGuardProfile_UnderRealXdgOrHomeConfig()
    {
        // Acceptance: with the real accessor — which is exactly what RelayDriver
        // passes in production (a null accessor falls through to the real process
        // env via KeyEnvFile.GetEnv) — ResolveProfilePath still returns
        // ${XDG_CONFIG_HOME:-$HOME/.config}/visual-relay/vr-guard.json, byte-for-byte.
        var realEnv = new ProcessEnvironmentAccessor();
        var viaProcessAccessor = NonoProfileEnsurer.ResolveProfilePath(realEnv);
        var viaNullDefault = NonoProfileEnsurer.ResolveProfilePath();

        Assert.Equal(viaNullDefault, viaProcessAccessor);
        Assert.Equal(
            Path.Combine(XdgConfig.ResolveConfigDir(realEnv), "visual-relay", "vr-guard.json"),
            viaProcessAccessor);
    }

    /// <summary>
    /// End-to-end guard for the production wiring
    /// <c>EnsureAsync(_dependencies.EnvironmentAccessor, …)</c> in
    /// <see cref="RelayDriver.RunTaskAsync"/>: a driver built via
    /// <see cref="RelayDriverDependencies.ForTests"/> with an injected temp XDG
    /// accessor must self-heal the vr-guard profile INTO that temp dir and leave the
    /// real <c>~/.config</c> profile byte-untouched. Reverting that one line to the
    /// no-accessor <c>EnsureAsync()</c> targets the real <c>~/.config</c> instead,
    /// which fails assertion (b) — the temp profile never appears — and, when the
    /// real profile was absent or differed, assertion (a) too.
    /// </summary>
    [Fact]
    public async Task RunTaskAsync_SelfHealsVrGuardProfileUnderInjectedXdg_LeavingRealHomeConfigUntouched()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("profile-isolation-e2e", "# Profile isolation e2e\n");

        // A dedicated, fresh temp XDG dir for THIS run (not the suite-shared default),
        // so "a profile appeared here" is attributable to this driver's self-heal and
        // stays revert-sensitive. Cleaned up below regardless of outcome.
        var xdgDir = Path.Combine(Path.GetTempPath(), "vr-iso-e2e", Guid.NewGuid().ToString("N"));
        var env = new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = xdgDir };
        var isolatedProfile = NonoProfileEnsurer.ResolveProfilePath(env);

        // Snapshot the REAL ~/.config profile target before the run (it may or may
        // not pre-exist on the host); the run must not create or alter it.
        var realProfile = NonoProfileEnsurer.ResolveProfilePath();
        var realExistedBefore = File.Exists(realProfile);
        var realBytesBefore = realExistedBefore ? await File.ReadAllTextAsync(realProfile) : null;

        try
        {
            var runner = new ScriptedSubagentRunner();
            runner.SeedHappyPath("src/status.cs", "tests/status.tests.cs");
            var driver = new RelayDriver(
                RelayDriverDependencies.ForTests(
                    runner,
                    new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
                    new InMemoryRelayEventSink(),
                    environmentAccessor: env),
                RelayDriverOptions.NoGitCommit);

            var outcome = await driver.RunTaskAsync(repo.Root, "profile-isolation-e2e");

            // The run completed end-to-end, proving EnsureAsync actually executed.
            Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
            // (b) The profile self-healed under the INJECTED temp XDG dir …
            Assert.True(
                File.Exists(isolatedProfile),
                $"driver must self-heal the vr-guard profile under the injected XDG dir: {isolatedProfile}");
            Assert.Equal(NonoProfileEnsurer.EmbeddedContent, await File.ReadAllTextAsync(isolatedProfile));
            // (a) … and the real ~/.config profile was neither created nor modified.
            Assert.Equal(realExistedBefore, File.Exists(realProfile));
            if (realExistedBefore)
                Assert.Equal(realBytesBefore, await File.ReadAllTextAsync(realProfile));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(xdgDir);
        }
    }
}
