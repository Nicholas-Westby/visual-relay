using System.Text.Json;

namespace VisualRelay.Core.Init;

// Detects test-command candidates from build-system markers in priority order.
// Detect() returns the first candidate (backward-compatible convenience).
// DetectCandidates() returns all candidates so callers can smoke-validate each
// and fall through to the next when the first can't even start.
//
// Priority order (strongest → weakest signal):
//   1. .NET         (*.slnx / *.sln / *.csproj)  → "dotnet test"
//   2. Bun           (bun.lock / bunfig.toml)     → "bun test"
//   3. Python        (pyproject.toml / setup.py / pytest.ini) → "pytest"
//   4. Rust          (Cargo.toml)                 → "cargo test"
//   5. Go            (go.mod)                     → "go test ./..."
//   6. Swift         (Package.swift)              → "swift test"
//   7. Node          (package.json)              → scripts.test value or "npm test"
//   8. Python (weak) (tests/ directory only)     → "pytest"  ← LAST, weakest signal
public static class TestCommandDetector
{
    /// <summary>
    /// Convenience: first candidate or empty string (backward-compatible).
    /// </summary>
    public static string Detect(string rootPath) =>
        DetectCandidates(rootPath).FirstOrDefault() ?? string.Empty;

    /// <summary>
    /// Returns every candidate in priority order so the caller can smoke-run
    /// each one and fall through to the next on rejection.
    /// </summary>
    public static IReadOnlyList<string> DetectCandidates(string rootPath)
    {
        var candidates = new List<string>();

        // 1. .NET
        if (HasAnyFile(rootPath, "*.slnx", "*.sln", "*.csproj"))
        {
            candidates.Add("dotnet test");
        }

        // 2. Bun
        if (File.Exists(Path.Combine(rootPath, "bun.lock"))
            || File.Exists(Path.Combine(rootPath, "bunfig.toml")))
        {
            candidates.Add("bun test");
        }

        // 3. Python (strong signals — NOT tests/ directory)
        if (File.Exists(Path.Combine(rootPath, "pyproject.toml"))
            || File.Exists(Path.Combine(rootPath, "setup.py"))
            || File.Exists(Path.Combine(rootPath, "pytest.ini")))
        {
            candidates.Add("pytest");
        }

        // 4. Rust
        if (File.Exists(Path.Combine(rootPath, "Cargo.toml")))
        {
            candidates.Add("cargo test");
        }

        // 5. Go
        if (File.Exists(Path.Combine(rootPath, "go.mod")))
        {
            candidates.Add("go test ./...");
        }

        // 6. Swift (SwiftPM)
        if (File.Exists(Path.Combine(rootPath, "Package.swift")))
        {
            candidates.Add("swift test");
        }

        // 7. Node — parse scripts.test when available, otherwise fall back to "npm test"
        if (File.Exists(Path.Combine(rootPath, "package.json")))
        {
            var script = ReadPackageJsonScriptsTest(rootPath);
            candidates.Add(script ?? "npm test");
        }

        // 8. Python (weak) — tests/ directory is a last-resort signal
        if (Directory.Exists(Path.Combine(rootPath, "tests")))
        {
            candidates.Add("pytest");
        }

        return candidates;
    }

