namespace VisualRelay.Domain;

public sealed record SubagentResult(
    string RawText,
    string? Json,
    bool IsValid,
    string? Error,
    // True for a HARD infra abort the caller must NOT escalate around — the absolute
    // wall-clock ceiling kill or a backend socket wedge — as opposed to an ordinary
    // escalatable failure (contract reject / nonzero exit / persistent stall). The
    // driver's fix-verify loop reads this to flag-immediately vs escalate-and-retry.
    bool HardAbort = false);

