using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class SwivalSubagentRunner : ISubagentRunner
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    // The nono capability-sandbox binary that always wraps swival. nono is the
    // WRAPPER command that runs swival — `nono run <flags> -- swival <args>` —
    // rather than delegating to swival's own `--sandbox nono` (see BuildArguments
    // for why).
    private const string NonoBinary = "nono";

    // The vr-guard profile (extends the registry-managed `swival` pack profile)
    // grants broad read + network and confines writes/deletes to the granted
    // workspace. workdir.access=readwrite in the swival profile means --allow-cwd
    // grants read+write to the cwd. VR OWNS this profile: NonoProfileEnsurer
    // embeds the content and rewrites it under $XDG_CONFIG_HOME/visual-relay every
    // run (self-heal), and nono loads it by ABSOLUTE PATH (--profile <path>) — not
    // by the name "vr-guard" resolved from ~/.config/nono/profiles/, which the old
    // launcher installed only-if-absent and so left stale forever.

    private readonly RelayConfig _config;
    private readonly IRelayEventSink? _eventSink;
    private readonly string _swivalBinary;
    private readonly string _nonoBinary;
    private readonly Func<CancellationToken, Task<BackendReadiness>> _probe;
    private readonly IGitInvoker? _gitInvoker;
    private readonly Func<string?> _proxyLogReader;
    public SwivalSubagentRunner(
        RelayConfig config,
        string swivalBinary = "swival",
        IRelayEventSink? eventSink = null,
        Func<CancellationToken, Task<BackendReadiness>>? backendProbe = null,
        IGitInvoker? gitInvoker = null,
        // The nono wrapper binary. Defaults to NonoBinary ("nono"); injectable so a
        // unit test can supply a transparent passthrough stub and exercise the
        // always-on nono-wrapped launch path without depending on the real nono's
        // Seatbelt/Landlock startup, rollback preflight, and timing.
        string? nonoBinary = null,
        // Reads the litellm proxy-log text consulted on a swival nonzero exit when
        // swival's own output yields no diagnostic (a model-backend error lives only
        // in the proxy log). Defaults to a best-effort read of the per-machine
        // BackendPaths log; injectable so a test can supply log content (or none)
        // without depending on a running proxy.
        Func<string?>? proxyLogReader = null)
    {
        _config = config;
        _swivalBinary = swivalBinary;
        _nonoBinary = nonoBinary ?? NonoBinary;
        _eventSink = eventSink;
        _probe = backendProbe ?? (token => BackendReadinessProbe.CheckWithRetryAsync(ModelBackend.BaseUrl, ProbeTimeout, cancellationToken: token));
        _gitInvoker = gitInvoker;
        _proxyLogReader = proxyLogReader ?? ReadProxyLogBestEffort;
    }

    // Default proxy-log reader: best-effort read of the per-machine litellm log.
    // Never throws — diagnostic enrichment must not break the run; a missing or
    // unreadable log simply yields null (no proxy reason folded in).
    private static string? ReadProxyLogBestEffort()
    {
        try
        {
            var path = BackendPaths.Resolve().LogFile;
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
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
        var args = new List<string>
        {
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
        };

        // Wire the command-guard middleware when the wrapper script exists
        // (the binary is published by CommandGuardEnsurer before run start).
        // The wrapper is at .githooks/command-guard relative to the repo root,
        // but swival resolves relative paths against --base-dir, so an
        // absolute path is safest.
        var guardPath = Path.Combine(invocation.TargetRoot, ".githooks", "command-guard");
        if (File.Exists(guardPath))
        {
            args.Add("--command-middleware");
            args.Add(guardPath);
        }

        return args;
    }

    /// <summary>
    /// Shared nono-prefix builder: Swival and verification callers produce
    /// identical prefixes except for <c>--rollback</c> / <c>--no-rollback-prompt</c>
    /// (controlled by <paramref name="rollback"/>).  The sandbox is always on, so
    /// this never returns empty.  Appends
    /// <see cref="RelayConfig.SandboxExtraAllowPaths"/> as <c>-a &lt;path&gt;</c>
    /// before <c>--</c> (and before <c>--rollback</c> when enabled).
    /// <paramref name="skipDirs"/> (basenames) are emitted as
    /// <c>--skip-dir &lt;name&gt;</c> — before <c>--</c> — so nono's rollback
    /// PREFLIGHT skips them and stays under its fixed budget on large repos
    /// (the swival path passes these; the verify path leaves them null).
    /// </summary>
    internal static IReadOnlyList<string> BuildNonoPrefix(
        RelayConfig config, bool rollback, IReadOnlyList<string>? skipDirs = null)
    {
        // Load by absolute path, not the global profile name: NonoProfileEnsurer
        // resolves the same VR-owned $XDG_CONFIG_HOME/visual-relay/vr-guard.json it
        // wrote (overwrite-always) at run start, so the sandbox can never run under
        // a stale installed-by-name copy.
        var args = new List<string> { "run", "--profile", NonoProfileEnsurer.ResolveProfilePath(), "--allow-cwd" };

        if (config.SandboxExtraAllowPaths is { Count: > 0 } paths)
        {
            foreach (var path in paths) { args.Add("-a"); args.Add(path); }
        }

        if (skipDirs is { Count: > 0 })
        {
            foreach (var name in skipDirs) { args.Add("--skip-dir"); args.Add(name); }
        }

        if (rollback) { args.Add("--rollback"); args.Add("--no-rollback-prompt"); }
        args.Add("--");
        return args;
    }
}
