namespace VisualRelay.Core.Init;

/// <summary>
/// Where a language conventionally keeps its unit tests.
/// </summary>
public enum TestLayoutBucket
{
    /// <summary>
    /// Unit tests live inside the implementation file, so splitting test from
    /// implementation by path cannot work.
    /// </summary>
    Inline,

    /// <summary>
    /// Unit tests live in files of their own, so a path classifier can gate them.
    /// </summary>
    Separate,

    /// <summary>
    /// Never decides the strategy: markup, style, data, query, template, shader
    /// and build languages, plus languages where automated tests are rare.
    /// </summary>
    None,
}

/// <summary>
/// One catalog row. <paramref name="Id"/> is a lowercase slug ("rust",
/// "objective-c", "visual-basic"); <paramref name="Extensions"/> are lowercase
/// with the leading dot and unique across the whole catalog;
/// <paramref name="Markers"/> are file names ("Cargo.toml") or "*.ext" name
/// patterns ("*.csproj"), unique across the catalog;
/// <paramref name="TestPathPatterns"/> is informational text from the catalog.
/// </summary>
public sealed record TestLayoutLanguage(
    string Id, TestLayoutBucket Bucket,
    IReadOnlyList<string> Extensions, IReadOnlyList<string> Markers, IReadOnlyList<string> TestPathPatterns);

/// <summary>
/// An extension two languages share, resolved by the detector, never listed in a
/// row's Extensions. A null <paramref name="DefaultLanguageId"/> means there is
/// no safe default and the detector has to look at markers or content.
/// </summary>
public sealed record AmbiguousExtension(string Extension, string? DefaultLanguageId, string AlternativeLanguageId);

// Data-only catalog of where each language keeps its unit tests, populated from
// llm-tasks/gate-author-tests-by-test-layout/test-layout-catalog.md. Only INLINE
// membership changes the default gating strategy; SEPARATE and NONE are carried
// so the detector can name what it found and reuse the path conventions.
//
// Alias rows of the source list are merged into their primary here, so a lookup
// never has to pick between two spellings of one language: ecmascript into
// javascript, bourne-shell into bash, caml into ocaml and
// kotlin-multiplatform into kotlin (see the SEPARATE partial), glsl-es into
// glsl, latex into tex and terraform-module into hcl (see the NONE partial).
// jscript is dropped outright: its only extension belongs to javascript.
//
// Every extension belongs to exactly one row and every marker maps to one row,
// both pinned by TestLayoutCatalogTests. Extensions two mainstream languages
// share went to the mainstream one; markers several languages share (Makefile,
// CMakeLists.txt, meson.build, and project directories such as *.xcodeproj)
// were dropped rather than attributed at random. No row carries a lock file as a
// marker either, since the detector filters those out before it looks.
public static partial class TestLayoutCatalog
{
    /// <summary>Every row, inline rows first, then separate, then none.</summary>
    public static IReadOnlyList<TestLayoutLanguage> All { get; }

    /// <summary>The rows whose unit tests sit inside the implementation file.</summary>
    public static IReadOnlyList<TestLayoutLanguage> Inline { get; }

    /// <summary>Extensions two languages share; never present on a row.</summary>
    public static IReadOnlyList<AmbiguousExtension> Ambiguous { get; }

    private static readonly Dictionary<string, TestLayoutLanguage> RowsById;
    private static readonly Dictionary<string, TestLayoutLanguage> RowsByExtension;
    private static readonly Dictionary<string, TestLayoutLanguage> RowsByMarkerName;
    private static readonly Dictionary<string, TestLayoutLanguage> RowsByMarkerExtension;

