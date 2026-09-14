using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

/// <summary>
/// A package.json test script runs through the project's package manager, the way the project and
/// its CI run it. Copying the script's body lost what the package manager adds, node_modules/.bin on
/// the PATH (and any pretest step). Measured on the Mac with moment/luxon, whose script is
/// "jest --coverage": bootstrap ran the body in a plain shell, got "/bin/sh: jest: command not found",
/// and wrote the placeholder test command, so no test would ever have run.
/// </summary>
public sealed partial class TestCommandDetectorTests
{
    [Theory]
    [InlineData(null, new[] { "npm test" })]
    [InlineData("yarn.lock", new[] { "yarn test", "npm test" })]
    [InlineData("pnpm-lock.yaml", new[] { "pnpm test", "npm test" })]
    [InlineData("bun.lock", new[] { "bun run test", "bun test" })]
    public void DetectCandidates_APackageJsonTestScript_RunsThroughItsPackageManager(string? lockfile, string[] expected)
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "package.json"), """{ "scripts": { "test": "jest --coverage" } }""");
        if (lockfile is not null)
            File.WriteAllText(Path.Combine(repo.Root, lockfile), "");

        Assert.Equal(expected, TestCommandDetector.DetectCandidates(repo.Root));
    }

    /// <summary>
    /// The targeted form still comes from the script's runner, reached through npx when the project
    /// installs it: the body's own binary is no more on the PATH for one file than for the suite.
    /// </summary>
    [Theory]
    [InlineData("jest --coverage", null, "npx jest {files}")]
    [InlineData("vitest", null, "npx vitest run {files}")]
    [InlineData("mocha --recursive", "mocha", "npx mocha --recursive {files}")]
    [InlineData("vitest run && tsc --noEmit", null, null)]
    [InlineData("node --test", null, null)]
    public void PerFileForm_OfAPackageManagerTestCommand_ComesFromTheScript(string body, string? localBin, string? expected)
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "package.json"), $$"""{ "scripts": { "test": "{{body}}" } }""");
        if (localBin is not null)
        {
            Directory.CreateDirectory(Path.Combine(repo.Root, "node_modules", ".bin"));
            File.WriteAllText(Path.Combine(repo.Root, "node_modules", ".bin", localBin), "");
        }

        Assert.Equal(expected, TestCommandDetector.PerFileForm("npm test", repo.Root));
    }
}
