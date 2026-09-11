using System.Text.RegularExpressions;
using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

public sealed class TestLayoutCatalogTests
{
    // Pinned row counts. CHANGE DELIBERATELY: moving a language between buckets
    // changes the default gating strategy for every repository written in it, so
    // these numbers exist to make such a change show up in a diff and a review.
    private const int SeparateRowCount = 78;
    private const int NoneRowCount = 106;

    [Fact]
    public void Inline_EveryRow_HasAnExtensionAndAMarker()
    {
        foreach (var row in TestLayoutCatalog.Inline)
        {
            var shared = TestLayoutCatalog.Ambiguous.Count(entry =>
                entry.DefaultLanguageId == row.Id || entry.AlternativeLanguageId == row.Id);

            var extensionCount = row.Extensions.Count + shared;
            var markerCount = row.Markers.Count;

            Assert.True(extensionCount > 0, $"Inline language '{row.Id}' has no extension.");
            Assert.True(markerCount > 0, $"Inline language '{row.Id}' has no marker file.");
        }
    }

    [Fact]
    public void All_Extensions_AppearInExactlyOneRow()
    {
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var clashes = new List<string>();

        foreach (var row in TestLayoutCatalog.All)
        {
            foreach (var extension in row.Extensions)
            {
                if (owners.TryGetValue(extension, out var first))
                    clashes.Add($"{extension}: {first} and {row.Id}");
                else
                    owners[extension] = row.Id;
            }
        }

        Assert.Empty(clashes);
    }

    [Fact]
    public void All_Extensions_AreLowercaseAndDotted()
    {
        var malformed = TestLayoutCatalog.All
            .SelectMany(row => row.Extensions)
            .Where(extension => !extension.StartsWith('.')
                                || extension.Length < 2
                                || extension != extension.ToLowerInvariant())
            .ToList();

        Assert.Empty(malformed);
    }

    [Fact]
    public void Ambiguous_Extensions_AreNeverListedOnARow()
    {
        var listed = TestLayoutCatalog.All
            .SelectMany(row => row.Extensions)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var leaked = TestLayoutCatalog.Ambiguous
            .Where(entry => listed.Contains(entry.Extension))
            .Select(entry => entry.Extension)
            .ToList();

        Assert.Empty(leaked);
    }

    [Fact]
    public void Ambiguous_Entries_NameKnownLanguages()
    {
        foreach (var entry in TestLayoutCatalog.Ambiguous)
        {
            if (entry.DefaultLanguageId is not null)
                Assert.NotNull(TestLayoutCatalog.ById(entry.DefaultLanguageId));

            Assert.NotNull(TestLayoutCatalog.ById(entry.AlternativeLanguageId));
        }
    }

    [Fact]
    public void All_Markers_MapToExactlyOneRow()
    {
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var clashes = new List<string>();

        foreach (var row in TestLayoutCatalog.All)
        {
            foreach (var marker in row.Markers)
            {
                if (owners.TryGetValue(marker, out var first))
                    clashes.Add($"{marker}: {first} and {row.Id}");
                else
                    owners[marker] = row.Id;
            }
        }

        Assert.Empty(clashes);
    }

