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
}
