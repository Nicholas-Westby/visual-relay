using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

/// <summary>
/// Candidates are validated the way the pipeline will run them — through
/// <c>/bin/sh -c</c>. Validating by argv-split handed <c>&amp;&amp;</c>, <c>npm</c> and
/// <c>run</c> to the first program as arguments, so commander.js's real
/// <c>scripts.test</c> was rejected and the repo got the no-op placeholder instead.
/// </summary>
public sealed class TestCommandShellValidationTests
{
    [Fact]
    public async Task ChainedCommand_ValidatesThroughTheShell()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "POSIX shell chain");
        using var repo = TestRepository.Create();

        var validation = await new TestCommandValidator(
                ProjectBootstrapper.CreateValidationRunner(TimeSpan.FromSeconds(30)))
            .ValidateAsync(repo.Root, "true && echo '3 tests passed'");

        Assert.True(validation.Accepted, validation.RejectionReason);
        Assert.Equal(0, validation.RunResult.ExitCode);
    }

    [Fact]
    public async Task MissingProgram_StillReportsCommandNotFound()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "POSIX shell chain");
        using var repo = TestRepository.Create();

        var validation = await new TestCommandValidator(
                ProjectBootstrapper.CreateValidationRunner(TimeSpan.FromSeconds(30)))
            .ValidateAsync(repo.Root, "vr-no-such-runner-xyz");

        Assert.False(validation.Accepted);
        Assert.Equal(127, validation.RunResult.ExitCode);
    }

    [Fact]
    public async Task RepoRelativeScript_ResolvesAgainstTheRepoRoot()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix executable bit");
        using var repo = TestRepository.Create();
        WriteExecutable(repo.Root, "run-tests.sh", "echo '2 tests passed'\n");

        var validation = await new TestCommandValidator(
                ProjectBootstrapper.CreateValidationRunner(TimeSpan.FromSeconds(30)))
            .ValidateAsync(repo.Root, "./run-tests.sh");

        Assert.True(validation.Accepted, validation.RejectionReason);
    }

    /// <summary>Writes an executable stub script at a repo-relative path.</summary>
    private static void WriteExecutable(string root, string relativePath, string body)
    {
        var full = Path.Combine(root, relativePath);
        File.WriteAllText(full, "#!/bin/sh\n" + body);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(full,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
