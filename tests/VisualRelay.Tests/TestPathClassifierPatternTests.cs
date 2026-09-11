using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// New test-layout patterns taught to <see cref="TestPathClassifier"/>: extra
/// file-name infixes/affixes, always-test extensions, directory exact names and
/// suffixes, and the ordinal camelCase source-set rule. Kept separate from
/// <see cref="TestPathClassifierTests"/> so neither file passes 300 lines.
/// </summary>
public sealed class TestPathClassifierPatternTests
{
    // ── File-name infixes: .t. .tftest. .tofutest. .testclasses. ────────────

    [Theory]
    [InlineData("src/foo.t.js")]
    [InlineData("infra/main.tftest.hcl")]
    [InlineData("infra/main.tofutest.hcl")]
    [InlineData("out/report.testclasses.xml")]
    public void NewInfixes_ClassifyAsTest(string path)
    {
        Assert.True(TestPathClassifier.IsTestRelated(path, []));
    }

    [Theory]
    [InlineData("src/format.ts")]              // near .t. — no infix present
    [InlineData("infra/main.tf")]              // near .tftest. — plain terraform file
    [InlineData("infra/main.tofu")]            // near .tofutest. — plain OpenTofu file
    [InlineData("out/report.classes.xml")]     // near .testclasses. — missing "test"
    public void NewInfixes_NearMiss_ReturnsFalse(string path)
    {
        Assert.False(TestPathClassifier.IsTestRelated(path, []));
    }

    // ── Stem suffixes: -tests _tests -spec -specs _specs _suite ─────────────

    [Theory]
    [InlineData("src/foo-tests.js")]
    [InlineData("src/foo_tests.py")]
    [InlineData("src/foo-spec.rb")]
    [InlineData("src/foo-specs.rb")]
    [InlineData("src/foo_specs.rb")]
    [InlineData("src/foo_suite.py")]
    public void NewStemSuffixes_ClassifyAsTest(string path)
    {
        Assert.True(TestPathClassifier.IsTestRelated(path, []));
    }

    [Theory]
    [InlineData("src/contests.js")]            // near -tests — no hyphen delimiter
    [InlineData("src/attests.py")]             // near _tests — no underscore delimiter
    [InlineData("src/codespec.rb")]            // near -spec — no hyphen delimiter
    [InlineData("src/codespecs.rb")]           // near -specs — no hyphen delimiter
    [InlineData("src/subspecs.rb")]            // near _specs — no underscore delimiter
    [InlineData("src/testsuite.py")]           // near _suite — no underscore delimiter
    public void NewStemSuffixes_NearMiss_ReturnsFalse(string path)
    {
        Assert.False(TestPathClassifier.IsTestRelated(path, []));
    }

    // ── Stem prefixes: tst_ test- ─────────────────────────────────────────

    [Theory]
    [InlineData("tst_config.py")]
    [InlineData("test-config.py")]
    public void NewStemPrefixes_ClassifyAsTest(string path)
    {
        Assert.True(TestPathClassifier.IsTestRelated(path, []));
    }

    [Theory]
    [InlineData("tstconfig.py")]                // near tst_ — no underscore delimiter
    [InlineData("testing-config.py")]           // near test- — "testing", not "test-"
    public void NewStemPrefixes_NearMiss_ReturnsFalse(string path)
    {
        Assert.False(TestPathClassifier.IsTestRelated(path, []));
    }

    // ── Extensions that are always tests: .t .bats .feature .robot .plt .pf ──

    [Theory]
    [InlineData("scripts/01-basic.t")]
    [InlineData("scripts/install.bats")]
    [InlineData("features/login.feature")]      // brief's named case: the extension, not the dir
    [InlineData("automation/login.robot")]
    [InlineData("lib/module.plt")]
    [InlineData("checks/policy.pf")]
    public void AlwaysTestExtensions_ClassifyAsTest(string path)
    {
        Assert.True(TestPathClassifier.IsTestRelated(path, []));
    }

    [Theory]
    [InlineData("scripts/01-basic.txt")]
    [InlineData("scripts/install.sh")]
    [InlineData("src/features/login.ts")]       // brief's named omission: features/ is not a dir rule
    [InlineData("automation/login.py")]
    [InlineData("lib/module.rkt")]
    [InlineData("checks/policy.conf")]
    public void AlwaysTestExtensions_NearMiss_ReturnsFalse(string path)
    {
        Assert.False(TestPathClassifier.IsTestRelated(path, []));
    }

    // ── Directory segment exact names (new) ──────────────────────────────

