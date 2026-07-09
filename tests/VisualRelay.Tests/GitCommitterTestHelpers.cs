namespace VisualRelay.Tests;

/// <summary>
/// Shared git helpers extracted from the former GitCommitterTests partial class
/// so companion files can be promoted to independent parallel test classes.
/// </summary>
internal static class GitCommitterTestHelpers
{
    public static void WriteActiveInfo(string repoRoot, string nonce)
    {
        var activeDir = Path.Combine(repoRoot, ".relay", "ACTIVE");
        Directory.CreateDirectory(activeDir);
        File.WriteAllText(Path.Combine(activeDir, "info.json"),
            $"{{\"nonce\":\"{nonce}\"}}");
    }
}
