using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace VisualRelay.Tests;

/// <summary>
/// Assembly-wide fixture that globs, reads, and Roslyn-parses every non-build-artifact
/// <c>.cs</c> file under <c>tests/VisualRelay.Tests/</c>, <c>src/</c>, and <c>tools/</c>
/// exactly once. The parsed <see cref="SyntaxTree"/> list is read-only after construction,
/// safe to share across all parallel test workers. Each guard test injects this fixture
/// and reads the pre-parsed list instead of re-globbing and re-parsing.
/// </summary>
public sealed class CachedSyntaxTreesFixture : IAsyncLifetime
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    /// <summary>
    /// All parsed trees, keyed by repo-relative path. Read-only after
    /// <see cref="IAsyncLifetime.InitializeAsync"/> completes.
    /// </summary>
    public IReadOnlyList<(string RelativePath, SyntaxTree Tree)> AllTrees { get; private set; } = [];

    async ValueTask IAsyncLifetime.InitializeAsync()
    {
        var root = RepoSetup.Root;
        var trees = new List<(string, SyntaxTree)>();

        foreach (var dir in new[] { "tests/VisualRelay.Tests", "src", "tools" })
        {
            var fullDir = Path.Combine(root, dir);
            if (!Directory.Exists(fullDir))
                continue;

            foreach (var file in Directory.EnumerateFiles(fullDir, "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildArtifact(file))
                    continue;

                var relativePath = Path.GetRelativePath(root, file);
                var source = await File.ReadAllTextAsync(file);
                var tree = CSharpSyntaxTree.ParseText(source, ParseOptions, file);
                trees.Add((relativePath, tree));
            }
        }

        AllTrees = trees;
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await Task.CompletedTask;
    }

    private static bool IsBuildArtifact(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(s => s is "bin" or "obj");
    }
}
