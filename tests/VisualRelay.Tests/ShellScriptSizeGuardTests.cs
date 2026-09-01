using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// The enforcing shell-script size guard-as-test (the house idiom mirrored from
/// <see cref="SplitGuardVerificationTests.AllTestCsFiles_AreAtMost300Lines"/>).
/// It walks the filesystem from <see cref="RepoSetup.Root"/>, classifies files with
/// the same <see cref="ShellScriptClassifier"/> heuristic the <c>shell-size</c>
/// runner uses, runs <see cref="ShellSizeGuard.FindViolations"/> at the shared
/// limit, and asserts no shell script exceeds 24 logic lines.
///
/// <para>The walk skips build output and anything under a NESTED repository: a
/// linked worktree or a second clone checked out below the repo root carries its
/// own copy of this project's scripts, and the bootstrap carve-out matches only the
/// exact path <c>visual-relay</c>, so a nested copy of the launcher would be judged
/// against the 24-line general ceiling and fail a gate it already passes at the
/// root. The CLI's own <c>shell-size</c> step never saw this because it enumerates
/// git-tracked paths.</para>
/// </summary>
public sealed class ShellScriptSizeGuardTests
{

    /// <summary>
    /// Every shell script belonging to this checkout is at most 24 logic lines
    /// (or 100 for the <c>visual-relay</c> bootstrap). This is the build-failing
    /// gate: it fails the moment any script of ours (by extension or hashbang) grows
    /// past its ceiling, committed or not.
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
        var at100 = new[] { ("visual-relay", ShellScript(100)) };
        var v100 = ShellSizeGuard.FindViolations(at100, ShellSizeGuard.DefaultLimit);
        Assert.Empty(v100);

        var at101 = new[] { ("visual-relay", ShellScript(101)) };
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
        var files = new[] { ("sub/visual-relay", ShellScript(25)) };
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
    /// <summary>
    /// A nested repository under the root is not this checkout's to judge.
    /// Regression: a linked worktree at <c>.claude/worktrees/&lt;name&gt;</c> carried its
    /// own copy of the launcher, and since the bootstrap carve-out matches only the
    /// exact path <c>visual-relay</c>, that copy was measured against the 24-line
    /// general ceiling and failed a gate the real launcher passes. Build output is
    /// pruned for the same reason: neither is a script anyone authored here.
    /// </summary>
    [Fact]
    public void EnumerateProjectScripts_SkipsNestedRepositoriesAndBuildOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-shell-walk-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fat = ShellScript(80);

            // Ours: found.
            Directory.CreateDirectory(root);
            File.WriteAllLines(Path.Combine(root, "ours.sh"), fat);

            // A linked worktree marks itself with a .git FILE, a clone with a dir.
            var worktree = Path.Combine(root, ".claude", "worktrees", "agent-1");
            Directory.CreateDirectory(worktree);
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: /elsewhere\n");
            File.WriteAllLines(Path.Combine(worktree, "visual-relay"), fat);

            var clone = Path.Combine(root, "vendor", "other-repo");
            Directory.CreateDirectory(Path.Combine(clone, ".git"));
            File.WriteAllLines(Path.Combine(clone, "theirs.sh"), fat);

            // Build output.
            Directory.CreateDirectory(Path.Combine(root, "obj"));
            File.WriteAllLines(Path.Combine(root, "obj", "staged.sh"), fat);

            var found = EnumerateProjectScripts(root).Select(f => f.Path).ToList();

            Assert.Equal(["ours.sh"], found);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception) { /* best-effort */ }
        }
    }

    private static List<(string Path, string[] Lines)> EnumerateProjectScripts(string repoRoot)
    {
        var results = new List<(string Path, string[] Lines)>();
        Walk(repoRoot, repoRoot, results);
        return results;
    }

    // Depth-first walk that can PRUNE a directory, which Directory.EnumerateFiles
    // with AllDirectories cannot. Pruning is the point: see the class summary.
    private static void Walk(string dir, string repoRoot, List<(string, string[])> results)
    {
        foreach (var full in SafeEnumerateFiles(dir))
        {
            var rel = Path.GetRelativePath(repoRoot, full);
            try
            {
                if (ShellScriptClassifier.IsShellScript(rel, ReadFirstLine(full)))
                    results.Add((rel, File.ReadAllLines(full)));
            }
            catch
            {
                // skip unreadable files
            }
        }

        foreach (var sub in SafeEnumerateDirectories(dir))
        {
            if (!IsOursToCheck(sub))
                continue;
            Walk(sub, repoRoot, results);
        }
    }

    /// <summary>
    /// Whether to descend into <paramref name="dir"/>. Excludes build output and any
    /// directory that is itself a repository: a <c>.git</c> entry means a nested clone
    /// (directory) or a linked worktree (file), whose scripts are not this checkout's
    /// to judge. The repo root is never passed here, so its own <c>.git</c> is safe.
    /// </summary>
    private static bool IsOursToCheck(string dir)
    {
        var name = Path.GetFileName(dir);
        if (name is ".git" or "bin" or "obj")
            return false;
        var git = Path.Combine(dir, ".git");
        return !Directory.Exists(git) && !File.Exists(git);
    }

    private static IEnumerable<string> SafeEnumerateFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir); }
        catch { return []; }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string dir)
    {
        try { return Directory.EnumerateDirectories(dir); }
        catch { return []; }
    }

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