    private static string? ReadPackageJsonScriptsTest(string rootPath)
    {
        try
        {
            var path = Path.Combine(rootPath, "package.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("scripts", out var scripts)
                && scripts.ValueKind == JsonValueKind.Object
                && scripts.TryGetProperty("test", out var testScript)
                && testScript.ValueKind == JsonValueKind.String)
            {
                var value = testScript.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }
        catch
        {
            // Best-effort — fall through to npm test on any parse failure.
        }

        return null;
    }

    internal static bool HasAnyFile(string rootPath, params string[] patterns) =>
        patterns.Any(pattern =>
            Directory.EnumerateFiles(rootPath, pattern, SearchOption.TopDirectoryOnly).Any());
}

/// <summary>
/// Detects repo policy guard commands by enumerating <c>tools/guards/*.sh</c>
/// and chaining them with <c> &amp;&amp; </c>. When a .NET solution file
/// (<c>*.slnx</c> or <c>*.sln</c>) exists in the repo root, appends
/// <c>dotnet format &lt;solution&gt; --verify-no-changes</c>. When a
/// SwiftPM manifest (<c>Package.swift</c>) exists, appends
/// <c>swift build</c>. Toolchain checks are appended even when no guard
/// scripts exist. Returns <c>null</c> when neither guards nor a recognized
/// toolchain marker is found — guard detection never blocks init.
/// </summary>
public static class GuardCommandDetector
{
    /// <summary>
    /// Detects the guard command or returns <c>null</c> when no guards or
    /// toolchain markers exist.
    /// </summary>
    public static string? Detect(string rootPath)
    {
        var parts = new List<string>();

        // Collect guard scripts when tools/guards/ exists.
        var guardsDir = Path.Combine(rootPath, "tools", "guards");
        if (Directory.Exists(guardsDir))
        {
            var scripts = Directory.EnumerateFiles(guardsDir, "*.sh")
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(Path.GetFileName)
                .ToList();

            foreach (var script in scripts)
            {
                parts.Add($"tools/guards/{script}");
            }
        }

        // Append dotnet format when a .NET solution file exists.
        var slnx = Directory.EnumerateFiles(rootPath, "*.slnx", SearchOption.TopDirectoryOnly).FirstOrDefault();
        var sln = Directory.EnumerateFiles(rootPath, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
        var solution = slnx ?? sln;
        if (solution is not null)
        {
            parts.Add($"dotnet format {Path.GetFileName(solution)} --verify-no-changes");
        }

        // Append "swift build" when a SwiftPM manifest exists.
        if (File.Exists(Path.Combine(rootPath, "Package.swift")))
        {
            parts.Add("swift build");
        }

        if (parts.Count == 0)
            return null;

        return string.Join(" && ", parts);
    }
}

/// <summary>
/// Detects a whole-project formatter command by inspecting build-system markers
/// in priority order (same order as <see cref="TestCommandDetector"/>).
/// Returns <c>null</c> when no recognized toolchain is found — format detection
/// never blocks init.
/// </summary>
public static class FormatCommandDetector
{
    /// <summary>
    /// Detects the format command or returns <c>null</c> when no toolchain
    /// markers are found.
    /// </summary>
    public static string? Detect(string rootPath)
    {
        // .NET solution or project
        var slnx = Directory.EnumerateFiles(rootPath, "*.slnx", SearchOption.TopDirectoryOnly).FirstOrDefault();
        var sln = Directory.EnumerateFiles(rootPath, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (slnx is not null || sln is not null)
            return $"dotnet format {Path.GetFileName(slnx ?? sln!)}";
        if (TestCommandDetector.HasAnyFile(rootPath, "*.csproj"))
            return "dotnet format";

        // Bun / Node — look for a format script in package.json; fall back to prettier
        if (File.Exists(Path.Combine(rootPath, "package.json")))
        {
            var fmt = ReadPackageJsonFormatScript(rootPath);
            return fmt ?? "prettier --write .";
        }

        // Go
        if (File.Exists(Path.Combine(rootPath, "go.mod")))
            return "gofmt -w .";

        // Rust
        if (File.Exists(Path.Combine(rootPath, "Cargo.toml")))
            return "cargo fmt";

        // SwiftPM — swiftformat is the de-facto formatter.
        if (File.Exists(Path.Combine(rootPath, "Package.swift")))
            return "swiftformat .";

        return null;
    }

    private static string? ReadPackageJsonFormatScript(string rootPath)
    {
        try
        {
            var path = Path.Combine(rootPath, "package.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("scripts", out var scripts)
                && scripts.ValueKind == JsonValueKind.Object
                && scripts.TryGetProperty("format", out var formatScript)
                && formatScript.ValueKind == JsonValueKind.String)
            {
                var value = formatScript.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }
        catch
        {
            // Best-effort — fall through to prettier on any parse failure.
        }

        return null;
    }
}
