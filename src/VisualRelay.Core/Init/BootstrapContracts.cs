namespace VisualRelay.Core.Init;

/// <summary>Where the written per-file test command came from.</summary>
public enum PerFileCommandSource
{
    /// <summary>The runner table's form, proven on real test files.</summary>
    Table,

    /// <summary>A model proposed it and it was proven on real test files.</summary>
    Proposed,

    /// <summary>Nothing was proven, so none was written and the gate runs the whole suite.</summary>
    None,

    /// <summary>The table's form, written without proof because the repository has no test file.</summary>
    Unproven,
}

/// <summary>
/// Asks for a per-file test command when the table has none or its form was rejected.
/// It is given the whole-suite command, the test files the proof will use, and any
/// rejected form with its output, so it can read the project's own scripts and answer
/// with a form that keeps their flags.
/// </summary>
/// <param name="testCommand">The whole-suite command already accepted.</param>
/// <param name="testFiles">The files the proof will substitute.</param>
/// <param name="rejectedForm">The table's form and why it failed, or null.</param>
/// <param name="cancellationToken">Cancellation.</param>
/// <returns>The proposed command, or null.</returns>
public delegate Task<string?> ProposePerFileCommand(
    string testCommand,
    IReadOnlyList<string> testFiles,
    string? rejectedForm,
    CancellationToken cancellationToken);

/// <summary>Where the written test command came from.</summary>
public enum TestCommandSource
{
    /// <summary>A built-in candidate from the marker-file table, which passed its check.</summary>
    Detected,

    /// <summary>A model proposed it after reading the repository, and it passed the same check.</summary>
    Proposed,

    /// <summary>Nothing passed, so the no-op placeholder was written.</summary>
    Placeholder,
}
