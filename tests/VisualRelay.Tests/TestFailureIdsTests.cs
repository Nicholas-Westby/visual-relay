using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// <see cref="TestFailureIds"/> names each failing test the same way in any run of the suite. Every
/// sample is a runner's real output: dotnet, go, PHPUnit and Surefire from the Windows arm's eval
/// repositories (LiteDB, gorilla/mux, FreshRSS, commons-lang), unittest from its distro, and pytest,
/// cargo, node:test and rspec from small suites run on the Mac.
/// </summary>
public sealed class TestFailureIdsTests
{
    private static string[] Ids(string output) => [.. TestFailureIds.Extract(output).Order(StringComparer.Ordinal)];

    [Fact]
    public void Extract_Dotnet_NamesTheTestWithoutItsDuration()
    {
        Assert.Equal(["LiteDB.Tests.BaselineCanaryTests.PreExistingFailure_IsOnTheBase"], Ids(
            "[xUnit.net 00:00:09.70]     LiteDB.Tests.BaselineCanaryTests.PreExistingFailure_IsOnTheBase [FAIL]\n"
            + "  Failed LiteDB.Tests.BaselineCanaryTests.PreExistingFailure_IsOnTheBase [< 1 ms]\n"
            + "Failed!  - Failed:     1, Passed:   865, Skipped:     7, Total:   873, Duration: 31 s - LiteDB.Tests.dll (net10.0)\n"));
        Assert.Equal(Ids("  Failed LiteDB.Tests.Engine.Index_Tests.Index_With_Like_Trailing_Underscore [6 ms]\n"),
            Ids("  Failed LiteDB.Tests.Engine.Index_Tests.Index_With_Like_Trailing_Underscore [4 ms]\n"));
        Assert.Equal(["Demo.Tests.Parse(input: \"a b\")"], Ids("  Failed Demo.Tests.Parse(input: \"a b\") [2 ms]\n"));
    }

    [Fact]
    public void Extract_ColouredOutput_ReadsThroughTheEscapes()
    {
        var esc = (char)27;
        Assert.Equal(["Demo.Tests.Colour"], Ids($"{esc}[31m  Failed Demo.Tests.Colour [3 ms]{esc}[0m\n"));
    }

    [Fact]
    public void Extract_Phpunit_NamesTheTestAndDataSetButNeverTheAssertionMessage()
    {
        Assert.Equal(
            ["LibRssTest::testEscapeToUnicodeAlternative with data set #0", "LibRssTest::testEscapeToUnicodeAlternative with data set #1"],
            Ids("There were 2 failures:\n"
                + "1) LibRssTest::testEscapeToUnicodeAlternative with data set #0 ('It's \"Q&A\" ^_^', true, 'It’s ＂Q＆A＂ ＾_＾')\n"
                + "Failed asserting that two strings are identical.\n"
                + "2) LibRssTest::testEscapeToUnicodeAlternative with data set #1 ('&quot;x&quot; &#039;y&#039;', true, '＂x＂ ’y’')\n"
                + "Tests: 739, Assertions: 1398, Failures: 2.\n"));
    }

    [Fact]
    public void Extract_Go_NamesTestsAndSubtestsButNotThePackageLine()
    {
        Assert.Equal(
            ["TestMethodsKeepsCallerSlice", "TestMethodsKeepsCallerSlice/caller_slice_is_left_as_passed",
             "TestMethodsKeepsCallerSlice/caller_slice_with_spare_capacity_is_left_as_passed", "TestSchemesKeepsCallerSlice"],
            Ids("--- FAIL: TestMethodsKeepsCallerSlice (0.00s)\n"
                + "    --- FAIL: TestMethodsKeepsCallerSlice/caller_slice_is_left_as_passed (0.00s)\n"
                + "    --- FAIL: TestMethodsKeepsCallerSlice/caller_slice_with_spare_capacity_is_left_as_passed (0.00s)\n"
                + "--- FAIL: TestSchemesKeepsCallerSlice (0.00s)\nFAIL\nFAIL\tgithub.com/gorilla/mux\t0.023s\n"));
    }

