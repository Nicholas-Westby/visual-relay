namespace VisualRelay.Cli.Gates;

/// <summary>
/// swival prerequisite (ported from <c>_require_swival</c> / <c>_offer_swival_install</c>).
/// swival is hard-required and NOT sandbox-gated: it is the agent that runs every
/// stage. When missing, offers a consent-gated Homebrew-tap install on a TTY
/// (overridable via VISUAL_RELAY_SWIVAL_INSTALLER); off a TTY it prints
/// instructions and signals a hard failure without prompting.
/// </summary>
public static class SwivalGate
{
    private const string TapInstall = "brew trust swival/tap && brew install swival/tap/swival";

    /// <summary>Returns 0 when swival is available (already, or after a consenting
    /// install); 127 when it is missing and no install happened.</summary>
    public static int Require(string workingDirectory)
    {
        if (ProcessLauncher.OnPath("swival"))
            return 0;

        if (OfferInstall(workingDirectory) && ProcessLauncher.OnPath("swival"))
            return 0;

        Console.Error.WriteLine(
            $"""
            visual-relay: swival was not found on PATH.

              swival is the agent that runs every stage and is a required dependency.
              Install swival via its Homebrew tap:

                {TapInstall}
                (or see https://swival.dev for other platforms)
            """);
        return 127;
    }

    /// <summary>Consent-gated install. True when swival was (re)installed. Honors
    /// VISUAL_RELAY_SWIVAL_INSTALLER as the install command override. Off a TTY,
    /// prints instructions and returns false (no prompt, no install).</summary>
    private static bool OfferInstall(string workingDirectory)
    {
        var installer = Environment.GetEnvironmentVariable("VISUAL_RELAY_SWIVAL_INSTALLER");
        var installCmd = string.IsNullOrEmpty(installer) ? TapInstall : installer;

        if (!Tty.IsInteractive)
        {
            Console.Error.WriteLine(
                $"visual-relay: install swival to run stages:\n  {TapInstall}");
            return false;
        }

        Console.Error.Write("visual-relay: swival was not found on PATH. Install it via Homebrew? [y/N] ");
        var answer = Console.ReadLine();
        if (!Tty.IsYes(answer))
        {
            Console.Error.WriteLine($"visual-relay: install swival manually:\n  {TapInstall}");
            return false;
        }

        var rc = Shell.RunInherited(installCmd, workingDirectory);
        if (rc != 0 || !ProcessLauncher.OnPath("swival"))
        {
            Console.Error.WriteLine("visual-relay: swival install ran but swival was still not found on PATH");
            return false;
        }
        return true;
    }
}
