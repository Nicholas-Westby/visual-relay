using System.Text.RegularExpressions;

namespace VisualRelay.Core.Execution;

/// <summary>
/// The failing tests a test run's output names, each as an id that is the same in another run of the
/// same suite: no duration, no line number, no assertion message. The baseline verify subtracts the
/// base run's ids from the task run's. It used to read only lines starting "Failed ", which kept
/// dotnet's duration in the id, turned PHPUnit's "Failed asserting that ..." message into an id every
/// such failure shared (so a new failure could be subtracted as pre-existing), and named nothing for
/// any other runner. Each pattern was written against real output. A break that fails no single test
/// is named too (a test file that does not load, a package or project that does not compile, a
/// package that panics first): unnamed, it hid behind whatever old failure the base still had.
/// </summary>
internal static partial class TestFailureIds
{
    /// <summary>The ids of the failing tests <paramref name="output"/> names; empty when it names none.</summary>
    public static HashSet<string> Extract(string? output)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(output)) return ids;
        // jest names a failure only within its file's block, so the block's file travels with it.
        string? jestFile = null;
        foreach (var raw in AnsiSequence().Replace(output, string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (JestFileHeader().Match(line) is { Success: true } header)
            {
                jestFile = WithProject(header, header.Groups["file"].Value);
                continue;
            }

            if (IdOf(line, jestFile) is { Length: > 0 } id)
                ids.Add(id);
        }

        return ids;
    }

    private static string? IdOf(string line, string? jestFile)
    {
        if (Jest().Match(line) is { Success: true } jest)
        {
            var name = jest.Groups["id"].Value.Trim();
            // The block of console output a test printed, not a failure.
            if (name == "Console") return null;
            return jestFile is null ? name : $"{jestFile} › {name}";
        }

        if (DotnetCompileError().Match(line) is { Success: true } compile)
        {
            // Named by file names only: the base runs in another directory, and edits move the line.
            return $"{FileNameOf(compile.Groups["project"].Value)}: {FileNameOf(compile.Groups["source"].Value)}: "
                + $"error {compile.Groups["code"].Value}: {compile.Groups["message"].Value}";
        }

        if (GoPackageBroken().Match(line) is { Success: true } broken)
            return $"{broken.Groups["package"].Value} [{broken.Groups["why"].Value}]";

        if (UnittestBlock().Match(line) is { Success: true } unittest)
        {
            // Python 3.11+ prints the whole dotted name in the parentheses; older versions only the class.
            var method = unittest.Groups["method"].Value;
            var where = unittest.Groups["where"].Value;
            return where.EndsWith("." + method, StringComparison.Ordinal) ? where : $"{where}.{method}";
        }

        if (SurefireOldForm().Match(line) is { Success: true } surefire)
            return $"{surefire.Groups["class"].Value}.{surefire.Groups["method"].Value}";

        foreach (var pattern in (Regex[])[XunitLive(), DotnetWithDuration(), DotnetBare(), TestingPlatform(), GoTest(), GoPackageFailed(),
                     CargoTest(), Pytest(), Phpunit(), SurefireNewForm(), NodeTest(), Vitest(), VitestFileFailed(),
                     RspecRerun(), TapNotOk()])
        {
            if (pattern.Match(line) is { Success: true } match)
                return WithProject(match, match.Groups["id"].Value.Trim());
        }

        return null;
    }

    private static string FileNameOf(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    // A project's name, "|runtime|" plain or " runtime " as a coloured badge, comes first: the same test runs in each project.
    private static string WithProject(Match match, string id) =>
        match.Groups["project"] is { Success: true } project ? $"[{project.Value.Trim('|')}] {id}" : id;

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

    // Microsoft.Testing.Platform: "failed Ns.Class+Nested.Test(a: 2) (1s 204ms)"
    [GeneratedRegex(@"^failed (?<id>.+?) \((?:\d+(?:\.\d+)?(?:ms|s|m|h) ?)+\)\s*$")]
    private static partial Regex TestingPlatform();

    // go test: "--- FAIL: TestName (0.00s)", indented for subtests.
    [GeneratedRegex(@"^\s*--- FAIL: (?<id>\S+) \(")]
    private static partial Regex GoTest();

    // go test's package verdict: "FAIL\tpkg\t0.008s", the only line for a package that panicked first.
    [GeneratedRegex(@"^FAIL\s+(?<id>\S+)\s+\d+(?:\.\d+)?s\s*$")]
    private static partial Regex GoPackageFailed();

    // go test: "FAIL\tpkg [build failed]" or "[setup failed]", when no test in the package ran.
    [GeneratedRegex(@"^FAIL\s+(?<package>\S+) \[(?<why>build failed|setup failed)\]\s*$")]
    private static partial Regex GoPackageBroken();

    // MSBuild: "/x/B.Tests/UnitTest1.cs(6,49): error CS0103: The name 'nope' ... [/x/B.Tests/B.Tests.csproj]"
    [GeneratedRegex(@"^\s*(?<source>.+?)\(\d+(?:,\d+)*\): error (?<code>(?:CS|FS|BC)\d+): (?<message>.+?) \[(?<project>[^\]]+)\]\s*$")]
    private static partial Regex DotnetCompileError();

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

    // jest: "  ● suite › test name", under its file's "FAIL path/x.test.js" header.
    [GeneratedRegex(@"^\s*● (?<id>.+?)\s*$")]
    private static partial Regex Jest();

    // jest's file header: "FAIL jest2/a.test.js" (" FAIL  jest2/a.test.js" once its colours are gone), with a
    // project "FAIL runtime test/a.test.js", and for a slow file "(6.2 s)" or "(6.2 s, 31 MB heap size)" after it.
    // Spaces only, so go's "FAIL\tpkg\t0.004s" is not taken for a project.
    [GeneratedRegex(@"^\s*FAIL +(?:(?<project>\S+) +)?(?<file>\S+)(?: +\(\d[^)]*\))? *$")]
    private static partial Regex JestFileHeader();

    // vitest: " FAIL  src/x.test.ts > suite > test name", with a project " FAIL  |runtime| src/x.test.ts > ..."
    [GeneratedRegex(@"^\s*FAIL\s+(?:(?<project>\S+) +)?(?<id>\S+ > .+?)\s*$")]
    private static partial Regex Vitest();

    // vitest, a file that did not load: " FAIL  broken.test.ts [ broken.test.ts ]", a project's name before the file
    [GeneratedRegex(@"^\s*FAIL\s+(?:(?<project>\S+) +)?(?<id>\S+) \[ [^\]]+ \]\s*$")]
    private static partial Regex VitestFileFailed();

    // rspec's rerun list: "rspec ./spec/x_spec.rb:12 # Group does a thing" (the line number moves with edits).
    [GeneratedRegex(@"^rspec \S+ # (?<id>.+?)\s*$")]
    private static partial Regex RspecRerun();

    // TAP: "not ok 3 - test name # time=1ms"
    [GeneratedRegex(@"^\s*not ok \d+ (?:- )?(?<id>.+?)(?:\s+#.*)?\s*$")]
    private static partial Regex TapNotOk();
}
