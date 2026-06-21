using VisualRelay.Core.ObsidianBridge;

namespace VisualRelay.Tests;

public sealed class ObsidianTaskImporterTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vr-obsidian-import-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (string VaultRoot, string RepoRoot, ObsidianVaultLayout Layout) SetupLayout(
        string repoName = "test-repo")
    {
        var vaultRoot = TempDir();
        var repoRoot = TempDir(); // the actual Relay repo (contains llm-tasks/)
        var layout = new ObsidianVaultLayout(vaultRoot, repoName);
        layout.EnsureScaffold();
        return (vaultRoot, repoRoot, layout);
    }

    // ── Scan ──────────────────────────────────────────────────────

    [Fact]
    public void Scan_FindsFreshMdFileInNewTasks()
    {
        var (vaultRoot, repoRoot, layout) = SetupLayout();
        try
        {
            var taskPath = Path.Combine(layout.NewTasksDir, "hello.md");
            File.WriteAllText(taskPath, "# Hello\n\nWorld.");
            File.SetLastWriteTimeUtc(taskPath, DateTime.UtcNow.AddSeconds(-30));

            var importer = new ObsidianTaskImporter();
            var candidates = importer.Scan(layout, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));

            Assert.Single(candidates);
            Assert.EndsWith("hello.md", candidates[0].FilePath, StringComparison.Ordinal);
            Assert.Equal("Hello", candidates[0].Title);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(vaultRoot);
            TestFileSystem.DeleteDirectoryResilient(repoRoot);
        }
    }

    [Fact]
    public void Scan_ExcludesInfoMd()
    {
        var (vaultRoot, repoRoot, layout) = SetupLayout();
        try
        {
            // INFO.md is seeded by EnsureScaffold; it must not be imported.
            var importer = new ObsidianTaskImporter();
            var candidates = importer.Scan(layout, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));

            // INFO.md exists in New Tasks/ but must be excluded.
            Assert.DoesNotContain(candidates,
                c => c.FilePath.EndsWith("INFO.md", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(vaultRoot);
            TestFileSystem.DeleteDirectoryResilient(repoRoot);
        }
    }

    [Fact]
    public void Scan_ExcludesReadmeMd()
    {
        var (vaultRoot, repoRoot, layout) = SetupLayout();
        try
        {
            var readmePath = Path.Combine(layout.NewTasksDir, "README.md");
            File.WriteAllText(readmePath, "# README");
            File.SetLastWriteTimeUtc(readmePath, DateTime.UtcNow.AddSeconds(-30));

            var importer = new ObsidianTaskImporter();
            var candidates = importer.Scan(layout, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));

            Assert.DoesNotContain(candidates,
                c => c.FilePath.EndsWith("README.md", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(vaultRoot);
            TestFileSystem.DeleteDirectoryResilient(repoRoot);
        }
    }

    [Fact]
    public void Scan_ExcludesFilesWithVrRecognizedFrontmatter()
    {
        var (vaultRoot, repoRoot, layout) = SetupLayout();
        try
        {
            var stampedPath = Path.Combine(layout.NewTasksDir, "already-stamped.md");
            File.WriteAllText(stampedPath, """
                ---
                vr-recognized: abc-def-0123-4567
                ---
                # Already imported

                This should be skipped.
                """);
            File.SetLastWriteTimeUtc(stampedPath, DateTime.UtcNow.AddSeconds(-30));

            var importer = new ObsidianTaskImporter();
            var candidates = importer.Scan(layout, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));

            Assert.DoesNotContain(candidates,
                c => c.FilePath.Contains("already-stamped"));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(vaultRoot);
            TestFileSystem.DeleteDirectoryResilient(repoRoot);
        }
    }

    [Fact]
    public void Scan_SkipsFilesNewerThanMinStableAge()
    {
        var (vaultRoot, repoRoot, layout) = SetupLayout();
        try
        {
            var freshPath = Path.Combine(layout.NewTasksDir, "just-arrived.md");
            File.WriteAllText(freshPath, "# Fresh\n\nJust synced.");
            // File was written just now — last write is very recent.
            File.SetLastWriteTimeUtc(freshPath, DateTime.UtcNow.AddSeconds(-2));

            var importer = new ObsidianTaskImporter();
            var candidates = importer.Scan(
                layout, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));

            // The file is only 2 seconds old; minStableAge is 10 s → must be skipped.
            Assert.DoesNotContain(candidates,
                c => c.FilePath.Contains("just-arrived"));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(vaultRoot);
            TestFileSystem.DeleteDirectoryResilient(repoRoot);
        }
    }

    [Fact]
    public void Scan_SkipsICloudPlaceholderFiles()
    {
        var (vaultRoot, repoRoot, layout) = SetupLayout();
        try
        {
            // An .icloud file is a zero-byte placeholder from iCloud.
            var placeholderPath = Path.Combine(layout.NewTasksDir, ".sync.icloud");
            File.WriteAllText(placeholderPath, ""); // empty file
            File.SetLastWriteTimeUtc(placeholderPath, DateTime.UtcNow.AddSeconds(-30));

            var importer = new ObsidianTaskImporter();
            var candidates = importer.Scan(layout, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));

            Assert.DoesNotContain(candidates,
                c => c.FilePath.EndsWith(".icloud", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(vaultRoot);
            TestFileSystem.DeleteDirectoryResilient(repoRoot);
        }
    }

    [Fact]
    public void Scan_SkipsZeroLengthFiles()
    {
        var (vaultRoot, repoRoot, layout) = SetupLayout();
        try
        {
            var emptyPath = Path.Combine(layout.NewTasksDir, "empty.md");
            File.WriteAllText(emptyPath, "");
            File.SetLastWriteTimeUtc(emptyPath, DateTime.UtcNow.AddSeconds(-30));

            var importer = new ObsidianTaskImporter();
            var candidates = importer.Scan(layout, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));

            Assert.DoesNotContain(candidates,
                c => c.FilePath.Contains("empty"));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(vaultRoot);
            TestFileSystem.DeleteDirectoryResilient(repoRoot);
        }
    }

    [Fact]
    public void Scan_OnlyScansTopLevelOfNewTasks()
    {
        var (vaultRoot, repoRoot, layout) = SetupLayout();
        try
        {
            // File inside Recognized/ must not be found.
            var recognizedPath = Path.Combine(layout.RecognizedDir, "deep.md");
            File.WriteAllText(recognizedPath, "# Deep\n\nShould not scan.");
            File.SetLastWriteTimeUtc(recognizedPath, DateTime.UtcNow.AddSeconds(-30));

            var importer = new ObsidianTaskImporter();
            var candidates = importer.Scan(layout, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));

            Assert.DoesNotContain(candidates,
                c => c.FilePath.Contains("deep"));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(vaultRoot);
            TestFileSystem.DeleteDirectoryResilient(repoRoot);
        }
    }

    [Fact]
    public async Task Recognize_CreatesTaskFileAndReturnsPath()
    {
        var (vaultRoot, repoRoot, layout) = SetupLayout();
        try
        {
            var sourcePath = Path.Combine(layout.NewTasksDir, "my-feature.md");
            File.WriteAllText(sourcePath, "# My Feature\n\nBuild something cool.");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(-30));

            var importer = new ObsidianTaskImporter();
            var candidate = new ImportCandidate(
                sourcePath, "My Feature", DateTimeOffset.UtcNow.AddSeconds(-30));
            var guid = new Guid("11111111-1111-1111-1111-111111111111");
            var result = await importer.Recognize(candidate, repoRoot, DateTimeOffset.UtcNow, guid);

            Assert.NotNull(result.Slug);
            Assert.Equal(guid, result.SourceGuid);
            Assert.NotNull(result.RecognizedPath);
            Assert.Null(result.SkipReason);

            var expectedTaskPath = Path.Combine(repoRoot, "llm-tasks", result.Slug, $"{result.Slug}.md");
            Assert.True(File.Exists(expectedTaskPath));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(vaultRoot);
            TestFileSystem.DeleteDirectoryResilient(repoRoot);
        }
    }

    [Fact]
    public async Task Recognize_StripsFrontmatterFromBody()
    {
        var (vaultRoot, repoRoot, layout) = SetupLayout();
        try
        {
            var sourcePath = Path.Combine(layout.NewTasksDir, "with-frontmatter.md");
            File.WriteAllText(sourcePath, """
                ---
                title: My Task
                tags: [important]
                ---
                # My Task

                The actual body.
                """);
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(-30));

            var importer = new ObsidianTaskImporter();
            var candidate = new ImportCandidate(
                sourcePath, "My Task", DateTimeOffset.UtcNow.AddSeconds(-30));
            var guid = new Guid("22222222-2222-2222-2222-222222222222");
            var result = await importer.Recognize(candidate, repoRoot, DateTimeOffset.UtcNow, guid);

            Assert.NotNull(result.Slug);
            var taskPath = Path.Combine(repoRoot, "llm-tasks", result.Slug, $"{result.Slug}.md");
            var body = File.ReadAllText(taskPath);

            Assert.DoesNotContain("---", body);
            Assert.Contains("The actual body.", body, StringComparison.Ordinal);
            Assert.Contains("# My Task", body, StringComparison.Ordinal);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(vaultRoot);
            TestFileSystem.DeleteDirectoryResilient(repoRoot);
        }
    }
}
