using VisualRelay.Core.Agent;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Covers the one place a stage's agent is built.
/// <para>
/// This used to choose between the Swival subprocess and the in-process loop on
/// a <c>VR_AGENT</c> environment variable. The subprocess is gone, so the
/// selector is gone with it and there is exactly one answer. What still matters
/// is that every call site funnels through here, which is what made the cutover
/// one decision rather than eight.
/// </para>
/// </summary>
public sealed class SubagentRunnerFactoryTests
{
    /// <summary>The factory builds the in-process loop.</summary>
    [Fact]
    public void TheFactory_BuildsTheInProcessLoop()
    {
        var runner = SubagentRunnerFactory.Create(
            RelayConfigLoader.Defaults(), new InMemoryRelayEventSink(),
            new DictionaryEnvironmentAccessor());

        Assert.IsType<FirstPartySubagentRunner>(runner);
    }

    /// <summary>
    /// It builds the same agent whatever the environment says. A leftover
    /// <c>VR_AGENT</c> in someone's shell must not select anything, because
    /// there is nothing else to select.
    /// </summary>
    /// <param name="selector">A value the old selector would have honoured.</param>
    [Theory]
    [InlineData("firstparty")]
    [InlineData("swival")]
    [InlineData("")]
    public void TheEnvironment_NoLongerSelectsAnything(string selector)
    {
        var env = new DictionaryEnvironmentAccessor { ["VR_AGENT"] = selector };

        var runner = SubagentRunnerFactory.Create(
            RelayConfigLoader.Defaults(), new InMemoryRelayEventSink(), env);

        Assert.IsType<FirstPartySubagentRunner>(runner);
    }

    /// <summary>
    /// The runner it builds satisfies the seam the driver is written against,
    /// which is what roughly eighty stage-level doubles depend on.
    /// </summary>
    [Fact]
    public void WhatItBuilds_SatisfiesTheDriverSeam()
    {
        Assert.IsAssignableFrom<ISubagentRunner>(
            SubagentRunnerFactory.Create(
                RelayConfigLoader.Defaults(), new InMemoryRelayEventSink(),
                new DictionaryEnvironmentAccessor()));
    }
}
