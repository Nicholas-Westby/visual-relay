namespace VisualRelay.Cli.Gates;

/// <summary>
/// nono OS-level sandbox prerequisite + provisioning (ported from the launcher's
/// <c>_require_nono</c> / <c>_provision_nono</c>). nono is a hard, always-required
/// dependency: when it is absent, prints install instructions and signals a hard
/// failure (exit 127). When present, pulls the swival base pack (idempotent); the
/// vr-guard profile is owned/self-healed by the app at run start, so no profile
/// is copied.
/// </summary>
public static class NonoGate
{
    /// <summary>
    /// Ensures nono is available. The sandbox is always on, so nono is required
    /// unconditionally. Returns 0 to proceed, or 127 when nono is missing (after
    /// printing install instructions).
    /// </summary>
    public static int Require(string root)
    {
        var (exitCode, message) = Decide(ProcessLauncher.OnPath("nono"), OperatingSystem.IsWindows());
        if (message is not null)
            Console.Error.WriteLine(message);
        return exitCode;
    }

    /// <summary>
    /// Pure OS-aware gate decision. nono is present → proceed (0). Missing on
    /// macOS/Linux → hard fail (127) with install instructions (the sandbox is a
    /// hard dependency there). Missing on Windows → proceed (0) silently: nono is the
    /// Unix sandbox and Windows simply uses a different one (MXC, gated at run time),
    /// so its absence is unremarkable and not worth a note. The returned message (when
    /// non-null) is what <see cref="Require"/> prints to stderr.
    /// </summary>
    public static (int ExitCode, string? Message) Decide(bool onPath, bool isWindows)
    {
        if (onPath || isWindows)
            return (0, null);

        return (127,
            """
            visual-relay: nono was not found on PATH.

              nono is a required dependency for the OS-level sandbox (Seatbelt on macOS,
              Landlock on Linux) that confines Swival writes and deletes to the workspace.
              The sandbox is always on; there is no opt-out. Install nono:

                brew install nono
                (or see https://github.com/jedisct1/nono for other platforms)

              If Nix is installed, the devshell provides nono automatically.
            """);
    }

    /// <summary>
    /// Idempotently pulls the swival base profile pack so the vr-guard profile
    /// (which extends swival) resolves. Best-effort and non-fatal: a network /
    /// Sigstore failure prints a hint but does not block launch. No-op when nono
    /// is absent.
    /// </summary>
    public static void Provision(string root)
    {
        if (!ProcessLauncher.OnPath("nono"))
            return;

        var rc = ProcessLauncher.Run("nono", ["pull", "jedisct1/swival"], root);
        if (rc != 0)
        {
            Console.Error.WriteLine("visual-relay: nono pull jedisct1/swival failed (network/Sigstore)");
            Console.Error.WriteLine("  The vr-guard profile extends swival; nono may error if swival is absent.");
            Console.Error.WriteLine("  Retry with: nono pull jedisct1/swival");
        }
    }
}