    [Theory]
    [InlineData("SRC/E2E/x.ts")]                 // brief's named case: case-insensitive
    [InlineData("src/integration/foo.py")]
    [InlineData("src/acceptance/checkout.rb")]
    [InlineData("cypress/login.cy.ts")]
    [InlineData("playwright/login.ts")]
    [InlineData("src/fixtures/data.rb")]
    [InlineData("src/__fixtures__/data.js")]
    [InlineData("src/__mocks__/api.js")]
    [InlineData("src/__snapshots__/App.snap")]
    [InlineData("ansible/molecule/scenario.yml")]
    [InlineData("hdl/testbench/uart_tb.v")]
    [InlineData("features/step_definitions/login_steps.rb")]
    public void NewExactDirNames_ClassifyAsTest(string path)
    {
        Assert.True(TestPathClassifier.IsTestRelated(path, []));
    }

    [Theory]
    [InlineData("src/e2e-notes/readme.md")]        // near e2e — not an exact segment
    [InlineData("src/integrations/foo.py")]        // near integration — plural
    [InlineData("src/acceptable/checkout.rb")]     // near acceptance — different word
    [InlineData("src/cypress-config/login.ts")]    // near cypress — not exact
    [InlineData("src/playwrights/login.ts")]       // near playwright — plural
    [InlineData("src/fixtureset/data.rb")]         // near fixtures — different word
    [InlineData("src/_fixtures_/data.js")]         // near __fixtures__ — single underscores
    [InlineData("src/mocks/api.js")]               // near __mocks__ — no dunders
    [InlineData("src/snapshots/App.snap")]         // near __snapshots__ — no dunders
    [InlineData("ansible/molecules/scenario.yml")] // near molecule — plural
    [InlineData("hdl/testbenches/uart_tb.v")]      // near testbench — plural
    [InlineData("features/step_defs/login_steps.rb")] // near step_definitions — abbreviated
    public void NewExactDirNames_NearMiss_ReturnsFalse(string path)
    {
        Assert.False(TestPathClassifier.IsTestRelated(path, []));
    }

    // ── Directory segment suffixes: .tests .test .unittests .integrationtests .specs ──

    [Theory]
    [InlineData("src/MyLib.Tests/Foo.cs")]
    [InlineData("src/Widget.Test/Foo.cs")]
    [InlineData("src/Widget.UnitTests/Foo.cs")]
    [InlineData("src/Widget.IntegrationTests/Foo.cs")]
    [InlineData("src/Widget.Specs/Foo.cs")]
    public void NewDirSuffixes_ClassifyAsTest(string path)
    {
        Assert.True(TestPathClassifier.IsTestRelated(path, []));
    }

    [Theory]
    [InlineData("src/MyLibTests/Foo.cs")]              // near .Tests — no dot; camelCase blocked (capital first letter)
    [InlineData("src/WidgetTest/Foo.cs")]              // near .Test — same reason
    [InlineData("src/Widget.UnitTest/Foo.cs")]         // near .UnitTests — singular, missing final s
    [InlineData("src/Widget.IntegrationTest/Foo.cs")]  // near .IntegrationTests — singular
    [InlineData("src/Widget.Spec/Foo.cs")]             // near .Specs — singular
    public void NewDirSuffixes_NearMiss_ReturnsFalse(string path)
    {
        Assert.False(TestPathClassifier.IsTestRelated(path, []));
    }

    // ── camelCase directory rule: ^[a-z][A-Za-z0-9]*Tests?$, Ordinal ─────────

    [Theory]
    [InlineData("app/commonTest/Foo.kt")]
    [InlineData("app/androidTest/Foo.kt")]
    [InlineData("app/jvmTest/Foo.kt")]
    [InlineData("src/integrationTest/Foo.java")]
    public void CamelCaseTestDirectory_ClassifiesAsTest(string path)
    {
        Assert.True(TestPathClassifier.IsTestRelated(path, []));
    }

    [Theory]
    [InlineData("src/contest/x.cs")]             // brief's named case: lowercase "test", not "Test"
    [InlineData("app/latest/Widget.kt")]
    [InlineData("app/manifest/Widget.kt")]
    public void CamelCaseTestDirectory_IsOrdinal_ReturnsFalse(string path)
    {
        Assert.False(TestPathClassifier.IsTestRelated(path, []));
    }

    // ── Brief's remaining named cases (true under pre-existing rules too) ───

    [Theory]
    [InlineData("Tests/FooTests.swift")]
    [InlineData("Foo.Tests.ps1")]
    public void BriefNamedCases_ClassifyAsTest(string path)
    {
        Assert.True(TestPathClassifier.IsTestRelated(path, []));
    }

    // ── Deliberate omissions: never classify as test ─────────────────────────

    [Theory]
    [InlineData("TestRunner.cs")]                // PascalCase Test PREFIX — not added
    [InlineData("config/test.py")]               // bare "test" stem — not added
    public void DeliberateOmissions_ReturnsFalse(string path)
    {
        Assert.False(TestPathClassifier.IsTestRelated(path, []));
    }
}
