using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

public sealed class TestCommandDetectorTests
{
    // ── Detect (backward-compatible convenience) ────────────────────────

    [Fact]
    public void Detect_DotnetProject_ReturnsDotnetTest()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "App.csproj"), "<Project/>");
        Assert.Equal("dotnet test", TestCommandDetector.Detect(repo.Root));
    }

    [Fact]
    public void Detect_PythonProject_ReturnsPytest()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "pyproject.toml"), "[project]");
        Assert.Equal("pytest", TestCommandDetector.Detect(repo.Root));
    }

    [Fact]
    public void Detect_NodeProject_ReturnsNpmTest()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "package.json"), "{}");
        Assert.Equal("npm test", TestCommandDetector.Detect(repo.Root));
    }

    [Fact]
    public void Detect_UnknownProject_ReturnsEmpty()
    {
        using var repo = TestRepository.Create();
        Assert.Equal(string.Empty, TestCommandDetector.Detect(repo.Root));
    }

    // ── New markers: Bun ────────────────────────────────────────────────

    [Fact]
    public void Detect_BunLock_ReturnsBunTest()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "bun.lock"), "");
        Assert.Equal("bun test", TestCommandDetector.Detect(repo.Root));
    }

    [Fact]
    public void Detect_BunfigToml_ReturnsBunTest()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "bunfig.toml"), "");
        Assert.Equal("bun test", TestCommandDetector.Detect(repo.Root));
    }

    [Fact]
    public void Detect_BunLockAndPackageJson_BunBeforeNpm()
    {
        // Bun markers must outrank package.json so a Bun+Node repo
        // (like JobFinder) is detected as bun, not npm.
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "bun.lock"), "");
        File.WriteAllText(Path.Combine(repo.Root, "package.json"), "{}");
        Assert.Equal("bun test", TestCommandDetector.Detect(repo.Root));
    }

    // ── New markers: package.json scripts.test ─────────────────────────

    [Fact]
    public void Detect_PackageJsonWithScriptsTest_ReturnsScriptValue()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "package.json"),
            """{ "scripts": { "test": "vitest run" } }""");
        Assert.Equal("vitest run", TestCommandDetector.Detect(repo.Root));
    }

    [Fact]
    public void Detect_PackageJsonScriptsTestFallsBackToNpmTest()
    {
        // package.json without scripts.test → "npm test" (current behavior,
        // still correct as a fallback).
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "package.json"), """{ "scripts": {} }""");
        Assert.Equal("npm test", TestCommandDetector.Detect(repo.Root));
    }

    // ── New markers: pytest.ini ────────────────────────────────────────

    [Fact]
    public void Detect_PytestIni_ReturnsPytest()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "pytest.ini"), "[pytest]");
        Assert.Equal("pytest", TestCommandDetector.Detect(repo.Root));
    }

    // ── THE BUG: tests/ dir + bun repo must NOT return pytest ───────────

    [Fact]
    public void Detect_TestsDirAndBunLock_BunBeforePytest()
    {
        // This is the exact scenario from the JobFinder bug report:
        // bun.lock + tests/ directory → must return "bun test", NOT "pytest".
        // The tests/ directory is a weak signal that must rank LAST.
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, "tests"));
        File.WriteAllText(Path.Combine(repo.Root, "bun.lock"), "");
        Assert.Equal("bun test", TestCommandDetector.Detect(repo.Root));
    }

    [Fact]
    public void Detect_TestsDirAndPackageJson_PackageJsonBeforePytest()
    {
        // package.json with tests/ dir → npm test (or scripts.test value)
        // must outrank pytest from the tests/ heuristic.
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, "tests"));
        File.WriteAllText(Path.Combine(repo.Root, "package.json"), "{}");
        Assert.Equal("npm test", TestCommandDetector.Detect(repo.Root));
    }

    [Fact]
    public void Detect_OnlyTestsDir_ReturnsPytest()
    {
        // The tests/ directory alone is still a valid (weak) signal for
        // pytest when nothing else matches.
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, "tests"));
        Assert.Equal("pytest", TestCommandDetector.Detect(repo.Root));
    }

    // ── DetectCandidates (priority-ordered list) ───────────────────────

    [Fact]
    public void DetectCandidates_BunLockAndPackageJsonWithScripts_ReturnsBothInOrder()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "bun.lock"), "");
        File.WriteAllText(Path.Combine(repo.Root, "package.json"),
            """{ "scripts": { "test": "vitest run" } }""");

        var candidates = TestCommandDetector.DetectCandidates(repo.Root);

        // bun.lock outranks package.json even when package.json has scripts.test.
        Assert.Equal(["bun test", "vitest run"], candidates);
    }

    [Fact]
    public void DetectCandidates_SlnAndPytestIni_ReturnsBothInOrder()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "App.sln"), "");
        File.WriteAllText(Path.Combine(repo.Root, "pytest.ini"), "[pytest]");

        var candidates = TestCommandDetector.DetectCandidates(repo.Root);

        // dotnet outranks python
        Assert.Equal(["dotnet test", "pytest"], candidates);
    }

    [Fact]
    public void DetectCandidates_AllMarkers_ReturnsFullPriorityOrder()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "App.sln"), "");           // dotnet
        File.WriteAllText(Path.Combine(repo.Root, "bun.lock"), "");          // bun
        File.WriteAllText(Path.Combine(repo.Root, "pyproject.toml"), "");    // python
        File.WriteAllText(Path.Combine(repo.Root, "Cargo.toml"), "");        // rust
        File.WriteAllText(Path.Combine(repo.Root, "go.mod"), "");            // go
        File.WriteAllText(Path.Combine(repo.Root, "package.json"), "{}");    // node
        Directory.CreateDirectory(Path.Combine(repo.Root, "tests"));         // weak python

        var candidates = TestCommandDetector.DetectCandidates(repo.Root);

        Assert.Equal([
            "dotnet test",
            "bun test",
            "pytest",
            "cargo test",
            "go test ./...",
            "npm test",
            "pytest"   // tests/ dir weak signal, last
        ], candidates);
    }

    [Fact]
    public void DetectCandidates_Unknown_ReturnsEmpty()
    {
        using var repo = TestRepository.Create();
        var candidates = TestCommandDetector.DetectCandidates(repo.Root);
        Assert.Empty(candidates);
    }

    [Fact]
    public void Detect_WhenCandidatesEmpty_ReturnsEmptyString()
    {
        // Detect() is DetectCandidates().FirstOrDefault() ?? ""
        using var repo = TestRepository.Create();
        Assert.Equal(string.Empty, TestCommandDetector.Detect(repo.Root));
    }

    [Fact]
    public void Detect_WhenCandidatesExist_ReturnsFirst()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "Cargo.toml"), "");
        File.WriteAllText(Path.Combine(repo.Root, "go.mod"), "");

        // Detect returns the highest-priority candidate (cargo before go in priority order)
        Assert.Equal("cargo test", TestCommandDetector.Detect(repo.Root));
    }
}
