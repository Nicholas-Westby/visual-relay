using VisualRelay.Core.Agent.Tools;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Models were observed calling one stage's command-tool name from another stage and
/// wasting turns on the correction. The names must therefore be stage-invariant, and
/// each of the two tools must point at the other so a model that picked the wrong one
/// can switch without guessing.
/// </summary>
public sealed class AgentShellToolNamingTests
{
    private const string ShellTool = "run_shell_command";
    private const string ArgvTool = "run_command";

    private static ToolCatalog BuildCatalog() =>
        CommandToolset.RegisterInto(
            FileToolset.RegisterInto(new ToolCatalog()),
            new SandboxedCommandExecutor(RelayConfigLoader.Defaults("dotnet test")));

    /// <summary>
    /// One catalog serves the whole run: <c>SubagentRunnerFactory.BuildTools</c> takes no
    /// stage, so Research (2), Author-tests (5) and Implement (6) are handed the same
    /// tool names by construction. Pinned so a future per-stage tool set cannot silently
    /// reintroduce the divergence.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(6)]
    public void EveryStageWithAShellTool_SeesTheSameNames(int stageNumber)
    {
        var stage = RelayStages.All[stageNumber - 1];
        var catalog = BuildCatalog();

        Assert.NotNull(catalog.Find(ShellTool));
        Assert.NotNull(catalog.Find(ArgvTool));
        Assert.NotEqual("none", stage.Commands);
    }

    [Fact]
    public void ShellTool_DescriptionNamesTheArgvTool()
    {
        var shell = BuildCatalog().Find(ShellTool)!;

        Assert.Contains(ArgvTool, shell.Definition.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ArgvTool_DescriptionNamesTheShellTool()
    {
        var argv = BuildCatalog().Find(ArgvTool)!;

        Assert.Contains(ShellTool, argv.Definition.Description, StringComparison.Ordinal);
    }
}
