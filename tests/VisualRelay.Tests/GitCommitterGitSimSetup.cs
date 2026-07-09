using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// Setup helpers shared by the migrated commit family (<see cref="GitCommitterTests"/>
/// and the resilience classes). Replaces the former real-git <c>init</c>/<c>add</c>/
/// <c>commit</c> + <c>git log</c> setup with GitSim seeding and inspection, so those
/// tests run in-memory with zero process spawns.
/// </summary>
internal static class GitCommitterGitSimSetup
{
    /// <summary>A fresh GitSim-backed repo on <c>main</c> at a throwaway root (unborn HEAD).</summary>
    public static (GitSimEngine Sim, TestRepository Repo) NewRepo()
    {
        var repo = TestRepository.Create();
        var sim = new GitSimEngine();
        sim.InitRepo(repo.Root, "main");
        return (sim, repo);
    }

    /// <summary>Writes a real working-tree file (creating parent dirs), as the tests dirty the tree.</summary>
    public static void Write(TestRepository repo, string relPath, string content)
    {
        var full = Path.Combine(repo.Root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>The subject line (first line) of a committed message, via GitSim inspection.</summary>
    public static string Subject(GitSimEngine sim, TestRepository repo, string sha) =>
        sim.CommitInfo(repo.Root, sha)!.Message.Split('\n')[0];
}
