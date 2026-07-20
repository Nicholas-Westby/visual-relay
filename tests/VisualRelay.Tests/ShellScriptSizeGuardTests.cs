using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// The enforcing shell-script size guard-as-test (the house idiom mirrored from
/// <see cref="SplitGuardVerificationTests.AllTestCsFiles_AreAtMost300Lines"/>).
/// It walks the filesystem from <see cref="RepoSetup.Root"/> (skipping .git),
/// classifies files with the same <see cref="ShellScriptClassifier"/> heuristic the
/// <c>shell-size</c> runner uses, runs <see cref="ShellSizeGuard.FindViolations"/>
/// at the shared limit, and asserts no shell script exceeds 24 logic lines.
/// </summary>
public sealed class ShellScriptSizeGuardTests
{

    /// <summary>
    /// Every git-tracked shell script in the live tree is at most 24 logic lines
    /// (or 100 for the <c>visual-relay</c> bootstrap). This is the build-failing
    /// gate: it fails the moment any tracked shell script (by extension or hashbang)
    /// grows past its ceiling.
    /// </summary>
    [Fact]
    public void AllTrackedShellScripts_AreWithinTheLimit()
    {
        var files = EnumerateProjectScripts(RepoSetup.Root);
        var violations = ShellSizeGuard.FindViolations(files, ShellSizeGuard.ResolveLimit());

        Assert.True(violations.Count == 0,
            "shell-size guard found violations (convert the logic to C#, do not relax the limit):\n" +
            string.Join("\n", violations.Select(v => $"{v.Path}: {v.Count} logic lines (limit {v.Limit})")));
    }

    /// <summary>
    /// The general ceiling is 24. <see cref="ShellSizeGuard.ResolveLimit"/> falls
    /// back to <see cref="ShellSizeGuard.DefaultLimit"/>, so asserting the constant
    /// pins the gate and the report to the same value and they can never diverge.
    /// (Asserted as a pure constant, not via env mutation, to honour the
    /// no-direct-env-mutation test convention; the env-override path is covered by the
    /// FindViolations unit tests.)
    /// </summary>
    [Fact]
    public void DefaultLimit_IsThe24LineCeiling()
    {
        Assert.Equal(24, ShellSizeGuard.DefaultLimit);
    }

    /// <summary>
    /// The bootstrap carve-out is a fixed 100-line ceiling with no env-var knob.
    /// Pinned here so it can never silently drift.
    /// </summary>
    [Fact]
    public void BootstrapLimit_Is100()
    {
        Assert.Equal(100, ShellSizeGuard.BootstrapLimit);
    }

    /// <summary>
    /// The gate bites: a synthetic 25-logic-line script added to the tracked set is
    /// reported as a violation at the limit (permanently encoding the deliberate-
    /// fattening proof so the enforcement can never silently regress), while the same
    /// script at exactly 24 lines passes (24 is the inclusive ceiling).
    /// </summary>
    [Fact]
    public void OverLimitScript_IsAViolation_AtLimitScript_IsNot()
    {
        var realFiles = EnumerateProjectScripts(RepoSetup.Root);

        var over = realFiles.Append(("fixtures/too-fat.sh", ShellScript(25))).ToList();
        var overViolations = ShellSizeGuard.FindViolations(over, ShellSizeGuard.ResolveLimit());
        Assert.Contains(overViolations, v => v is { Path: "fixtures/too-fat.sh", Count: 25 });

        var atLimit = realFiles.Append(("fixtures/exactly-24.sh", ShellScript(24))).ToList();
        var atLimitViolations = ShellSizeGuard.FindViolations(atLimit, ShellSizeGuard.ResolveLimit());
        Assert.DoesNotContain(atLimitViolations, v => v.Path == "fixtures/exactly-24.sh");
    }

    /// <summary>
    /// The bootstrap path (<c>visual-relay</c>) is allowed 100 logic lines: at
    /// exactly 100 it passes, at 101 it violates with the bootstrap limit reported
    /// in <see cref="ShellSizeGuard.Violation.Limit"/>.
    /// </summary>
    [Fact]
    public void BootstrapPath_At100_Passes_At101_Violates()
    {
        var at100 = new (string, string[])[] { ("visual-relay", ShellScript(100)) };
        var v100 = ShellSizeGuard.FindViolations(at100, ShellSizeGuard.DefaultLimit);
        Assert.Empty(v100);

        var at101 = new (string, string[])[] { ("visual-relay", ShellScript(101)) };
        var v101 = ShellSizeGuard.FindViolations(at101, ShellSizeGuard.DefaultLimit);
        var violation = Assert.Single(v101);
        Assert.Equal("visual-relay", violation.Path);
        Assert.Equal(101, violation.Count);
        Assert.Equal(100, violation.Limit);
    }

    /// <summary>
    /// A nested path like <c>sub/visual-relay</c> does NOT match the bootstrap
    /// carve-out (ordinal comparison only) and gets the general 24-line ceiling.
    /// </summary>
    [Fact]
    public void NestedBootstrapPath_UsesGeneralLimit()
    {
        var files = new (string, string[])[] { ("sub/visual-relay", ShellScript(25)) };
        var violations = ShellSizeGuard.FindViolations(files, ShellSizeGuard.DefaultLimit);
        var violation = Assert.Single(violations);
        Assert.Equal("sub/visual-relay", violation.Path);
        Assert.Equal(25, violation.Count);
        Assert.Equal(24, violation.Limit);
    }

    /// <summary>
    /// Walks the filesystem at <paramref name="repoRoot"/>, skipping .git
    /// directories, and returns (relativePath, lines) for every file classified
    /// as a shell script by <see cref="ShellScriptClassifier"/>.
    /// </summary>
    private static List<(string Path, string[] Lines)> EnumerateProjectScripts(string repoRoot)
    {
        var results = new List<(string Path, string[] Lines)>();
        foreach (var full in Directory.EnumerateFiles(repoRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(repoRoot, full);
            if (IsInsideGitDir(rel))
                continue;
            try
            {
                var firstLine = ReadFirstLine(full);
                if (ShellScriptClassifier.IsShellScript(rel, firstLine))
                    results.Add((rel, File.ReadAllLines(full)));
            }
            catch
            {
                // skip unreadable files
            }
        }
        return results;
    }

    private static bool IsInsideGitDir(string rel) =>
        rel == ".git"
        || rel.StartsWith(".git/", StringComparison.Ordinal)
        || rel.StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static string? ReadFirstLine(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            return reader.ReadLine();
        }
        catch
        {
            return null;
        }
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
