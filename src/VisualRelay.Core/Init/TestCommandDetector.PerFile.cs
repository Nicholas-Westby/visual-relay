namespace VisualRelay.Core.Init;

public static partial class TestCommandDetector
{
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
    /// The per-file form of a command run in <paramref name="rootPath"/>. A package manager's test
    /// command takes it from the script the package manager runs, and a runner the project installs
    /// is reached through npx: the script's binary is no more on a plain shell's PATH for one file
    /// than for the whole suite.
    /// </summary>
    /// <param name="testCommand">The detected whole-suite command.</param>
    /// <param name="rootPath">The repository the command runs in.</param>
    /// <returns>The command with a <c>{files}</c> token, or null.</returns>
    public static string? PerFileForm(string testCommand, string rootPath)
    {
        if (!PackageManagerTests.Contains(testCommand.Trim()) || ReadPackageJsonScriptsTest(rootPath) is not { } script)
            return PerFileForm(testCommand);

        if (PerFileForm(script) is not { } form)
            return null;
        var runner = form.Split(' ', 2)[0];
        return !runner.Contains('/') && File.Exists(Path.Combine(rootPath, "node_modules", ".bin", runner))
            ? $"npx {form}"
            : form;
    }

    private static readonly string[] PackageManagerTests = ["npm test", "yarn test", "pnpm test", "bun run test"];

    /// <summary>
    /// The package manager commands that run a package.json test script: the lockfile's manager, and
    /// npm after yarn or pnpm, which can run the same installed script when that manager is missing.
    /// </summary>
    private static IReadOnlyList<string> PackageManagerTestCommands(string rootPath)
    {
        if (File.Exists(Path.Combine(rootPath, "bun.lock")) || File.Exists(Path.Combine(rootPath, "bun.lockb")))
            return ["bun run test"];
        if (File.Exists(Path.Combine(rootPath, "pnpm-lock.yaml")))
            return ["pnpm test", "npm test"];
        return File.Exists(Path.Combine(rootPath, "yarn.lock")) ? ["yarn test", "npm test"] : ["npm test"];
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
}
