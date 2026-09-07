using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

public sealed class RelayGitignoreWriterTests
{
    [Fact]
    public void EnsureWritten_NoRelayDir_NoOp()
    {
        using var repo = TestRepository.Create();

        var written = RelayGitignoreWriter.EnsureWritten(repo.Root);

        Assert.False(written);
        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".relay")));
    }

    [Fact]
    public void EnsureWritten_RelayDirWithoutGitignore_WritesPolicy()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));

        var written = RelayGitignoreWriter.EnsureWritten(repo.Root);

        Assert.True(written);
        var content = File.ReadAllText(Path.Combine(repo.Root, ".relay", ".gitignore"));
        // Blanket-ignore everything Visual Relay writes while leaving the file
        // itself and the repo config trackable, so an author who wants the
        // config in git can add it by hand.
        Assert.Contains("\n*\n", content);
        Assert.Contains("!.gitignore", content);
        Assert.Contains("!config.json", content);
    }

    [Fact]
    public void EnsureWritten_ExistingFile_IsNeverModified()
    {
        using var repo = TestRepository.Create();
        var relayDir = Path.Combine(repo.Root, ".relay");
        Directory.CreateDirectory(relayDir);
        var path = Path.Combine(relayDir, ".gitignore");
        File.WriteAllText(path, "# hand-tuned by repo owner\nrun.log\n");

        var written = RelayGitignoreWriter.EnsureWritten(repo.Root);

        Assert.False(written);
        Assert.Equal("# hand-tuned by repo owner\nrun.log\n", File.ReadAllText(path));
    }

    [Fact]
    public void EnsureWritten_IsIdempotent()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));

        Assert.True(RelayGitignoreWriter.EnsureWritten(repo.Root));
        Assert.False(RelayGitignoreWriter.EnsureWritten(repo.Root));
    }

    [Fact]
    public void ConfigWriter_Write_AlsoEstablishesGitignore()
    {
        using var repo = TestRepository.Create();

        RelayConfigWriter.Write(repo.Root, "dotnet test");

        var path = Path.Combine(repo.Root, ".relay", ".gitignore");
        Assert.True(File.Exists(path));
        Assert.Equal(RelayGitignoreWriter.Content, File.ReadAllText(path));
    }

    [Fact]
    public void Content_Comment_SaysTheCommitStageNeverStagesRelay()
    {
        // The comment embedded in the generated .gitignore must not promise a
        // force-add: nothing under .relay/ reaches a commit any more, and a
        // reader who tracks config.json by hand should know why they can.
        Assert.Contains("never stages it", RelayGitignoreWriter.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("force-added", RelayGitignoreWriter.Content, StringComparison.Ordinal);
    }
}
