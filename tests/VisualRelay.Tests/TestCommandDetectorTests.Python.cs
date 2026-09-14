using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

/// <summary>
/// Companion to <see cref="TestCommandDetectorTests"/> — the interpreter a Python
/// repository ships inside itself, and which detected commands have a real
/// per-file form. A repository whose only runner is <c>.venv/bin/pytest</c> got
/// the placeholder test command, because nothing looked inside the repository.
/// </summary>
public sealed partial class TestCommandDetectorTests
{
    /// <summary>Creates an executable stub at a repo-relative path.</summary>
    private static void Interpreter(TestRepository repo, string relativePath)
    {
        var full = Path.Combine(repo.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "#!/bin/sh\nexit 0\n");
    }

    [Fact]
    public void DetectCandidates_VenvRunner_IsOfferedBeforeThePathRunner()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "pyproject.toml"), "[project]");
        Interpreter(repo, ".venv/bin/pytest");
        Interpreter(repo, ".venv/bin/python");

        var candidates = TestCommandDetector.DetectCandidates(repo.Root);

        Assert.Equal(".venv/bin/pytest -q", candidates[0]);
        Assert.Equal(".venv/bin/python -m pytest -q", candidates[1]);
        Assert.Contains("pytest", candidates);
        var order = candidates.ToList();
        Assert.True(
            order.IndexOf(".venv/bin/pytest -q") < order.IndexOf("pytest"),
            "the repo-local interpreter must be tried before the one on PATH");
    }

    [Fact]
    public void DetectCandidates_PlainVenvDirectory_IsOfferedToo()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "pyproject.toml"), "[project]");
        Interpreter(repo, "venv/bin/pytest");

        var candidates = TestCommandDetector.DetectCandidates(repo.Root);

        Assert.Equal("venv/bin/pytest -q", candidates[0]);
    }

    [Fact]
    public void DetectCandidates_NoVenv_IsUnchanged()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "pyproject.toml"), "[project]");

        Assert.Equal(["pytest"], TestCommandDetector.DetectCandidates(repo.Root));
    }

    /// <summary>
    /// Only a runner that takes test file paths gets a per-file form. Seeding one
    /// for the rest made the gate claim a targeted run that was the whole suite.
    /// </summary>
    [Theory]
    [InlineData("pytest", "pytest {files}")]
    [InlineData(".venv/bin/pytest -q", ".venv/bin/pytest -q {files}")]
    [InlineData(".venv/bin/python -m pytest -q", ".venv/bin/python -m pytest -q {files}")]
    [InlineData("bun test", "bun test {files}")]
    [InlineData("cargo test", null)]
    [InlineData("go test ./...", null)]
    [InlineData("dotnet test", null)]
    [InlineData("swift test --disable-sandbox", null)]
    [InlineData("./gradlew test", null)]
    [InlineData("bundle exec rake test", null)]
    [InlineData("npm test", null)]
    [InlineData("", null)]
    public void PerFileForm_IsOnlyOfferedForRunnersThatTakeFilePaths(string command, string? expected) =>
        Assert.Equal(expected, TestCommandDetector.PerFileForm(command));

    /// <summary>A command already carrying the token is returned unchanged.</summary>
    [Fact]
    public void PerFileForm_ACommandThatAlreadyTargets_IsKept() =>
        Assert.Equal("pytest -q {files}", TestCommandDetector.PerFileForm("pytest -q {files}"));

    /// <summary>A shell chain has no trailing argument list to append to.</summary>
    [Fact]
    public void PerFileForm_AShellChain_HasNone() =>
        Assert.Null(TestCommandDetector.PerFileForm("cmake -S . -B build && ctest --test-dir build"));
}
