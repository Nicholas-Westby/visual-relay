using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

/// <summary>
/// A package.json format script runs through the package manager, and only a script that writes is
/// the formatter. Measured on the Windows arm with i18next/i18next: bootstrap copied the "format"
/// script's body, which runs a prettier installed in node_modules, so the pre-guard format answered
/// "/bin/sh: 1: prettier: not found" on every run and nobody saw it; and that script only checks
/// (--check), while the writer is "format:fix". moment/luxon on the Mac got the same copied body.
/// </summary>
public sealed partial class FormatCommandDetectorTests
{
    private const string I18NextScripts = """
        { "scripts": {
          "format": "prettier \"{,**/}*.{ts,tsx,mts,js,mjs,json,md}\" --check",
          "format:fix": "prettier \"{,**/}*.{ts,tsx,mts,js,mjs,json,md}\" --write"
        } }
        """;

    private const string LuxonScripts = """
        { "scripts": {
          "format": "prettier --write 'src/**/*.js' 'test/**/*.js' 'benchmarks/*.js'",
          "format-check": "prettier --check 'src/**/*.js' 'test/**/*.js' 'benchmarks/*.js'"
        } }
        """;

    [Theory]
    [InlineData(I18NextScripts, null, "npm run format:fix")]
    [InlineData(LuxonScripts, null, "npm run format")]
    [InlineData(LuxonScripts, "bun.lock", "bun run format")]
    public void Detect_AWritingFormatScript_RunsThroughThePackageManager(string packageJson, string? lockfile, string expected)
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "package.json"), packageJson);
        if (lockfile is not null)
            File.WriteAllText(Path.Combine(repo.Root, lockfile), "");

        Assert.Equal(expected, FormatCommandDetector.Detect(repo.Root));
    }

    /// <summary>A format script that only checks never reformats anything, so it is no formatter.</summary>
    [Fact]
    public void Detect_OnlyACheckingFormatScript_IsNoFormatter()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "package.json"),
            """{ "scripts": { "format": "prettier . --check" } }""");

        Assert.Null(FormatCommandDetector.Detect(repo.Root));
    }
}
