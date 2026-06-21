namespace VisualRelay.Cli.Gates;

/// <summary>
/// Reads <c>bypassSandbox</c> from <c>&lt;root&gt;/.relay/config.json</c> the same
/// way the bash <c>_read_bypass_sandbox</c> did: a present file with
/// <c>bypassSandbox:true</c> bypasses the sandbox; absent/false (or a missing
/// file entirely) keeps it enabled, matching <c>RelayConfig.BypassSandbox=false</c>.
/// Deliberately a tolerant string scan — the sandbox gate must not depend on the
/// full config being loadable on a fresh/greenfield checkout.
/// </summary>
public static class SandboxConfig
{
    public static bool BypassSandbox(string root)
    {
        var configPath = Path.Combine(root, ".relay", "config.json");
        if (!File.Exists(configPath))
            return false; // no config → sandbox enabled

        string text;
        try
        {
            text = File.ReadAllText(configPath);
        }
        catch (Exception)
        {
            return false; // unreadable → fail safe to enabled
        }

        return System.Text.RegularExpressions.Regex.IsMatch(
            text, "\"bypassSandbox\"\\s*:\\s*true");
    }
}
