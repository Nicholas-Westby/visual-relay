using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

/// <summary>What one author-test gate run established about the stage's tests.</summary>
public enum AuthorTestCheck
{
    /// <summary>The targeted command failed without the implementation: the tests are proven red.</summary>
    Red,

    /// <summary>
    /// Nothing could be established. The outcome's reason says which of the ways
    /// to prove nothing this was.
    /// </summary>
    Unproven,
}

/// <summary>
/// One author-test gate run, and everything an operator needs to read it back:
/// what was judged, what ran, what it returned and what was taken away first.
/// </summary>
/// <param name="Check">The check to record, or null when the run is a hard failure the caller must flag.</param>
/// <param name="Reason">
/// Why the check reads as it does: one of the reason constants when unproven, the
/// flag reason when <paramref name="Check"/> is null, and null for a proven red
/// (whose reason is distilled from the command's own output).
/// </param>
/// <param name="Command">The targeted command that ran, or would have run.</param>
/// <param name="ExitCode">What the command returned, or null when nothing ran.</param>
/// <param name="StrippedFiles">The manifest files the gate stashed before running the command.</param>
/// <param name="Verdicts">The scope verdict of every file the stage declared a test.</param>
/// <param name="ReaskRequested">True when the caller must re-ask stage 5 once and judge the result again.</param>
public sealed record AuthorTestGateOutcome(
    AuthorTestCheck? Check,
    string? Reason,
    string Command,
    int? ExitCode,
    IReadOnlyList<string> StrippedFiles,
    IReadOnlyList<AuthorTestScopeVerdict> Verdicts,
    bool ReaskRequested)
{
    // The reason vocabulary is a contract, not an implementation detail: the exact
    // strings reach status.json, run.log and the operator docs, so each one is named
    // here even where only this type spells it today.
    // ReSharper disable MemberCanBePrivate.Global

    /// <summary>The tests passed before anything was taken away, on a file that can carry implementation.</summary>
    public const string GreenBeforeImplementation = "green before implementation";

    /// <summary>The tests passed and there was no implementation to take away.</summary>
    public const string NoImplementationToStrip = "no implementation to strip";

    /// <summary>The command could not run, or ran no tests, so its verdict means nothing.</summary>
    public const string GateCommandUnusable = "gate command unusable";

    /// <summary>The configured command is the bootstrap placeholder: it exits 0 having run nothing.</summary>
    public const string PlaceholderTestCommand = "placeholder test command";

    /// <summary>The stage declared no test files, so there was nothing to gate.</summary>
    public const string NoTestFilesDeclared = "no test files declared";

    /// <summary>The flag reason for tests that still pass once the implementation is gone.</summary>
    public const string StrippedAndGreen = "author-tests passed after implementation files were stripped";

    // ReSharper restore MemberCanBePrivate.Global

    /// <summary>The check as status.json, the seal and the events spell it; null when flagged.</summary>
    public string? CheckName => Check switch
    {
        AuthorTestCheck.Red => "red",
        AuthorTestCheck.Unproven => "unproven",
        _ => null
    };

    /// <summary>An outcome for a gate that never got to run its command.</summary>
    /// <param name="reason">One of the reason constants.</param>
    /// <param name="command">The command that would have run.</param>
    /// <param name="verdicts">The scope verdicts.</param>
    /// <returns>The unproven outcome.</returns>
    internal static AuthorTestGateOutcome Unproven(
        string reason, string command, IReadOnlyList<AuthorTestScopeVerdict> verdicts) =>
        new(AuthorTestCheck.Unproven, reason, command, null, [], verdicts, false);

    /// <summary>An outcome the caller must turn into a flag.</summary>
    /// <param name="reason">The flag reason.</param>
    /// <param name="command">The command that ran or would have run.</param>
    /// <param name="verdicts">The scope verdicts.</param>
    /// <returns>The flagging outcome.</returns>
    internal static AuthorTestGateOutcome Flagged(
        string reason, string command, IReadOnlyList<AuthorTestScopeVerdict> verdicts) =>
        new(null, reason, command, null, [], verdicts, false);

    /// <summary>
    /// Judges one completed gate run. A non-zero exit is the proof the stage
    /// exists for; a zero exit proves something only when the implementation was
    /// taken away first, and is otherwise unproven for one of two reasons — the
    /// declared tests could carry the implementation themselves (re-ask once), or
    /// there was simply nothing to take away.
    /// </summary>
    /// <param name="command">The targeted command that ran.</param>
    /// <param name="result">What the command returned.</param>
    /// <param name="gateUnusable">True when the runner could not execute meaningfully.</param>
    /// <param name="strippedFiles">The files the gate actually stashed before running.</param>
    /// <param name="verdicts">The scope verdicts.</param>
    /// <param name="reaskUsed">True when this run already follows a re-ask; a second one is never asked.</param>
    /// <returns>The run's outcome.</returns>
    internal static AuthorTestGateOutcome ForRun(
        string command,
        TestRunResult result,
        bool gateUnusable,
        IReadOnlyList<string> strippedFiles,
        IReadOnlyList<AuthorTestScopeVerdict> verdicts,
        bool reaskUsed)
    {
        var exitCode = result.ExitCode;
        if (gateUnusable)
            return new(AuthorTestCheck.Unproven, GateCommandUnusable, command, exitCode, strippedFiles, verdicts, false);
        if (exitCode != 0)
            return new(AuthorTestCheck.Red, null, command, exitCode, strippedFiles, verdicts, false);
        if (strippedFiles.Count > 0)
            return new(null, StrippedAndGreen, command, exitCode, strippedFiles, verdicts, false);

        return InlineOrSuspect(verdicts).Count > 0
            ? new(AuthorTestCheck.Unproven, GreenBeforeImplementation, command, exitCode, strippedFiles, verdicts, !reaskUsed)
            : new(AuthorTestCheck.Unproven, NoImplementationToStrip, command, exitCode, strippedFiles, verdicts, false);
    }

    /// <summary>The declared test files that could carry implementation themselves.</summary>
    /// <param name="verdicts">The scope verdicts.</param>
    /// <returns>Their paths, in the order the stage listed them.</returns>
    internal static IReadOnlyList<string> InlineOrSuspect(IReadOnlyList<AuthorTestScopeVerdict> verdicts) =>
        [.. verdicts.Where(v => v.Kind is not AuthorTestScopeKind.Separate).Select(v => v.Path)];
}
