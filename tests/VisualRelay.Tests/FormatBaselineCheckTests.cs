using System.Text.Json.Nodes;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// A detected formatter is kept only when the clean checkout already satisfies it. Visual Relay
/// runs formatCmd over the working tree before every guard, so a formatter the baseline fails
/// rewrites the whole project in every task's commit. Measured on the Windows arm with
/// litedb-org/LiteDB: bootstrap set formatCmd <c>dotnet format LiteDB.sln</c>, whose check exits 2
/// on the untouched checkout with 1257 whitespace findings, and set that same check as the guard,
/// which the baseline guard gate would have refused every run over.
/// </summary>
public sealed class FormatBaselineCheckTests
{
    [Fact]
    public async Task AFormatterTheCleanCheckoutFails_IsLeftOut_WithItsCheckFromTheGuard()
    {
        using var repo = TestRepository.Create();
        WriteConfig(repo, formatCmd: "dotnet format LiteDB.sln",
            guardCmd: "tools/guards/size.sh && dotnet format LiteDB.sln --verify-no-changes");
        var runner = new RecordingRunner(new TestRunResult(2, "error WHITESPACE: Fix whitespace formatting."));

        var note = await FormatBaselineCheck.ApplyAsync(repo.Root, runner, CancellationToken.None);

        Assert.Equal(["dotnet format LiteDB.sln --verify-no-changes"], runner.Commands);
        var config = ReadConfig(repo);
        Assert.Null(config["formatCmd"]);
        Assert.Equal("tools/guards/size.sh", config["guardCmd"]!.GetValue<string>());
        Assert.Contains("dotnet format LiteDB.sln", note, StringComparison.Ordinal);
        Assert.Contains("exit 2", note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGuardThatWasOnlyTheFormatCheck_IsLeftOutWhole()
    {
        using var repo = TestRepository.Create();
        WriteConfig(repo, formatCmd: "cargo fmt", guardCmd: "cargo fmt --check");

        await FormatBaselineCheck.ApplyAsync(repo.Root, new RecordingRunner(new TestRunResult(1, "Diff in src/lib.rs")), CancellationToken.None);

        var config = ReadConfig(repo);
        Assert.Null(config["formatCmd"]);
        Assert.Null(config["guardCmd"]);
    }

    [Fact]
    public async Task AFormatterTheCleanCheckoutSatisfies_IsKept()
    {
        using var repo = TestRepository.Create();
        WriteConfig(repo, formatCmd: "gofmt -w .", guardCmd: null);

        var note = await FormatBaselineCheck.ApplyAsync(repo.Root, new RecordingRunner(new TestRunResult(0, "")), CancellationToken.None);

        Assert.Null(note);
        Assert.Equal("gofmt -w .", ReadConfig(repo)["formatCmd"]!.GetValue<string>());
    }

    [Fact]
    public async Task AFormatterWhoseCheckRunsOutOfTime_IsLeftOut_WithoutClaimingTheCheckoutFailsIt()
    {
        using var repo = TestRepository.Create();
        WriteConfig(repo, formatCmd: "dotnet format", guardCmd: null);

        var note = await FormatBaselineCheck.ApplyAsync(
            repo.Root, new RecordingRunner(new TestRunResult(-1, "Restoring...", TimedOut: true)), CancellationToken.None);

        Assert.Null(ReadConfig(repo)["formatCmd"]);
        Assert.Contains("did not finish", note, StringComparison.Ordinal);
        Assert.DoesNotContain("does not pass", note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bootstrap_LeavesOutAFormatterTheCleanCheckoutFails_AndSaysSo()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "go.mod"), "module m\n\ngo 1.22\n");
        var runner = new RecordingRunner(new TestRunResult(0, "ok  \tm\t0.012s\n"), new TestRunResult(1, "main.go\n"));

        var result = await ProjectBootstrapper.BootstrapAsync(repo.Root, new VisualRelay.GitSim.GitSim(), runner);

        Assert.Equal("go test ./...", result.TestCommand);
        Assert.Equal("test -z \"$(gofmt -l .)\"", runner.Commands[^1]);
        Assert.Null(ReadConfig(repo)["formatCmd"]);
        Assert.Contains("gofmt -w .", result.FormatNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFormatterWithNoKnownCheck_IsKeptUnchecked()
    {
        using var repo = TestRepository.Create();
        WriteConfig(repo, formatCmd: "npm run format", guardCmd: null);
        var runner = new RecordingRunner();

        Assert.Null(await FormatBaselineCheck.ApplyAsync(repo.Root, runner, CancellationToken.None));

        Assert.Empty(runner.Commands);
        Assert.Equal("npm run format", ReadConfig(repo)["formatCmd"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("dotnet format App.slnx", "dotnet format App.slnx --verify-no-changes")]
    [InlineData("dotnet format", "dotnet format --verify-no-changes")]
    [InlineData("cargo fmt", "cargo fmt --check")]
    [InlineData("prettier --write .", "prettier --check .")]
    [InlineData("gofmt -w .", "test -z \"$(gofmt -l .)\"")]
    [InlineData("swiftformat .", "swiftformat --lint .")]
    public void TheCheckForm_OfEachDetectedFormatter(string formatCmd, string check)
    {
        Assert.Equal(check, FormatBaselineCheck.CheckFormOf(formatCmd));
    }

    private static void WriteConfig(TestRepository repo, string formatCmd, string? guardCmd)
    {
        var json = new JsonObject { ["testCmd"] = "true", ["formatCmd"] = formatCmd };
        if (guardCmd is not null) json["guardCmd"] = guardCmd;
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        File.WriteAllText(Path.Combine(repo.Root, ".relay", "config.json"), json.ToJsonString());
    }

    private static JsonObject ReadConfig(TestRepository repo) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(repo.Root, ".relay", "config.json")))!.AsObject();

    private sealed class RecordingRunner(params TestRunResult[] results) : Core.Execution.ITestRunner
    {
        private readonly Queue<TestRunResult> _results = new(results);
        public List<string> Commands { get; } = [];

        public Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : new TestRunResult(0, ""));
        }
    }
}
