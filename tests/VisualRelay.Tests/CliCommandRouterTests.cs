using VisualRelay.Cli;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for the pure command-name → known-command mapping in the C# CLI
/// that replaced the launcher's bash <c>case</c> dispatch. The router only
/// classifies the verb; it does not execute anything (IO-thin handlers do that),
/// so it is fully testable without spawning processes.
/// </summary>
public sealed class CliCommandRouterTests
{
    [Theory]
    [InlineData("build")]
    [InlineData("test")]
    [InlineData("format")]
    [InlineData("screenshot")]
    [InlineData("run-task")]
    [InlineData("init")]
    [InlineData("check")]
    [InlineData("inspect")]
    [InlineData("gen-backend-config")]
    [InlineData("guards")]
    [InlineData("install-hooks")]
    [InlineData("launch")]
    [InlineData("run")]
    public void Recognizes_EveryKnownSubcommand(string cmd)
    {
        Assert.True(CommandRouter.IsKnown(cmd), $"'{cmd}' should be a known subcommand");
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("")]
    [InlineData("sample-reset")]
    [InlineData("--help")]
    public void RejectsUnknownVerbs(string cmd)
    {
        Assert.False(CommandRouter.IsKnown(cmd), $"'{cmd}' should not be a known subcommand");
    }

    [Fact]
    public void UsageLine_ListsCoreCommands_AndOmitsSampleReset()
    {
        var usage = CommandRouter.UsageLine;
        Assert.Contains("build", usage, StringComparison.Ordinal);
        Assert.Contains("test", usage, StringComparison.Ordinal);
        Assert.Contains("check", usage, StringComparison.Ordinal);
        Assert.Contains("install-hooks", usage, StringComparison.Ordinal);
        Assert.DoesNotContain("sample-reset", usage, StringComparison.Ordinal);
    }
}
