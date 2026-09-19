using VisualRelay.Cli.Gates;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// The launch's offer to run <c>setup-wsl</c> when the WSL gate fails for something setup can
/// finish. It follows the rule every Visual Relay install follows: ask y/N with no as the
/// default, act only on an explicit yes, and never ask when nobody is at the terminal.
/// </summary>
public sealed class WslSetupOfferTests
{
    private readonly StringWriter _output = new();
    private readonly List<string> _calls = [];

    /// <summary>What the probe after the setup finds.</summary>
    private readonly WslProbe _ready = WslProbeFixtures.Usable();

    [Fact]
    public async Task WithNobodyAtTheTerminal_NothingIsAsked()
    {
        var result = await OfferAsync(WslProbeFixtures.NonoMissing(), answer: "y", interactive: false);

        Assert.Null(result);
        Assert.Equal("", _output.ToString());
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task WhatSetupCannotFinish_IsNeverOffered()
    {
        var result = await OfferAsync(WslProbeFixtures.Wsl1(), answer: "y");

        Assert.Null(result);
        Assert.Equal("", _output.ToString());
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task TheQuestion_NamesTheProblemAndEveryStep_BeforeAsking()
    {
        await OfferAsync(WslProbeFixtures.NonoMissing(), answer: "");

        var asked = _output.ToString();
        Assert.StartsWith("visual-relay: nono was not found inside the WSL distro 'Ubuntu'.", asked);
        foreach (var step in WslSetupPlan.For(WslProbeFixtures.NonoMissing(), "alice").Steps)
            Assert.Contains(step.Description, asked);
        Assert.EndsWith("Set it up now? [y/N] ", asked);
    }

    [Theory]
    [InlineData("")]
    [InlineData("n")]
    [InlineData("no")]
    [InlineData("sure")]
    [InlineData(null)]
    public async Task AnythingButAnExplicitYes_RunsNothing(string? answer)
    {
        var result = await OfferAsync(WslProbeFixtures.NonoMissing(), answer);

        Assert.Null(result);
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData("y")]
    [InlineData("Y")]
    [InlineData(" yes ")]
    public async Task AnExplicitYes_RunsTheSetup_AndHandsBackTheUsableProbe(string answer)
    {
        var result = await OfferAsync(WslProbeFixtures.NonoMissing(), answer);

        Assert.NotEmpty(_calls);
        Assert.Equal(0, result?.ExitCode);
        Assert.Same(_ready, result?.Ready);
    }

    [Fact]
    public async Task ASetupThatFails_HandsBackItsExitCodeAndNoProbe()
    {
        var result = await OfferAsync(WslProbeFixtures.NonoMissing(), "y", failing: WslSetupScripts.Nono);

        Assert.Equal(1, result?.ExitCode);
        Assert.Null(result?.Ready);
    }

    private Task<(int ExitCode, WslProbe? Ready)?> OfferAsync(
        WslProbe probe, string? answer, bool interactive = true, string? failing = null)
    {
        var host = new WslSetupHost(
            _ => Task.FromResult(_ready),
            (argv, _) =>
            {
                _calls.Add(string.Join(' ', argv));
                return Task.FromResult(failing is not null && argv.Contains(failing) ? (1, "failed\n") : (0, ""));
            },
            "alice", LocalNonoDeb: null);
        return WslSetupOffer.OfferAsync(probe, host, interactive, new StringReader(answer is null ? "" : answer + "\n"),
            _output, CancellationToken.None);
    }
}
