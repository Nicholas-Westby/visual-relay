using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Writes the <c>stageN-attemptM.report.json</c> artifact that the real runner
/// leaves behind, so tests that care about cost, model, or turn parsing
/// have something for <c>RelayCostEstimator</c> to read. Test doubles opt in —
/// most driver tests assert against a run with no reports at all.
/// </summary>
internal static class StageReportSeed
{
    /// <summary>Records the invocation's tier as the model, and two llm_call
    /// timeline entries so the parsed turn count is a stable 2.</summary>
    public static void Write(StageInvocation invocation)
    {
        if (string.IsNullOrEmpty(invocation.ReportFile))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(invocation.ReportFile)!);
        File.WriteAllText(invocation.ReportFile,
            $$"""
            {
              "model": "{{invocation.Tier}}",
              "result": { "answer": "ok" },
              "stats": {},
              "timeline": [
                { "type": "llm_call", "prompt_tokens_est": 1000 },
                { "type": "llm_call", "prompt_tokens_est": 2000 }
              ]
            }
            """);
    }
}
