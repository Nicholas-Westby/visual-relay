using System.Collections.Frozen;
using VisualRelay.Core.Execution;

namespace VisualRelay.Core.Init;

/// <summary>
/// What init found out about a repository's test layout.
/// </summary>
/// <param name="DetectedLanguages">
/// The reportable languages present, ordered by counted files descending then id.
/// Never contains a <see cref="TestLayoutBucket.None"/> language: markup, data and
/// build files do not decide how tests are gated.
/// </param>
/// <param name="InlineTestExtensions">The inline extensions those languages contribute.</param>
/// <param name="CountsByExtension">Every counted extension and its file count, for the record.</param>
/// <param name="CountedFiles">How many files the counts were taken over.</param>
public sealed record TestLayoutDetection(
    IReadOnlyList<string> DetectedLanguages,
    IReadOnlyList<string> InlineTestExtensions,
    IReadOnlyDictionary<string, int> CountsByExtension,
    int CountedFiles);

/// <summary>
/// Decides which languages a repository is written in, and therefore which
/// extensions may legitimately carry tests beside the implementation. Works from
/// the tracked-file list alone — never a filesystem walk — so a build tree, a
/// vendored dependency or an untracked scratch file cannot vote.
/// </summary>
public static partial class TestLayoutDetector
{
    // Nothing tracked, nothing detected. Frozen (not a plain Dictionary) so this
    // shared singleton can never be corrupted by a caller that downcasts
    // CountsByExtension and mutates it — every DetectAsync/Detect caller that
    // finds nothing gets back the exact same safe-to-share instance.
    private static readonly TestLayoutDetection Empty =
        new([], [], FrozenDictionary<string, int>.Empty, 0);

    // A marker only speaks for the project it sits in: monorepos keep Cargo.toml
    // under packages/*, but a manifest buried deeper is a fixture or an example.
    private const int MaxMarkerDepth = 3;

    /// <summary>
    /// Runs <c>git ls-files</c> in <paramref name="rootPath"/> and detects the
    /// layout from it. A folder that is not a repository yet detects nothing.
    /// </summary>
    /// <param name="rootPath">The repository root.</param>
    /// <param name="git">The injected git invoker.</param>
    /// <param name="cancellationToken">Cancels the git call.</param>
    /// <returns>The detection.</returns>
    public static async Task<TestLayoutDetection> DetectAsync(
        string rootPath, IGitInvoker git, CancellationToken cancellationToken)
    {
        var (exitCode, output, timedOut) = await git.RunAsync(rootPath, ["ls-files"], cancellationToken);
        if (exitCode != 0 || timedOut)
            return Empty;

        var tracked = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Detect(tracked, path => ReadHead(rootPath, path));
    }

    /// <summary>
    /// The pure detection over an already-enumerated tracked-file list.
    /// </summary>
    /// <param name="trackedPaths">Repository-relative tracked paths.</param>
    /// <param name="readHead">
    /// Returns the first characters of a tracked file, or null when it cannot be
    /// read. Consulted only for the extensions Prolog shares with Perl.
    /// </param>
    /// <returns>The detection.</returns>
    public static TestLayoutDetection Detect(IReadOnlyList<string> trackedPaths, Func<string, string?> readHead)
    {
        var surviving = trackedPaths
            .Select(path => path.Replace('\\', '/').Trim())
            .Where(path => path.Length > 0 && !IsExcluded(path))
            .ToList();
        if (surviving.Count == 0)
            return Empty;

        var objectStems = new HashSet<string>(
            surviving.Where(path => path.EndsWith(".o", StringComparison.OrdinalIgnoreCase)).Select(StemKey),
            StringComparer.OrdinalIgnoreCase);

        var countsByExtension = new Dictionary<string, int>(StringComparer.Ordinal);
        var countsByLanguage = new Dictionary<string, int>(StringComparer.Ordinal);
        var counted = 0;

        foreach (var path in surviving)
        {
            if (SuffixOf(Path.GetFileName(path)) is not { } extension)
                continue;

            var (skip, languageId) = ResolveLanguage(path, extension, objectStems, readHead);
            if (skip)
                continue;

            counted++;
            countsByExtension[extension] = countsByExtension.GetValueOrDefault(extension) + 1;
            if (languageId is not null)
                countsByLanguage[languageId] = countsByLanguage.GetValueOrDefault(languageId) + 1;
        }

        var present = MarkedLanguages(surviving);
        foreach (var (id, count) in countsByLanguage)
        {
            if (TestLayoutCatalog.ById(id) is { } row && ReachesFloor(row, count, counted))
                present.Add(id);
        }

        var detected = present
            .OrderByDescending(countsByLanguage.GetValueOrDefault)
            .ThenBy(id => id, StringComparer.Ordinal)
            .ToList();

        return new TestLayoutDetection(
            detected, TestLayoutCatalog.InlineExtensionsFor(detected), countsByExtension, counted);
    }

    // A marker file means its language is present whatever the counts say — one
    // Cargo.toml is proof enough that .rs files may carry inline test modules.
    private static HashSet<string> MarkedLanguages(IEnumerable<string> paths)
    {
        var marked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (path.Count(character => character == '/') > MaxMarkerDepth)
                continue;
            if (TestLayoutCatalog.ByMarker(path) is { Bucket: not TestLayoutBucket.None } row)
                marked.Add(row.Id);
        }

        return marked;
    }

    /// <summary>
    /// Without a marker a language has to earn its place. An inline language is
    /// reported at two files, because a single one with a <c>#[cfg(test)]</c>
    /// module already breaks path gating; a separate-file language needs three
    /// files, or two that are at least two percent of the repository, so a stray
    /// sample in a documentation folder never renames the project's language.
    /// </summary>
    private static bool ReachesFloor(TestLayoutLanguage row, int count, int countedFiles) => row.Bucket switch
    {
        TestLayoutBucket.Inline => count >= 2,
        TestLayoutBucket.Separate => count >= 3 || (count >= 2 && count * 50 >= countedFiles),
        _ => false,
    };
}
