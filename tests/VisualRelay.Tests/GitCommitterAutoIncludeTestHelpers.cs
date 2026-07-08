namespace VisualRelay.Tests;

/// <summary>
/// Shared git helpers extracted from the former GitCommitterAutoIncludeTests partial
/// class so companion files can be promoted to independent parallel test classes.
/// </summary>
internal static class GitCommitterAutoIncludeTestHelpers
{
    // ReSharper disable once AsyncMethodWithoutAwait — async kept so awaiting sites surface sync git failures via the awaited task.
    public static async Task InitGitRepo(string root)
    {
        Directory.CreateDirectory(root);
        TestGit.Run(root, "init");
        TestGit.Run(root, "config", "user.email", "test@example.test");
        TestGit.Run(root, "config", "user.name", "Test");
    }

    // ReSharper disable once AsyncMethodWithoutAwait — see InitGitRepo above.
    public static async Task StageAndCommitSeed(string root, string message)
    {
        TestGit.Run(root, "add", ".");
        TestGit.Run(root, "commit", "-m", message);
    }
}
