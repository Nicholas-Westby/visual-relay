namespace VisualRelay.Core.Execution.Wsl;

/// <summary>
/// Which wsl.exe starts a plain distro command when a resolved context may or
/// may not exist: a workspace picked inside a distro needs git and chmod there
/// even before the gate has passed. The probed one when there is a context, else
/// the bare name, which Windows resolves on PATH (System32 always carries it).
/// </summary>
public static class WslExe
{
    private const string Default = "wsl.exe";

    public static string Resolve(WslContext? context) => context?.WslExePath ?? Default;
}
