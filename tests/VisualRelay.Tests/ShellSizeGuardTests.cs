using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for <see cref="ShellSizeGuard.FindViolations"/> —
/// integrates classifier + counter on synthetic file sets,
/// returns ordered (path, count, limit) for shell scripts over limit.
/// Written TDD: these must fail against absent code.
/// </summary>
public sealed class ShellSizeGuardTests
{
    // ── Basic integration ───────────────────────────────────────────

    [Fact]
    public void ThinWrapper_PassesLimit()
    {
        var files = new (string Path, string[] Lines)[]
        {
            ("tools/thin.sh", [
                "#!/usr/bin/env bash",
                "set -euo pipefail",
                "dotnet run --project \"$SCRIPT_DIR/tools/Foo/Foo.csproj\" -- \"$@\""
            ]),
        };

        const int limit = 20;
        var violations = ShellSizeGuard.FindViolations(files, limit);

        // Hashbang excluded, 2 logic lines → well under 20 → no violation.
        Assert.Empty(violations);
    }

    [Fact]
    public void BranchingScript_ExceedsLimit()
    {
        // 25 logic lines (hashbang + 26 lines total, 1 hashbang excluded = 25 logic).
        var scriptLines = new List<string> { "#!/bin/bash" };
        for (int i = 1; i <= 25; i++)
        {
            scriptLines.Add($"echo line {i}");
        }

        var files = new (string Path, string[] Lines)[]
        {
            ("tools/big.sh", scriptLines.ToArray()),
        };

        const int limit = 20;
        var violations = ShellSizeGuard.FindViolations(files, limit);

        Assert.Single(violations);
        Assert.Equal("tools/big.sh", violations[0].Path);
        Assert.Equal(25, violations[0].Count);
        Assert.Equal(limit, violations[0].Limit);
    }

    [Fact]
    public void NonShellFile_IsIgnored()
    {
        var files = new (string Path, string[] Lines)[]
        {
            ("src/Program.cs", [
                "using System;",
                "class Program { static void Main() => Console.WriteLine(\"hi\"); }"
            ]),
            ("tools/big.sh", [
                "#!/bin/bash",
                "echo 1", "echo 2", "echo 3", "echo 4", "echo 5",
                "echo 6", "echo 7", "echo 8", "echo 9", "echo 10",
                "echo 11", "echo 12", "echo 13", "echo 14", "echo 15",
                "echo 16", "echo 17", "echo 18", "echo 19", "echo 20",
                "echo 21"
            ]),
        };

        const int limit = 20;
        var violations = ShellSizeGuard.FindViolations(files, limit);

        // Only the .sh file (21 logic lines) violates; .cs is ignored.
        Assert.Single(violations);
        Assert.Equal("tools/big.sh", violations[0].Path);
    }

    // ── Ordering ────────────────────────────────────────────────────

    [Fact]
    public void MultipleViolations_OrderedByPath()
    {
        var files = new (string Path, string[] Lines)[]
        {
            ("zzz/last.sh", CreateScriptLines(22)),
            ("aaa/first.sh", CreateScriptLines(25)),
            ("mmm/middle.sh", CreateScriptLines(21)),
        };

        const int limit = 20;
        var violations = ShellSizeGuard.FindViolations(files, limit);

        Assert.Equal(3, violations.Count);
        Assert.Equal("aaa/first.sh", violations[0].Path);
        Assert.Equal("mmm/middle.sh", violations[1].Path);
        Assert.Equal("zzz/last.sh", violations[2].Path);
    }

    [Fact]
    public void NoViolations_ReturnsEmpty()
    {
        var files = new (string Path, string[] Lines)[]
        {
            ("a.sh", CreateScriptLines(10)),
            ("b.sh", CreateScriptLines(15)),
            ("c.sh", CreateScriptLines(20)), // exactly at limit, not over
        };

        const int limit = 20;
        var violations = ShellSizeGuard.FindViolations(files, limit);

        Assert.Empty(violations);
    }

    // ── Limit edge cases ────────────────────────────────────────────

    [Fact]
    public void FileAtLimit_DoesNotViolate()
    {
        var files = new (string Path, string[] Lines)[]
        {
            ("exact.sh", CreateScriptLines(20)),
        };

        const int limit = 20;
        var violations = ShellSizeGuard.FindViolations(files, limit);

        Assert.Empty(violations);
    }

    [Fact]
    public void FileOneOverLimit_Violates()
    {
        var files = new (string Path, string[] Lines)[]
        {
            ("oneover.sh", CreateScriptLines(21)),
        };

        const int limit = 20;
        var violations = ShellSizeGuard.FindViolations(files, limit);

        Assert.Single(violations);
        Assert.Equal(21, violations[0].Count);
    }

    // ── Empty input ─────────────────────────────────────────────────

    [Fact]
    public void EmptyFileList_ReturnsEmpty()
    {
        var files = Array.Empty<(string Path, string[] Lines)>();
        var violations = ShellSizeGuard.FindViolations(files, 20);
        Assert.Empty(violations);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Creates a synthetic shell script with a hashbang followed by
    /// <paramref name="logicLineCount"/> echo lines.
    /// </summary>
    private static string[] CreateScriptLines(int logicLineCount)
    {
        var lines = new string[1 + logicLineCount];
        lines[0] = "#!/bin/bash";
        for (int i = 0; i < logicLineCount; i++)
        {
            lines[1 + i] = $"echo line {i + 1}";
        }

        return lines;
    }
}
