using System.Diagnostics;
using System.Text.RegularExpressions;

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
    /// The file-size guard script must exit 0.  Before the split it exits 1
    /// with 12+ violations; after the split every .cs file is ≤ 300 lines.
    /// </summary>
    [Fact]
    public void GuardScript_ExitsZero()
    {
        var guardPath = Path.Combine(RepoRoot, "tools", "guards", "check-file-size.sh");
        Assert.True(File.Exists(guardPath), $"guard script not found at {guardPath}");

        var startInfo = new ProcessStartInfo("/bin/bash", guardPath)
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["VISUAL_RELAY_FILE_LINE_LIMIT"] = "300";

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(10_000);

        Assert.True(process.ExitCode == 0,
            $"guard exited {process.ExitCode}, expected 0.\nstderr:\n{stderr}\nstdout:\n{stdout}");
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
    /// the baseline of 130: 127 established on 2026-06-10 before any split,
    /// +3 on 2026-06-10 (CpuPulse partial: cpu-pulse survival, true-wedge kill,
    /// killed-output persistence — the fs-blinded-watchdog regression family).
    ///
    /// Baseline composition:
    ///   SwivalSubagentRunnerWatchdogTests.cs (+ .CpuPulse.cs) 14
    ///   Installer5LauncherTests.cs                            20
    ///   GitCommitterTests.cs                                   9
    ///   RelayDriverResumeTests.cs                              5
    ///   BackendConfigGeneratorTests.cs                        13
    ///   GitCommitterAutoIncludeTests.cs + .Snapshot.cs        14
    ///   RelayDriverGitCommitTests.cs                           9
    ///   SwivalSubagentRunnerCommandFilterTests.cs             15
    ///   SwivalSubagentRunnerTests.cs                           9
    ///   RelayDriverTests.cs                                   13
    ///   NoCommitContaminationTests.cs                          3
    ///   PlanPhaseRunnerTests.cs                                6
    ///                                                       ----
    ///   Total (oversized families)                           130
    /// </summary>
    [Fact]
    public void FactCount_AcrossOversizedFiles_MatchesBaseline()
    {
        const int baseline = 130;

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
