using System.Text.RegularExpressions;

namespace VisualRelay.Core.Execution;

/// <summary>
/// The failing tests a test run's output names, each as an id that is the same in another run of the
/// same suite: no duration, no line number, no assertion message. The baseline verify subtracts the
/// base run's ids from the task run's. It used to read only lines starting "Failed ", which kept
/// dotnet's duration in the id, turned PHPUnit's "Failed asserting that ..." message into an id every
/// such failure shared (so a new failure could be subtracted as pre-existing), and named nothing for
/// any other runner. Each pattern was written against real output.
/// </summary>
internal static partial class TestFailureIds
{
    /// <summary>The ids of the failing tests <paramref name="output"/> names; empty when it names none.</summary>
    public static HashSet<string> Extract(string? output)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(output)) return ids;
        foreach (var line in AnsiSequence().Replace(output, string.Empty).Split('\n'))
        {
            if (IdOf(line.TrimEnd('\r')) is { Length: > 0 } id)
                ids.Add(id);
        }

        return ids;
    }

    private static string? IdOf(string line)
    {
        if (UnittestBlock().Match(line) is { Success: true } unittest)
        {
            // Python 3.11+ prints the whole dotted name in the parentheses; older versions only the class.
            var method = unittest.Groups["method"].Value;
            var where = unittest.Groups["where"].Value;
            return where.EndsWith("." + method, StringComparison.Ordinal) ? where : $"{where}.{method}";
        }

        if (SurefireOldForm().Match(line) is { Success: true } surefire)
            return $"{surefire.Groups["class"].Value}.{surefire.Groups["method"].Value}";

        foreach (var pattern in (Regex[])[XunitLive(), DotnetWithDuration(), DotnetBare(), GoTest(), CargoTest(),
                     Pytest(), Phpunit(), SurefireNewForm(), NodeTest(), Jest(), Vitest(), RspecRerun(), TapNotOk()])
        {
            if (pattern.Match(line) is { Success: true } match)
                return match.Groups["id"].Value.Trim();
        }

        return null;
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex AnsiSequence();

    // xUnit's live line: [xUnit.net 00:00:09.70]     Ns.Class.Test [FAIL]
    [GeneratedRegex(@"^\[xUnit\.net [^\]]*\]\s+(?<id>.+?) \[FAIL\]\s*$")]
    private static partial Regex XunitLive();

    // dotnet test's summary line: "  Failed Ns.Class.Test(x: 1) [4 ms]"
    [GeneratedRegex(@"^\s*Failed (?<id>.+?) \[[^\]]*\]\s*$")]
    private static partial Regex DotnetWithDuration();

    // A bare "Failed Name" of one token; PHPUnit's "Failed asserting that ..." is a message, not a test.
    [GeneratedRegex(@"^\s*Failed (?<id>[^\s\[]+)\s*$")]
    private static partial Regex DotnetBare();

    // go test: "--- FAIL: TestName (0.00s)", indented for subtests.
    [GeneratedRegex(@"^\s*--- FAIL: (?<id>\S+) \(")]
    private static partial Regex GoTest();

    // cargo test: "test tests::name ... FAILED"
    [GeneratedRegex(@"^test (?<id>\S+) \.\.\. FAILED\s*$")]
    private static partial Regex CargoTest();

    // pytest's summary: "FAILED path::test[a b] - message" (a parameter may hold spaces).
    [GeneratedRegex(@"^(?:FAILED|ERROR) (?<id>[^\s(].*?)(?: - .*)?\s*$")]
    private static partial Regex Pytest();

    // unittest: "FAIL: test_x (pkg.Class.test_x)" or, before 3.11, "ERROR: test_x (pkg.Class)"
    [GeneratedRegex(@"^(?:FAIL|ERROR): (?<method>\S+) \((?<where>[^)\s]+)\)\s*$")]
    private static partial Regex UnittestBlock();

    // PHPUnit: "1) Ns\Class::testName with data set #0 ('args', ...)" (the arguments are dropped).
    [GeneratedRegex(@"^\d+\) (?<id>[\w\\]+::\w+(?: with data set (?:#\d+|""[^""]*""))?)")]
    private static partial Regex Phpunit();

    // Surefire: "[ERROR] org.x.ClassTest.testName -- Time elapsed: 0.001 s <<< ERROR!"
    [GeneratedRegex(@"^\[ERROR\] (?<id>[\w.$]+) -- Time elapsed: .*<<< (?:FAILURE|ERROR)!")]
    private static partial Regex SurefireNewForm();

    // Older Surefire: "[ERROR] testName(org.x.ClassTest)  Time elapsed: 0.001 s  <<< FAILURE!"
    [GeneratedRegex(@"^\[ERROR\] (?<method>\w+)\((?<class>[\w.$]+)\)\s+Time elapsed:.*<<< (?:FAILURE|ERROR)!")]
    private static partial Regex SurefireOldForm();

    // node --test: "✖ test name (0.56ms)"
    [GeneratedRegex(@"^\s*✖ (?<id>.+?) \(\d+(?:\.\d+)?m?s\)\s*$")]
    private static partial Regex NodeTest();

    // jest: "  ● suite › test name"
    [GeneratedRegex(@"^\s*● (?<id>.+?)\s*$")]
    private static partial Regex Jest();

    // vitest: " FAIL  src/x.test.ts > suite > test name"
    [GeneratedRegex(@"^\s*FAIL\s+(?<id>\S+ > .+?)\s*$")]
    private static partial Regex Vitest();

    // rspec's rerun list: "rspec ./spec/x_spec.rb:12 # Group does a thing" (the line number moves with edits).
    [GeneratedRegex(@"^rspec \S+ # (?<id>.+?)\s*$")]
    private static partial Regex RspecRerun();

    // TAP: "not ok 3 - test name # time=1ms"
    [GeneratedRegex(@"^\s*not ok \d+ (?:- )?(?<id>.+?)(?:\s+#.*)?\s*$")]
    private static partial Regex TapNotOk();
}
