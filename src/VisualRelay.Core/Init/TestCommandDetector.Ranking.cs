namespace VisualRelay.Core.Init;

/// <summary>A detected test command and the toolchain whose marker produced it.</summary>
/// <param name="Command">The command, as bootstrap would write it.</param>
/// <param name="Toolchain">
/// The toolchain: dotnet, node, bun, python, rust, go, swift, maven, gradle, ruby, php, cmake, or
/// <see cref="TestsFolderGuess"/>.
/// </param>
public sealed record TestCommandCandidate(string Command, string Toolchain)
{
    /// <summary>The toolchain of pytest guessed from a tests folder alone, with no build marker.</summary>
    public const string TestsFolderGuess = "tests-folder";

    /// <summary>Whether only a tests folder suggested this candidate: a guess, not a detected toolchain.</summary>
    public bool IsGuess => Toolchain == TestsFolderGuess;
}

public static partial class TestCommandDetector
{
    private static readonly string[] ScriptExtensions =
        [".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".mts", ".cts", ".vue", ".svelte"];

    private static readonly string[] JvmExtensions = [".java", ".kt", ".scala", ".groovy"];

    /// <summary>The source files each toolchain's tests cover, by extension.</summary>
    private static readonly Dictionary<string, string[]> SourceExtensions = new(StringComparer.Ordinal)
    {
        ["dotnet"] = [".cs", ".fs", ".vb"],
        ["node"] = ScriptExtensions,
        ["bun"] = ScriptExtensions,
        ["python"] = [".py"],
        [TestCommandCandidate.TestsFolderGuess] = [".py"],
        ["rust"] = [".rs"],
        ["go"] = [".go"],
        ["swift"] = [".swift"],
        ["maven"] = JvmExtensions,
        ["gradle"] = JvmExtensions,
        ["ruby"] = [".rb"],
        ["php"] = [".php"],
        ["cmake"] = [".c", ".cc", ".cpp", ".cxx", ".h", ".hh", ".hpp"],
    };

    /// <summary>
    /// Moves a candidate whose toolchain covers under a tenth of the source files of the
    /// best-represented candidate's toolchain after the others, keeping the priority order within
    /// each group. A second toolchain that small is the repository's tooling, not its product:
    /// zeroshot's package.json test script ran 4 tooling tests beside 634 Rust files.
    /// </summary>
    private static List<TestCommandCandidate> RankByLanguageShare(
        List<TestCommandCandidate> candidates, IReadOnlyDictionary<string, int> filesByExtension)
    {
        int FilesOf(TestCommandCandidate candidate) =>
            SourceExtensions.GetValueOrDefault(candidate.Toolchain, []).Sum(filesByExtension.GetValueOrDefault);

        var most = candidates.Select(FilesOf).DefaultIfEmpty(0).Max();
        return [.. candidates.OrderBy(candidate => FilesOf(candidate) * 10 < most)];
    }
}