    [Fact]
    public void All_Ids_AreLowercaseSlugsAndUnique()
    {
        var slug = new Regex("^[a-z0-9]+(-[a-z0-9]+)*$");
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in TestLayoutCatalog.All)
        {
            Assert.Matches(slug, row.Id);
            Assert.True(seen.Add(row.Id), $"Duplicate id '{row.Id}'.");
        }
    }

    [Fact]
    public void Inline_IdSet_MatchesTheSnapshot()
    {
        string[] expected = ["cairo", "d", "prolog", "racket", "roc", "rust", "zig"];

        var actual = TestLayoutCatalog.Inline
            .Select(row => row.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void All_BucketCounts_MatchThePinnedNumbers()
    {
        var inline = TestLayoutCatalog.All.Count(row => row.Bucket == TestLayoutBucket.Inline);
        var separate = TestLayoutCatalog.All.Count(row => row.Bucket == TestLayoutBucket.Separate);
        var none = TestLayoutCatalog.All.Count(row => row.Bucket == TestLayoutBucket.None);
        var total = TestLayoutCatalog.All.Count;

        Assert.Equal(SeparateRowCount, separate);
        Assert.Equal(NoneRowCount, none);
        Assert.Equal(TestLayoutCatalog.Inline.Count, inline);
        Assert.Equal(inline + SeparateRowCount + NoneRowCount, total);
    }

    [Fact]
    public void ByExtension_UppercaseRustExtension_ResolvesToRust()
    {
        Assert.Equal("rust", TestLayoutCatalog.ByExtension(".RS")?.Id);
    }

    [Theory]
    [InlineData(".pl")]
    [InlineData(".pro")]
    [InlineData(".p")]
    [InlineData(".d")]
    public void ByExtension_AmbiguousExtension_ReturnsNull(string extension)
    {
        Assert.Null(TestLayoutCatalog.ByExtension(extension));
    }

    [Fact]
    public void ByExtension_UnknownExtension_ReturnsNull()
    {
        Assert.Null(TestLayoutCatalog.ByExtension(".nosuchextension"));
    }

    [Fact]
    public void ByMarker_ProjectFilePattern_ResolvesToCSharp()
    {
        Assert.Equal("csharp", TestLayoutCatalog.ByMarker("foo.csproj")?.Id);
    }

    [Theory]
    [InlineData("Cargo.toml", "rust")]
    [InlineData("build.zig", "zig")]
    [InlineData("build.zig.zon", "zig")]
    [InlineData("dub.json", "d")]
    [InlineData("dub.sdl", "d")]
    [InlineData("info.rkt", "racket")]
    [InlineData("Scarb.toml", "cairo")]
    [InlineData("main.roc", "roc")]
    [InlineData("pack.pl", "prolog")]
    public void ByMarker_InlineDecidingMarker_ResolvesToItsLanguage(string marker, string id)
    {
        Assert.Equal(id, TestLayoutCatalog.ByMarker(marker)?.Id);
    }

    [Fact]
    public void ByMarker_UnknownName_ReturnsNull()
    {
        Assert.Null(TestLayoutCatalog.ByMarker("nothing-in-the-catalog.unknown"));
    }

    [Fact]
    public void ByMarker_EclipseProjectFile_BelongsToNoLanguage()
    {
        // Eclipse writes .project into countless Java repositories, so reading it
        // as Smalltalk would report the wrong language for a whole ecosystem.
        Assert.Null(TestLayoutCatalog.ByMarker(".project"));
    }

    [Fact]
    public void ByMarker_GenericBuildScriptName_BelongsToNoLanguage()
    {
        // build.lua is a plain build-script name any Lua project may carry; only
        // latexmkrc and *.ins are specific enough to mean TeX.
        Assert.Null(TestLayoutCatalog.ByMarker("build.lua"));
        Assert.Equal("tex", TestLayoutCatalog.ByMarker("latexmkrc")?.Id);
    }

    [Fact]
    public void ById_KnownAndUnknownIds_ResolveAsExpected()
    {
        Assert.Equal(TestLayoutBucket.Separate, TestLayoutCatalog.ById("python")?.Bucket);
        Assert.Equal(TestLayoutBucket.None, TestLayoutCatalog.ById("yaml")?.Bucket);
        Assert.Null(TestLayoutCatalog.ById("ecmascript"));
    }

    [Fact]
    public void TestPathPatterns_CarryTheConventionsFromTheCatalog()
    {
        var python = TestLayoutCatalog.ById("python");
        Assert.NotNull(python);
        Assert.Contains("conftest.py", python.TestPathPatterns);

        // A language whose tests only ever sit in the implementation file has no
        // path convention to record.
        var roc = TestLayoutCatalog.ById("roc");
        Assert.NotNull(roc);
        Assert.Empty(roc.TestPathPatterns);
    }

    [Fact]
    public void InlineExtensionsFor_RustAndPython_ReturnsOnlyTheRustExtension()
    {
        string[] expected = [".rs"];
        Assert.Equal(expected, TestLayoutCatalog.InlineExtensionsFor(["rust", "python"]));
    }

    [Fact]
    public void InlineExtensionsFor_Prolog_IncludesTheAmbiguousExtensions()
    {
        string[] expected = [".p", ".pl", ".plt", ".pro"];
        Assert.Equal(expected, TestLayoutCatalog.InlineExtensionsFor(["prolog"]));
    }

    [Fact]
    public void InlineExtensionsFor_SeparateLanguage_IsEmpty()
    {
        Assert.Empty(TestLayoutCatalog.InlineExtensionsFor(["go"]));
    }
}
