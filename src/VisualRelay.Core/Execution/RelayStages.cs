using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public static class RelayStages
{
    public static IReadOnlyList<RelayStageDefinition> All { get; } =
    [
        Stage(1, "Ideate", "cheap", "none", "git,ls,cat", """{ "summary": string, "options": string[] }"""),
        Stage(2, "Research", "cheap", "some", "all", """{ "findings": string, "constraints": string[] }"""),
        Stage(3, "Diagnose", "balanced", "some", "all", """{ "evidence": string, "excerpts": string[], "repro": string }"""),
        Stage(4, "Plan", "balanced", "some", "all", """{ "plan": string, "manifest": string[] }"""),
        Stage(5, "Author-tests", "balanced", "all", "all", """{ "testFiles": string[], "rationale": string }"""),
        Stage(6, "Implement", "balanced", "all", "all", """{ "summary": string }"""),
        Stage(7, "Review", "frontier", "some", "all", """{ "verdict": "pass"|"changes", "issues": [] }"""),
        Stage(8, "Fix", "balanced", "all", "all", """{ "summary": string }"""),
        Stage(9, "Verify", "cheap", "some", "all", """{ "summary": string }"""),
        Stage(10, "Fix-verify", "balanced", "all", "all", """{ "summary": string, "amendManifest"?: string[] }"""),
        new(11, "Commit", "cheap", "driver", "none", "git", string.Empty, string.Empty)
    ];

    private static RelayStageDefinition Stage(
        int number,
        string name,
        string tier,
        string files,
        string commands,
        string contract) =>
        new(
            number,
            name,
            tier,
            "llm",
            files,
            commands,
            SystemPromptFor(name),
            $"End your reply with a single fenced ```json block matching: {contract}");

    private static string SystemPromptFor(string name) => name switch
    {
        "Ideate" => "Frame the task and list 2-3 solution options. Do not edit files.",
        "Research" => "Investigate the codebase; record findings and constraints. Do not edit files.",
        "Diagnose" => "Read application logs and extract evidence that explains the issue.",
        "Plan" => "Write a concrete plan and exact impacted code and test files.",
        "Author-tests" => "Write tests for the target behavior only. They must fail before implementation.",
        "Implement" => "Implement the change within the manifest files.",
        "Review" => "Review the actual diff and classify issues.",
        "Fix" => "Resolve every blocker and warning from review.",
        "Verify" => "Summarize the final state; the driver decides pass/fail mechanically.",
        "Fix-verify" => "Fix failures from the pinned suite.",
        _ => string.Empty
    };
}
