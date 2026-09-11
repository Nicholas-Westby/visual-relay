using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

/// <summary>How a file the Author-tests stage called a test can be gated.</summary>
public enum AuthorTestScopeKind
{
    /// <summary>A real test file: the path classifier recognizes it, so the path gate applies.</summary>
    Separate,

    /// <summary>
    /// Its language keeps unit tests inside the implementation file, so the file
    /// is legitimately both and only the as-is red check can judge it.
    /// </summary>
    InlineCapable,

    /// <summary>Neither: kept, but reported, because it is probably implementation.</summary>
    Suspect,
}

/// <summary>One file and the scope it was classified into.</summary>
/// <param name="Path">The repository-relative path the model listed.</param>
/// <param name="Kind">Its scope.</param>
public sealed record AuthorTestScopeVerdict(string Path, AuthorTestScopeKind Kind);

/// <summary>
/// Classifies the Author-tests stage's declared test files, so the gate can tell
/// a test file from an implementation file the model called a test. The two facts
/// it needs are properties of the language, not of the model's claim: whether the
/// path reads as a test path, and whether the extension belongs to a language
/// whose unit tests live in the implementation file.
/// </summary>
public static class AuthorTestScope
{
    /// <summary>
    /// Classifies every entry, keeping the order the model listed them in.
    /// </summary>
    /// <param name="testFiles">The stage's declared test files.</param>
    /// <param name="config">The repository config, for its test globs and inline extensions.</param>
    /// <returns>One verdict per entry.</returns>
    public static IReadOnlyList<AuthorTestScopeVerdict> Classify(
        IReadOnlyList<string> testFiles, RelayConfig config)
    {
        var inline = new HashSet<string>(config.AuthorTests.InlineTestExtensions, StringComparer.OrdinalIgnoreCase);

        return
        [
            .. testFiles.Select(path => new AuthorTestScopeVerdict(path, Kind(path, config.TestPaths, inline)))
        ];
    }

    /// <summary>
    /// The verdicts as one log-safe line: <c>path=verdict</c> joined with
    /// semicolons, empty when nothing was classified.
    /// </summary>
    /// <param name="verdicts">The verdicts to render.</param>
    /// <returns>The rendered line.</returns>
    public static string Describe(IReadOnlyList<AuthorTestScopeVerdict> verdicts) =>
        string.Join(';', verdicts.Select(verdict => $"{verdict.Path}={Name(verdict.Kind)}"));

    private static AuthorTestScopeKind Kind(string path, IReadOnlyList<string>? testPaths, HashSet<string> inline)
    {
        if (TestPathClassifier.IsTestRelated(path, testPaths))
            return AuthorTestScopeKind.Separate;

        var extension = Path.GetExtension(path);
        return extension.Length > 0 && inline.Contains(extension)
            ? AuthorTestScopeKind.InlineCapable
            : AuthorTestScopeKind.Suspect;
    }

    private static string Name(AuthorTestScopeKind kind) => kind switch
    {
        AuthorTestScopeKind.Separate => "separate",
        AuthorTestScopeKind.InlineCapable => "inline-capable",
        _ => "suspect",
    };
}
