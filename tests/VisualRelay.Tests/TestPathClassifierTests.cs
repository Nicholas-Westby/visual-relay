using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Full test matrix for <see cref="TestPathClassifier"/> covering the 40-repo
/// survey conventions, backward-compatibility with the old 4-rule heuristic,
/// config-glob overrides, and runnable-vs-non-runnable filtering.
/// </summary>
public sealed class TestPathClassifierTests
{
    // ── IsTestRelated: survey positives (one per convention row) ─────────

    [Theory]
    [InlineData("test/foo.c")]                       // test/ top-level (node, express, pytorch, go, k8s, llvm, etc.)
    [InlineData("spec/user_spec.rb")]                // spec/ top-level (RSpec: mastodon, discourse)
    [InlineData("t/t0001-init.sh")]                  // t/ top-level (git)
    [InlineData("Tests/AlamofireTests.swift")]       // Tests/ capitalized (Alamofire)
    [InlineData("src/test/java/FooTest.java")]       // src/test/ (Maven/Gradle: spring-boot, guava)
    [InlineData("packages/x/__tests__/y.js")]        // __tests__/ nested (react, vue, svelte)
    [InlineData("Button.test.tsx")]                  // .test. infix (Jest default)
    [InlineData("ReactHooks-test.js")]               // -test suffix (react)
    [InlineData("test_config.py")]                   // test_ prefix (pytest default)
    [InlineData("fmt/errors_test.go")]               // _test suffix, colocated (Go)
    [InlineData("tokio/tests-integration/src/lib.rs")] // tests- prefix dir variant (tokio)
    [InlineData("testdata/golden.json")]             // testdata/ (Go fixtures)
    [InlineData("test_suite/basic.rs")]              // test_suite/ (serde)
    [InlineData("testfixtures/data.xml")]            // testfixtures/ exact
    [InlineData("htesting/util.go")]                 // htesting/ (hugo)
    [InlineData("unittests/foo.cpp")]                // unittests/ (llvm)
    [InlineData("integration-test/src/Foo.java")]    // -test suffix dir (spring-boot)
    [InlineData("test-support/Helper.kt")]           // test- prefix dir (spring-boot)
    [InlineData("FooTest.cs")]                       // PascalCase Test suffix + .cs (xUnit/NUnit)
    [InlineData("FooTests.php")]                     // PascalCase Tests suffix + .php (PHPUnit)
    [InlineData("FooSpec.swift")]                    // PascalCase Spec suffix + .swift (XCTest)
    [InlineData("deep/nested/path/to/tests/bar.cs")] // tests/ anywhere in path
    [InlineData("src/foo_test.py")]                  // _test suffix (old rule, backward compat)
    public void IsTestRelated_SurveyConventions_ClassifiesAsTest(string path)
    {
        Assert.True(TestPathClassifier.IsTestRelated(path, []));
    }

    // ── IsTestRelated: negatives (should NOT classify as test) ───────────

    [Theory]
    [InlineData("attestation/keys.cs")]              // "test" embedded but not a segment
    [InlineData("contest.py")]                       // "test" embedded in filename
    [InlineData("latest.ts")]                        // "test" embedded in filename
    [InlineData("retest/foo.py")]                    // "retest" ends with "test" but not -test/_test
    [InlineData("testimony/doc.md")]                 // "test" embedded in segment name
    [InlineData("src/detest/foo.cs")]                // "detest" not an exact match
    [InlineData("protest/readme.md")]                // "protest" not an exact match
    [InlineData("controller/testable_utils.cs")]     // "testable" not a prefix/suffix match
    [InlineData("src/Utils/AttestationHelper.java")] // PascalCase suffix "Helper", not "Test/Tests/Spec"
    [InlineData("MyClass.cs")]                       // Not a test file (ordinary C# impl)
    [InlineData("README.md")]                        // Not a test file
    public void IsTestRelated_NonTestPaths_ReturnsFalse(string path)
    {
        Assert.False(TestPathClassifier.IsTestRelated(path, []));
    }

    // ── IsTestRelated: backward-compat (all 4 old rules still pass) ─────

    [Theory]
    [InlineData("tests/app.tests.cs")]               // tests/ dir + .tests. infix
    [InlineData("src/foo_test.py")]                  // _test suffix
    [InlineData("spec/thing.spec.ts")]               // .spec. infix (under spec/ dir too)
    [InlineData("deep/path/tests/bar.cs")]           // /tests/ anywhere
    [InlineData("Tests/CaseInsensitive.Tests.fs")]   // case-insensitive Tests/ + .Tests.
    public void IsTestRelated_OldRules_StillPass(string path)
    {
        Assert.True(TestPathClassifier.IsTestRelated(path, []));
    }

    // ── IsTestRelated: config glob override ──────────────────────────────

    [Theory]
    [InlineData("spec/models/user_spec.rb", "spec/**")]
    [InlineData("custom-test-dir/foo_test.py", "custom-test-dir/**")]
    [InlineData("features/step_definitions.rb", "features/**")]
    [InlineData("qa/integration/Test.java", "qa/**")]
    public void IsTestRelated_ConfigGlob_ClassifiesAsTest(string path, string glob)
    {
        Assert.True(TestPathClassifier.IsTestRelated(path, [glob]));
    }

