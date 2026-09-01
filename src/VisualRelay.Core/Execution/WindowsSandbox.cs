namespace VisualRelay.Core.Execution;

/// <summary>The active Windows sandbox mode for task execution.</summary>
public enum WindowsSandboxMode
{
    /// <summary>Microsoft Execution Containers — the chosen wrapper (confined writes).</summary>
    Mxc,

    /// <summary>No sandbox available — execution must be blocked, never silent.</summary>
    Blocked,
}

/// <summary>
/// The Windows arm of the sandbox seam: it selects the mode (MXC when available,
/// otherwise blocked) and builds the OS-appropriate launch wrapper. There is no
/// opt-out, matching the Unix arm, where the nono prefix is likewise unconditional.
/// </summary>
public static class WindowsSandbox
{
    /// <summary>
    /// Selects the mode: MXC when <paramref name="mxcAvailable"/>; otherwise blocked.
    /// Never returns a silent unsandboxed mode.
    /// </summary>
    public static WindowsSandboxMode Select(bool mxcAvailable) =>
        mxcAvailable ? WindowsSandboxMode.Mxc : WindowsSandboxMode.Blocked;

    /// <summary>
    /// MXC wrapper: <c>wxc-exec.exe &lt;policy.json&gt; -- &lt;program&gt; &lt;args…&gt;</c> —
    /// the confined-write container around an agent command or the verify command. The
    /// <c>--</c> separator is REQUIRED: without it wxc-exec parses the program as its own
    /// flags (verified against wxc-exec v0.7.0-rc1), and it lets the command override the
    /// policy's (omitted) <c>process.commandLine</c>.
    /// </summary>
    public static (string FileName, IReadOnlyList<string> Args) BuildMxcLaunch(
        string wxcExe, string policyPath, string program, IReadOnlyList<string> programArgs)
    {
        var args = new List<string> { policyPath, "--", program };
        args.AddRange(programArgs);
        return (wxcExe, args);
    }

    /// <summary>The message shown when execution is blocked for want of a sandbox.</summary>
    public const string BlockedMessage =
        "Windows task execution is blocked: no sandbox is available. Provision Microsoft "
        + "Execution Containers (wxc-exec) to confine writes, then retry.";

    /// <summary>A one-line description of the active mode for the run log and UI.</summary>
    public static string DescribeMode(WindowsSandboxMode mode) => mode switch
    {
        WindowsSandboxMode.Mxc =>
            "MXC: writes confined to the workspace, broad reads, network open.",
        _ => "blocked: no sandbox available.",
    };
}
