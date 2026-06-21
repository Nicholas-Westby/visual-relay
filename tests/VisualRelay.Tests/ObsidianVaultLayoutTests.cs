using VisualRelay.Core.ObsidianBridge;

namespace VisualRelay.Tests;

public sealed class ObsidianVaultLayoutTests
{
    private static string TempVaultDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vr-obsidian-vault-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── Path composition ──────────────────────────────────────────────

    [Fact]
    public void RepoDir_CombinesVaultRootAndRepoName()
    {
        var vault = "/vault";
        var layout = new ObsidianVaultLayout(vault, "my-repo");

        Assert.Equal("/vault/my-repo", layout.RepoDir);
    }

    [Fact]
    public void NewTasksDir_IsUnderRepoDir()
    {
        var vault = "/vault";
        var layout = new ObsidianVaultLayout(vault, "repo");

        Assert.Equal(
            Path.Combine("/vault", "repo", "New Tasks"),
            layout.NewTasksDir);
    }

    [Fact]
    public void RecognizedDir_IsUnderNewTasksDir()
    {
        var vault = "/vault";
        var layout = new ObsidianVaultLayout(vault, "repo");

        Assert.Equal(
            Path.Combine("/vault", "repo", "New Tasks", "Recognized"),
            layout.RecognizedDir);
    }

    [Fact]
    public void CompletedDir_YieldsYyyyMmDd()
    {
        var vault = "/vault";
        var layout = new ObsidianVaultLayout(vault, "repo");
        var date = new DateOnly(2026, 6, 21);

        var completedDir = layout.CompletedDir(date);

        Assert.Equal(
            Path.Combine("/vault", "repo", "Completed", "2026-06-21"),
            completedDir);
    }

    [Fact]
    public void CompletedDir_PadsMonthAndDayToTwoDigits()
    {
        var vault = "/vault";
        var layout = new ObsidianVaultLayout(vault, "repo");
        var date = new DateOnly(2026, 1, 5);

        var completedDir = layout.CompletedDir(date);

        Assert.EndsWith("2026-01-05", completedDir, StringComparison.Ordinal);
    }

    [Fact]
    public void SummaryPath_ReturnsMarkdownFileInDateFolder()
    {
        var vault = "/vault";
        var layout = new ObsidianVaultLayout(vault, "repo");
        var date = new DateOnly(2026, 12, 31);

        var path = layout.SummaryPath("my-task", date);

        Assert.Equal(
            Path.Combine("/vault", "repo", "Completed", "2026-12-31", "my-task.md"),
            path);
    }

    // ── Repo name sanitization ────────────────────────────────────────

    [Fact]
    public void RepoName_StripsDirectorySeparators()
    {
        var vault = "/vault";
        var layout = new ObsidianVaultLayout(vault, "path/to/repo");

        Assert.Equal("/vault/path-to-repo", layout.RepoDir);
    }

    [Fact]
    public void RepoName_StripsAltDirectorySeparators()
    {
        var vault = "/vault";
        var layout = new ObsidianVaultLayout(vault, "repo\\sub");

        // Should not contain backslash in any path.
        Assert.DoesNotContain("\\", layout.RepoDir);
    }

    [Fact]
    public void RepoName_Empty_FallsBackToProject()
    {
        var vault = "/vault";
        var layout = new ObsidianVaultLayout(vault, "");

        Assert.Equal("/vault/project", layout.RepoDir);
    }

    [Fact]
    public void RepoName_WhitespaceOnly_FallsBackToProject()
    {
        var vault = "/vault";
        var layout = new ObsidianVaultLayout(vault, "   ");

        Assert.Equal("/vault/project", layout.RepoDir);
    }

    // ── Reserved file names ───────────────────────────────────────────

    [Fact]
    public void ReservedFileNames_IncludesInfoMd()
    {
        var names = ObsidianVaultLayout.ReservedFileNames;

        Assert.Contains("info.md", names);
    }

