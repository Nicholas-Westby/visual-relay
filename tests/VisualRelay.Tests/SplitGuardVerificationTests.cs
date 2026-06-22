using System.Text.RegularExpressions;
using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// Self-validating guard tests for the split-oversized-test-files task.
/// These MUST fail before the split lands and pass after every oversized
/// test file is under 300 lines with zero [Fact] loss.
/// </summary>
public sealed partial class SplitGuardVerificationTests
{
    private static string RepoRoot => RepoSetup.Root;
    private static string TestsDir => Path.Combine(RepoRoot, "tests", "VisualRelay.Tests");

    /// <summary>
    /// The C# file-size guard must report zero violations on the live tree at
    /// the 300-line limit — the same gate the Cli <c>check</c> now runs (porting
    /// the retired <c>check-file-size.sh</c>). Before the split it found 12+;
    /// after the split every src/tests/tools *.cs/*.axaml file is ≤ 300 lines.
    /// </summary>
    [Fact]
    public void FileSizeGuard_ReportsNoViolations()
    {
        string[] roots = ["src", "tests", "tools"];
        var violations = FileSizeGuard.Enumerate(RepoRoot, roots, 300);

        Assert.True(violations.Count == 0,
            "file-size guard found violations:\n" +
            string.Join("\n", violations.Select(v => $"{v.Path}: {v.Lines} lines (limit {v.Limit})")));
    }

