using System.Text.Json;

namespace VisualRelay.Core.Init;

// Detects test-command candidates from build-system markers in priority order.
// Detect() returns the first candidate (backward-compatible convenience).
// DetectCandidates() returns all candidates so callers can smoke-validate each
// and fall through to the next when the first can't even start.
//
// Priority order (strongest → weakest signal):
//   1. .NET         (*.slnx / *.sln / *.csproj)  → "dotnet test"
//   2. Node          (package.json)              → scripts.test value or "npm test"
//   3. Bun           (bun.lock / bunfig.toml)     → "bun test"
//   4. Python        (pyproject.toml / setup.py / pytest.ini) → "pytest"
//   5. Rust          (Cargo.toml)                 → "cargo test"
//   6. Go            (go.mod)                     → "go test ./..."
//   7. Swift         (Package.swift)              → "swift test"
//   8. Maven         (pom.xml)                    → "./mvnw test" | "mvn test"
//   9. Gradle        (build.gradle[.kts] / settings.gradle[.kts])
//                                                 → "./gradlew test" | "gradle test"
//  10. Ruby          (Gemfile / Rakefile)         → "bundle exec rake test", then
//                                                   "bundle exec rake"
//  11. CMake         (CMakeLists.txt)             → configure + build + ctest
//  12. Python (weak) (tests/ or test/ directory only)     → "pytest"  ← LAST, weakest signal
//
// An explicit project script (package.json scripts.test) beats an inferred runner
// (bun test). This way a Bun-lockfile repo whose tests are vitest via scripts.test
// gets the correct command validated first.
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

        // 2. Node — parse scripts.test when available, otherwise fall back to "npm test"
        if (File.Exists(Path.Combine(rootPath, "package.json")))
        {
            var script = ReadPackageJsonScriptsTest(rootPath);
            candidates.Add(script ?? "npm test");
        }

        // 3. Bun
        if (File.Exists(Path.Combine(rootPath, "bun.lock"))
            || File.Exists(Path.Combine(rootPath, "bunfig.toml")))
        {
            candidates.Add("bun test");
        }

        // 4. Python (strong signals — NOT tests/ directory)
        if (File.Exists(Path.Combine(rootPath, "pyproject.toml"))
            || File.Exists(Path.Combine(rootPath, "setup.py"))
            || File.Exists(Path.Combine(rootPath, "pytest.ini")))
        {
            AddPythonCandidates(rootPath, candidates);
        }

        // 5. Rust
        if (File.Exists(Path.Combine(rootPath, "Cargo.toml")))
        {
            candidates.Add("cargo test");
        }

        // 6. Go
        if (File.Exists(Path.Combine(rootPath, "go.mod")))
        {
            candidates.Add("go test ./...");
        }

        // 7. Swift (SwiftPM)
        if (File.Exists(Path.Combine(rootPath, "Package.swift")))
        {
            candidates.Add("swift test");
        }

        // 8. Maven. `pom.xml` is REQUIRED by a Maven build, so it is the single
        //    unambiguous marker; Gradle files can additionally appear in a
        //    Maven-primary repo as an auxiliary/included build, so Maven ranks first
        //    of the two. `test` (not `verify`) is the narrowest lifecycle phase that
        //    runs the Surefire unit tests — `verify` also runs Failsafe integration
        //    tests and packaging, which is far slower and routinely needs external
        //    services, so it is the wrong default for a per-stage test command.
        if (File.Exists(Path.Combine(rootPath, "pom.xml")))
        {
            candidates.Add($"{WrapperOrTool(rootPath, "mvnw", "mvn")} test");
        }

        // 9. Gradle. `test` rather than `check` or `build`: `check` also runs whichever
        //    static-analysis plugins the project applies (checkstyle/spotbugs/ktlint),
        //    which belong in guardCmd, not testCmd, and `build` additionally packages.
        if (HasAnyGradleManifest(rootPath))
        {
            candidates.Add($"{WrapperOrTool(rootPath, "gradlew", "gradle")} test");
        }

        // 10. Ruby. Either marker is enough: a Gemfile is what `bundle exec` needs, and a
        //     Rakefile is where the test task lives — most gems carry both, some only one.
        //     `rake test` is the near-universal task name; the bare `rake` default task is
        //     offered after it for the projects that route tests through it instead.
        if (File.Exists(Path.Combine(rootPath, "Gemfile"))
            || File.Exists(Path.Combine(rootPath, "Rakefile")))
        {
            candidates.Add("bundle exec rake test");
            candidates.Add("bundle exec rake");
        }

        // 11. CMake. `ctest` alone fails on a repo that has never been configured, so the
        //     candidate carries the whole three-step sequence — configure, build, test —
        //     into a `build/` directory the project keeps out of git. It is validated (and
        //     later run) through a shell, which is what makes the && chain work.
        if (File.Exists(Path.Combine(rootPath, "CMakeLists.txt")))
        {
            candidates.Add(
                "cmake -S . -B build && cmake --build build "
                + "&& ctest --test-dir build --output-on-failure");
        }

        // 12. Python (weak) — tests/ or test/ directory is a last-resort signal
        if (Directory.Exists(Path.Combine(rootPath, "tests"))
            || Directory.Exists(Path.Combine(rootPath, "test")))
        {
            AddPythonCandidates(rootPath, candidates);
        }

        return [.. candidates.Distinct(StringComparer.Ordinal)];
    }

    // An interpreter the repository ships inside itself comes first: a virtualenv
    // is how most Python projects pin their runner, and `pytest` is then absent
    // from PATH entirely — which is how a 90-file project with a working
    // .venv/bin/pytest was bootstrapped with the placeholder test command.
    // `-q` because the full pytest header is noise in a stage prompt.
    private static void AddPythonCandidates(string rootPath, List<string> candidates)
    {
        foreach (var venv in new[] { ".venv", "venv" })
        {
            if (File.Exists(Path.Combine(rootPath, venv, "bin", "pytest")))
                candidates.Add($"{venv}/bin/pytest -q");
            if (File.Exists(Path.Combine(rootPath, venv, "bin", "python")))
                candidates.Add($"{venv}/bin/python -m pytest -q");
        }

        candidates.Add("pytest");
    }

    /// <summary>
    /// The per-file form of a detected whole-suite command, or <c>null</c> when the
    /// toolchain has none.
    /// <para>
    /// Only a runner that takes test FILE PATHS as trailing arguments gets one.
    /// Seeding <c>testFileCmd</c> with a copy of the whole-suite command — which is
    /// what bootstrap used to do — makes the stage-5 gate report a targeted run
    /// that is in fact the entire suite; a null leaves the driver's own fallback to
    /// <c>testCmd</c> in charge, and the run log says so.
    /// </para>
    /// </summary>
    /// <param name="testCommand">The detected whole-suite command.</param>
    /// <returns>The command with a <c>{files}</c> token, or null.</returns>
    public static string? PerFileForm(string testCommand)
    {
        var command = testCommand.Trim();
        if (command.Length == 0)
            return null;
        if (command.Contains("{files}", StringComparison.Ordinal))
            return command;
        // A chain, a pipe or a redirect has no trailing argument list to extend.
        if (command.Contains("&&", StringComparison.Ordinal)
            || command.Contains('|') || command.Contains('>') || command.Contains(';'))
            return null;

        var tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return OneShotForm(tokens) ?? (TakesTestFilePaths(tokens) ? command + " {files}" : null);
    }

    /// <summary>
    /// The two runners whose detected command must be REPLACED rather than extended.
    /// Both take file paths, but the detected string is routinely a wrapper script,
    /// and a bare <c>vitest</c> is watch mode — a targeted run that never returns.
    /// Their own one-shot invocation is the per-file form; <c>npx</c> resolves the
    /// project's own copy out of its dependencies first.
    /// </summary>
    private static string? OneShotForm(string[] tokens)
    {
        if (Names(tokens, "vitest"))
            return "npx vitest run {files}";
        return Names(tokens, "jest") ? "npx jest {files}" : null;
    }

    private static bool TakesTestFilePaths(string[] tokens)
    {
        var runner = tokens[0];
        // bun test a.ts b.ts
        if (runner is "bun" && tokens is [_, "test", ..])
            return true;
        // mix test test/a_test.exs
        if (IsNamed(runner, "mix") && tokens is [_, "test", ..])
            return true;
        // pytest tests/a.py, .venv/bin/pytest tests/a.py
        if (IsNamed(runner, "pytest"))
            return true;
        // rspec spec/a_spec.rb and bundle exec rspec …; npx mocha test/a.js;
        // vendor/bin/phpunit tests/AT.php and php vendor/bin/phpunit … — each is
        // routinely reached through a launcher, so the whole command is searched.
        if (Names(tokens, "rspec") || Names(tokens, "mocha") || Names(tokens, "phpunit"))
            return true;
        // python -m pytest tests/a.py, .venv/bin/python -m pytest tests/a.py
        return (IsNamed(runner, "python") || IsNamed(runner, "python3"))
            && tokens.Contains("-m", StringComparer.Ordinal)
            && tokens.Contains("pytest", StringComparer.Ordinal);
    }

    /// <summary>Whether the command names <paramref name="tool"/> — bare, at the end of a path, or after a launcher.</summary>
    private static bool Names(string[] tokens, string tool) =>
        Array.Exists(tokens, token => IsNamed(token, tool));

    /// <summary>Whether a token is the named tool, bare or at the end of a path.</summary>
    private static bool IsNamed(string token, string tool) =>
        token == tool || token.EndsWith("/" + tool, StringComparison.Ordinal);

    // Prefer the checked-in build-tool wrapper over the bare tool: `./mvnw` and
    // `./gradlew` pin the exact build-tool version the project was authored against
    // (and are what its own CI runs), while a machine-wide `mvn`/`gradle` may be
    // absent entirely or a different major version that refuses the build.
    private static string WrapperOrTool(string rootPath, string wrapper, string tool) =>
        File.Exists(Path.Combine(rootPath, wrapper)) ? $"./{wrapper}" : tool;

    // Any one of the four root Gradle manifests marks a Gradle build: Groovy or
    // Kotlin DSL, and settings-only (an umbrella/composite build whose modules
    // carry the build scripts) as well as build-script-only (a single project).
    private static bool HasAnyGradleManifest(string rootPath) =>
        File.Exists(Path.Combine(rootPath, "build.gradle"))
        || File.Exists(Path.Combine(rootPath, "build.gradle.kts"))
        || File.Exists(Path.Combine(rootPath, "settings.gradle"))
        || File.Exists(Path.Combine(rootPath, "settings.gradle.kts"));

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
