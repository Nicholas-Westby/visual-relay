using System.Text;
using System.Text.Json.Serialization;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>
    /// Structured per-check breakdown for setup/validation gates (bootstrap,
    /// guard, new-guard-probe, and test). Built from stage-10 pre-agent data
    /// or fix-verify loop iteration results. Exposed through the control API
    /// /state endpoint and persisted as a per-attempt JSON artifact alongside
    /// the verify-output text file so every failure is machine-diagnosable.
    /// </summary>
    internal sealed record SetupCheckResults(
        string? BootstrapCheck,
        string? BootstrapCommand,
        string? BootstrapOutput,
        string? GuardCheck,
        string? GuardCommand,
        string? GuardOutput,
        string? NewGuardProbeCheck,
        string? NewGuardProbeOutput,
        string? TestCheck,
        string TestCommand,
        int? TestExitCode)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<SandboxDenial>? BootstrapDenials { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<SandboxDenial>? GuardDenials { get; init; }

        public static SetupCheckResults FromPreAgentData(
            Stage10PreAgentData data, RelayConfig config)
        {
            string? bootstrapCheck = null;
            if (data.BootstrapCmd is not null)
                bootstrapCheck = data.BootstrapFailed ? "red" : "green";

            string? guardCheck = null;
            if (config.GuardCommand is not null)
                guardCheck = data.GuardFailed ? "red" : "green";

            string? newGuardProbeCheck = null;
            if (data.NewGuardOutput is not null)
                newGuardProbeCheck = "red";

            return new SetupCheckResults(
                BootstrapCheck: bootstrapCheck,
                BootstrapCommand: data.BootstrapCmd,
                BootstrapOutput: data.BootstrapFailed ? data.BootstrapFailureOutput : null,
                GuardCheck: guardCheck,
                GuardCommand: config.GuardCommand,
                GuardOutput: data.GuardFailed ? data.GuardOutput : null,
                NewGuardProbeCheck: newGuardProbeCheck,
                NewGuardProbeOutput: data.NewGuardOutput,
                TestCheck: data.TestResult.ExitCode == 0 ? "green" : "red",
                TestCommand: config.TestCommand,
                TestExitCode: data.TestResult.ExitCode);
        }

        public static SetupCheckResults FromFixVerifyIteration(
            TestRunResult? bootstrapResult,
            string? bootstrapCmd,
            string? guardOutput,
            string? guardCmd,
            TestRunResult testResult,
            string testCommand,
            IReadOnlyList<SandboxDenial>? bootstrapDenials = null,
            IReadOnlyList<SandboxDenial>? guardDenials = null)
        {
            string? bootstrapCheck = null;
            if (bootstrapCmd is not null)
                bootstrapCheck = bootstrapResult is not null ? "red" : "green";

            string? guardCheck = null;
            if (guardCmd is not null)
                guardCheck = guardOutput is not null ? "red" : "green";

            return new SetupCheckResults(
                BootstrapCheck: bootstrapCheck,
                BootstrapCommand: bootstrapCmd,
                BootstrapOutput: bootstrapResult?.Output,
                GuardCheck: guardCheck,
                GuardCommand: guardCmd,
                GuardOutput: guardOutput,
                NewGuardProbeCheck: null,
                NewGuardProbeOutput: null,
                TestCheck: testResult.ExitCode == 0 ? "green" : "red",
                TestCommand: testCommand,
                TestExitCode: testResult.ExitCode)
            {
                BootstrapDenials = bootstrapDenials,
                GuardDenials = guardDenials
            };
        }

        public Dictionary<string, string> ToEventData()
        {
            var d = new Dictionary<string, string>();
            if (BootstrapCheck is not null) d["bootstrapCheck"] = BootstrapCheck;
            if (BootstrapCommand is not null) d["bootstrapCommand"] = BootstrapCommand;
            if (GuardCheck is not null) d["guardCheck"] = GuardCheck;
            if (NewGuardProbeCheck is not null) d["newGuardProbeCheck"] = NewGuardProbeCheck;
            if (TestCheck is not null) d["testCheck"] = TestCheck;
            if (TestExitCode.HasValue) d["testExitCode"] = TestExitCode.Value.ToString();
            if (BootstrapDenials is { Count: > 0 }) d["bootstrapDenials"] = string.Join(", ", BootstrapDenials.Select(dd => dd.Target));
            if (GuardDenials is { Count: > 0 }) d["guardDenials"] = string.Join(", ", GuardDenials.Select(dd => dd.Target));
            return d;
        }

        public string ToSummaryLines()
        {
            var sb = new StringBuilder();
            sb.AppendLine("--- Setup check breakdown ---");
            sb.AppendLine(FormatLine("bootstrap", BootstrapCheck, BootstrapDenials));
            sb.AppendLine(FormatLine("guard", GuardCheck, GuardDenials));
            sb.AppendLine(FormatLine("new-guard-probe", NewGuardProbeCheck, null));
            sb.AppendLine(FormatLine("test", TestCheck, null));
            return sb.ToString();
        }

        private static string FormatLine(string name, string? check, IReadOnlyList<SandboxDenial>? denials) =>
            check switch
            {
                "red" when denials is { Count: > 0 } =>
                    $"✗ {name}: red (sandbox denial: {denials[0].Target})",
                "green" => $"✓ {name}: green",
                "red" => $"✗ {name}: red",
                _ => $"— {name}: skipped"
            };

        public bool IsAnyRed() =>
            BootstrapCheck == "red" || GuardCheck == "red";
    }
}
