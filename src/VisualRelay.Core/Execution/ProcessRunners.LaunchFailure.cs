namespace VisualRelay.Core.Execution;

// Recognizing when a sandboxed swival process could not START at all (a launch/DLL-init
// failure) as distinct from a model-backend error — consulted by BuildNonzeroExitReason
// in ProcessRunners.Diagnostics.cs, split out to keep that file under the size guard.
public sealed partial class SwivalSubagentRunner
{
    // Windows NTSTATUS codes for a process that could not START — DLL init failed, a
    // required DLL was missing, or a bad image. A swival exit with one of these means the
    // sandbox could not launch the agent at all (it never reached the model), so it must
    // NOT be reported as a model-backend error. Harmless cross-platform: these exact
    // negative values do not occur as normal Unix exit codes.
    private static bool IsProcessLaunchFailureExitCode(int exitCode) => exitCode is
        unchecked((int)0xC0000142) or   // STATUS_DLL_INIT_FAILED
        unchecked((int)0xC0000135) or   // STATUS_DLL_NOT_FOUND
        unchecked((int)0xC000007B);     // STATUS_INVALID_IMAGE_FORMAT

    private const string ProcessLaunchFailedHint =
        "the sandboxed process failed to START (a process-launch / DLL-initialization " +
        "failure), so it never called the model — this is NOT a model-backend error. On " +
        "Windows this usually means the sandbox could not launch the agent: the MXC " +
        "BaseContainer backend is unavailable and its DACL fallback broke process init. " +
        "Retry with VR_WINDOWS_SANDBOX=builtin (degraded but functional).";
}
