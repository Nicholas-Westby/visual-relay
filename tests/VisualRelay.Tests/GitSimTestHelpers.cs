using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// Shared helpers for the always-on GitSim unit tests: a convenience wrapper over
/// <c>GitSim.RunAsync</c> that asserts the never-timed-out contract and
/// returns just the exit code + combined output, plus an environment-carrying form
/// for the commit-token/hook facts.
/// </summary>
internal static class GitSimTestHelpers
{
    public static Task<(int Exit, string Output)> Git(this GitSimEngine sim, string root, params string[] args) =>
        sim.GitEnv(root, environment: null, args);

    public static async Task<(int Exit, string Output)> GitEnv(
        this GitSimEngine sim, string root, IReadOnlyDictionary<string, string>? environment, params string[] args)
    {
        var (exit, output, timedOut) = await sim.RunAsync(root, args, CancellationToken.None, environment: environment);
        Assert.False(timedOut, "GitSim must never report a timeout.");
        return (exit, output);
    }

    /// <summary>A fresh, registered in-memory repo rooted at a throwaway temp dir.</summary>
    public static (GitSimEngine Sim, TestRepository Repo) NewRepo(string branch = "main")
    {
        var repo = TestRepository.Create();
        var sim = new GitSimEngine();
        sim.InitRepo(repo.Root, branch);
        return (sim, repo);
    }

    public static void Write(TestRepository repo, string relPath, string content)
    {
        var full = Path.Combine(repo.Root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public static bool IsFortyHex(string sha) =>
        sha.Length == 40 && sha.All(Uri.IsHexDigit);
}
