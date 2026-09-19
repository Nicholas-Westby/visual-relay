using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;
using VisualRelay.Domain;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// Bootstrap writes the per-file command the author-tests gate runs on every task, and
/// nothing ever ran it before the first task depended on it. It now gets the same
/// treatment the whole-suite command already gets: whatever is about to be written is
/// first run against the repository's own test files.
/// </summary>
public sealed class ProjectBootstrapperPerFileTests
{
    /// <summary>
    /// A Python repository, because pytest is one of the runners the table DOES have a
    /// per-file form for. Go deliberately has none, which is the other half of the
    /// story and is covered by the proposal facts below.
    /// </summary>
    private static (TestRepository Repo, GitSimEngine Sim) PytestRepoWithATest()
    {
        var repo = TestRepository.Create();
        var sim = new GitSimEngine();
        sim.InitRepo(repo.Root);
        sim.Seed(repo.Root, "pyproject.toml", "[project]\nname = \"m\"\n");
        sim.Seed(repo.Root, "m.py", "def add(a, b):\n    return a + b\n");
        sim.Seed(repo.Root, "tests/test_m.py", "def test_add():\n    assert True\n");
        sim.Commit(repo.Root, "seed");
        return (repo, sim);
    }

    /// <summary>The raw configured value: a loaded null falls back to testCmd.</summary>
    private static string? RawTestFileCommand(string rootPath)
    {
        var json = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(rootPath, ".relay", "config.json")));
        return json.RootElement.TryGetProperty("testFileCmd", out var value)
            && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;
    }

    /// <summary>Answers per command, so the whole-suite check and the proof can differ.</summary>
    private sealed class ByCommand(Func<string, TestRunResult> answer) : ITestRunner
    {
        public Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default) =>
            Task.FromResult(answer(command));
    }

    private static TestRunResult Green(string _) => new(0, "ok  example.com/m  1 passed");

    [Fact]
    public async Task ATableFormThatPasses_IsWrittenWithSourceTable()
    {
        var (repo, sim) = PytestRepoWithATest();
        using var _ = repo;

        var result = await ProjectBootstrapper.BootstrapAsync(
            repo.Root, gitInvoker: sim, validationRunner: new ByCommand(Green));

        Assert.Equal(PerFileCommandSource.Table, result.PerFileCommandSource);
        Assert.Contains("{files}", RawTestFileCommand(repo.Root)!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The proof runs the form on real test files, so a form that cannot run them is
    /// caught here rather than by the first task's gate.
    /// </summary>
    [Fact]
    public async Task ATableFormThatFailsItsProof_AndNoProposer_WritesNull()
    {
        var (repo, sim) = PytestRepoWithATest();
        using var _ = repo;
        // The whole-suite command passes; the per-file form does not.
        var runner = new ByCommand(c => c.Contains("test_m.py", StringComparison.Ordinal)
            ? new TestRunResult(127, "sh: go: command not found")
            : new TestRunResult(0, "ok  example.com/m  1 passed"));

        var result = await ProjectBootstrapper.BootstrapAsync(
            repo.Root, gitInvoker: sim, validationRunner: runner);

        Assert.Equal(PerFileCommandSource.None, result.PerFileCommandSource);
        Assert.Null(RawTestFileCommand(repo.Root));
    }

    [Fact]
    public async Task ARejectedFormAndAProvenProposal_WritesTheProposal()
    {
        var (repo, sim) = PytestRepoWithATest();
        using var _ = repo;
        var runner = new ByCommand(c => c.Contains("test_m.py", StringComparison.Ordinal)
            ? new TestRunResult(127, "sh: go: command not found")
            : new TestRunResult(0, "ok  example.com/m  1 passed"));
        string? sawCommand = null;
        IReadOnlyList<string>? sawFiles = null;
        string? sawRejected = null;

        var result = await ProjectBootstrapper.BootstrapAsync(
            repo.Root,
            gitInvoker: sim,
            validationRunner: runner,
            proposalRunner: _ => new ByCommand(Green),
            proposePerFile: (command, files, rejected, _) =>
            {
                sawCommand = command;
                sawFiles = files;
                sawRejected = rejected;
                return Task.FromResult<string?>("pytest -p no:cacheprovider {files}");
            });

        Assert.Equal(PerFileCommandSource.Proposed, result.PerFileCommandSource);
        Assert.Equal("pytest -p no:cacheprovider {files}", RawTestFileCommand(repo.Root));

        // The proposer was handed what it needs to answer with a form that keeps the
        // project's own flags: the whole-suite command, the files, and what failed.
        Assert.Contains("pytest", sawCommand!, StringComparison.Ordinal);
        Assert.Contains("tests/test_m.py", sawFiles!);
        Assert.NotNull(sawRejected);
    }

    [Fact]
    public async Task TwoRejectedProposals_WriteNullWithSourceNone()
    {
        var (repo, sim) = PytestRepoWithATest();
        using var _ = repo;
        var runner = new ByCommand(c => c.Contains("test_m.py", StringComparison.Ordinal)
            ? new TestRunResult(127, "nope")
            : new TestRunResult(0, "ok  example.com/m  1 passed"));
        var proposals = new Queue<string>(["first {files}", "second {files}"]);

        var result = await ProjectBootstrapper.BootstrapAsync(
            repo.Root,
            gitInvoker: sim,
            validationRunner: runner,
            proposalRunner: _ => new ByCommand(_ => new TestRunResult(127, "nope")),
            proposePerFile: (_, _, _, _) =>
                Task.FromResult(proposals.Count > 0 ? proposals.Dequeue() : null));

        Assert.Equal(PerFileCommandSource.None, result.PerFileCommandSource);
        Assert.Null(RawTestFileCommand(repo.Root));
    }

    /// <summary>
    /// With no test file there is nothing to prove with, so the table's form is written
    /// as it always was and the status says it is unproven.
    /// </summary>
    [Fact]
    public async Task NoTestFiles_WritesTheTableFormUnproven()
    {
        using var repo = TestRepository.Create();
        var sim = new GitSimEngine();
        sim.InitRepo(repo.Root);
        sim.Seed(repo.Root, "pyproject.toml", "[project]\nname = \"m\"\n");
        sim.Seed(repo.Root, "m.py", "def add(a, b):\n    return a + b\n");
        sim.Commit(repo.Root, "seed");

        var result = await ProjectBootstrapper.BootstrapAsync(
            repo.Root, gitInvoker: sim, validationRunner: new ByCommand(Green));

        Assert.Equal(PerFileCommandSource.Unproven, result.PerFileCommandSource);
        Assert.Contains(
            "no test file to prove it with",
            MainWindowViewModel.DescribeBootstrap(result),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The proof applies only to what bootstrap itself is about to write. A command the
    /// operator wrote survives the placeholder upgrade untouched.
    /// </summary>
    [Fact]
    public async Task AnOperatorsOwnPerFileCommand_SurvivesTheUpgrade()
    {
        using var repo = TestRepository.Create();
        var sim = new GitSimEngine();
        sim.InitRepo(repo.Root);
        await ProjectBootstrapper.BootstrapAsync(repo.Root, gitInvoker: sim);
        // As an operator does it: by editing the file.
        var configPath = Path.Combine(repo.Root, ".relay", "config.json");
        File.WriteAllText(configPath, File.ReadAllText(configPath).Replace(
            "\"testFileCmd\": null", "\"testFileCmd\": \"my own runner {files}\"", StringComparison.Ordinal));
        sim.Seed(repo.Root, "pyproject.toml", "[project]\nname = \"m\"\n");
        sim.Seed(repo.Root, "tests/test_m.py", "def test_add():\n    assert True\n");
        sim.Commit(repo.Root, "scaffold");

        await ProjectBootstrapper.TryUpgradePlaceholderTestCommandAsync(
            repo.Root, sim, new ByCommand(Green));

        Assert.Equal("my own runner {files}", RawTestFileCommand(repo.Root));
    }

    [Fact]
    public async Task TheStatus_ForAProvenTableForm_SaysItWasProven()
    {
        var (repo, sim) = PytestRepoWithATest();
        using var _ = repo;

        var result = await ProjectBootstrapper.BootstrapAsync(
            repo.Root, gitInvoker: sim, validationRunner: new ByCommand(Green));

        Assert.Contains(
            "testFileCmd proven on the repository's own test files",
            MainWindowViewModel.DescribeBootstrap(result),
            StringComparison.Ordinal);
    }
}
