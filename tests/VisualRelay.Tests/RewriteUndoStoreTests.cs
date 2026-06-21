using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// "Revert" after a "Rewrite with AI" must restore the WHOLE task folder, not
/// just the spec .md — the rewrite copy-back recreates the entire folder, so the
/// model can add, modify, or delete attachment files. Restoring only the spec
/// string is lossy for non-spec folder contents.
///
/// <see cref="RewriteUndoStore"/> snapshots the whole folder before the
/// copy-back and restores the whole folder on revert. These unit tests pin that
/// behavior directly.
/// </summary>
public sealed class RewriteUndoStoreTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vr-undo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Restore_RestoresWholeFolder_AddedModifiedAndDeletedFiles()
    {
        var workspace = NewTempDir();
        var store = new RewriteUndoStore();
        try
        {
            var taskDir = Path.Combine(workspace, "llm-tasks", "my-task");
            Directory.CreateDirectory(taskDir);
            await File.WriteAllTextAsync(Path.Combine(taskDir, "my-task.md"), "# Original\n");
            await File.WriteAllTextAsync(Path.Combine(taskDir, "keep.txt"), "keep me\n");
            await File.WriteAllTextAsync(Path.Combine(taskDir, "will-change.txt"), "before\n");

            // Snapshot the pre-rewrite folder.
            store.Capture("my-task", taskDir);
            Assert.True(store.Has("my-task"));

            // Simulate the rewrite copy-back mutating the folder:
            //  - spec changed, a file added, a file modified, a file deleted.
            await File.WriteAllTextAsync(Path.Combine(taskDir, "my-task.md"), "# Rewritten\n");
            await File.WriteAllTextAsync(Path.Combine(taskDir, "added.txt"), "new attachment\n");
            await File.WriteAllTextAsync(Path.Combine(taskDir, "will-change.txt"), "after\n");
            File.Delete(Path.Combine(taskDir, "keep.txt"));

            // Revert restores the WHOLE folder to its pre-rewrite state.
            await store.RestoreAsync("my-task", taskDir);

            Assert.Equal("# Original\n", await File.ReadAllTextAsync(Path.Combine(taskDir, "my-task.md")));
            Assert.True(File.Exists(Path.Combine(taskDir, "keep.txt")),
                "a file the rewrite DELETED must be restored");
            Assert.Equal("keep me\n", await File.ReadAllTextAsync(Path.Combine(taskDir, "keep.txt")));
            Assert.Equal("before\n", await File.ReadAllTextAsync(Path.Combine(taskDir, "will-change.txt")));
            Assert.False(File.Exists(Path.Combine(taskDir, "added.txt")),
                "a file the rewrite ADDED must be removed on revert");
        }
        finally
        {
            store.DiscardAll();
            TestFileSystem.DeleteDirectoryResilient(workspace);
        }
    }

    [Fact]
    public void Discard_RemovesKey_AndDeletesSnapshotTempDir()
    {
        var workspace = NewTempDir();
        var store = new RewriteUndoStore();
        try
        {
            var taskDir = Path.Combine(workspace, "t");
            Directory.CreateDirectory(taskDir);
            File.WriteAllText(Path.Combine(taskDir, "t.md"), "x");

            store.Capture("t", taskDir);
            var snapshot = store.SnapshotPathForTests("t");
            Assert.NotNull(snapshot);
            Assert.True(Directory.Exists(snapshot));

            store.Discard("t");

            Assert.False(store.Has("t"), "discard must drop the key");
            Assert.False(Directory.Exists(snapshot),
                "discard must delete the snapshot temp dir so snapshots never leak");
        }
        finally
        {
            store.DiscardAll();
            TestFileSystem.DeleteDirectoryResilient(workspace);
        }
    }

    [Fact]
    public void Capture_Twice_ReplacesSnapshot_AndDeletesTheOldOne()
    {
        var workspace = NewTempDir();
        var store = new RewriteUndoStore();
        try
        {
            var taskDir = Path.Combine(workspace, "t");
            Directory.CreateDirectory(taskDir);
            File.WriteAllText(Path.Combine(taskDir, "t.md"), "x");

            store.Capture("t", taskDir);
            var first = store.SnapshotPathForTests("t");
            store.Capture("t", taskDir);
            var second = store.SnapshotPathForTests("t");

            Assert.NotEqual(first, second);
            Assert.False(Directory.Exists(first), "re-capturing must delete the prior snapshot");
            Assert.True(Directory.Exists(second));
        }
        finally
        {
            store.DiscardAll();
            TestFileSystem.DeleteDirectoryResilient(workspace);
        }
    }
}
