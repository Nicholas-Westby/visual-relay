using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Support for running one stage's command inside the sandbox: the prompt that
/// goes in, the nono prefix and environment it runs under, the tools it needs on
/// PATH, and the failure reason distilled from what comes back.
/// <para>
/// These were static members of the retired subprocess runner. They are not about
/// that runner: the in-process agent loop builds the same prompt, launches the
/// same sandbox and distils the same failures, so they outlive it and live here
/// under a name that says what they do.
/// </para>
/// </summary>
public static partial class SandboxedStage
{
    /// <summary>The sandbox binary every stage command is wrapped in.</summary>
    private const string NonoBinary = "nono";

    /// <summary>
    /// Shared nono-prefix builder: agent and verification callers produce
    /// identical prefixes except for <c>--rollback</c> / <c>--no-rollback-prompt</c>
    /// (controlled by <paramref name="rollback"/>).  The sandbox is always on, so
    /// this never returns empty.  Appends
    /// <see cref="RelayConfig.SandboxExtraAllowPaths"/> as <c>-a &lt;path&gt;</c>
    /// before <c>--</c> (and before <c>--rollback</c> when enabled).
    /// <paramref name="skipDirs"/> (basenames) are emitted as
    /// <c>--skip-dir &lt;name&gt;</c> — before <c>--</c> — so nono's rollback
    /// PREFLIGHT skips them and stays under its fixed budget on large repos
    /// (the agent path passes these; the verify path leaves them null).
    /// When <paramref name="verboseDiagnostics"/> is false (the default = quiet)
    /// <c>--silent</c> is added so nono prints none of its own banner / summary /
    /// status / WARN-preflight / failure-footer chatter, leaving only the child
    /// command's output. This is OUTPUT-ONLY: enforcement, the loaded profile, the
    /// network policy, and the child exit code are all unchanged. Passing <c>true</c>
    /// restores nono's full diagnostics for debugging the sandbox itself.
    /// </summary>
    internal static IReadOnlyList<string> BuildNonoPrefix(
        RelayConfig config, bool rollback, IReadOnlyList<string>? skipDirs = null,
        bool verboseDiagnostics = false, string? userTemplatesDirOverride = null,
        string? workspaceRoot = null, bool requestDiagnostics = false)
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

        // Standing write grant for the user task-templates dir so sandboxed runs can
        // author and update templates. Created eagerly so the grant always resolves
        // and users can discover the folder. Kilobytes of markdown — negligible
        // against nono's rollback-preflight copy budget.
        var templatesDir = userTemplatesDirOverride ?? TaskTemplates.ResolveUserTemplatesDir();
        Directory.CreateDirectory(templatesDir);
        args.Add("-a");
        args.Add(templatesDir);

        // Auto-grant the workspace volume's .TemporaryItems directory when the
        // workspace root lives on an external macOS volume. Foundation atomic writes
        // stage temp files at the volume root — outside --allow-cwd — which causes
        // EPERM (PolicyBlocked) for swift build, swiftformat, and any tool doing
        // NSWriteAuxiliaryFile. This is an internal-only grant; users cannot add it
        // via SandboxExtraAllowPaths (RelayConfigLoader rejects paths outside $HOME).
        if (workspaceRoot is { Length: > 0 })
        {
            var volumeTemp = WorkspaceVolumeTempDir.Resolve(workspaceRoot);
            if (volumeTemp is not null)
            {
                // nono accepts -a for paths that don't yet exist. If it ever doesn't,
                // best-effort create and fall back to no grant — never fail the run.
                args.Add("-a");
                args.Add(volumeTemp);
            }
        }

        if (skipDirs is { Count: > 0 })
        {
            foreach (var name in skipDirs) { args.Add("--skip-dir"); args.Add(name); }
        }

        if (rollback) { args.Add("--rollback"); args.Add("--no-rollback-prompt"); }

        // Request machine-readable diagnostics JSON on stderr.  Only the
        // verification path requests this; agent runs do not.
        // Independent of verboseDiagnostics — we keep --silent so nono's
        // human footer stays suppressed.
        if (requestDiagnostics) { args.Add("--diagnostics-json"); }

        // Quiet by default: suppress nono's own banner/summary/status/WARN-preflight
        // and the failure footer so only the child command's output reaches the
        // captured log (the footer is a known red herring that can fill the agent's
        // tail window). OUTPUT-ONLY — it changes nothing about enforcement or the
        // child exit code. The verbose-diagnostics preference flips it off to restore
        // nono's full output when diagnosing the sandbox itself.
        if (!verboseDiagnostics) { args.Add("--silent"); }

        args.Add("--");
        return args;
    }

    /// <summary>
    /// Returns the TAIL of <paramref name="value"/> (last <paramref name="tailChars"/>
    /// characters), prepended with "…" when truncated. The real error is usually
    /// at the tail after a sandbox banner, so we keep the end rather than the head.
    /// The default window is deliberately large (2000 chars) so the pass/fail summary
    /// AND the real error survive any trailing noise when this output is handed to the
    /// Verify/Fix-verify agent; the diagnostics extractors pass their own smaller caps.
    /// Internal so the window can be asserted directly.
    /// </summary>
    internal static string TrimForTail(string value, int tailChars = 2000)
    {
        var text = value.Trim();
        return text.Length <= tailChars ? text : "…" + text[^tailChars..];
    }

    /// <summary>
    /// Distills the real failure text from merged stdout/stderr by dropping
    /// nono's per-run advisory WARNs (lines containing <c>is blocked by '</c> and
    /// <c>use --bypass-protection</c>, which print every run regardless of the
    /// failure), nono's standing system-services / keychain advisory and its
    /// remediation hints (<see cref="IsNonoSystemServiceAdvisory"/>, which trail
    /// AFTER the test summary), and pure banner/decoration rows, then keeping the
    /// most relevant remainder. Anchoring is two-pass: first prefer a high-confidence failure
    /// marker (<c>cannot find binary path</c>, <c>command execution failed</c>,
    /// <c>command not found</c>); only when none is present fall back to
    /// word-boundary weak keywords (<c>error</c>/<c>fatal</c>/… as whole words, so
    /// a benign "loaded config with 0 errors" line is NOT mis-anchored). The reason
    /// runs from the anchor line down to the end (so a multi-line traceback
    /// survives), or the tail of the surviving lines when nothing looks like a
    /// failure, capped to <paramref name="tailChars"/>. Returns
    /// <see cref="NoDiagnosticOutput"/> when the output is nothing but noise/empty.
    /// </summary>
    internal static string ExtractFailureReason(string output, int tailChars = 600) =>
        DistillFailure(output, tailChars).Reason;
}
