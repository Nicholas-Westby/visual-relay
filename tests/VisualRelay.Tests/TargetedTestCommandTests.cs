using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for <see cref="RelayDriver.BuildTargetedTestCommand"/>.
/// IsTestFile = !IsImpl — files whose extension is in NonCodeExtensions (.md, .json, .yaml, …).
/// </summary>
public sealed class TargetedTestCommandTests
{
    private static RelayConfig MakeConfig(string testCmd, string testFileCmd) =>
        RelayConfigLoader.Defaults(testCmd) with { TestFileCommand = testFileCmd };

    [Fact]
    public void BuildTargetedTestCommand_WithFilesToken_AndTestFiles_ReturnsSubstitutedCommand()
    {
        // .json has no-code extension → IsTestFile=true → included in substitution
        var config = MakeConfig("dotnet test", "bun test {files}");
        var manifest = new List<string> { "src/app.cs", "config.json" };

        var result = RelayDriver.BuildTargetedTestCommand(config, manifest);

        Assert.Equal("bun test config.json", result);
    }

    [Fact]
    public void BuildTargetedTestCommand_NoFilesToken_ReturnsFallbackTestCommand()
    {
        // testFileCmd has no {files} token → always falls back to testCmd
        var config = MakeConfig(
            "dotnet test",
            "dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj");
        var manifest = new List<string> { "src/app.cs", "tests/app.tests.cs" };

        var result = RelayDriver.BuildTargetedTestCommand(config, manifest);

        Assert.Equal("dotnet test", result);
    }

    [Fact]
    public void BuildTargetedTestCommand_WithFilesToken_ButNoTestFiles_ReturnsFallbackTestCommand()
    {
        // All files are code (IsImpl=true) → no non-code "test files" → fallback
        var config = MakeConfig("dotnet test", "bun test {files}");
        var manifest = new List<string> { "src/app.cs", "tests/app.tests.cs" };

        var result = RelayDriver.BuildTargetedTestCommand(config, manifest);

        Assert.Equal("dotnet test", result);
    }

    [Fact]
    public void BuildTargetedTestCommand_WithFilesToken_MixedManifest_OnlyNonCodeFilesSubstituted()
    {
        // IsTestFile = !IsImpl. .cs → IsImpl=true (code). .json, .yaml → IsImpl=false.
        var config = MakeConfig("dotnet test", "bun test {files}");
        var manifest = new List<string>
        {
            "src/app.cs",
            "tests/app.tests.cs",
            "config.json",
            "settings.yaml"
        };

        var result = RelayDriver.BuildTargetedTestCommand(config, manifest);

        // Only non-code files appear in substitution
        Assert.Equal("bun test config.json settings.yaml", result);
    }

    [Fact]
    public void BuildTargetedTestCommand_MultipleNonCodeFiles_SpaceJoined()
    {
        var config = MakeConfig("bun test", "bun test {files}");
        var manifest = new List<string>
        {
            "config.json",
            "schema.yaml",
            "src/app.ts"
        };

        var result = RelayDriver.BuildTargetedTestCommand(config, manifest);

        // .json, .yaml → IsTestFile=true; .ts → IsTestFile=false
        Assert.Equal("bun test config.json schema.yaml", result);
    }
}

/// <summary>
/// Integration tests: verify stages 6, 8, and 10 receive the targeted test command
/// in their <see cref="StageInvocation.TestCommand"/> field.
/// </summary>
public sealed class TargetedTestInvocationTests
{
    [Fact]
    public async Task RunTaskAsync_StagesImplementAndFix_ReceiveTargetedTestCommand()
    {
        // Config with {files} token — targeted command will be built from non-code files
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """
            {
              "testCmd": "dotnet test",
              "testFileCmd": "bun test {files}",
              "logSources": [],
              "baselineVerify": false,
              "maxVerifyLoops": 1
            }
            """);
        repo.WriteTask("targeted-test", "# Targeted test\n");
        var runner = new CapturingSubagentRunner();
        // Manifest: src/app.cs (IsImpl=true) + config.json (IsImpl=false → IsTestFile=true)
        runner.SeedHappyPath("src/app.cs", "config.json");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),    // stage 5 author gate
            new TestRunResult(0, "green")); // stage 9 verify — green
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "targeted-test");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // Stage 6 (Implement) receives targeted command
        var stage6 = runner.Invocations.Single(i => i.Stage.Number == 6);
        Assert.Equal("bun test config.json", stage6.TestCommand);

        // Stage 8 (Fix) receives targeted command
        var stage8 = runner.Invocations.Single(i => i.Stage.Number == 8);
        Assert.Equal("bun test config.json", stage8.TestCommand);
    }

    [Fact]
    public async Task RunTaskAsync_NoFilesToken_StagesReceiveFallbackTestCommand()
    {
        // testFileCmd without {files} → fallback to config.TestCommand
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """
            {
              "testCmd": "dotnet test",
              "testFileCmd": "dotnet test tests/MyProject.Tests/MyProject.Tests.csproj",
              "logSources": [],
              "baselineVerify": false,
              "maxVerifyLoops": 1
            }
            """);
        repo.WriteTask("fallback-test", "# Fallback test\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),    // stage 5 author gate
            new TestRunResult(0, "green")); // stage 9 verify — green
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "fallback-test");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);

        // Stages 6 and 8 receive config.TestCommand as fallback
        var stage6 = runner.Invocations.Single(i => i.Stage.Number == 6);
        Assert.Equal("dotnet test", stage6.TestCommand);

        var stage8 = runner.Invocations.Single(i => i.Stage.Number == 8);
        Assert.Equal("dotnet test", stage8.TestCommand);
    }

    [Fact]
    public async Task RunTaskAsync_Stage9HarnessStillUsesFullTestCommand()
    {
        // Even with {files} token, the harness TestRunner.RunAsync at stage 9 uses config.TestCommand
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """
            {
              "testCmd": "dotnet test",
              "testFileCmd": "bun test {files}",
              "logSources": [],
              "baselineVerify": false,
              "maxVerifyLoops": 1
            }
            """);
        repo.WriteTask("verify-harness", "# Verify harness\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "config.json");
        var recordingTests = new RecordingTestRunner(
            new TestRunResult(1, "red"),    // stage 5 author gate
            new TestRunResult(0, "green")); // stage 9 verify — green
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, recordingTests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "verify-harness");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        // Harness stage-9 TestRunner call must use the full config.TestCommand (not targeted)
        Assert.Contains(recordingTests.Calls, call => call.Command == "dotnet test");
        Assert.DoesNotContain(recordingTests.Calls, call => call.Command.Contains("{files}"));
    }

    [Fact]
    public async Task RunTaskAsync_FixVerifyLoop_Stage10ReceivesTargetedTestCommandInPrompt()
    {
        // Stage 9 red → fix-verify loop → stage 10 agent prompt uses targeted cmd
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """
            {
              "testCmd": "dotnet test",
              "testFileCmd": "bun test {files}",
              "logSources": [],
              "baselineVerify": false,
              "maxVerifyLoops": 2
            }
            """);
        repo.WriteTask("fix-verify-targeted", "# Fix-verify targeted\n");
        var runner = new CapturingSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "config.json");
        var tests = new ScriptedTestRunner(
            new TestRunResult(1, "red"),           // stage 5 author gate
            new TestRunResult(1, "Failed TestX"),  // stage 9 verify — red
            new TestRunResult(0, "green"));         // fix-verify re-verify — green
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "fix-verify-targeted");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var stage10 = runner.Invocations.Single(i => i.Stage.Number == 10);
        Assert.Equal("bun test config.json", stage10.TestCommand);
    }
}