    [Theory]
    [InlineData("[ERROR] org.apache.commons.lang3.StringUtilsTest.testJoin_ArrayString_NullDelimiter -- Time elapsed: 0.001 s <<< ERROR!")]
    [InlineData("[ERROR] testJoin_ArrayString_NullDelimiter(org.apache.commons.lang3.StringUtilsTest)  Time elapsed: 0.001 s  <<< FAILURE!")]
    public void Extract_Surefire_NamesClassAndMethodInEitherForm(string line)
    {
        Assert.Equal(["org.apache.commons.lang3.StringUtilsTest.testJoin_ArrayString_NullDelimiter"], Ids(
            "[ERROR] Tests run: 175, Failures: 0, Errors: 1, Skipped: 1, Time elapsed: 0.108 s <<< FAILURE! -- in org.apache.commons.lang3.StringUtilsTest\n"
            + line + "\n[ERROR] Errors: \n"
            + "[ERROR]   StringUtilsTest.testJoin_ArrayString_NullDelimiter:1150 » NullPointer The delimiter must not be null\n"));
    }

    [Fact]
    public void Extract_Pytest_StopsAtTheMessageButKeepsSpacesInsideParameters()
    {
        Assert.Equal(
            ["test_sample.py::TestGroup::test_method", "test_sample.py::test_fails_on_purpose",
             "test_sample.py::test_param[a b]", "test_sample.py::test_param[c-d]"],
            Ids("FAILED test_sample.py::test_fails_on_purpose - assert (1 + 1) == 3\n"
                + "FAILED test_sample.py::test_param[a b] - AssertionError: assert 'a b' == 'zz'\n"
                + "FAILED test_sample.py::test_param[c-d] - AssertionError: assert 'c-d' == 'zz'\n"
                + "FAILED test_sample.py::TestGroup::test_method - ValueError: boom\n"));
    }

    [Fact]
    public void Extract_Unittest_NamesTheDottedTestInBothPythonForms()
    {
        Assert.Equal(["test_sample.SampleTest.test_errors", "test_sample.SampleTest.test_fails_on_purpose"], Ids(
            "test_fails_on_purpose (test_sample.SampleTest.test_fails_on_purpose) ... FAIL\n"
            + "======================================================================\n"
            + "FAIL: test_fails_on_purpose (test_sample.SampleTest.test_fails_on_purpose)\n"
            + "ERROR: test_errors (test_sample.SampleTest)\n"
            + "----------------------------------------------------------------------\n"
            + "FAILED (failures=1, errors=1)\n"));
    }

    [Fact]
    public void Extract_CargoNodeRspecJestVitestAndTap_NameTheirTests()
    {
        Assert.Equal(["tests::adds_wrongly_on_purpose", "tests::nested::deep_fail"], Ids(
            "test tests::adds ... ok\ntest tests::nested::deep_fail ... FAILED\n"
            + "test tests::adds_wrongly_on_purpose ... FAILED\n\nfailures:\n    tests::nested::deep_fail\n"
            + "test result: FAILED. 1 passed; 2 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.00s\n"));
        Assert.Equal(["fails on purpose", "group", "nested fails"], Ids(
            "✔ passes (0.686084ms)\n✖ fails on purpose (0.559333ms)\n▶ group\n  ✖ nested fails (3.423834ms)\n"
            + "✖ group (3.539167ms)\n\n✖ failing tests:\n\ntest at sample.test.mjs:4:1\n✖ fails on purpose (0.559333ms)\n"));
        Assert.Equal(["Sample fails on purpose", "Sample nested raises"], Ids(
            "rspec ./spec/sample_spec.rb:5 # Sample fails on purpose\nrspec ./spec/sample_spec.rb:9 # Sample nested raises\n"));
        Assert.Equal(["sum › adds wrongly"], Ids(" FAIL  src/sum.test.js\n  ● sum › adds wrongly\n"));
        Assert.Equal(["src/sum.test.ts > sum > adds wrongly"], Ids(" FAIL  src/sum.test.ts > sum > adds wrongly\n"));
        Assert.Equal(["adds wrongly"], Ids("ok 1 - adds\nnot ok 2 - adds wrongly # time=1.2ms\n"));
    }

    [Fact]
    public void Extract_OutputThatNamesNoTest_IsEmpty()
    {
        Assert.Empty(Ids("error: something went wrong\nFailed asserting that false is true.\nnpm error code 1\n"));
    }
}
