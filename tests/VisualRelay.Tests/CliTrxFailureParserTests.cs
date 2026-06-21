using VisualRelay.Cli;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for the TRX failed-test extraction that moved out of
/// <c>test.sh</c>'s grep/sed pipeline into a tested C# parser. On a failing run
/// the CLI prints the de-duplicated, sorted set of failed <c>testName</c>s.
/// </summary>
public sealed class CliTrxFailureParserTests
{
    private const string SampleTrx = """
        <?xml version="1.0" encoding="UTF-8"?>
        <TestRun>
          <Results>
            <UnitTestResult testName="VisualRelay.Tests.Foo.Bravo" outcome="Passed" />
            <UnitTestResult testName="VisualRelay.Tests.Foo.Alpha" outcome="Failed" />
            <UnitTestResult testName="VisualRelay.Tests.Foo.Charlie" outcome="Failed" />
            <UnitTestResult testName="VisualRelay.Tests.Foo.Alpha" outcome="Failed" />
          </Results>
        </TestRun>
        """;

    [Fact]
    public void ExtractsFailedTestNames_SortedAndDeduplicated()
    {
        var failed = TrxFailureParser.ExtractFailedTestNames(SampleTrx);
        Assert.Equal(
            new[] { "VisualRelay.Tests.Foo.Alpha", "VisualRelay.Tests.Foo.Charlie" },
            failed);
    }

    [Fact]
    public void IgnoresPassedResults()
    {
        var failed = TrxFailureParser.ExtractFailedTestNames(SampleTrx);
        Assert.DoesNotContain("VisualRelay.Tests.Foo.Bravo", failed);
    }

    [Fact]
    public void ReturnsEmpty_WhenNoFailures()
    {
        const string allPass = """
            <TestRun><Results>
              <UnitTestResult testName="A" outcome="Passed" />
            </Results></TestRun>
            """;
        Assert.Empty(TrxFailureParser.ExtractFailedTestNames(allPass));
    }

    [Fact]
    public void ReturnsEmpty_OnGarbageInput()
    {
        Assert.Empty(TrxFailureParser.ExtractFailedTestNames("not xml at all"));
    }
}
