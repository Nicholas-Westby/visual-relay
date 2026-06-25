namespace VisualRelay.Core.Execution;

/// <summary>The active Windows sandbox mode for task execution.</summary>
public enum WindowsSandboxMode
{
    /// <summary>Microsoft Execution Containers — the chosen wrapper (confined writes).</summary>
    Mxc,

    /// <summary>swival's own <c>--sandbox builtin</c> — cross-platform but degraded
    /// (its shell tool is an unguarded write/delete escape). Explicit opt-in only.</summary>
    Builtin,

    /// <summary>No sandbox available — execution must be blocked, never silent.</summary>
    Blocked,
}

/// <summary>
/// The Windows arm of the sandbox seam: it selects the mode (MXC by default, the
/// degraded builtin only on explicit opt-in, otherwise blocked) and builds the
/// OS-appropriate launch wrapper. The Unix arm stays the nono prefix, unchanged.
/// </summary>
public static class WindowsSandbox
{
    /// <summary>Opt-in override env var (e.g. <c>builtin</c>); unset means MXC-or-blocked.</summary>
    public const string OptInEnvVar = "VR_WINDOWS_SANDBOX";

    /// <summary>
    /// Selects the mode: an explicit <c>builtin</c> opt-in always wins; otherwise MXC
    /// when <paramref name="mxcAvailable"/>; otherwise blocked. Never returns a silent
    /// unsandboxed mode.
    /// </summary>
    public static WindowsSandboxMode Select(string? optIn, bool mxcAvailable)
    {
        if (string.Equals(optIn, "builtin", StringComparison.OrdinalIgnoreCase))
            return WindowsSandboxMode.Builtin;
        if (mxcAvailable)
            return WindowsSandboxMode.Mxc;
        return WindowsSandboxMode.Blocked;
    }

    /// <summary>
    /// MXC wrapper: <c>wxc-exec.exe &lt;policy.json&gt; &lt;program&gt; &lt;args…&gt;</c> —
    /// the confined-write container around swival or the verify command.
    /// </summary>
    public static (string FileName, IReadOnlyList<string> Args) BuildMxcLaunch(
        string wxcExe, string policyPath, string program, IReadOnlyList<string> programArgs)
    {
        var args = new List<string> { policyPath, program };
        args.AddRange(programArgs);
        return (wxcExe, args);
    }

    /// <summary>
    /// Builtin: swival self-sandboxes via <c>--sandbox builtin</c>; there is no
    /// external wrapper, so swival itself is the launch target with the flag appended.
    /// </summary>
    public static (string FileName, IReadOnlyList<string> Args) BuildBuiltinSwivalLaunch(
        string swivalBin, IReadOnlyList<string> swivalArgs)
    {
        var args = new List<string>(swivalArgs) { "--sandbox", "builtin" };
        return (swivalBin, args);
    }

    /// <summary>The message shown when execution is blocked for want of a sandbox.</summary>
    public const string BlockedMessage =
        "Windows task execution is blocked: no sandbox is available. Provision Microsoft "
        + "Execution Containers (wxc-exec) to confine writes, or opt into the degraded swival "
        + "builtin sandbox with VR_WINDOWS_SANDBOX=builtin (its shell tool is an unguarded "
        + "write/delete escape).";

    /// <summary>A one-line description of the active mode for the run log and UI.</summary>
    public static string DescribeMode(WindowsSandboxMode mode) => mode switch
    {
        WindowsSandboxMode.Mxc =>
            "MXC (processcontainer): writes confined to the workspace, broad reads, network open.",
        WindowsSandboxMode.Builtin =>
            "swival builtin (DEGRADED): app-layer file guards only; the shell tool can still "
            + "write or delete outside the workspace.",
        _ => "blocked: no sandbox available.",
    };
}
