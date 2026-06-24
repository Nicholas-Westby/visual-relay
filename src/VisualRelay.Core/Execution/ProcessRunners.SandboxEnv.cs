using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class SwivalSubagentRunner
{
    // Build environment overrides shared by every nono-wrapped invocation (the
    // swival stage and the SandboxedTestRunner verify path). Two jobs:
    // (1) redirect transitive-dependency caches into ~/.config/swival (already in
    //     the swival profile write-allow list) so nono's vr-guard sandbox does not
    //     block them — see nono-grant-swival-workspace-writes (stage 6);
    // (2) stop `dotnet test` leaving orphaned descendants that outlive the finished
    //     tests and keep the nono wrapper alive past completion (the stage-5/9
    //     timeout-after-tests-pass) — defence in depth with the runner's idle-reap.
    internal static IReadOnlyDictionary<string, string> BuildSandboxEnvironment(RelayConfig config)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new Dictionary<string, string>
        {
            ["HF_HOME"] = Path.Combine(home, ".config", "swival", "huggingface"),
            ["XDG_CACHE_HOME"] = Path.Combine(home, ".config", "swival", "cache"),
            ["UV_CACHE_DIR"] = Path.Combine(home, ".config", "swival", "uv-cache"),
            // Stop Python under nono writing .pyc into its (denied) stdlib dir, e.g. the Homebrew python@3.14 Cellar — that triggers an interactive ~50-path "Review denied paths" prompt that blocks the run; PYCACHEPREFIX redirects any re-enabled bytecode to a write-allowed dir.
            ["PYTHONDONTWRITEBYTECODE"] = "1",
            ["PYTHONPYCACHEPREFIX"] = Path.Combine(home, ".config", "swival", "pycache"),
            // MSBUILDDISABLENODEREUSE=1 makes MSBuild node-reuse workers exit instead of lingering past the tests; the telemetry opt-out drops the background uploader. Both are ignored by non-.NET targets. (UseSharedCompilation=false in the configured testCmd already disables the Roslyn build server.)
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        };
    }
}
