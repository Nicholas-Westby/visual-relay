namespace VisualRelay.Cli;

/// <summary>
/// Pure classification of the launcher verb. Replaces the bash <c>case "$cmd"</c>
/// dispatch: it only answers "is this a known subcommand" and supplies the usage
/// line. Execution lives in the IO-thin per-command handlers.
/// </summary>
public static class CommandRouter
{
    /// <summary>Known subcommands, in usage order. <c>run</c> is an alias of <c>launch</c>.</summary>
    private static readonly IReadOnlyList<string> KnownCommands =
    [
        "launch", "run", "build", "test", "format", "screenshot",
        "run-task", "init", "check", "inspect", "gen-backend-config",
        "guards", "install-hooks", "bump-version",
    ];

    public static bool IsKnown(string? command) =>
        command is not null && KnownCommands.Contains(command);

    public static string UsageLine =>
        "usage: ./visual-relay [launch|build|test|format|screenshot|run-task|init|" +
        "install-hooks|bump-version|check|inspect|guards|gen-backend-config]";
}
