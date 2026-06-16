using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class SwivalSubagentRunner : ISubagentRunner
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    // The nono capability-sandbox binary used to wrap swival when the sandbox is
    // enabled (BypassSandbox == false). nono is the WRAPPER command that runs
    // swival — `nono run <flags> -- swival <args>` — rather than delegating to
    // swival's own `--sandbox nono` (see BuildArguments for why).
    private const string NonoBinary = "nono";

    // The vr-guard profile (~/.config/nono/profiles/vr-guard.json, extends the
    // registry-managed `swival` pack profile) grants broad read + network and
    // confines writes/deletes to the granted workspace. workdir.access=readwrite
    // in the swival profile means --allow-cwd grants read+write to the cwd.
    private const string NonoProfile = "vr-guard";

    private readonly RelayConfig _config;
    private readonly IRelayEventSink? _eventSink;
    private readonly string _swivalBinary;
    private readonly Func<CancellationToken, Task<BackendReadiness>> _probe;
    public SwivalSubagentRunner(
        RelayConfig config,
        string swivalBinary = "swival",
        IRelayEventSink? eventSink = null,
        Func<CancellationToken, Task<BackendReadiness>>? backendProbe = null)
    {
        _config = config;
        _swivalBinary = swivalBinary;
        _eventSink = eventSink;
        _probe = backendProbe ?? (token => BackendReadinessProbe.CheckWithRetryAsync(ModelBackend.BaseUrl, ProbeTimeout, cancellationToken: token));
    }

    // Pure swival arguments — no sandbox flags. swival 1.0.25+ does support
    // `--sandbox nono` (it re-execs itself under nono), but we deliberately drive
    // `nono run` ourselves (see BuildLaunchTarget) to pin the exact profile and
    // invocation and avoid swival<->nono version skew. Delegating to swival once
    // had it re-exec into a mismatched nono that printed its version and exited 1
    // — that was version skew, not a missing flag.
    internal List<string> BuildArguments(StageInvocation invocation, string? resolvedCommands = null)
    {
        var profile = _config.TierProfiles.TryGetValue(invocation.Tier, out var value) ? value : invocation.Tier;
        var commands = resolvedCommands ?? invocation.Stage.Commands;
        return
        [
            "-q",
            "--profile", profile,
            "--api-key", "not-needed",
            "--base-dir", invocation.TargetRoot,
            "--system-prompt", invocation.Stage.SystemPrompt,
            "--no-lifecycle",
            "--no-history",
            "--files", invocation.Stage.Files,
            "--commands", commands,
            "--trace-dir", invocation.TraceDirectory,
            "--report", invocation.ReportFile,
            "--max-turns", invocation.MaxTurns.ToString()
        ];
    }

    /// <summary>
    /// Shared nono-prefix builder: Swival and verification callers produce
    /// identical prefixes except for <c>--rollback</c> / <c>--no-rollback-prompt</c>
    /// (controlled by <paramref name="rollback"/>).  Returns empty when
    /// <see cref="RelayConfig.BypassSandbox"/> is true.  Appends
    /// <see cref="RelayConfig.SandboxExtraAllowPaths"/> as <c>-a &lt;path&gt;</c>
    /// before <c>--</c> (and before <c>--rollback</c> when enabled).
    /// </summary>
    internal static IReadOnlyList<string> BuildNonoPrefix(RelayConfig config, bool rollback)
    {
        if (config.BypassSandbox)
            return [];

        var args = new List<string> { "run", "-p", NonoProfile, "--allow-cwd" };

        if (config.SandboxExtraAllowPaths is { Count: > 0 } paths)
        {
            foreach (var path in paths) { args.Add("-a"); args.Add(path); }
        }

        if (rollback) { args.Add("--rollback"); args.Add("--no-rollback-prompt"); }
        args.Add("--");
        return args;
    }
}
