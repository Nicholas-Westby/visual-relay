using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Shared fixture for the sandbox-prefix tests. These used to hang off the
/// Swival runner's own sandbox test class; the prefix builder outlived that
/// runner, so its tests did too and needed a home of their own.
/// </summary>
public sealed partial class SandboxedStageSandboxTests
{
    // The VR-owned profile abs path the prefix carries (--profile <abs>),
    // resolved from the real process env exactly as production does.
    private static string ProfilePath => NonoProfileEnsurer.ResolveProfilePath();

    private static string TemplatesDir => TaskTemplates.ResolveUserTemplatesDir();

    private static RelayConfig TestConfig() =>
        new("llm-tasks", "true", "true", [],
            new Dictionary<string, string> { ["cheap"] = "cheap" },
            true, 1, 1, false, true,
            SubagentTimeoutMilliseconds: 5_000,
            TestTimeoutMilliseconds: 300_000,
            FirstOutputTimeoutMsByTier: new Dictionary<string, int>
            { ["cheap"] = 90_000, ["balanced"] = 120_000, ["frontier"] = 660_000 },
            FirstOutputTimeoutMs: 660_000);
}
