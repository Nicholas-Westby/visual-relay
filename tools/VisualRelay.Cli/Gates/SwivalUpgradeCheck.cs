namespace VisualRelay.Cli.Gates;

/// <summary>
/// Weekly, consent-gated swival upgrade check (ported from <c>_swival_upgrade_check</c>).
/// Runs at most once per 7 days, tracked by a per-machine XDG-state timestamp
/// (never the repo tree). Always rewrites the timestamp after a check so a
/// declined upgrade does not re-nag. Best-effort and NON-FATAL: any failure here
/// must never block launch. The probe and upgrader are overridable via
/// VISUAL_RELAY_SWIVAL_LATEST_CMD and VISUAL_RELAY_SWIVAL_UPGRADER.
/// </summary>
public static class SwivalUpgradeCheck
{
    private const long IntervalSecs = 7L * 24 * 60 * 60;

    private const string DefaultLatestCmd =
        "brew update --quiet >/dev/null 2>&1; brew outdated swival/tap/swival 2>/dev/null";
    private const string DefaultUpgrader = "brew upgrade swival/tap/swival";

    /// <summary>Runs the check, swallowing all exceptions (set-e-safe).</summary>
    public static void Run(string workingDirectory)
    {
        try
        {
            RunInner(workingDirectory);
        }
        catch (Exception)
        {
            // Never let an upgrade-check failure propagate into launch.
        }
    }

    private static void RunInner(string workingDirectory)
    {
        if (!ProcessLauncher.OnPath("swival"))
            return;

        var stampFile = StampFile();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (!SwivalUpgradeDecision.ShouldProbe(ReadStamp(stampFile), now, IntervalSecs))
            return;

        var latestCmd = EnvOr("VISUAL_RELAY_SWIVAL_LATEST_CMD", DefaultLatestCmd);
        var latestOut = Shell.Capture(latestCmd, workingDirectory);

        // ALWAYS rewrite the timestamp after a check (declined/failed/up-to-date
        // all reset the 7-day window).
        WriteStamp(stampFile, now);

        if (!SwivalUpgradeDecision.UpgradeAvailable(latestOut))
            return;

        var installed = InstalledVersion(workingDirectory);

        if (!Tty.IsInteractive)
        {
            Console.Error.WriteLine(
                $"visual-relay: a newer swival is available (you have {installed}). Upgrade with:");
            Console.Error.WriteLine("  brew upgrade swival/tap/swival");
            return;
        }

        Console.Error.Write(
            $"visual-relay: a newer swival is available (you have {installed}). Upgrade now? [y/N] ");
        var answer = Console.ReadLine();
        if (Tty.IsYes(answer))
        {
            var upgrader = EnvOr("VISUAL_RELAY_SWIVAL_UPGRADER", DefaultUpgrader);
            if (Shell.RunInherited(upgrader, workingDirectory) != 0)
                Console.Error.WriteLine("visual-relay: swival upgrade failed; continuing launch");
        }
        else
        {
            Console.Error.WriteLine(
                "visual-relay: skipping swival upgrade; run later with: brew upgrade swival/tap/swival");
        }
    }

    private static string StampFile()
    {
        var xdgState = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (string.IsNullOrEmpty(xdgState))
            xdgState = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        return Path.Combine(xdgState, "visual-relay", "swival-upgrade-check");
    }

    private static long? ReadStamp(string stampFile)
    {
        if (!File.Exists(stampFile))
            return null;
        try
        {
            var text = File.ReadAllText(stampFile).Trim();
            return long.TryParse(text, out var v) ? v : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void WriteStamp(string stampFile, long now)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stampFile)!);
            File.WriteAllText(stampFile, now + "\n");
        }
        catch (Exception)
        {
            // best-effort
        }
    }

    private static string InstalledVersion(string workingDirectory)
    {
        var output = Shell.Capture("swival --version 2>/dev/null | head -n1", workingDirectory);
        var line = output.Trim();
        return string.IsNullOrEmpty(line) ? "unknown" : line;
    }

    private static string EnvOr(string name, string fallback)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(v) ? fallback : v;
    }
}
