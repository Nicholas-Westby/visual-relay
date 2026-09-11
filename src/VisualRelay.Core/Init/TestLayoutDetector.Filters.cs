namespace VisualRelay.Core.Init;

// The rules that decide what a tracked path is worth: which paths are somebody
// else's code, what counts as a suffix, and how the two extensions the catalog
// leaves ambiguous are resolved.
public static partial class TestLayoutDetector
{
    // Dependencies, build output and generated trees carry other people's
    // languages. Counting them would report the vendored ecosystem instead of
    // the repository's own.
    private static readonly HashSet<string> ExcludedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "vendor", "third_party", "thirdparty", "dist", "build", "out", "target",
        "bin", "obj", ".venv", "venv", "site-packages", "Pods", "Carthage", "bower_components",
        ".terraform", "_build", "deps", "generated", "gen", ".gradle", ".next", ".svelte-kit", "coverage",
        "testdata", "fixtures", "__fixtures__",
    };

    private static readonly string[] ExcludedNames = ["package-lock.json", "yarn.lock", "Cargo.lock"];

    private static readonly string[] ExcludedNameSuffixes = [".lock", ".pb.go", ".g.dart", "_pb2.py"];

    // How much of a file the Prolog sniff may look at. A directive sits at the
    // top of the file or nowhere.
    private const int HeadCharacters = 2048;

    // The two collisions the catalog refuses to resolve on its own. ".d" is
    // either D or a Makefile depfile; ".pl", ".pro" and ".p" are either Perl (or
    // nothing) or Prolog.
    private const string DepfileExtension = ".d";

    // Whether this tracked path is vendored, generated or a lock file.
    private static bool IsExcluded(string path)
    {
        foreach (var segment in path.Split('/'))
        {
            if (ExcludedSegments.Contains(segment))
                return true;
        }

        var name = path[(path.LastIndexOf('/') + 1)..];
        if (ExcludedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            return true;

        foreach (var suffix in ExcludedNameSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return name.Contains(".generated.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The lowercased last suffix, or null when the name carries none. A dotfile
    /// such as <c>.gitignore</c> is a name rather than a suffix, so it is skipped
    /// instead of counting as an extension no language claims.
    /// </summary>
    private static string? SuffixOf(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Length < 2 || extension.Length == fileName.Length
            ? null
            : extension.ToLowerInvariant();
    }

    /// <summary>
    /// Whether the head of a file reads as Prolog: a first non-blank line opening
    /// with a directive, or a module/test declaration anywhere in the head.
    /// </summary>
    private static bool IsPrologSource(string? head)
    {
        if (string.IsNullOrWhiteSpace(head))
            return false;

        if (head.Contains(":- module(", StringComparison.Ordinal)
            || head.Contains(":- begin_tests(", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var line in head.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                return trimmed.StartsWith(":-", StringComparison.Ordinal);
        }

        return false;
    }

    // The language a counted file belongs to: null means the extension is real
    // but no catalog row claims it, and Skip means the file is not source at all.
    private static (bool Skip, string? LanguageId) ResolveLanguage(
        string path, string extension, HashSet<string> objectStems, Func<string, string?> readHead)
    {
        var ambiguous = TestLayoutCatalog.Ambiguous
            .FirstOrDefault(entry => string.Equals(entry.Extension, extension, StringComparison.Ordinal));
        if (ambiguous is null)
            return (false, TestLayoutCatalog.ByExtension(extension)?.Id);

        // A ".d" beside a same-stem ".o" in the same directory is a Makefile
        // depfile, which is generated output rather than a D source file.
        if (string.Equals(extension, DepfileExtension, StringComparison.Ordinal))
        {
            return objectStems.Contains(StemKey(path))
                ? (true, null)
                : (false, ambiguous.AlternativeLanguageId);
        }

        if (IsPrologSource(readHead(path)))
            return (false, ambiguous.AlternativeLanguageId);

        // No directive: ".pl" reads as Perl, and ".pro"/".p" have no safe default.
        return ambiguous.DefaultLanguageId is { } fallback ? (false, fallback) : (true, null);
    }

    // Directory plus stem, so "src/foo.d" and "src/foo.o" share a key while
    // "tools/foo.o" does not.
    private static string StemKey(string path)
    {
        var slash = path.LastIndexOf('/');
        return path[..(slash + 1)] + Path.GetFileNameWithoutExtension(path[(slash + 1)..]);
    }

    // Best-effort peek at a tracked file for the Prolog sniff; an unreadable file
    // simply carries no evidence.
    private static string? ReadHead(string rootPath, string relativePath)
    {
        try
        {
            var full = Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
                return null;

            using var reader = new StreamReader(full);
            var buffer = new char[HeadCharacters];
            var read = reader.Read(buffer, 0, buffer.Length);
            return read <= 0 ? null : new string(buffer, 0, read);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
