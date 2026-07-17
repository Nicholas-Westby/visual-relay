namespace VisualRelay.Domain;

/// <summary>
/// Structured kill context from the activity watchdog: reason, last signal source,
/// silence duration, and path to the persisted killed-output autopsy artifact.
/// Carried on <see cref="SubagentResult"/> so callers can distinguish a watchdog
/// kill from genuinely invalid model output and enrich flag reasons accordingly.
/// </summary>
public sealed record KillSignature(
    string Reason,        // "absolute_ceiling" | "socket_wedge" | "stall"
    string LastSignal,    // "cpu" | "trace" | "process"
    long SilenceMs,       // silence at kill time
    string? AutopsyPath); // absolute path to .killed-output.txt, or null

public sealed record SubagentResult(
    string RawText,
    string? Json,
    bool IsValid,
    string? Error,
    // True for a HARD infra abort the caller must NOT escalate around — the absolute
    // wall-clock ceiling kill or a backend socket wedge — as opposed to an ordinary
    // escalatable failure (contract reject / nonzero exit / persistent stall). The
    // driver's fix-verify loop reads this to flag-immediately vs escalate-and-retry.
    bool HardAbort = false,
    KillSignature? Kill = null);
