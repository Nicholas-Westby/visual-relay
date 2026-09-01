using VisualRelay.Core.Agent;

namespace VisualRelay.Tests;

/// <summary>
/// Covers the selector that decides which agent runs a stage. It is the shadow
/// mechanism: both agents read the same config and write the same report, so a
/// drain can be run under either and compared without a branch.
/// </summary>
public sealed class SubagentRunnerFactoryTests
{
    private static DictionaryEnvironmentAccessor Env(string? value)
    {
        var env = new DictionaryEnvironmentAccessor();
        if (value is not null) env[SubagentRunnerFactory.SelectorEnvVar] = value;
        return env;
    }

    /// <summary>
    /// Unset means the existing subprocess runner. The new loop is opt-in until
    /// it has been measured against real work.
    /// </summary>
    [Fact]
    public void UnsetSelector_KeepsTheExistingAgent()
    {
        Assert.False(SubagentRunnerFactory.UsesFirstParty(Env(null)));
    }

    /// <summary>The documented value selects the first-party loop.</summary>
    [Fact]
    public void TheDocumentedValue_SelectsTheFirstPartyLoop()
    {
        Assert.True(SubagentRunnerFactory.UsesFirstParty(
            Env(SubagentRunnerFactory.FirstPartyValue)));
    }

    /// <summary>
    /// The comparison ignores case, so a shell that upper-cases the value still
    /// selects what the operator meant.
    /// </summary>
    [Fact]
    public void TheValue_IsMatchedIgnoringCase()
    {
        Assert.True(SubagentRunnerFactory.UsesFirstParty(Env("FirstParty")));
        Assert.True(SubagentRunnerFactory.UsesFirstParty(Env("FIRSTPARTY")));
    }

    /// <summary>
    /// Anything else keeps the existing agent. A typo must not silently select
    /// an agent the operator did not ask for, in either direction.
    /// </summary>
    /// <param name="value">A value that is not the documented one.</param>
    [Theory]
    [InlineData("")]
    [InlineData("swival")]
    [InlineData("first-party")]
    [InlineData("1")]
    public void AnythingElse_KeepsTheExistingAgent(string value)
    {
        Assert.False(SubagentRunnerFactory.UsesFirstParty(Env(value)));
    }

    /// <summary>
    /// The selector name is the one the documentation gives, so a reader
    /// following the docs sets a variable that is actually read.
    /// </summary>
    [Fact]
    public void TheSelectorName_MatchesTheDocumentation()
    {
        Assert.Equal("VR_AGENT", SubagentRunnerFactory.SelectorEnvVar);
        Assert.Equal("firstparty", SubagentRunnerFactory.FirstPartyValue);

        var docs = File.ReadAllText(Path.Combine(RepoSetup.Root, "docs", "OPERATIONS.md"));
        Assert.Contains(SubagentRunnerFactory.SelectorEnvVar, docs, StringComparison.Ordinal);
        Assert.Contains(SubagentRunnerFactory.FirstPartyValue, docs, StringComparison.Ordinal);
    }
}
