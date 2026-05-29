namespace VisualRelay.Domain;

public sealed record SubagentResult(
    string RawText,
    string? Json,
    bool IsValid,
    string? Error);

