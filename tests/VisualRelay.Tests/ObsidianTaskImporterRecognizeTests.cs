using VisualRelay.Core.ObsidianBridge;
using VisualRelay.Core.Tasks;

namespace VisualRelay.Tests;

/// <summary>
/// Recognize tests for <see cref="ObsidianTaskImporter"/>.
/// Split from <see cref="ObsidianTaskImporterTests"/> to stay under the 300-line guard.
/// </summary>
public sealed class ObsidianTaskImporterRecognizeTests : IDisposable
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "vr-obsidian-import-tests", Guid.NewGuid().ToString("N"));

    private static (string VaultRoot, string RepoRoot, ObsidianVaultLayout Layout) SetupLayout(
        string repoName = "test-repo")
    {
        var vaultRoot = TempDir();
        var repoRoot = TempDir();
        Directory.CreateDirectory(repoRoot);
        Directory.CreateDirectory(Path.Combine(repoRoot, "llm-tasks"));
        var layout = new ObsidianVaultLayout(vaultRoot, repoName);
        layout.EnsureScaffold();
        return (vaultRoot, repoRoot, layout);
    }

    public void Dispose() { /* individual cleanup per test */ }

    [Fact]
    public async Task Recognize_MovesSourceToRecognizedDirectory()
    {
        var (vaultRoot, repoRoot, layout) = SetupLayout();
        try
        {
            var sourcePath = Path.Combine(layout.NewTasksDir, "move-me.md");
            File.WriteAllText(sourcePath, "# Move Me\n\nContent.");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(-30));

            var importer = new ObsidianTaskImporter();
            var candidate = new ImportCandidate(
                sourcePath, "Move Me", DateTimeOffset.UtcNow.AddSeconds(-30));
            var guid = new Guid("33333333-3333-3333-3333-333333333333");
            var result = await importer.Recognize(candidate, repoRoot, DateTimeOffset.UtcNow, guid);

            Assert.False(File.Exists(sourcePath));
            Assert.NotNull(result.RecognizedPath);
            Assert.True(File.Exists(result.RecognizedPath));
            Assert.Contains(layout.RecognizedDir, result.RecognizedPath, StringComparison.Ordinal);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(vaultRoot);
            TestFileSystem.DeleteDirectoryResilient(repoRoot);
        }
    }

    [Fact]
    public async Task Recognize_StampsSourceWithVrFrontmatter()
    {
        var (vaultRoot, repoRoot, layout) = SetupLayout();
        try
        {
            var sourcePath = Path.Combine(layout.NewTasksDir, "stamp-me.md");
            var originalContent = "# Stamp Me\n\nContent to preserve.";
            File.WriteAllText(sourcePath, originalContent);
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(-30));

            var importer = new ObsidianTaskImporter();
            var candidate = new ImportCandidate(
                sourcePath, "Stamp Me", DateTimeOffset.UtcNow.AddSeconds(-30));
            var now = DateTimeOffset.UtcNow;
            var guid = new Guid("44444444-4444-4444-4444-444444444444");
            var result = await importer.Recognize(candidate, repoRoot, now, guid);

            Assert.NotNull(result.RecognizedPath);
            var stamped = File.ReadAllText(result.RecognizedPath);
            Assert.Contains($"vr-task-id: {result.Slug}", stamped, StringComparison.Ordinal);
            Assert.Contains($"vr-recognized: {guid}", stamped, StringComparison.Ordinal);
            Assert.Contains("vr-recognized-at:", stamped, StringComparison.Ordinal);
            Assert.Contains("vr-repo:", stamped, StringComparison.Ordinal);
            Assert.EndsWith(originalContent, stamped, StringComparison.Ordinal);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(vaultRoot);
            TestFileSystem.DeleteDirectoryResilient(repoRoot);
        }
    }

    [Fact]
    public async Task Recognize_DerivesTitleFromFrontmatterTitleField()
    {
        var (vaultRoot, repoRoot, layout) = SetupLayout();
        try
        {
            var sourcePath = Path.Combine(layout.NewTasksDir, "titled.md");
            File.WriteAllText(sourcePath, """
                ---
                title: Overridden Title
                ---
                # Not This One

                Body here.
                """);
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(-30));

            var importer = new ObsidianTaskImporter();
            var candidate = new ImportCandidate(
                sourcePath, "whatever", DateTimeOffset.UtcNow.AddSeconds(-30));
            var result = await importer.Recognize(
                candidate, repoRoot, DateTimeOffset.UtcNow, Guid.NewGuid());

            Assert.NotNull(result.Slug);
            Assert.Contains("overridden", result.Slug, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(vaultRoot);
            TestFileSystem.DeleteDirectoryResilient(repoRoot);
        }
    }

    [Fact]
    public async Task Recognize_DerivesTitleFromH1WhenNoFrontmatterTitle()
    {
        var (vaultRoot, repoRoot, layout) = SetupLayout();
        try
        {
            var sourcePath = Path.Combine(layout.NewTasksDir, "no-title-field.md");
            File.WriteAllText(sourcePath, "# First Heading\n\nBody.");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(-30));

            var importer = new ObsidianTaskImporter();
            var candidate = new ImportCandidate(
                sourcePath, "First Heading", DateTimeOffset.UtcNow.AddSeconds(-30));
            var result = await importer.Recognize(
                candidate, repoRoot, DateTimeOffset.UtcNow, Guid.NewGuid());

            Assert.NotNull(result.Slug);
            Assert.Contains("first-heading", result.Slug, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(vaultRoot);
            TestFileSystem.DeleteDirectoryResilient(repoRoot);
        }
    }

    [Fact]
    public async Task Recognize_DerivesTitleFromFilenameWhenNoH1OrFrontmatterTitle()
    {
        var (vaultRoot, repoRoot, layout) = SetupLayout();
        try
        {
            var sourcePath = Path.Combine(layout.NewTasksDir, "just-filename.md");
            File.WriteAllText(sourcePath, "Just a paragraph, no heading.");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(-30));

            var importer = new ObsidianTaskImporter();
            var candidate = new ImportCandidate(
                sourcePath, "just-filename", DateTimeOffset.UtcNow.AddSeconds(-30));
            var result = await importer.Recognize(
                candidate, repoRoot, DateTimeOffset.UtcNow, Guid.NewGuid());

            Assert.NotNull(result.Slug);
            Assert.Contains("just-filename", result.Slug, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(vaultRoot);
            TestFileSystem.DeleteDirectoryResilient(repoRoot);
        }
    }

    [Fact]
    public async Task Recognize_ResolvesSlugCollisionWithSuffix()
    {
        var (vaultRoot, repoRoot, layout) = SetupLayout();
        try
        {
            await RelayTaskWriter.CreateAsync(repoRoot, "my-task", "# Existing");

            var sourcePath = Path.Combine(layout.NewTasksDir, "my-task.md");
            File.WriteAllText(sourcePath, "# My Task\n\nSecond one.");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(-30));

            var importer = new ObsidianTaskImporter();
            var candidate = new ImportCandidate(
                sourcePath, "My Task", DateTimeOffset.UtcNow.AddSeconds(-30));
            var result = await importer.Recognize(
                candidate, repoRoot, DateTimeOffset.UtcNow, Guid.NewGuid());

            Assert.NotNull(result.Slug);
            Assert.NotEqual("my-task", result.Slug);
            Assert.StartsWith("my-task-", result.Slug, StringComparison.Ordinal);
            Assert.True(File.Exists(
                Path.Combine(repoRoot, "llm-tasks", result.Slug, $"{result.Slug}.md")));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(vaultRoot);
            TestFileSystem.DeleteDirectoryResilient(repoRoot);
        }
    }

    [Fact]
    public async Task Recognize_ReportsUnresolvableCollisionAndLeavesFileUntouched()
    {
        var (vaultRoot, repoRoot, layout) = SetupLayout();
        try
        {
            for (var i = 0; i <= 100; i++)
            {
                var slug = i == 0 ? "hot-topic" : $"hot-topic-{i}";
                var dir = Path.Combine(repoRoot, "llm-tasks", slug);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, $"{slug}.md"), $"# {slug}");
                }
            }

            var sourcePath = Path.Combine(layout.NewTasksDir, "hot-topic.md");
            File.WriteAllText(sourcePath, "# Hot Topic\n\nCollision city.");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(-30));

            var importer = new ObsidianTaskImporter();
            var candidate = new ImportCandidate(
                sourcePath, "Hot Topic", DateTimeOffset.UtcNow.AddSeconds(-30));
            var result = await importer.Recognize(
                candidate, repoRoot, DateTimeOffset.UtcNow, Guid.NewGuid());

            Assert.Null(result.Slug);
            Assert.NotNull(result.SkipReason);
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(vaultRoot);
            TestFileSystem.DeleteDirectoryResilient(repoRoot);
        }
    }

    [Fact]
    public async Task ReScanAfterRecognize_ReturnsNothing()
    {
        var (vaultRoot, repoRoot, layout) = SetupLayout();
        try
        {
            var sourcePath = Path.Combine(layout.NewTasksDir, "once-only.md");
            File.WriteAllText(sourcePath, "# Once Only\n\nImport me.");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(-30));

            var importer = new ObsidianTaskImporter();

            var firstScan = importer.Scan(layout, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
            Assert.Single(firstScan);

            await importer.Recognize(firstScan[0], repoRoot, DateTimeOffset.UtcNow, Guid.NewGuid());

            var secondScan = importer.Scan(layout, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
            Assert.Empty(secondScan);
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(vaultRoot);
            TestFileSystem.DeleteDirectoryResilient(repoRoot);
        }
    }
}
