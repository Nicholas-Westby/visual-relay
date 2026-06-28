using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// The enforcing sync-over-async guard-as-test (the house idiom mirrored from
/// <see cref="RealSleepGuardTests"/>). The matcher
/// (<see cref="SyncOverAsyncGuard.FindViolations"/>) is pure: it Roslyn-parses each
/// source and flags blocking <c>.Result</c>, <c>.GetAwaiter().GetResult()</c>, or
/// <c>.Wait()</c> calls inside <c>[Fact]</c>/<c>[AvaloniaFact]</c>/<c>[Theory]</c>/
/// <c>[AvaloniaTheory]</c> test method bodies — the classic sync-over-async deadlock
/// on the single-threaded Avalonia headless dispatcher.
///
/// This file carries the matcher's behavioural facts plus the live enumeration gate.
/// It is self-exempt by filename in <see cref="SyncOverAsyncGuard"/> because it
/// contains sync-over-async fixtures; that exemption is exactly why the live gate can
/// scan the test project without tripping on these strings.
/// </summary>
public sealed class SyncOverAsyncGuardTests
{
    /// <summary>
    /// A <c>.Result</c> member access on a Task-shaped receiver (an <c>…Async()</c>
    /// invocation) inside an <c>[AvaloniaFact]</c> test method body is reported as a
    /// sync-over-async deadlock.
    /// </summary>
    [Fact]
    public void DotResult_InAvaloniaFact_IsReported()
    {
        const string source = """
            using Xunit;
            using Avalonia.Headless.XUnit;
            class C { [AvaloniaFact] void M() { _ = GetTaskAsync().Result; } static Task GetTaskAsync() => Task.CompletedTask; }
            """;

        var violations = SyncOverAsyncGuard.FindViolations([("Fixtures/Deadlock.cs", source)]);

        var v = Assert.Single(violations);
        Assert.Equal("Fixtures/Deadlock.cs", v.Path);
        Assert.Contains(".Result", v.Reason);
    }

    /// <summary>
    /// A <c>.GetAwaiter().GetResult()</c> chain inside a <c>[Fact]</c> test method body
    /// is reported.
    /// </summary>
    [Fact]
    public void GetAwaiterGetResult_InFact_IsReported()
    {
        const string source = """
            using Xunit;
            class C { [Fact] void M() { GetTask().GetAwaiter().GetResult(); } static Task GetTask() => Task.CompletedTask; }
            """;

        var violations = SyncOverAsyncGuard.FindViolations([("Fixtures/GetResult.cs", source)]);

        var v = Assert.Single(violations);
        Assert.Equal("Fixtures/GetResult.cs", v.Path);
        Assert.Contains(".GetAwaiter().GetResult()", v.Reason);
    }

    /// <summary>
    /// A <c>.Wait()</c> call inside an <c>[AvaloniaFact]</c> test method body is reported.
    /// </summary>
    [Fact]
    public void Wait_InAvaloniaFact_IsReported()
    {
        const string source = """
            using Xunit;
            using Avalonia.Headless.XUnit;
            class C { [AvaloniaFact] void M() { GetTaskAsync().Wait(); } static Task GetTaskAsync() => Task.CompletedTask; }
            """;

        var violations = SyncOverAsyncGuard.FindViolations([("Fixtures/Wait.cs", source)]);

        var v = Assert.Single(violations);
        Assert.Equal("Fixtures/Wait.cs", v.Path);
        Assert.Contains(".Wait()", v.Reason);
    }