    [Fact]
    public void ReservedFileNames_IncludesReadmeMd()
    {
        var names = ObsidianVaultLayout.ReservedFileNames;

        Assert.Contains("readme.md", names);
    }

    [Fact]
    public void ReservedFileNames_IsCaseInsensitive()
    {
        var names = ObsidianVaultLayout.ReservedFileNames;

        Assert.Contains("INFO.MD", names);
        Assert.Contains("Info.md", names);
    }

    // ── EnsureScaffold ────────────────────────────────────────────────

    [Fact]
    public void EnsureScaffold_CreatesAllDirectories()
    {
        var vault = TempVaultDir();
        try
        {
            var layout = new ObsidianVaultLayout(vault, "test-repo");

            layout.EnsureScaffold();

            Assert.True(Directory.Exists(layout.RepoDir));
            Assert.True(Directory.Exists(layout.NewTasksDir));
            Assert.True(Directory.Exists(layout.RecognizedDir));
            Assert.True(Directory.Exists(Path.Combine(layout.RepoDir, "Completed")));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(vault);
        }
    }

    [Fact]
    public void EnsureScaffold_SeedsFourInfoMds()
    {
        var vault = TempVaultDir();
        try
        {
            var layout = new ObsidianVaultLayout(vault, "test-repo");

            layout.EnsureScaffold();

            // Root INFO.md
            Assert.True(File.Exists(Path.Combine(layout.RepoDir, "INFO.md")));

            // New Tasks INFO.md
            Assert.True(File.Exists(Path.Combine(layout.NewTasksDir, "INFO.md")));

            // Recognized INFO.md
            Assert.True(File.Exists(Path.Combine(layout.RecognizedDir, "INFO.md")));

            // Completed INFO.md
            Assert.True(File.Exists(Path.Combine(layout.RepoDir, "Completed", "INFO.md")));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(vault);
        }
    }

    [Fact]
    public void EnsureScaffold_InfoMdContainsRepoName()
    {
        var vault = TempVaultDir();
        try
        {
            var layout = new ObsidianVaultLayout(vault, "my-cool-project");

            layout.EnsureScaffold();

            var rootInfo = File.ReadAllText(Path.Combine(layout.RepoDir, "INFO.md"));
            Assert.Contains("my-cool-project", rootInfo, StringComparison.Ordinal);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(vault);
        }
    }

    [Fact]
    public void EnsureScaffold_Idempotent_SecondCallIsNoOp()
    {
        var vault = TempVaultDir();
        try
        {
            var layout = new ObsidianVaultLayout(vault, "test-repo");

            layout.EnsureScaffold();
            var dirsBefore = Directory.GetDirectories(vault, "*", SearchOption.AllDirectories).Length;
            var filesBefore = Directory.GetFiles(vault, "*", SearchOption.AllDirectories).Length;

            // Second call should not change anything.
            layout.EnsureScaffold();
            var dirsAfter = Directory.GetDirectories(vault, "*", SearchOption.AllDirectories).Length;
            var filesAfter = Directory.GetFiles(vault, "*", SearchOption.AllDirectories).Length;

            Assert.Equal(dirsBefore, dirsAfter);
            Assert.Equal(filesBefore, filesAfter);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(vault);
        }
    }

    [Fact]
    public void EnsureScaffold_DoesNotOverwriteUserEditedInfoMd()
    {
        var vault = TempVaultDir();
        try
        {
            var layout = new ObsidianVaultLayout(vault, "test-repo");

            layout.EnsureScaffold();
            var rootInfoPath = Path.Combine(layout.RepoDir, "INFO.md");

            // Simulate user editing the file.
            var userContent = "# My custom guide\n\nI edited this.\n";
            File.WriteAllText(rootInfoPath, userContent);

            // Re-scaffold must not clobber.
            layout.EnsureScaffold();
            var content = File.ReadAllText(rootInfoPath);
            Assert.Equal(userContent, content);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(vault);
        }
    }
}