    /// <summary>
    /// Every .cs test file (excluding bin/obj) must be ≤ 300 lines.
    /// This is the same check the guard performs, scoped to the test directory.
    /// </summary>
    [Fact]
    public void AllTestCsFiles_AreAtMost300Lines()
    {
        const int limit = 300;
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(TestsDir, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/")
                                 && !f.Contains("\\bin\\") && !f.Contains("\\obj\\"))
                     .OrderBy(f => f))
        {
            var lines = File.ReadAllLines(file).Length;
            if (lines > limit)
                violations.Add($"{Path.GetRelativePath(RepoRoot, file)}: {lines} lines (limit {limit})");
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// The total [Fact] count across the oversized-file families must match
    /// the baseline of 143: 127 established on 2026-06-10 before any split,
    /// +3 on 2026-06-10 (CpuPulse partial: cpu-pulse survival, true-wedge kill,
    /// killed-output persistence — the fs-blinded-watchdog regression family),
    /// +3 on 2026-06-11 (NonzeroExit: retry-and-persist nonzero swival exits),
    /// +2 on 2026-06-11 (manifest gitignore validation: GitCommitter backstop
    /// + RelayDriverGitCommitTests end-to-end backstop),
    /// +2 on 2026-06-12 (watchdog socket-wedge regression:
    /// WaitAsync_BurstThenTotalSilence unit test + RunAsync_EarlyBurst
    /// integration test),
    /// +2 then -2 on 2026-06-13 (proof-opt-out: 2 new facts added then
    /// extracted to standalone RelayDriverGitCommitProofOptOutTests.cs,
    /// net zero change to the oversized-family count).
    /// +1 on 2026-06-14 (tasks-dir exclusion: CommitAsync_ExcludesTasksDirFile
    /// FromAutoInclude_WhenCreatedMidRun in GitCommitterAutoIncludeTests.TasksDir.cs).
    /// +3 on 2026-06-14 (Kimi K2.7 Code upgrade: KimiK2_UpstreamModel_IsKimiK2_7Code,
    /// KimiK2_GeneratedConfig_ContainsKimiK2_7Code, KimiK2_Template_DoesNotContainK2_6
    /// in BackendConfigGeneratorTests.KimiK2_7Upstream.cs).
    ///
    /// Baseline composition:
    ///   SwivalSubagentRunnerWatchdogTests.cs (+ .CpuPulse.cs,
    ///     .ActivityWatchdog.cs, .TierWindows.cs, .NonzeroExit.cs) 19
    ///   Installer5LauncherTests.cs                                20
    ///   GitCommitterTests.cs + .CommitMsgHooks.cs                 10
    ///   RelayDriverResumeTests.cs (+ .CommitGate.cs, .ReAdd.cs,
    ///     .ReAdd2.cs)                                               9
    ///   BackendConfigGeneratorTests.cs                            17
    ///   GitCommitterAutoIncludeTests.cs + .Snapshot.cs            14
    ///   RelayDriverGitCommitTests.cs (+ .ResumeCommit.cs,
    ///     .GitignoredBackstop.cs)                                 10
    ///   SwivalSubagentRunnerCommandFilterTests.cs                 15
    ///   SwivalSubagentRunnerTests.cs                              10
    ///   RelayDriverTests.cs                                       13
    ///   NoCommitContaminationTests.cs                              3
    ///   PlanPhaseRunnerTests.cs                                    6
    ///                                                           ----
    ///   Total (oversized families)                               147
    /// </summary>
    [Fact]
    public void FactCount_AcrossOversizedFiles_MatchesBaseline()
    {
        // Bumped 149→150 on 2026-06-18: the GLM 5.2 frontier upgrade added
        // PerModelTimeout_FrontierGlm52Has480s to the BackendConfigGeneratorTests
        // family (BackendConfigGeneratorTests.PerModelTimeout.cs).
        // Bumped 150→156 on 2026-06-21: the in-run agent self-commit squash added
        // 5 facts to the GitCommitterTests family (GitCommitterTests.RunBaseSquash.cs)
        // and 1 to the RelayDriverGitCommitTests family
        // (RelayDriverGitCommitTests.SelfCommitSquash.cs).
        // Bumped 156→160 on 2026-06-21: the squash data-loss guards added 4 facts
        // to the GitCommitterTests family (GitCommitterTests.RunBaseSquashGuards.cs):
        // sealed-commit-in-range skip + only-bare control, all-candidates-rejected
        // HEAD restore, and committed-only-content preservation.
        // Dropped 160→151 on 2026-06-21: the launcher's subcommand logic moved into
        // VisualRelay.Cli, so the bash-asserting Installer5LauncherTests family lost
        // 9 facts (per-command published-binary/needs_dotnet/init-dispatch checks).
        // The behavior is now covered by the Cli* suites (CliInitCommandTests,
        // CliNonoGateTests, CliSwivalGateTests, CliSwivalUpgradeCheckTests,
        // CliWatchdogTests, CliCommandRouterTests), which are not oversized families.
        // Dropped 151→150 on 2026-06-21: porting tools/backend/backend.sh to the
        // published VisualRelay.Backend C# tool retired the script, so the
        // Installer5LauncherTests family lost its BackendSh_EndsWithMainInvocation
        // structural guard (the script no longer exists). The proxy lifecycle is now
        // covered by the (non-oversized) BackendLifecycle* / BackendConfigStep suites.
        // Dropped 150→149 on 2026-06-21: removing the sandbox-bypass capability
        // retired the bash BypassSandbox_ReadsConfigFromScriptDir fact from the
        // Installer5LauncherTests family. Its replacement (a stale-key still-requires-
        // nono regression) lives in the non-oversized CliNonoGateTests suite.
        // Bumped 149→150 on 2026-06-22: surfacing the real model-backend cause on a
        // swival nonzero exit added RunAsync_ModelAuthFailureSurfacesProxyAuthCause-
        // NotPromptEcho to the SwivalSubagentRunnerTests family.
        // Bumped 150→152 on 2026-06-22: the improve-live-tiers-ui task added two
        // TierRows_* [Fact]s to the BackendConfigGeneratorTests family
        // (BackendConfigGeneratorTests.cs: TierRows_HfOnlyAndDeepSeek,
        // TierRows_ClaudePresentAndEmptyKeys).
        const int baseline = 152;

        string[] prefixes =
        [
            "SwivalSubagentRunnerWatchdogTests",
            "Installer5LauncherTests",
            "GitCommitterTests",
            "GitCommitterAutoIncludeTests",
            "RelayDriverResumeTests",
            "BackendConfigGeneratorTests",
            "RelayDriverGitCommitTests",
            "SwivalSubagentRunnerCommandFilterTests",
            "SwivalSubagentRunnerTests",
            "RelayDriverTests",
            "NoCommitContaminationTests",
            "PlanPhaseRunnerTests",
        ];

        int count = 0;
        var details = new List<string>();

        foreach (var filePath in Directory.EnumerateFiles(TestsDir, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(filePath);
            var belongs = prefixes.Any(p =>
                fileName == p + ".cs" || fileName.StartsWith(p + ".", StringComparison.Ordinal));

            if (!belongs) continue;

            var fileFacts = Regex.Matches(File.ReadAllText(filePath), @"\[Fact\]").Count;
            count += fileFacts;
            details.Add($"{fileName}: {fileFacts}");
        }

        Assert.True(count == baseline,
            $"Expected {baseline} [Fact] attributes across oversized families, found {count}.\n" +
            string.Join("\n", details));
    }
}
