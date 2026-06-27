using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for <see cref="RelayDriver.BuildTargetedTestCommand"/>.
/// Targeting selects authored test files (IsTestFile), toolchain-agnostic, so
/// .NET test files (tests/FooTests.cs) narrow the gate, not the full suite.
/// </summary>
public sealed class TargetedTestCommandTests
{
    private static RelayConfig MakeConfig(string testCmd, string testFileCmd) =>
        RelayConfigLoader.Defaults(testCmd) with { TestFileCommand = testFileCmd };

    [Fact]
    public void BuildTargetedTestCommand_WithFilesToken_AndTestFiles_ReturnsSubstitutedCommand()
    {
        // tests/app.tests.cs is an authored test file (IsTestFile=true) → substituted
        var config = MakeConfig("dotnet test", "bun test {files}");
        var manifest = new List<string> { "src/app.cs", "tests/app.tests.cs" };

        var result = RelayDriver.BuildTargetedTestCommand(config, manifest);

        Assert.Equal("bun test tests/app.tests.cs", result);
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
        // Manifest has no authored test files (IsTestFile=false for all) → fallback
        var config = MakeConfig("dotnet test", "bun test {files}");
        var manifest = new List<string> { "src/app.cs", "src/helper.cs" };

        var result = RelayDriver.BuildTargetedTestCommand(config, manifest);

        Assert.Equal("dotnet test", result);
    }

    [Fact]
    public void BuildTargetedTestCommand_WithFilesToken_MixedManifest_OnlyTestFilesSubstituted()
    {
        // Only authored test files are substituted; impl + config/docs excluded.
        var config = MakeConfig("dotnet test", "bun test {files}");
        var manifest = new List<string>
        {
            "src/app.cs",
            "tests/app.tests.cs",
            "config.json",
            "settings.yaml"
        };

        var result = RelayDriver.BuildTargetedTestCommand(config, manifest);

        Assert.Equal("bun test tests/app.tests.cs", result);
    }

    [Fact]
    public void BuildTargetedTestCommand_MultipleTestFiles_SpaceJoined()
    {
        var config = MakeConfig("bun test", "bun test {files}");
        var manifest = new List<string>
        {
            "tests/a.tests.cs",
            "tests/b.tests.cs",
            "src/app.ts"
        };

        var result = RelayDriver.BuildTargetedTestCommand(config, manifest);

        // Both authored test files are joined; src/app.ts (impl) is excluded.
        Assert.Equal("bun test tests/a.tests.cs tests/b.tests.cs", result);
    }

    [Fact]
    public void BuildTargetedTestCommand_DotNetTestFileUnderTests_ReturnsTargetedCommand()
    {
        // Regression: a .NET test file under tests/ has a .cs extension; the old
        // !IsImpl filter excluded it and ran the full suite. IsTestFile narrows it.
        var config = MakeConfig("dotnet test", "sh tools/dotnet-test-files.sh {files}");
        var manifest = new List<string>
        {
            "src/App/Foo.cs",
            "tests/VisualRelay.Tests/FooTests.cs"
        };

        var result = RelayDriver.BuildTargetedTestCommand(config, manifest);

        Assert.Equal(
            "sh tools/dotnet-test-files.sh tests/VisualRelay.Tests/FooTests.cs", result);
        Assert.NotEqual(config.TestCommand, result); // NOT the full suite
    }

    // ── TestFileCommandWarning (the {files}-less footgun) ────────────────

    [Fact]
    public void TestFileCommandWarning_NoFilesToken_Warns()
    {
        // A full command masquerading as a targeted one: the red gate silently
        // runs the whole suite. This is exactly VR's pre-fix testFileCmd.
        var config = MakeConfig("dotnet test",
            "dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj -m:1");

        var warning = RelayDriver.TestFileCommandWarning(config);

        Assert.NotNull(warning);
        Assert.Contains("{files}", warning);
    }

    [Fact]
    public void TestFileCommandWarning_WithFilesToken_NoWarning()
    {
        var config = MakeConfig("dotnet test", "sh tools/dotnet-test-files.sh {files}");
        Assert.Null(RelayDriver.TestFileCommandWarning(config));
    }

    [Fact]
    public void TestFileCommandWarning_Empty_NoWarning()
    {
        // No targeting configured is not a footgun — only a non-empty command
        // that looks targeted but isn't.
        var config = MakeConfig("dotnet test", "   ");
        Assert.Null(RelayDriver.TestFileCommandWarning(config));
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
        // Config with {files} token; targeted command is built from authored test files
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
        // Manifest: src/app.cs (impl) + tests/app.tests.cs (authored test file)
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
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
        Assert.Equal("bun test tests/app.tests.cs", stage6.TestCommand);

        // Stage 8 (Fix) receives targeted command
        var stage8 = runner.Invocations.Single(i => i.Stage.Number == 8);
        Assert.Equal("bun test tests/app.tests.cs", stage8.TestCommand);
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
    public async Task RunTaskAsync_FixVerifyLoop_Stage10ReceivesFullGateCommandInPrompt()
    {
        // Stage 9 red → fix-verify loop → stage 10 agent prompt uses full gate cmd (not targeted)
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
            new TestRunResult(1, "Failed TestX"),  // stage 9 verify — first run fails
            new TestRunResult(1, "Failed TestX"),  // stage 9 verify — retry also fails
            new TestRunResult(1, "Failed TestX"),  // fix-verify attempt 1 first run — red
            new TestRunResult(0, "green"));         // fix-verify attempt 1 retry — green
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "fix-verify-targeted");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var stage10 = runner.Invocations.Single(i => i.Stage.Number == 10);
        Assert.Equal("dotnet test", stage10.TestCommand);  // full gate, not the targeted subset
    }
}
