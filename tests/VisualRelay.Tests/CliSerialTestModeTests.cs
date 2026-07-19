using VisualRelay.Cli;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for the serial test mode: <c>./visual-relay test serial [Filter]</c>.
/// The builder appends <c>-- xUnit.ParallelizeTestCollections=false</c> without
/// changing the default (non-serial) argument shape.
/// </summary>
public sealed class CliSerialTestModeTests
{
    private const string FakeProject = "/fake/tests.csproj";
    private const string FakeStem = "20260719T000000_host_9999";
    private const string FakeResultsDir = "/tmp/logs";

    [Fact]
    public void SerialLeadingToken_AppendsRunSettingsAndNoSerialFilter()
    {
        var args = TestRunner.BuildTestArgs(FakeProject, ["serial"], FakeStem, FakeResultsDir, noBuild: false, out var isSerial);

        Assert.True(isSerial);
        // Must end with the RunSettings tail.
        Assert.EndsWith("xUnit.ParallelizeTestCollections=false", args[^1]);
        Assert.Equal("--", args[^2]);
        // Must NOT contain "serial" as a filter value.
        Assert.DoesNotContain("FullyQualifiedName~serial", args);
    }

    [Fact]
    public void SerialWithFilter_IncludesBothRunSettingsAndFilter()
    {
        var args = TestRunner.BuildTestArgs(FakeProject, ["serial", "GitCommitter"], FakeStem, FakeResultsDir, noBuild: false, out var isSerial);

        Assert.True(isSerial);
        Assert.Contains("--filter", args);
        Assert.Contains("FullyQualifiedName~GitCommitter", args);
        Assert.EndsWith("xUnit.ParallelizeTestCollections=false", args[^1]);
        Assert.Equal("--", args[^2]);
    }

    [Fact]
    public void FilterAlone_ProducesIdenticalArgs()
    {
        var args = TestRunner.BuildTestArgs(FakeProject, ["GitCommitter"], FakeStem, FakeResultsDir, noBuild: false, out var isSerial);

        Assert.False(isSerial);
        var expected = new[]
        {
            "test", FakeProject,
            "-m:1", "-p:UseSharedCompilation=false",
            "--logger", "console;verbosity=normal",
            "--logger", $"trx;LogFileName={FakeStem}.trx",
            "--results-directory", FakeResultsDir,
            "--filter", "FullyQualifiedName~GitCommitter",
        };
        Assert.Equal(expected, args);
    }

    [Fact]
    public void SerialTimeout_DefaultsTo1800s_WhenEnvUnset()
    {
        // Use the pure Resolve seam so the test is isolated from process-global env state.
        var timeout = WatchdogTimeouts.Resolve(rawValue: null, defaultSecs: 1800);
        Assert.Equal(TimeSpan.FromSeconds(1800), timeout);
    }

    [Fact]
    public void SerialTimeout_EnvWins_WhenSet()
    {
        var timeout = WatchdogTimeouts.Resolve(rawValue: "90", defaultSecs: 1800);
        Assert.Equal(TimeSpan.FromSeconds(90), timeout);
    }
}
