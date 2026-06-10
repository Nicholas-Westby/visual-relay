using System.Diagnostics;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class SwivalSubagentRunner : ISubagentRunner
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    // The nono capability-sandbox binary used to wrap swival when the sandbox is
    // enabled (BypassSandbox == false). nono is the WRAPPER command that runs
    // swival — `nono run <flags> -- swival <args>` — not flags passed to swival.
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
        _probe = backendProbe ?? (token => BackendReadinessProbe.CheckAsync(ModelBackend.BaseUrl, ProbeTimeout, token));
    }

    // Pure swival arguments. The sandbox is applied by WRAPPING this whole
    // invocation in `nono run` (see BuildLaunchTarget) — never by passing
    // sandbox flags to swival (swival has no --sandbox/--nono-* flags; doing so
    // made nono print its version and exit 1, breaking every call).
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
}