    static TestLayoutCatalog()
    {
        Inline = InlineRows;
        Ambiguous = AmbiguousExtensions;
        All = [.. InlineRows, .. SeparateRows, .. NoneRows];

        RowsById = new Dictionary<string, TestLayoutLanguage>(StringComparer.OrdinalIgnoreCase);
        RowsByExtension = new Dictionary<string, TestLayoutLanguage>(StringComparer.OrdinalIgnoreCase);
        RowsByMarkerName = new Dictionary<string, TestLayoutLanguage>(StringComparer.OrdinalIgnoreCase);
        RowsByMarkerExtension = new Dictionary<string, TestLayoutLanguage>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in All)
        {
            // TryAdd rather than Add: a duplicate is a data defect the catalog
            // tests report by name, not a crash on first touch of the type.
            RowsById.TryAdd(row.Id, row);

            foreach (var extension in row.Extensions)
                RowsByExtension.TryAdd(extension, row);

            foreach (var marker in row.Markers)
            {
                if (marker.StartsWith("*.", StringComparison.Ordinal))
                    RowsByMarkerExtension.TryAdd(marker[1..], row);
                else
                    RowsByMarkerName.TryAdd(marker, row);
            }
        }
    }

    /// <summary>The row with this id, or null when the id is unknown.</summary>
    public static TestLayoutLanguage? ById(string id) =>
        string.IsNullOrWhiteSpace(id) ? null : RowsById.GetValueOrDefault(id);

    /// <summary>
    /// The row owning this extension. Null for an unknown extension and for one
    /// in <see cref="Ambiguous"/>, which only the detector can resolve.
    /// </summary>
    public static TestLayoutLanguage? ByExtension(string extension) =>
        string.IsNullOrWhiteSpace(extension) ? null : RowsByExtension.GetValueOrDefault(extension);

    /// <summary>
    /// The row a marker file belongs to, matching an exact name first and then a
    /// "*.ext" pattern. Accepts a path as well as a bare file name.
    /// </summary>
    public static TestLayoutLanguage? ByMarker(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var name = Path.GetFileName(fileName);
        if (name.Length == 0)
            return null;

        if (RowsByMarkerName.TryGetValue(name, out var byName))
            return byName;

        var extension = Path.GetExtension(name);
        return extension.Length == 0 ? null : RowsByMarkerExtension.GetValueOrDefault(extension);
    }

    /// <summary>
    /// The extensions whose files may legitimately carry tests next to the
    /// implementation, for the languages named by <paramref name="languageIds"/>.
    /// Ids outside the inline bucket contribute nothing; ambiguous extensions
    /// naming an inline language are included. Distinct, sorted ordinal.
    /// </summary>
    public static IReadOnlyList<string> InlineExtensionsFor(IEnumerable<string> languageIds)
    {
        var ids = new HashSet<string>(languageIds, StringComparer.OrdinalIgnoreCase);
        var extensions = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var row in InlineRows.Where(row => ids.Contains(row.Id)))
        {
            foreach (var extension in row.Extensions)
                extensions.Add(extension);

            foreach (var entry in AmbiguousExtensions.Where(entry => Names(entry, row.Id)))
                extensions.Add(entry.Extension);
        }

        return [.. extensions];
    }

    private static bool Names(AmbiguousExtension entry, string id) =>
        string.Equals(entry.DefaultLanguageId, id, StringComparison.OrdinalIgnoreCase)
        || string.Equals(entry.AlternativeLanguageId, id, StringComparison.OrdinalIgnoreCase);

    // ── INLINE (7) — same-file unit tests; path-based separation cannot work ──
    // The markers here are the ones that decide an inline membership, so each
    // row keeps at least one and the detector can trust a single file.
    private static readonly TestLayoutLanguage[] InlineRows =
    [
        new("rust", TestLayoutBucket.Inline, [".rs"], ["Cargo.toml"], ["src/**/tests.rs", "tests/*.rs"]),
        new("zig", TestLayoutBucket.Inline, [".zig"], ["build.zig", "build.zig.zon"], ["src/tests.zig", "test/"]),
        new("d", TestLayoutBucket.Inline, [".di"], ["dub.json", "dub.sdl"], ["test/"]),
        new("racket", TestLayoutBucket.Inline, [".rkt", ".rktl"], ["info.rkt"], ["tests/", "*-test.rkt"]),
        new("cairo", TestLayoutBucket.Inline, [".cairo"], ["Scarb.toml"], ["tests/*.cairo"]),
        new("roc", TestLayoutBucket.Inline, [".roc"], ["main.roc"], []),
        new("prolog", TestLayoutBucket.Inline, [".plt"], ["pack.pl"], ["*.plt"]),
    ];

    // Extensions two languages share where neither reading is safe enough to put
    // the extension on a row. ".d" has no default because its other reading is a
    // Makefile depfile, which is not a language and so cannot be named here: the
    // detector treats a ".d" beside a same-stem ".o" as generated output.
    private static readonly AmbiguousExtension[] AmbiguousExtensions =
    [
        new(".pl", "perl", "prolog"),
        new(".pro", null, "prolog"),
        new(".p", null, "prolog"),
        new(".d", null, "d"),
    ];
}
