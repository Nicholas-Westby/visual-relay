using VisualRelay.Core.ObsidianBridge;

namespace VisualRelay.Tests;

/// <summary>
/// FIX 4: the vr-recognized "already imported" check must be anchored to the
/// leading frontmatter block, not matched anywhere in the first 1KB. Split from
/// <see cref="ObsidianTaskImporterTests"/> to stay under the 300-line guard.
/// </summary>
public sealed class ObsidianTaskImporterScanFrontmatterTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vr-obsidian-import-fm-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (string VaultRoot, string RepoRoot, ObsidianVaultLayout Layout) SetupLayout()
    {
        var vaultRoot = TempDir();
        var repoRoot = TempDir();
        var layout = new ObsidianVaultLayout(vaultRoot, "test-repo");
        layout.EnsureScaffold();
        return (vaultRoot, repoRoot, layout);
    }

    [Fact]
    public void Scan_DoesNotSkipFileWhoseBodyMentionsVrRecognized()
    {
        // Bug: a Multiline regex matched "vr-recognized:" ANYWHERE in the first 1KB,
        // so a brand-new task whose BODY merely talks about the vr-recognized stamp
        // was wrongly treated as already-imported and silently dropped. The check
        // must be anchored to the leading frontmatter block only.
        var (vaultRoot, repoRoot, layout) = SetupLayout();
        try
        {
            var taskPath = Path.Combine(layout.NewTasksDir, "talks-about-stamp.md");
            File.WriteAllText(taskPath, """
                ---
                title: Document the bridge
                ---
                # Document the bridge

                Explain that each recognized file gets a `vr-recognized: <guid>`
                line in its frontmatter so it is only ever taken once.
                """);
            File.SetLastWriteTimeUtc(taskPath, DateTime.UtcNow.AddSeconds(-30));

            var importer = new ObsidianTaskImporter();
            var candidates = importer.Scan(layout, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));

            Assert.Contains(candidates, c => c.FilePath.Contains("talks-about-stamp"));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(vaultRoot);
            TestFileSystem.DeleteDirectoryResilient(repoRoot);
        }
    }

    [Fact]
    public void Scan_DoesNotSkipFileWithNoFrontmatterMentioningVrRecognized()
    {
        // No leading frontmatter block at all, but the body mentions the stamp:
        // must still be imported (the anchored match finds no frontmatter to scan).
        var (vaultRoot, repoRoot, layout) = SetupLayout();
        try
        {
            var taskPath = Path.Combine(layout.NewTasksDir, "no-fm.md");
            File.WriteAllText(taskPath,
                "# Plain task\n\nThis mentions vr-recognized: in prose but has no frontmatter.\n");
            File.SetLastWriteTimeUtc(taskPath, DateTime.UtcNow.AddSeconds(-30));

            var importer = new ObsidianTaskImporter();
            var candidates = importer.Scan(layout, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));

            Assert.Contains(candidates, c => c.FilePath.Contains("no-fm"));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(vaultRoot);
            TestFileSystem.DeleteDirectoryResilient(repoRoot);
        }
    }

    [Fact]
    public void Scan_StillSkipsFileWithVrRecognizedInFrontmatter()
    {
        // Anchoring must NOT regress the real case: a true vr-recognized line inside
        // the leading frontmatter block still excludes the file from import.
        var (vaultRoot, repoRoot, layout) = SetupLayout();
        try
        {
            var taskPath = Path.Combine(layout.NewTasksDir, "really-stamped.md");
            File.WriteAllText(taskPath, """
                ---
                vr-task-id: foo
                vr-recognized: 11111111-2222-3333-4444-555555555555
                ---
                # Already imported

                Body.
                """);
            File.SetLastWriteTimeUtc(taskPath, DateTime.UtcNow.AddSeconds(-30));

            var importer = new ObsidianTaskImporter();
            var candidates = importer.Scan(layout, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));

            Assert.DoesNotContain(candidates, c => c.FilePath.Contains("really-stamped"));
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(vaultRoot);
            TestFileSystem.DeleteDirectoryResilient(repoRoot);
        }
    }
}
