using System.Xml.Linq;
using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for <see cref="FakeClockGuard"/> — enforces that no fake-clock
/// identifiers appear in production source trees (<c>src/</c> and <c>tools/</c>),
/// that every <c>TimeProvider?</c>-typed parameter defaults to <c>null</c>,
/// and that no <c>src/</c> csproj references a time-testing package.
/// </summary>
public sealed class FakeClockGuardTests
{
    private readonly CachedSyntaxTreesFixture _trees;

    public FakeClockGuardTests(CachedSyntaxTreesFixture trees)
    {
        _trees = trees;
    }

    /// <summary>
    /// A <c>ManualTimeProvider</c> identifier in a source snippet is flagged.
    /// </summary>
    [Fact]
    public void ManualTimeProviderIdentifier_IsReported()
    {
        const string source = "class C { void M(ManualTimeProvider tp) { } }";

        var violations = FakeClockGuard.FindViolations([("Fixtures/Faker.cs", source)]);

        var v = Assert.Single(violations);
        Assert.Equal("Fixtures/Faker.cs", v.Path);
        Assert.Contains("ManualTimeProvider", v.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>FakeTimeProvider</c> identifier is also flagged.
    /// </summary>
    [Fact]
    public void FakeTimeProviderIdentifier_IsReported()
    {
        const string source = "class C { void M() { var tp = new FakeTimeProvider(); } }";

        var violations = FakeClockGuard.FindViolations([("Fixtures/Faker.cs", source)]);

        Assert.NotEmpty(violations);
    }

    /// <summary>
    /// A <c>TimeProvider?</c> parameter with a non-null default is flagged.
    /// </summary>
    [Fact]
    public void TimeProviderParameter_NonNullDefault_IsReported()
    {
        const string source = "class C { void M(TimeProvider? tp = TimeProvider.System) { } }";

        var violations = FakeClockGuard.FindViolations([("Fixtures/Default.cs", source)]);

        var v = Assert.Single(violations);
        Assert.Contains("non-null default", v.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>TimeProvider?</c> parameter defaulting to <c>null</c> is NOT flagged.
    /// </summary>
    [Fact]
    public void TimeProviderParameter_NullDefault_IsNotReported()
    {
        const string source = "class C { void M(TimeProvider? tp = null) { } }";

        var violations = FakeClockGuard.FindViolations([("Fixtures/Ok.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// A <c>TimeProvider?</c> parameter with no default at all is NOT flagged
    /// (non-optional parameters are fine — only defaults matter).
    /// </summary>
    [Fact]
    public void TimeProviderParameter_NoDefault_IsNotReported()
    {
        const string source = "class C { void M(TimeProvider? tp) { } }";

        var violations = FakeClockGuard.FindViolations([("Fixtures/Ok.cs", source)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// A csproj referencing <c>Microsoft.Extensions.Time.Testing</c> is flagged.
    /// </summary>
    [Fact]
    public void Csproj_TimeTestingPackageReference_IsReported()
    {
        const string xml = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.Extensions.Time.Testing" Version="9.0.0" />
              </ItemGroup>
            </Project>
            """;

        var violations = FakeClockGuard.FindCsprojViolations([("src/Foo/Foo.csproj", xml)]);

        var v = Assert.Single(violations);
        Assert.Equal("src/Foo/Foo.csproj", v.Path);
        Assert.Contains("Microsoft.Extensions.Time.Testing", v.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A csproj without any time-testing package references is clean.
    /// </summary>
    [Fact]
    public void Csproj_NoTimeTestingPackage_IsNotReported()
    {
        const string xml = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="5.3.0" />
              </ItemGroup>
            </Project>
            """;

        var violations = FakeClockGuard.FindCsprojViolations([("src/Foo/Foo.csproj", xml)]);

        Assert.Empty(violations);
    }

    /// <summary>
    /// The live enforcing gate: every C# file under <c>src/</c> and <c>tools/</c>
    /// is free of fake-clock identifiers, every <c>TimeProvider?</c> parameter
    /// defaults to <c>null</c>, and no <c>src/</c> csproj references a time-testing
    /// package. Any violation flips the guard to a build failure.
    /// </summary>
    [Fact]
    public void LiveTree_HasNoFakeClocksInProduction()
    {
        var trees = _trees.AllTrees
            .Where(t => t.RelativePath.StartsWith("src/", StringComparison.Ordinal)
                        || t.RelativePath.StartsWith("tools/", StringComparison.Ordinal))
            .ToList();

        var violations = FakeClockGuard.FindViolations(trees);

        // Rule (c): scan src/*.csproj files for time-testing package references.
        var root = RepoSetup.Root;
        var csprojViolations = new List<FakeClockGuard.Violation>();
        foreach (var csproj in Directory.EnumerateFiles(
            Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            if (csproj.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || csproj.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            var rel = Path.GetRelativePath(root, csproj);
            var xml = File.ReadAllText(csproj);
            var csprojVios = FakeClockGuard.FindCsprojViolations([(rel, xml)]);
            csprojViolations.AddRange(csprojVios);
        }

        var allViolations = violations.Concat(csprojViolations).ToList();

        Assert.True(allViolations.Count == 0,
            "fake-clock guard found violations in production source (fake clocks are a tests-only seam):\n" +
            string.Join("\n", allViolations.Select(v => $"{v.Path}:{v.Line}: {v.Snippet} — {v.Reason}")));
    }
}
