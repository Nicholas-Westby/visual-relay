using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class RedGateTests
{
    [Fact]
    public async Task StripToRedAsync_SkipsAbsentPathsAndRestoresTheStash()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "src.txt"), "old\n");
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "visual-relay@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Visual Relay Tests");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "chore: seed repo");
        File.WriteAllText(Path.Combine(repo.Root, "src.txt"), "new\n");

        var tag = RedGate.StashTag("task", "absent-path");
        var stashed = await RedGate.StripToRedAsync(repo.Root, ["src.txt", "ghost.txt"], tag, CancellationToken.None);

        Assert.True(stashed);
        Assert.Equal("old\n", File.ReadAllText(Path.Combine(repo.Root, "src.txt")));
        Assert.NotNull(await RedGate.FindStashRefAsync(repo.Root, tag, CancellationToken.None));
        Assert.Equal(RedGateRestoreResult.Restored, await RedGate.RestoreStashAsync(repo.Root, tag, CancellationToken.None));
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(repo.Root, "src.txt")));
    }

    [Fact]
    public void ComputeStripSet_ExcludesAuthoredTestFiles()
    {
        var stripSet = RedGate.ComputeStripSet(
            ["src/app.cs", "tests/app.tests.cs", "src/extra.cs"],
            ["tests/app.tests.cs"]);

        Assert.Equal(["src/app.cs", "src/extra.cs"], stripSet);
    }
}
