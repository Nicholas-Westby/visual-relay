using VisualRelay.Core.Tasks;

namespace VisualRelay.Tests;

/// <summary>
/// Tests verifying that <see cref="RelayTaskRepository.ListAsync"/>
/// best-effort cleans a stale <c>.relay-scratch/</c> directory from the
/// workspace root.
/// </summary>
public sealed class LegacyScratchCleanupTests : IDisposable
{
    private readonly string _workspace;

    public LegacyScratchCleanupTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "vr-legacy-scratch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        // Minimal valid config so ListAsync doesn't bail early.
        var relayDir = Path.Combine(_workspace, ".relay");
        Directory.CreateDirectory(relayDir);
        File.WriteAllText(Path.Combine(relayDir, "config.json"),
            """{"testCmd":"true","tasksDir":"llm-tasks"}""");
        var tasksDir = Path.Combine(_workspace, "llm-tasks");
        Directory.CreateDirectory(tasksDir);
    }

    [Fact]
    public async Task PopulatedLegacyScratchDir_IsRemovedByListAsync()
    {
        var scratch = Path.Combine(_workspace, ".relay-scratch");
        Directory.CreateDirectory(scratch);
        File.WriteAllText(Path.Combine(scratch, "match_screenshot.png"), "fake png content");
        var subDir = Path.Combine(scratch, "screenshot-root");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "frame.png"), "another fake");

        Assert.True(Directory.Exists(scratch), "scratch dir must exist before ListAsync");

        var repo = new RelayTaskRepository(_workspace);
        await repo.ListAsync();

        Assert.False(Directory.Exists(scratch), "scratch dir should be removed by ListAsync");
    }

    [Fact]
    public async Task AbsentLegacyScratchDir_ListAsyncIsNoOp()
    {
        var scratch = Path.Combine(_workspace, ".relay-scratch");
        Assert.False(Directory.Exists(scratch), "scratch dir must not exist before test");

        var repo = new RelayTaskRepository(_workspace);
        await repo.ListAsync();

        // Should not throw and should not create the dir.
        Assert.False(Directory.Exists(scratch));
    }

    [Fact]
    public async Task LockedLegacyScratchDir_ListAsyncCompletesWithoutThrow()
    {
        var scratch = Path.Combine(_workspace, ".relay-scratch");
        Directory.CreateDirectory(scratch);
        File.WriteAllText(Path.Combine(scratch, "stuck.png"), "stuck");

        // Make the directory read-only so deletion fails.
        if (!OperatingSystem.IsWindows())
        {
            // Remove write+execute permissions from the directory itself.
            File.SetUnixFileMode(scratch, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        try
        {
            var repo = new RelayTaskRepository(_workspace);
            // Must not throw even though the dir can't be deleted.
            await repo.ListAsync();
        }
        finally
        {
            // Restore permissions so Dispose can clean up.
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(scratch, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workspace))
                Directory.Delete(_workspace, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
