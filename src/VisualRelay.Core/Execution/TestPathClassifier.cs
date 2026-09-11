using System.Text.RegularExpressions;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Classifies paths as test-related or runnable test files using
/// repo-agnostic heuristics and optional repo-specific glob overrides.
/// Replaces the old 4-rule private <c>IsTestFile</c> heuristic.
/// </summary>
public static class TestPathClassifier
{
    /// <summary>Extensions that classify as non-code (docs/config/fixtures).</summary>
    internal static readonly HashSet<string> NonCodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".md", ".txt", ".json", ".yaml", ".yml", ".toml", ".csv" };

    private static readonly HashSet<string> ExactDirNames = new(StringComparer.OrdinalIgnoreCase)
    { "test", "tests", "spec", "specs", "t", "__tests__", "unittests",
      "testdata", "test_suite", "testfixtures", "htesting",
      "e2e", "integration", "acceptance", "cypress", "playwright",
      "fixtures", "__fixtures__", "__mocks__", "__snapshots__",
      "molecule", "testbench", "step_definitions" };

    private static readonly HashSet<string> PascalTestExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".java", ".kt", ".cs", ".php", ".swift" };

    /// <summary>Extensions that classify a file as a test regardless of its name.</summary>
    private static readonly HashSet<string> AlwaysTestExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".t", ".bats", ".feature", ".robot", ".plt", ".pf" };

    /// <summary>
    /// camelCase test source-set directories (Gradle/Android: <c>androidTest</c>,
    /// <c>commonTest</c>, <c>jvmTest</c>, <c>integrationTest</c>…). Ordinal
    /// (case-sensitive) by design: the capital <c>T</c> is the only thing telling
    /// "commonTest" apart from an unrelated word that merely ends in the letters
    /// "test", like "contest" or "latest".
    /// </summary>
    private static readonly Regex CamelCaseTestDirectory =
        new("^[a-z][A-Za-z0-9]*Tests?$", RegexOptions.Compiled);

    /// <summary>
    /// Returns true when <paramref name="path"/> is test-related, either
    /// through built-in heuristics or configured <paramref name="testPaths"/>
    /// globs. Use for impl-exclusion gates (TDD, code-change detection)
    /// where fixture non-code files still count as test-related.
    /// </summary>
    public static bool IsTestRelated(string path, IReadOnlyList<string>? testPaths)
    {
        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/');

        foreach (var segment in segments)
        {
            if (IsTestDirectorySegment(segment))
                return true;
        }

        if (IsTestFileName(segments[^1]))
            return true;

        if (testPaths is { Count: > 0 })
        {
            foreach (var glob in testPaths)
            {
                if (RelayDriver.MatchesGuardGlob(normalized, glob))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when <paramref name="path"/> is test-related AND has a
    /// runnable code extension (i.e. not in <see cref="NonCodeExtensions"/>
    /// and not extensionless). Use for <c>{files}</c> expansion in
    /// <c>testFileCmd</c> — fixture data must not be handed to the test runner.
    /// </summary>
    public static bool IsRunnableTestFile(string path, IReadOnlyList<string>? testPaths)
    {
        if (!IsTestRelated(path, testPaths))
            return false;
        var ext = Path.GetExtension(path);
        return ext.Length > 0 && !NonCodeExtensions.Contains(ext);
    }

    private static bool IsTestDirectorySegment(string segment)
    {
        if (ExactDirNames.Contains(segment))
            return true;

        if (segment.StartsWith("test-", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("tests-", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("test_", StringComparison.OrdinalIgnoreCase))
            return true;

        if (segment.EndsWith("-test", StringComparison.OrdinalIgnoreCase) ||
            segment.EndsWith("-tests", StringComparison.OrdinalIgnoreCase) ||
            segment.EndsWith("_test", StringComparison.OrdinalIgnoreCase) ||
            segment.EndsWith("_tests", StringComparison.OrdinalIgnoreCase) ||
            segment.EndsWith(".tests", StringComparison.OrdinalIgnoreCase) ||
            segment.EndsWith(".test", StringComparison.OrdinalIgnoreCase) ||
            segment.EndsWith(".unittests", StringComparison.OrdinalIgnoreCase) ||
            segment.EndsWith(".integrationtests", StringComparison.OrdinalIgnoreCase) ||
            segment.EndsWith(".specs", StringComparison.OrdinalIgnoreCase))
            return true;

        // camelCase source-set convention (Gradle/Android), Ordinal: see
        // CamelCaseTestDirectory's doc comment for why case sensitivity matters.
        return CamelCaseTestDirectory.IsMatch(segment);
    }

    private static bool IsTestFileName(string fileName)
    {
        // Extensions that are always a test, regardless of the rest of the name.
        var ext = Path.GetExtension(fileName);
        if (AlwaysTestExtensions.Contains(ext))
            return true;

        // Infix rules (on full filename including extension)
        if (fileName.Contains(".tests.", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains(".spec.", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains(".test.", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains(".t.", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains(".tftest.", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains(".tofutest.", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains(".testclasses.", StringComparison.OrdinalIgnoreCase))
            return true;

        var stem = Path.GetFileNameWithoutExtension(fileName);

        // Suffix rules (on name without extension)
        if (stem.EndsWith("_test", StringComparison.OrdinalIgnoreCase) ||
            stem.EndsWith("-test", StringComparison.OrdinalIgnoreCase) ||
            stem.EndsWith("_tests", StringComparison.OrdinalIgnoreCase) ||
            stem.EndsWith("-tests", StringComparison.OrdinalIgnoreCase) ||
            stem.EndsWith("_spec", StringComparison.OrdinalIgnoreCase) ||
            stem.EndsWith("-spec", StringComparison.OrdinalIgnoreCase) ||
            stem.EndsWith("_specs", StringComparison.OrdinalIgnoreCase) ||
            stem.EndsWith("-specs", StringComparison.OrdinalIgnoreCase) ||
            stem.EndsWith("_suite", StringComparison.OrdinalIgnoreCase))
            return true;

        // Prefix rules (on name without extension)
        if (stem.StartsWith("test_", StringComparison.OrdinalIgnoreCase) ||
            stem.StartsWith("test-", StringComparison.OrdinalIgnoreCase) ||
            stem.StartsWith("tst_", StringComparison.OrdinalIgnoreCase))
            return true;

        // PascalCase …Test / …Tests / …Spec — only for known xUnit/JUnit/PHPUnit/XCTest extensions
        if (PascalTestExtensions.Contains(ext) &&
            (stem.EndsWith("Test", StringComparison.OrdinalIgnoreCase) ||
             stem.EndsWith("Tests", StringComparison.OrdinalIgnoreCase) ||
             stem.EndsWith("Spec", StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }
}