    /// <summary>
    /// The correct <c>async Task</c> with <c>await</c> pattern inside an
    /// <c>[AvaloniaFact]</c> yields zero violations — no sync-over-async, no deadlock.
    /// </summary>
    [Fact]
    public void AwaitPattern_IsClean()
    {
        const string source = """
            using Xunit;
            using Avalonia.Headless.XUnit;
            class C { [AvaloniaFact] async Task M() { await GetTask(); } static Task GetTask() => Task.CompletedTask; }
            """;

        var violations = SyncOverAsyncGuard.FindViolations([("Fixtures/Clean.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// <c>new BackendVenv.Result(null)</c> inside a <c>[Fact]</c> is an object creation
    /// where <c>Result</c> is part of a qualified type name — not a member-access
    /// <c>.Result</c> on a Task. Not reported.
    /// </summary>
    [Fact]
    public void BackendVenvDotResult_IsNotReported()
    {
        const string source = """
            using Xunit;
            namespace VR { class BackendVenv { public record Result(string? Path); } }
            class C { [Fact] void M() { var r = new VR.BackendVenv.Result(null); } }
            """;

        var violations = SyncOverAsyncGuard.FindViolations([("Fixtures/BackendVenv.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// A <c>.GetAwaiter().GetResult()</c> in a static helper method that is not a
    /// <c>[Fact]</c>/<c>[AvaloniaFact]</c> is not reported — the guard only scopes
    /// inside test-method bodies.
    /// </summary>
    [Fact]
    public void GetAwaiterGetResult_InStaticHelper_NotInTestMethod_IsNotReported()
    {
        const string source = """
            using Xunit;
            class C {
                static void Helper() { GetTask().GetAwaiter().GetResult(); }
                [Fact] void M() { Helper(); }
                static Task GetTask() => Task.CompletedTask;
            }
            """;

        var violations = SyncOverAsyncGuard.FindViolations([("Fixtures/Helper.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// A <c>.Result</c> line carrying <c>// vr-allow-sync-over-async: justified</c>
    /// is suppressed — the allow-marker with a reason excuses the violation.
    /// </summary>
    [Fact]
    public void AllowMarker_WithReason_Suppresses()
    {
        const string source = """
            using Xunit;
            using Avalonia.Headless.XUnit;
            class C { [AvaloniaFact] void M() { _ = GetTaskAsync().Result; } // vr-allow-sync-over-async: justified
            static Task GetTaskAsync() => Task.CompletedTask; }
            """;

        var violations = SyncOverAsyncGuard.FindViolations([("Fixtures/Allowed.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// A bare marker is not a valid suppression: <c>// vr-allow-sync-over-async:</c>
    /// with no reason after the colon does not excuse the violation — it is still reported.
    /// </summary>
    [Fact]
    public void BareAllowMarker_StillReported()
    {
        const string source = """
            using Xunit;
            using Avalonia.Headless.XUnit;
            class C { [AvaloniaFact] void M() { _ = GetTaskAsync().Result; } // vr-allow-sync-over-async:
            static Task GetTaskAsync() => Task.CompletedTask; }
            """;

        var violations = SyncOverAsyncGuard.FindViolations([("Fixtures/BareMarker.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// The live enforcing gate: every <c>*.cs</c> in the test project (excluding bin/obj)
    /// is free of sync-over-async deadlocks. The existing suite uses the correct async
    /// pattern, so this gate starts green and stays that way — any reintroduced
    /// sync-over-async flip the guard to a build failure.
    /// </summary>
    [Fact]
    public void AllTestProjectCsFiles_HaveNoSyncOverAsync()
    {
        var testsDir = Path.Combine(RepoSetup.Root, "tests", "VisualRelay.Tests");

        var files = Directory.EnumerateFiles(testsDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildArtifact(f))
            .Select(f => (Path.GetRelativePath(RepoSetup.Root, f), File.ReadAllText(f)))
            .ToList();

        var violations = SyncOverAsyncGuard.FindViolations(files);

        Assert.True(violations.Count == 0,
            "sync-over-async guard found deadlock patterns in the test suite (use async/await " +
            "instead of .Result/.GetAwaiter().GetResult()/.Wait(); do not suppress):\n" +
            string.Join("\n", violations.Select(v => $"{v.Path}:{v.Line}: {v.Snippet} — {v.Reason}")));
    }

    /// <summary>True when the path lives under a <c>bin</c> or <c>obj</c> build-output segment.</summary>
    private static bool IsBuildArtifact(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(s => s is "bin" or "obj");
    }
}
