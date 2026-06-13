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
        // Blanket-ignore diagnostics while keeping the file itself and the
        // repo config trackable; the canonical record relies on the commit
        // stage's force-add, not on negations here.
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
}
