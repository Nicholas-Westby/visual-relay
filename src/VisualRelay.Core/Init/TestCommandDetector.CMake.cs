using System.Text.RegularExpressions;

namespace VisualRelay.Core.Init;

public static partial class TestCommandDetector
{
    /// <summary>
    /// The <c>-D&lt;option&gt;=ON</c> arguments that switch on the test options the top-level
    /// CMakeLists.txt declares (BUILD_TESTING, FOO_BUILD_TESTS, FOO_ENABLE_TESTING, ...), each with a
    /// leading space; empty when it declares none. Measured with odygrd/quill: its suite only builds
    /// with QUILL_BUILD_TESTS=ON, and without it ctest found no tests and still exited 0.
    /// </summary>
    private static string CMakeTestOptions(string rootPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(Path.Combine(rootPath, "CMakeLists.txt"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }

        return string.Concat(TestOption().Matches(text)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .Select(name => $" -D{name}=ON"));
    }

    // An option whose name ends in the test switch itself; "EXTENSIVE_TESTS" and the like stay off.
    [GeneratedRegex(@"option\(\s*(?<name>\w*?(?:BUILD_?TESTS?|BUILD_?TESTING|ENABLE_TESTS?|ENABLE_TESTING|WITH_TESTS?))\s",
        RegexOptions.IgnoreCase)]
    private static partial Regex TestOption();
}
