namespace VisualRelay.Core.CommandGuard;

/// <summary>
/// Verdict from <see cref="CommandGuardDecider.Decide"/>.
/// Serialized by the guard binary to the JSON protocol swival expects.
/// </summary>
public sealed class CommandGuardResult
{
    /// <summary>
    /// Swival action: <c>"allow"</c> or <c>"deny"</c>.
    /// </summary>
    private string Action { get; }

    /// <summary>
    /// When rewriting, the mode to emit: <c>"argv"</c> or <c>"shell"</c>.
    /// <c>null</c> for pass-through allow and deny.
    /// </summary>
    public string? Mode { get; }

    /// <summary>
    /// When rewriting, the command to emit: a <see cref="string"/> for shell
    /// mode or an <see cref="IReadOnlyList{String}"/> for argv mode.
    /// <c>null</c> for pass-through allow and deny.
    /// </summary>
    public object? Command { get; }

    /// <summary>
    /// Human-readable reason for a deny verdict. <c>null</c> for allow verdicts.
    /// </summary>
    public string? Reason { get; }

    public bool IsAllow => Action == "allow";
    public bool IsDeny => Action == "deny";
    public bool IsPassThrough => IsAllow && Mode is null;
    public bool IsRewritten => IsAllow && Mode is not null;

    private CommandGuardResult(string action, string? mode, object? command, string? reason = null)
    {
        Action = action;
        Mode = mode;
        Command = command;
        Reason = reason;
    }

    /// <summary>Pass-through: let the original command proceed unchanged.</summary>
    public static readonly CommandGuardResult Allow = new("allow", null, null);

    /// <summary>Rewrite the command before execution.</summary>
    public static CommandGuardResult AllowRewritten(string mode, object command) =>
        new("allow", mode, command);

    /// <summary>
    /// Deny the command. Swival will block execution and show
    /// <paramref name="reason"/> to the agent.
    /// </summary>
    public static CommandGuardResult Deny(string reason) =>
        new("deny", null, null, reason);
}
