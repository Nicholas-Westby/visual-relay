using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// A disposable clone of the <see cref="PipelineTestFixture"/> seed directory.
/// Each test gets its own clone — it owns the temp directory and the
/// in-memory <see cref="GitSimEngine"/> registered at that root, so a test
/// that mutates repo state (commit, archive, edit files) never touches the
/// shared seed or another test's clone.
/// </summary>
public sealed class PipelineClone : IDisposable
{
    public string Root { get; }
    public GitSimEngine Sim { get; }

    internal PipelineClone(string root, GitSimEngine sim)
    {
        Root = root;
        Sim = sim;
    }

    public void Dispose() => TestFileSystem.DeleteDirectoryResilient(Root);
}
