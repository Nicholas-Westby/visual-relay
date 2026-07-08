using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Shared helpers extracted from the former RelayDriverResumeTests partial class
/// so companion files can be promoted to independent parallel test classes.
/// </summary>
internal static class RelayDriverResumeTestHelpers
{
    public static async Task RunHappyPath(TestRepository repo, string taskId)
    {
        var runner = new ArtifactWritingSubagentRunner();
        runner.SeedHappyPath("src/status.cs", "tests/status.tests.cs");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, new ScriptedTestRunner(
                new TestRunResult(1, "red"),
                new TestRunResult(0, "green")), new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);
        Assert.Equal(RelayTaskOutcomeStatus.Committed,
            (await driver.RunTaskAsync(repo.Root, taskId)).Status);
    }

    /// <summary>
    /// Sets up a task directory with status.json (stages 1–11 Done, stage 12
    /// Flagged), a seal chain of 11 entries, manifest.txt, ledger.md, and a
    /// NEEDS-REVIEW marker — exactly what a prior run leaves behind when it
    /// flags at the commit gate.
    /// </summary>
    public static void SetupCommitGateResumeScenario(
        string repoRoot,
        string taskId,
        string[] manifest,
        string matchingTreeHash)
    {
        var taskDir = Path.Combine(repoRoot, ".relay", taskId);
        Directory.CreateDirectory(taskDir);

        // ── status.json ────────────────────────────────────────────────
        var statusEntries = new List<StageStatusEntry>(12);
        foreach (var stage in RelayStages.All)
        {
            if (stage.Number <= 11)
            {
                statusEntries.Add(new StageStatusEntry(
                    stage.Number, stage.Name, "Done",
                    Check: "green", CostUsd: 0, DurationSeconds: 1));
            }
            else
            {
                statusEntries.Add(new StageStatusEntry(
                    stage.Number, stage.Name, "Flagged",
                    Error: "target root is not a git repository"));
            }
        }

        File.WriteAllText(
            Path.Combine(taskDir, "status.json"),
            System.Text.Json.JsonSerializer.Serialize(statusEntries,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                }));

        // ── seals ──────────────────────────────────────────────────────
        var seals = new List<string>();
        var previousSeal = string.Empty;
        for (int n = 1; n <= 11; n++)
        {
            var stage = RelayStages.All[n - 1];
            var artifactHash = Hashing.Sha256Hex(
                n.ToString(), stage.Name, $"body for stage {n}");
            // tree hash only from stage 4 onward (matches driver behavior).
            var treeHash = n >= 4 ? matchingTreeHash : string.Empty;
            var seal = Hashing.Sha256Hex(
                previousSeal, n.ToString(),
                DateTimeOffset.UtcNow.ToString("O"),
                artifactHash, treeHash, "green");
            previousSeal = seal;

            var json = new Dictionary<string, object?>
            {
                ["kind"] = "stage",
                ["n"] = n,
                ["ts"] = DateTimeOffset.UtcNow.ToString("O"),
                ["artifactHash"] = artifactHash,
                ["treeHash"] = treeHash,
                ["seal"] = seal,
                ["check"] = "green"
            };
            seals.Add(System.Text.Json.JsonSerializer.Serialize(json));
        }

        File.WriteAllText(
            Path.Combine(taskDir, $"{taskId}.seals"),
            string.Join(Environment.NewLine, seals) + Environment.NewLine);

        // ── manifest.txt ───────────────────────────────────────────────
        File.WriteAllText(
            Path.Combine(taskDir, "manifest.txt"),
            string.Join(Environment.NewLine, manifest) + Environment.NewLine);

        // ── ledger.md ──────────────────────────────────────────────────
        var ledger = new System.Text.StringBuilder();
        for (int n = 1; n <= 11; n++)
        {
            var stage = RelayStages.All[n - 1];
            ledger.AppendLine($"## Stage {n} - {stage.Name}");
            ledger.AppendLine();
            ledger.AppendLine($"body for stage {n}");
            ledger.AppendLine();
        }

        File.WriteAllText(Path.Combine(taskDir, "ledger.md"), ledger.ToString());

        // ── NEEDS-REVIEW ───────────────────────────────────────────────
        File.WriteAllText(Path.Combine(taskDir, "NEEDS-REVIEW"), string.Empty);
    }

    /// <summary>
    /// Replicates <c>RelayDriver.WorkingTreeHash</c> so tests can compute the
    /// expected tree hash for a given manifest. Uses <see cref="Hashing.Sha256Hex"/>
    /// which is accessible via <c>InternalsVisibleTo</c>.
    /// </summary>
    public static string ComputeTreeHash(string rootPath, string[] manifest)
    {
        var parts = new List<string>();
        foreach (var relative in manifest.Order(StringComparer.Ordinal))
        {
            var fullPath = Path.Combine(rootPath, relative);
            parts.Add(relative);
            parts.Add(File.Exists(fullPath) ? File.ReadAllText(fullPath) : string.Empty);
        }

        return Hashing.Sha256Hex(parts.ToArray());
    }

    public static void InitTestRepo(string root)
    {
        TestGit.Run(root, "init", "-b", "main");
        TestGit.Run(root, "config", "user.email", "test@test");
        TestGit.Run(root, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(root, ".gitignore"), ".relay/*\n");
        TestGit.Run(root, "add", ".gitignore");
        TestGit.Run(root, "commit", "-m", "initial");
    }
}
