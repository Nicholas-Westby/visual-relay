using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// Assembly-wide fixture that creates a single pre-seeded pipeline directory
/// once and provides a fast <see cref="Clone"/> for every test that needs the
/// standard pipeline shape (config: <c>test -f src/status.cs</c> /
/// archiveOnDone:true, task: ship-status / batch:2, seed: src/status.cs="old",
/// one GitSim commit).
///
/// <para>Tests that mutate the repo (RelayDriver commits, archives tasks,
/// edits files) call <see cref="Clone"/> to get their own disposable copy.
/// The seed directory is never written to after <see cref="InitializeAsync"/>.
/// </para>
/// </summary>
public sealed partial class PipelineTestFixture : IAsyncLifetime
{
    private string _seedRoot = null!;

    /// <summary>The seed directory root (read-only after initialization).</summary>
    public string SeedRoot => _seedRoot;

    /// <summary>
    /// Creates a disposable copy of the seed directory with a fresh GitSim
    /// engine registered at the clone root. Safe to call concurrently from
    /// parallel test collections: the seed is read-only after init and the
    /// copy is a pure directory-level operation.
    /// </summary>
    public PipelineClone Clone()
    {
        var cloneRoot = Path.Combine(Path.GetTempPath(), "visual-relay-tests", Guid.NewGuid().ToString("N"));
        CopyDirectoryRecursive(_seedRoot, cloneRoot);

        var sim = new GitSimEngine();
        sim.InitRepo(cloneRoot);
        sim.Git(cloneRoot, "add", "-A").GetAwaiter().GetResult();
        sim.Commit(cloneRoot, "chore: seed repo");

        return new PipelineClone(cloneRoot, sim);
    }

    async ValueTask IAsyncLifetime.InitializeAsync()
    {
        _seedRoot = Path.Combine(Path.GetTempPath(), "visual-relay-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_seedRoot);
        SeedStandard(_seedRoot, new GitSimEngine());
        // Avoid CS1998 (no await) — the method body is synchronous but must match ValueTask.
        await Task.CompletedTask;
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        TestFileSystem.DeleteDirectoryResilient(_seedRoot);
        await Task.CompletedTask;
    }

    private static void CopyDirectoryRecursive(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var entry in Directory.GetFileSystemEntries(source))
        {
            var destEntry = Path.Combine(dest, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                CopyDirectoryRecursive(entry, destEntry);
            }
            else
            {
                File.Copy(entry, destEntry);
            }
        }
    }
}