    [Fact]
    public void IsTestRelated_ConfigGlob_DoesNotMatchUnrelatedPath()
    {
        Assert.False(TestPathClassifier.IsTestRelated("src/app.cs", ["spec/**"]));
    }

    [Fact]
    public void IsTestRelated_EmptyTestPaths_BehavesLikeBuiltInOnly()
    {
        // Empty array is the default; still matches built-in heuristics.
        Assert.True(TestPathClassifier.IsTestRelated("tests/foo.cs", []));
        Assert.False(TestPathClassifier.IsTestRelated("unlisted/dir/file.cs", []));
    }

    // ── IsRunnableTestFile: filters non-code extensions ─────────────────

    [Theory]
    [InlineData("test/foo.cs")]                      // .cs is runnable
    [InlineData("tests/bar.ts")]                     // .ts is runnable
    [InlineData("spec/baz_spec.rb")]                 // .rb is runnable
    [InlineData("test_config.py")]                   // .py is runnable
    [InlineData("Button.test.tsx")]                  // .tsx is runnable
    public void IsRunnableTestFile_CodeExtensions_ReturnsTrue(string path)
    {
        Assert.True(TestPathClassifier.IsRunnableTestFile(path, []));
    }

    [Theory]
    [InlineData("testdata/golden.json")]             // .json is non-code
    [InlineData("tests/fixtures/data.yaml")]         // .yaml is non-code
    [InlineData("test_suite/expected.toml")]         // .toml is non-code
    [InlineData("tests/readme.md")]                  // .md is non-code
    [InlineData("testdata/input.csv")]               // .csv is non-code
    [InlineData("tests/config.txt")]                 // .txt is non-code
    [InlineData("tests/fixtures/data.yml")]          // .yml is non-code
    public void IsRunnableTestFile_NonCodeExtensions_ReturnsFalse(string path)
    {
        // Still test-related for gate purposes, but not runnable.
        Assert.True(TestPathClassifier.IsTestRelated(path, []));
        Assert.False(TestPathClassifier.IsRunnableTestFile(path, []));
    }

    [Fact]
    public void IsRunnableTestFile_NoExtension_ReturnsFalse()
    {
        // Files with no extension (e.g. Dockerfile, Makefile) are not runnable test files.
        Assert.False(TestPathClassifier.IsRunnableTestFile("tests/Dockerfile", []));
    }

    [Fact]
    public void IsRunnableTestFile_NonTestPath_ReturnsFalse()
    {
        // Not test-related at all → not runnable.
        Assert.False(TestPathClassifier.IsRunnableTestFile("src/app.cs", []));
    }

    // ── PascalCase: only for the specified extensions ────────────────────

    [Theory]
    [InlineData("FooTest.java")]
    [InlineData("FooTests.kt")]
    [InlineData("FooTest.cs")]
    [InlineData("FooTests.php")]
    [InlineData("FooSpec.swift")]
    public void IsTestRelated_PascalCase_AllowedExtensions_ReturnsTrue(string path)
    {
        Assert.True(TestPathClassifier.IsTestRelated(path, []));
    }

    [Theory]
    [InlineData("FooTest.py")]                       // .py not in PascalTestExtensions
    [InlineData("FooTest.ts")]                       // .ts not in PascalTestExtensions
    [InlineData("FooTest.rb")]                       // .rb not in PascalTestExtensions
    [InlineData("FooTest.go")]                       // .go not in PascalTestExtensions
    [InlineData("FooSpec.js")]                       // .js not in PascalTestExtensions
    public void IsTestRelated_PascalCase_DisallowedExtensions_ReturnsFalse(string path)
    {
        // PascalCase suffix is only for JVM/.NET/PHP/Swift ecosystems.
        // These files don't match any other rule either (no test dir, no infix).
        Assert.False(TestPathClassifier.IsTestRelated(path, []));
    }

    // ── Segment matching: verify segment boundaries ──────────────────────

    [Theory]
    [InlineData("a/test/b/code.cs")]                 // "test" as exact segment
    [InlineData("a/tests/b/code.cs")]                // "tests" as exact segment
    [InlineData("a/TEST/b/code.cs")]                 // case-insensitive
    [InlineData("a/Test/b/code.cs")]                 // case-insensitive
    public void IsTestRelated_SegmentExactMatch_ReturnsTrue(string path)
    {
        Assert.True(TestPathClassifier.IsTestRelated(path, []));
    }

    [Theory]
    [InlineData("atest/b/code.cs")]                  // "atest" is not exact match "test"
    [InlineData("testa/b/code.cs")]                  // "testa" is not exact match
    public void IsTestRelated_SegmentNoFalsePrefixSuffix_ReturnsFalse(string path)
    {
        Assert.False(TestPathClassifier.IsTestRelated(path, []));
    }
}
