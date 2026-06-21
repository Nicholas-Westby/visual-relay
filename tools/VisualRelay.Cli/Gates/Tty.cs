namespace VisualRelay.Cli.Gates;

/// <summary>
/// TTY detection + yes-parsing shared by the consent-gated install/upgrade
/// offers. Mirrors the launcher's <c>[[ -t 0 &amp;&amp; -t 1 ]]</c> guard: when stdin or
/// stdout is redirected we never prompt.
/// </summary>
public static class Tty
{
    public static bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public static bool IsYes(string? answer) =>
        answer is not null
        && (answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase)
            || answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase));
}
