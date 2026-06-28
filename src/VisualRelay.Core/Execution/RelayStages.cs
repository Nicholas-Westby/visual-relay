using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public static class RelayStages
{
    public static IReadOnlyList<RelayStageDefinition> All { get; } =
    [
        Stage(1, "Ideate", "cheap", "none", "git,ls,cat", """{ "summary": string, "options": string[] }"""),
        Stage(2, "Research", "cheap", "some", "git,ls,cat,grep,find,head,tail,wc,sort,uniq,cut,tr,awk,sed", """{ "findings": string, "constraints": string[] }"""),
        Stage(3, "Diagnose", "balanced", "some", "git,ls,cat,grep,find,head,tail,wc,sort,uniq,cut,tr,awk,sed", """{ "evidence": string, "excerpts": string[], "repro": string }"""),
        Stage(4, "Plan", "balanced", "some", "git,ls,cat,grep,find,head,tail,wc,sort,uniq,cut,tr,awk,sed", """{ "plan": string, "manifest": string[] }"""),
        // Stage 5 writes are "all" because the swival/nono sandbox has no partial-write
        // affordance ("some" = read-only). WorktreeFilter.DiscardNonTestEditsAsync
        // enforces test-only edits post-hoc — non-testFile changes are reverted before
        // the red-gate runs, so only test edits survive into stage 6.
        Stage(5, "Author-tests", "balanced", "all", "all", """{ "testFiles": string[], "rationale": string }"""),
        Stage(6, "Implement", "balanced", "all", "all", """{ "summary": string }"""),
        Stage(7, "Review", "frontier", "some", "all", """{ "verdict": "pass"|"changes", "issues": [] }"""),
        Stage(8, "Fix", "balanced", "all", "all", """{ "summary": string }"""),
        Stage(9, "Verify", "cheap", "some", "all", """{ "summary": string, "commitMessages": string[] }"""),
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
            $"End your reply with a single fenced ```json block, nothing after it, matching: {contract}");

    private const string SelfVerifyStopRule =
        "Run that targeted command at most twice total. The harness re-runs the " +
        "authoritative gate after you return, so do NOT keep re-running it to chase a " +
        "clean local result — if it hangs or times out, record your work and return.";

    private static string SystemPromptFor(string name) => name switch
    {
        "Ideate" => "Frame the task and list 2-3 solution options. Do not edit files.",
        "Research" => "Investigate the codebase; record findings and constraints. Do not edit files.",
        "Diagnose" => "Read application logs and extract evidence that explains the issue.",
        "Plan" => "Write a concrete plan and exact impacted code and test files. The manifest must list only code files — never files under the tasks directory (e.g. llm-tasks/). For files that already exist, use their exact repo-relative path. For files that do not yet exist and will be created, prefix the path with '+' (e.g. '+src/NewFeature.cs').",
        "Author-tests" =>
            "Write tests for the target behavior only. They must fail before implementation. " +
            "Verify your tests compile and fail using ONLY the targeted test command shown in the " +
            "## Verify command section of the prompt. Do NOT run the project's full " +
            "check, lint, format, build, or screenshot gate (e.g. `./visual-relay check`) — " +
            "the harness runs the full gate at its Verify/Commit stages. " +
            SelfVerifyStopRule,
        "Implement" =>
            "Implement the change within the manifest files. " +
            "Verify your changes using ONLY the targeted test command shown in the " +
            "## Verify command section of the prompt. Do NOT run the project's full " +
            "check, lint, or format gate (e.g. `./visual-relay check`) during " +
            "implementation — the harness runs the full gate at the Verify stage. " +
            "Treat a nonzero exit as a real, unfinished failure even when the summary " +
            "says '0 failed': inspect the output tail for a non-test gate and resolve " +
            "it legitimately. Resolving means an edit, not repeated re-runs. " +
            SelfVerifyStopRule + " " +
            "Make MINIMAL, diff-scoped edits: change only what the task requires and " +
            "do NOT reformat, reflow, or compact unrelated code to satisfy size or style budgets.",
        "Review" =>
            "Review the actual diff and classify issues. " +
            "If you need to verify any behavior, use ONLY the targeted test command shown in the " +
            "## Verify command section of the prompt. Do NOT run the project's full " +
            "check, lint, format, build, or screenshot gate (e.g. `./visual-relay check`) — " +
            "the harness runs the full gate at its Verify/Commit stages. " +
            SelfVerifyStopRule,
        "Fix" =>
            "Resolve every blocker and warning from review. " +
            "Verify your changes using ONLY the targeted test command shown in the " +
            "## Verify command section of the prompt. Do NOT run the project's full " +
            "check, lint, or format gate during implementation — the harness runs the " +
            "full gate at the Verify stage. " +
            "Treat a nonzero exit as a real, unfinished failure even when the summary " +
            "says '0 failed': inspect the output tail for a non-test gate and resolve " +
            "it legitimately. Resolving means an edit, not repeated re-runs. " +
            SelfVerifyStopRule + " " +
            "Make MINIMAL, diff-scoped edits: change only what the task requires and " +
            "do NOT reformat, reflow, or compact unrelated code to satisfy size or style budgets.",
        "Verify" => "Summarize the final state; also produce 3-5 DISTINCT Conventional-Commit subject candidates, best-first, deliberately varied (some terse, at least one avoiding file names/paths). The driver decides pass/fail mechanically. Do NOT execute the test suite yourself — the harness has already run it mechanically; use the captured output in ## Verify output below for your summary.",
        "Fix-verify" =>
            "Fix all failures from the full test suite gate shown in ## Verify command. " +
            "The command in ## Verify command IS the full gate — run exactly that command " +
            "and confirm it exits 0 before returning success. " +
            "Treat a nonzero exit as a real, unfinished failure even when the summary " +
            "says '0 failed': inspect the output tail for a non-test gate (perf/wall-clock " +
            "ceiling, lint/coverage ratchet, a throwing setup/teardown hook) and resolve it " +
            "legitimately — do NOT delete tests, weaken assertions, or skip hooks to beat " +
            "the gate. If a non-test gate is not safely fixable within this task's scope, " +
            "report it explicitly as a non-test gate failure instead of hacking around it. " +
            "Do NOT run the project's broader orchestration gate (e.g. `./visual-relay check`). " +
            "The harness runs the full gate mechanically; your job is to make it pass cleanly. " +
            "Make MINIMAL, diff-scoped edits: change only what the task requires and " +
            "do NOT reformat, reflow, or compact unrelated code to satisfy size or style budgets.",
        _ => string.Empty
    };

    internal const string ConfirmImplementationSystemPrompt =
        "The implementation appears to already be in the working tree (an earlier stage wrote it). " +
        "Do NOT re-narrate or re-implement. Read the existing diff against the manifest, confirm it " +
        "matches the plan, and make ONLY small corrective amendments if something is missing or wrong. " +
        "Verify using ONLY the targeted test command shown in the ## Verify command section — do NOT " +
        "run the project's full check, lint, format, build, or screenshot gate; the harness runs the " +
        "full gate at the Verify stage.";
}
