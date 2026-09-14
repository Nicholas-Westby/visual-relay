namespace VisualRelay.Tests;

/// <summary>
/// A run can fail with no failing test to name: a test file that no longer loads, a package or
/// project that no longer compiles, a package that panics before its tests start. Unnamed, such a
/// break was invisible to the baseline subtraction, so a task that caused one while an old failure
/// remained looked as if it had only the base's failures, and committed. jest's "Test suite failed
/// to run" was worse: the same text for every file, so an old broken file hid a new one. Every
/// sample is real output from the Mac (jest 29, vitest 3.2, go 1.26, dotnet 10), paths shortened.
/// </summary>
public sealed partial class TestFailureIdsTests
{
    [Fact]
    public void Extract_Jest_NamesEachFailureWithItsFile()
    {
        Assert.Equal(
            ["jest2/a.test.js › sum › adds wrongly", "jest2/b.test.js › sum › adds wrongly",
             "jest2/broken.test.js › Test suite failed to run", "jest2/broken2.test.js › Test suite failed to run"],
            Ids("FAIL jest2/broken2.test.js\n"
                + "  ● Test suite failed to run\n\n"
                + "    Cannot find module './does-not-exist' from 'broken2.test.js'\n\n"
                + "FAIL jest2/b.test.js\n"
                + "  ● sum › adds wrongly\n\n"
                + "    expect(received).toBe(expected) // Object.is equality\n\n"
                + "FAIL jest2/a.test.js\n"
                + "  ● Console\n\n"
                + "    console.log\n"
                + "      hello from a\n\n"
                + "  ● sum › adds wrongly\n\n"
                + "FAIL jest2/broken.test.js\n"
                + "  ● Test suite failed to run\n\n"
                + "    Jest encountered an unexpected token\n\n"
                + "     • If you are trying to use ECMAScript Modules, see https://jestjs.io/docs/ecmascript-modules for how to enable it.\n\n"
                + "Test Suites: 4 failed, 4 total\n"
                + "Tests:       2 failed, 1 passed, 3 total\n"));
    }

    [Fact]
    public void Extract_ColouredJest_ReadsTheFileThroughTheEscapes()
    {
        var esc = (char)27;
        Assert.Equal(["jest2/a.test.js › sum › adds wrongly"], Ids(
            $"{esc}[0m{esc}[7m{esc}[1m{esc}[31m FAIL {esc}[39m{esc}[22m{esc}[27m{esc}[0m {esc}[2mjest2/{esc}[22m{esc}[1ma.test.js{esc}[22m\n"
            + $"{esc}[1m{esc}[31m  {esc}[1m● {esc}[22m{esc}[1msum › adds wrongly{esc}[39m{esc}[22m\n"));
    }

    [Fact]
    public void Extract_Vitest_NamesAFileThatFailedToLoad()
    {
        Assert.Equal(["a.test.ts > sum > adds wrongly", "broken.test.ts"], Ids(
            " FAIL  broken.test.ts [ broken.test.ts ]\n"
            + "Error: Cannot find module './does-not-exist' imported from '/Users/admin/vitest2/broken.test.ts'\n"
            + " FAIL  a.test.ts > sum > adds wrongly\n"
            + "AssertionError: expected 3 to be 4 // Object.is equality\n"));
    }

    [Fact]
    public void Extract_Go_NamesAPackageThatFailedToBuildToSetUpOrToStart()
    {
        Assert.Equal(
            ["TestAddWrongly", "example.com/vrids/a", "example.com/vrids/b [build failed]",
             "example.com/vrids/c [setup failed]", "example.com/vrids/d"],
            Ids("# example.com/vrids/c\n"
                + "c/c.go:3:8: no required module provides package example.com/vrids/missing; to add it:\n"
                + "\tgo get example.com/vrids/missing\n"
                + "FAIL\texample.com/vrids/c [setup failed]\n"
                + "# example.com/vrids/b [example.com/vrids/b.test]\n"
                + "b/b_test.go:6:18: undefined: nope\n"
                + "--- FAIL: TestAddWrongly (0.00s)\n"
                + "    a_test.go:7: got 3\n"
                + "FAIL\n"
                + "FAIL\texample.com/vrids/a\t0.004s\n"
                + "FAIL\texample.com/vrids/b [build failed]\n"
                + "panic: boom in init\n\n"
                + "goroutine 1 [running]:\n"
                + "FAIL\texample.com/vrids/d\t0.008s\n"
                + "FAIL\n"));
        Assert.Empty(Ids("ok  \texample.com/vrids/b\t0.004s\n"));
    }

    [Fact]
    public void Extract_DotnetCompileError_NamesTheProjectFileAndErrorButNotWhereItSits()
    {
        const string error = "/Users/admin/ids/B.Tests/UnitTest1.cs(6,49): error CS0103: The name 'nope' does not exist in the current context [/Users/admin/ids/B.Tests/B.Tests.csproj]\n";

        Assert.Equal(["B.Tests.csproj: UnitTest1.cs: error CS0103: The name 'nope' does not exist in the current context"], Ids(error));
        // The base runs in another snapshot directory, and an edit above the error moves its line.
        Assert.Equal(Ids(error), Ids(error.Replace("/Users/admin/ids", "/tmp/vr-base-1a2b").Replace("(6,49)", "(9,49)")));
        Assert.Empty(Ids("/Users/admin/ids/B.Tests/UnitTest1.cs(5,20): warning CS0169: The field 'UnitTest1.field' is never used [/Users/admin/ids/B.Tests/B.Tests.csproj]\n"));
    }
}
