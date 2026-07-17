using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

public sealed partial class PipelineTestFixture
{
    /// <summary>
    /// Writes the standard pipeline shape into <paramref name="root"/> and
    /// seeds a GitSim repo with one commit, returning the seed commit sha.
    /// The caller must already have created the directory.
    /// </summary>
    internal static string SeedStandard(string root, GitSimEngine sim)
    {
        // Standard config: single test command, archiveOnDone:true.
        Directory.CreateDirectory(Path.Combine(root, ".relay"));
        File.WriteAllText(
            Path.Combine(root, ".relay", "config.json"),
            """
            {
              "testCmd": "test -f src/status.cs",
              "logSources": [],
              "baselineVerify": true,
              "enableFixVerify": false,
              "maxStageFailures": 3,
              "archiveOnDone": true,
              "tierProfiles": { "vision": "vision" }
            }
            """);

        // Standard task: ship-status with batch:2.
        Directory.CreateDirectory(Path.Combine(root, "llm-tasks"));
        File.WriteAllText(
            Path.Combine(root, "llm-tasks", "ship-status.md"),
            "batch: 2\n\n# Ship status\n");

        // Standard seed file: src/status.cs = "old".
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "status.cs"), "old");

        // Init GitSim, stage everything, commit.
        sim.InitRepo(root);
        sim.Git(root, "add", "-A").GetAwaiter().GetResult();
        return sim.Commit(root, "chore: seed repo");
    }
}
