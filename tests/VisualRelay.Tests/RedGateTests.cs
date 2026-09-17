using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class RedGateTests
{
    [Fact]
    public async Task StripToRedAsync_SkipsAbsentPathsAndRestoresTheStash()
    {
        using var repo = TestRepository.Create();
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src.txt", "old\n");
        sim.Commit(repo.Root, "chore: seed repo");
        File.WriteAllText(Path.Combine(repo.Root, "src.txt"), "new\n");

        var tag = RedGate.StashTag("task", "absent-path");
        var stashed = await RedGate.StripToRedAsync(repo.Root, ["src.txt", "ghost.txt"], tag, sim, CancellationToken.None);

        Assert.True(stashed);
        Assert.Equal("old\n", File.ReadAllText(Path.Combine(repo.Root, "src.txt")));
        Assert.NotNull(await RedGate.FindStashRefAsync(repo.Root, tag, sim, CancellationToken.None));
        Assert.Equal(RedGateRestoreResult.Restored, await RedGate.RestoreStashAsync(repo.Root, tag, sim, CancellationToken.None));
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(repo.Root, "src.txt")));
    }

    [Fact]
    public void ComputeStripSet_ExcludesAuthoredTestFiles()
    {
        var stripSet = RedGate.ComputeStripSet(
            "/repo",
            ["src/app.cs", "tests/app.tests.cs", "src/extra.cs"],
            ["tests/app.tests.cs"]);

        Assert.Equal(["src/app.cs", "src/extra.cs"], stripSet);
    }

    /// <summary>
    /// The manifest and the declared test files are written by two different stages,
    /// and only the test list was normalized. Compared Ordinal, a plan that said
    /// <c>./tests/app.tests.cs</c> beside a declaration of <c>tests/app.tests.cs</c>
    /// put the DECLARED TEST in the strip set — so the gate stashed the very test it
    /// was about to run and reported a pass earned with the implementation stripped.
    /// </summary>
    [Fact]
    public void ComputeStripSet_NormalizesBothSides_SoOneFileIsNeverTwoPaths()
    {
        var stripSet = RedGate.ComputeStripSet(
            "/repo",
            ["./src/app.cs", @"src\extra.cs", "./tests/app.tests.cs", "+src/new.cs", "src/dir/"],
            ["tests/app.tests.cs"]);

        Assert.Equal(["src/app.cs", "src/extra.cs", "src/new.cs", "src/dir"], stripSet);
    }

    /// <summary>
    /// A rooted path in the strip set is a file nothing can resolve: git would be
    /// handed an absolute pathspec for a file it is meant to find inside the
    /// workspace. Both lists reach here already resolved, so one that survives is a
    /// bug and is left out rather than stashed under a name that names nothing.
    /// </summary>
    [Fact]
    public void ComputeStripSet_NeverHoldsARootedPath()
    {
        var stripSet = RedGate.ComputeStripSet(
            "/repo",
            ["/repo/src/app.cs", "/elsewhere/src/stray.cs", @"C:\other\win.cs", "src/plain.cs"],
            []);

        Assert.Equal(["src/app.cs", "src/plain.cs"], stripSet);
        Assert.DoesNotContain(stripSet, path => path.StartsWith('/') || path.Contains(':', StringComparison.Ordinal));
    }
}
