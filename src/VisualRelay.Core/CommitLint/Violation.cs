namespace VisualRelay.Core.CommitLint;

/// <summary>
/// One commit-message rule violation, carrying a human-readable message that
/// mirrors the wording of ai-sorcery's <c>check-commit-message.ts</c>. The hook
/// prints one bullet per violation.
/// </summary>
public sealed record Violation(string Message);
