namespace VisualRelay.Core.Init;

// Best-guess test command for a project root, by build-system markers. Returns
// empty string when the project type is unrecognized (caller falls back to
// manual entry or LLM discovery). .NET is checked first so a .NET repo that also
// has a tests/ directory is not mistaken for Python.
public static class TestCommandDetector
{
    public static string Detect(string rootPath)
    {
        if (HasAnyFile(rootPath, "*.slnx", "*.sln", "*.csproj"))
        {
            return "dotnet test";
        }

        if (File.Exists(Path.Combine(rootPath, "pyproject.toml"))
            || File.Exists(Path.Combine(rootPath, "setup.py"))
            || Directory.Exists(Path.Combine(rootPath, "tests")))
        {
            return "pytest";
        }

        if (File.Exists(Path.Combine(rootPath, "package.json")))
        {
            return "npm test";
        }

        if (File.Exists(Path.Combine(rootPath, "Cargo.toml")))
        {
            return "cargo test";
        }

        if (File.Exists(Path.Combine(rootPath, "go.mod")))
        {
            return "go test ./...";
        }

        return string.Empty;
    }

    private static bool HasAnyFile(string rootPath, params string[] patterns) =>
        patterns.Any(pattern =>
            Directory.EnumerateFiles(rootPath, pattern, SearchOption.TopDirectoryOnly).Any());
}
