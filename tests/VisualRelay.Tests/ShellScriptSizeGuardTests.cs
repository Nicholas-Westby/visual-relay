using VisualRelay.Core.Execution;
using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// The enforcing shell-script size guard-as-test (the house idiom mirrored from
/// <see cref="SplitGuardVerificationTests.AllTestCsFiles_AreAtMost300Lines"/>).
/// It enumerates the git-tracked tree from <see cref="RepoSetup.Root"/> through the
/// same <see cref="IGitInvoker"/> seam the <c>shell-size</c> runner uses, runs
/// <see cref="ShellSizeGuard.FindViolations"/> at the shared limit, and asserts no
/// tracked shell script (the <c>visual-relay</c> bootstrap, the thin wrappers, and
/// the two git hooks) exceeds 20 logic lines. The limit is the single global
/// <see cref="ShellSizeGuard.DefaultLimit"/> — there is no allowlist. Flipping the
/// task-12 advisory tool to fail here is the deliverable: a chunky script must be
/// converted to C#, never excused.
/// </summary>
public sealed class ShellScriptSizeGuardTests
{
    private static readonly IGitInvoker Git = new GitInvoker();

    /// <summary>
    /// Every git-tracked shell script in the live tree is at most 20 logic lines.
    /// This is the build-failing gate: it fails the moment any tracked shell script
    /// (by extension or hashbang) grows past the limit.
    /// </summary>
    [Fact]
    public async Task AllTrackedShellScripts_AreWithinTheLimit()
    {
        var files = await ReadTrackedFilesAsync(RepoSetup.Root);
        var violations = ShellSizeGuard.FindViolations(files, ShellSizeGuard.ResolveLimit());

        Assert.True(violations.Count == 0,
            "shell-size guard found violations (convert the logic to C#, do not relax the limit):\n" +
            string.Join("\n", violations.Select(v => $"{v.Path}: {v.Count} logic lines (limit {v.Limit})")));
    }

    /// <summary>
    /// The single global knob is 20. The enforcing test above and the <c>shell-size</c>
    /// runner both resolve the limit through <see cref="ShellSizeGuard.ResolveLimit"/>,
    /// which falls back to <see cref="ShellSizeGuard.DefaultLimit"/> — so asserting the
    /// constant pins the gate and the report to the same value and they can never
    /// diverge. (Asserted as a pure constant, not via env mutation, to honour the
    /// no-direct-env-mutation test convention; the env-override path is covered by the
    /// FindViolations unit tests.)
    /// </summary>
    [Fact]
    public void DefaultLimit_IsThe20LineCeiling()
    {
        Assert.Equal(20, ShellSizeGuard.DefaultLimit);
    }

    /// <summary>
    /// The gate bites: a synthetic 25-logic-line script added to the tracked set is
    /// reported as a violation at the limit (permanently encoding the deliberate-
    /// fattening proof so the enforcement can never silently regress), while the same
    /// script at exactly 20 lines passes (20 is the inclusive ceiling).
    /// </summary>
    [Fact]
    public async Task OverLimitScript_IsAViolation_AtLimitScript_IsNot()
    {
        var realFiles = await ReadTrackedFilesAsync(RepoSetup.Root);

        var over = realFiles.Append(("fixtures/too-fat.sh", ShellScript(25))).ToList();
        var overViolations = ShellSizeGuard.FindViolations(over, ShellSizeGuard.ResolveLimit());
        Assert.Contains(overViolations, v => v is { Path: "fixtures/too-fat.sh", Count: 25 });

        var atLimit = realFiles.Append(("fixtures/exactly-20.sh", ShellScript(20))).ToList();
        var atLimitViolations = ShellSizeGuard.FindViolations(atLimit, ShellSizeGuard.ResolveLimit());
        Assert.DoesNotContain(atLimitViolations, v => v.Path == "fixtures/exactly-20.sh");
    }

    /// <summary>
    /// Reads the git-tracked tree from <paramref name="repoRoot"/> through the same
    /// <c>git ls-files</c> + <see cref="File.ReadAllLines(string)"/> path the runner
    /// uses, returning (relativePath, lines) for every readable tracked file.
    /// </summary>
    private static async Task<List<(string Path, string[] Lines)>> ReadTrackedFilesAsync(string repoRoot)
    {
        var (exitCode, output, timedOut) = await Git.RunAsync(repoRoot, ["ls-files"], CancellationToken.None);
        Assert.False(timedOut, "git ls-files timed out");
        Assert.Equal(0, exitCode);

        var files = new List<(string Path, string[] Lines)>();
        foreach (var rel in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(f => f.TrimEnd('\r')))
        {
            var full = Path.Combine(repoRoot, rel);
            if (File.Exists(full))
            {
                files.Add((rel, File.ReadAllLines(full)));
            }
        }

        return files;
    }

    /// <summary>A hashbanged shell script with <paramref name="logicLines"/> echo lines.</summary>
    private static string[] ShellScript(int logicLines)
    {
        var lines = new string[1 + logicLines];
        lines[0] = "#!/usr/bin/env bash";
        for (var i = 0; i < logicLines; i++)
        {
            lines[i + 1] = $"echo line {i}";
        }

        return lines;
    }
}
