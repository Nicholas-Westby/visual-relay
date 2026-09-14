using System.Text.Json.Nodes;
using VisualRelay.Core.Init;
using VisualRelay.Domain;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// A project audited by Apache RAT keeps its tasks in a hidden directory. RAT fails the build on
/// every visible file without an approved license header, ignores .git/info/exclude, and skips
/// hidden entries. Measured with apache/commons-lang on the Windows arm: `mvn test` failed once a
/// task file existed, an agent wrote .mvn/maven.config with -Drat.skip=true to get past it, and
/// after the run the two committed DONE files failed the project's own `mvn validate` on HEAD.
/// </summary>
public sealed class LicenseAuditTasksDirTests
{
    private const string CommonsParentPom = """
        <project>
          <parent>
            <groupId>org.apache.commons</groupId>
            <artifactId>commons-parent</artifactId>
            <version>85</version>
          </parent>
          <artifactId>commons-lang3</artifactId>
        </project>
        """;

    private const string RatPluginPom = """
        <project>
          <artifactId>demo</artifactId>
          <build><plugins><plugin>
            <groupId>org.apache.rat</groupId>
            <artifactId>apache-rat-plugin</artifactId>
          </plugin></plugins></build>
        </project>
        """;

    [Theory]
    [InlineData("pom.xml", CommonsParentPom)]
    [InlineData("pom.xml", RatPluginPom)]
    [InlineData("build.gradle.kts", "plugins { id(\"org.nosphere.apache.rat\") version \"0.8.1\" }\n")]
    public async Task Bootstrap_OfARatAuditedProject_PutsTheTasksInAHiddenDirectory(string buildFile, string content)
    {
        using var repo = TestRepository.Create();
        await File.WriteAllTextAsync(Path.Combine(repo.Root, buildFile), content);

        var result = await BootstrapAsync(repo);

        Assert.Equal(".llm-tasks", ReadConfig(repo)["tasksDir"]!.GetValue<string>());
        Assert.Contains(".llm-tasks", result.TasksDirNote, StringComparison.Ordinal);
        Assert.Contains("RAT", result.TasksDirNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bootstrap_OfAProjectWithoutRat_KeepsTheDefaultTasksDirectory()
    {
        using var repo = TestRepository.Create();
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "pom.xml"), "<project><artifactId>plain</artifactId></project>");

        var result = await BootstrapAsync(repo);

        Assert.Null(ReadConfig(repo)["tasksDir"]);
        Assert.Null(result.TasksDirNote);
    }

    [Fact]
    public async Task Bootstrap_OfARatProjectThatAlreadyHasTasks_LeavesThemWhereTheyAre_AndSaysWhy()
    {
        using var repo = TestRepository.Create();
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "pom.xml"), RatPluginPom);
        Directory.CreateDirectory(Path.Combine(repo.Root, "llm-tasks", "existing"));

        var result = await BootstrapAsync(repo);

        Assert.Null(ReadConfig(repo)["tasksDir"]);
        Assert.Contains("llm-tasks", result.TasksDirNote, StringComparison.Ordinal);
        Assert.Contains("tasksDir", result.TasksDirNote, StringComparison.Ordinal);
    }

    private static Task<ProjectBootstrapResult> BootstrapAsync(TestRepository repo) =>
        ProjectBootstrapper.BootstrapAsync(repo.Root, new GitSimEngine(),
            new ScriptedTestRunner(new TestRunResult(0, "Tests run: 3, Failures: 0, Errors: 0, Skipped: 0\n")));

    private static JsonObject ReadConfig(TestRepository repo) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(repo.Root, ".relay", "config.json")))!.AsObject();
}
