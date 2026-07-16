using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed partial class SwivalSubagentRunnerSandboxTests
{
    [Fact]
    public void BuildNonoPrefix_GrantsUserTemplatesDir()
    {
        // Use a deterministic temp path so we can assert on it.
        var tempDir = Directory.CreateTempSubdirectory().FullName;
        var templatesSubdir = Path.Combine(tempDir, "templates");

        try
        {
            var config = TestConfig();
            var prefix = SwivalSubagentRunner.BuildNonoPrefix(
                config, rollback: false, userTemplatesDirOverride: templatesSubdir);

            // The templates -a pair must appear after any SandboxExtraAllowPaths pairs.
            // With no extra paths, the pair is at indices 4,5 (after run, --profile, <abs>, --allow-cwd).
            var aIndexes = new List<int>();
            for (var i = 0; i < prefix.Count; i++)
            {
                if (prefix[i] == "-a")
                {
                    aIndexes.Add(i);
                }
            }

            Assert.Contains(aIndexes, i => i + 1 < prefix.Count && prefix[i + 1] == templatesSubdir);

            // Directory must now exist (eagerly created by BuildNonoPrefix).
            Assert.True(Directory.Exists(templatesSubdir));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
