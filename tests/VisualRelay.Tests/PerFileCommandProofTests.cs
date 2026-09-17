using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// The author-tests gate runs only the test files a task just wrote, through
/// <c>testFileCmd</c>, and nothing ever ran that command before the first task depended
/// on it. Where the table's form was wrong the gate failed for the wrong reason — a red
/// from a broken command is not proof that the new tests fail — so bootstrap now proves
/// whatever it is about to write against real test files.
/// </summary>
public sealed class PerFileCommandProofTests
{
    private sealed class Recorder(TestRunResult result) : ITestRunner
    {
        public List<string> Commands { get; } = [];

        public Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task APassingRunWithATestCount_IsProven()
    {
        var runner = new Recorder(new TestRunResult(0, "ok  example.com/m  2 passed"));

        var proof = await PerFileCommandProof.ProveAsync(
            "/repo", "go test {files}", ["a_test.go", "b_test.go"], runner, CancellationToken.None);

        Assert.True(proof.Proven);
        Assert.Null(proof.Reason);
    }

    /// <summary>Two files, so a form that only works for one is caught, space-joined.</summary>
    [Fact]
    public async Task TwoFilesAreSubstituted_SpaceJoined()
    {
        var runner = new Recorder(new TestRunResult(0, "2 passed"));

        await PerFileCommandProof.ProveAsync(
            "/repo", "go test {files}", ["a_test.go", "b_test.go", "c_test.go"], runner, CancellationToken.None);

        Assert.Equal("go test a_test.go b_test.go", Assert.Single(runner.Commands));
    }

    [Fact]
    public async Task OneFileIsSubstituted_WhenTheRepositoryHasOne()
    {
        var runner = new Recorder(new TestRunResult(0, "1 passed"));

        await PerFileCommandProof.ProveAsync(
            "/repo", "go test {files}", ["only_test.go"], runner, CancellationToken.None);

        Assert.Equal("go test only_test.go", Assert.Single(runner.Commands));
    }

    [Fact]
    public async Task ANonZeroExit_IsRejectedWithTheOutputHead()
    {
        var runner = new Recorder(new TestRunResult(1, "FAIL\nsomething broke"));

        var proof = await PerFileCommandProof.ProveAsync(
            "/repo", "go test {files}", ["a_test.go"], runner, CancellationToken.None);

        Assert.False(proof.Proven);
        Assert.Contains("exited 1", proof.Reason!, StringComparison.Ordinal);
        Assert.Contains("something broke", proof.OutputHead, StringComparison.Ordinal);
    }

    /// <summary>A command that starts and finds nothing is exactly as useless as one that cannot start.</summary>
    [Fact]
    public async Task AZeroExitThatRanNoTests_IsRejectedAsUnusable()
    {
        var runner = new Recorder(new TestRunResult(0, "no tests found"));

        var proof = await PerFileCommandProof.ProveAsync(
            "/repo", "npx jest {files}", ["a.test.js"], runner, CancellationToken.None);

        Assert.False(proof.Proven);
        Assert.Contains("ran no tests", proof.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommandWithoutTheToken_IsRejectedWithoutRunning()
    {
        var runner = new Recorder(new TestRunResult(0, "2 passed"));

        var proof = await PerFileCommandProof.ProveAsync(
            "/repo", "go test ./...", ["a_test.go"], runner, CancellationToken.None);

        Assert.False(proof.Proven);
        Assert.Contains("{files}", proof.Reason!, StringComparison.Ordinal);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task NoTestFiles_IsRejectedWithoutRunning()
    {
        var runner = new Recorder(new TestRunResult(0, "2 passed"));

        var proof = await PerFileCommandProof.ProveAsync(
            "/repo", "go test {files}", [], runner, CancellationToken.None);

        Assert.False(proof.Proven);
        Assert.Empty(runner.Commands);
    }

    /// <summary>The two smallest, so the proof is quick, and only paths the gate would accept.</summary>
    [Fact]
    public void ChooseTestFiles_TakesTheTwoSmallestRunnableTestFiles()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, "tests"));
        File.WriteAllText(Path.Combine(repo.Root, "tests", "big_test.go"), new string('x', 4000));
        File.WriteAllText(Path.Combine(repo.Root, "tests", "small_test.go"), "x");
        File.WriteAllText(Path.Combine(repo.Root, "tests", "mid_test.go"), new string('x', 100));
        File.WriteAllText(Path.Combine(repo.Root, "tests", "fixture.json"), "{}");
        File.WriteAllText(Path.Combine(repo.Root, "main.go"), "package main");

        var chosen = PerFileCommandProof.ChooseTestFiles(
            repo.Root,
            ["tests/big_test.go", "tests/small_test.go", "tests/mid_test.go", "tests/fixture.json", "main.go"],
            null);

        Assert.Equal(["tests/small_test.go", "tests/mid_test.go"], chosen);
    }
}
